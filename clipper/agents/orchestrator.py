"""Orchestrator: one video in, reviewed clip packages out.

Coordination is plain code (a resumable state machine), not a model: which subagent runs
when, retries, escalation and state are things that should behave the same way every time.
The creative decisions belong to the subagents it launches."""
from __future__ import annotations

import json
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Callable, TypeVar

from pydantic import BaseModel

from ..media import render as renderer
from ..rights import rights_ok
from ..schemas import (EDL, AnalysisResult, Candidate, CaptionResult, MetadataSet, PackageInfo, RenderResult,
                       ReviewVerdict, SourceRecord, Transcript)
from . import output
from .analysis import AnalysisAgent
from ..llm import PendingAnswer
from .base import AgentFailed, Context, StepMeta
from .captions import CaptionsAgent
from .editing import EditingAgent
from .metadata import MetadataAgent
from .reviewer import ReviewerAgent

T = TypeVar("T", bound=BaseModel)
MAX_REVISIONS = 1  # one re-edit when the Reviewer rejects for a fixable reason


class Orchestrator:
    def __init__(self, ctx: Context):
        self.ctx, self.s, self.store, self.log = ctx, ctx.settings, ctx.store, ctx.log
        self.analysis, self.editing = AnalysisAgent(ctx), EditingAgent(ctx)
        self.captions, self.metadata, self.reviewer = CaptionsAgent(ctx), MetadataAgent(ctx), ReviewerAgent(ctx)

    # ---- step runner: cache, timing, audit trail ----
    def step(self, job_id: str, clip_id: str | None, agent: str, key: str, model: type[T],
             fn: Callable[[], tuple[T, StepMeta]], valid: Callable[[T], bool] | None = None) -> T:
        cached = self.store.get_done_step(job_id, clip_id, agent, key)
        if cached:
            out = model.model_validate(cached["output"])
            if valid is None or valid(out):
                return out
        t0 = time.time()
        try:
            out, meta = fn()
        except Exception as e:
            attempts = e.attempts if isinstance(e, AgentFailed) else 1
            self.store.record_step(job_id=job_id, clip_id=clip_id, agent=agent, key=key, status="failed", attempts=attempts,
                                   error=str(e)[:4000], seconds=round(time.time() - t0, 2))
            raise
        self.store.record_step(job_id=job_id, clip_id=clip_id, agent=agent, key=key, status="done", attempts=meta.attempts,
                               prompt_version=meta.prompt_version, model=meta.model, usage=meta.usage,
                               output=out.model_dump(), seconds=round(time.time() - t0, 2))
        return out

    # ---- job ----
    def run(self, source_id: str | None = None, *, job_id: str | None = None, transcript: str | None = None,
            max_clips: int | None = None, review: bool = False, selection: list[str] | None = None) -> dict:
        """Run a job. With `review`, stop after the analysis (status awaiting_selection) so a person can
        choose which candidates become clips; `selection` (candidate ids, stored with the job so a resume
        keeps it) then restricts generation to those candidates."""
        if job_id:
            job = self.store.get_job(job_id)
            if job is None:
                raise ValueError(f"No existe el trabajo {job_id}")
            source_id, opts = job["source_id"], job["options"]
            transcript, max_clips = opts.get("transcript"), opts.get("max_clips")
            review, selection = opts.get("review", False), opts.get("selection")
            self.store.update_job(job_id, status="running")
        else:
            opts = {"transcript": transcript, "max_clips": max_clips, "languages": self.s.languages,
                    "llm": "offline" if self.ctx.llm.offline else "anthropic", "review": review, **self.s.options}
        record = self.store.get_source(source_id) if source_id else None
        if record is None:
            raise ValueError(f"La fuente {source_id} no está en el registro de derechos. Regístrala con `clipper source add`.")
        ok, why = rights_ok(record)
        if not ok:
            raise ValueError(f"La fuente {source_id} no puede procesarse: {why}")
        source = SourceRecord(**record)
        if not Path(source.path).exists():
            raise ValueError(f"No se encuentra el archivo de la fuente: {source.path}")
        job_id = job_id or self.store.create_job(source.id, opts)
        job_dir = self.s.job_dir(job_id)
        t0 = time.time()
        self.log(f"Trabajo {job_id} · fuente {source.id} ({source.duration:.0f}s, {source.rights_status})")

        try:
            self.log("  [Análisis] transcripción, señales y candidatos")
            analysis = self.step(job_id, None, "analysis", "analysis", AnalysisResult,
                                 lambda: self.analysis.run(source, job_dir, transcript),
                                 valid=lambda a: Path(a.transcript_path).exists())
        except Exception as e:
            self.store.update_job(job_id, status="failed", summary={"error": str(e)[:2000]})
            raise
        tr = Transcript.model_validate_json(Path(analysis.transcript_path).read_text(encoding="utf-8"))
        if review and selection is None:
            self.log(f"  {len(analysis.candidates)} momentos sugeridos. No se genera nada hasta que elijas cuáles.")
            summary = self.summarise(job_id, time.time() - t0)
            self.store.update_job(job_id, status="awaiting_selection", summary=summary)
            return summary
        if selection is not None:
            wanted = set(selection)
            todo = [(i, c) for i, c in enumerate(analysis.candidates) if c.id in wanted]
        else:
            todo = list(enumerate(analysis.candidates[: max_clips or self.s.max_clips]))
        self.log(f"  {len(todo)} candidatos: " + ", ".join(f"{c.start:.0f}-{c.end:.0f}s ({c.score:.0f})" for _, c in todo))

        for idx, cand in todo:
            clip_id = self.store.upsert_clip(job_id, idx, cand.model_dump())
            if self.store.get_clip(clip_id)["status"] in ("pending_approval", "needs_human", "approved", "rejected", "rejected_by_human"):
                continue  # finished in an earlier run
            self.log(f"  Clip {clip_id} ({cand.start:.0f}-{cand.end:.0f}s)")
            try:
                self.process_clip(job_id, clip_id, cand, source, tr, job_dir)
            except AgentFailed as e:
                self.log(f"    ESCALADO a revisión humana: {e}")
                self.store.update_clip(clip_id, status="agent_failed", decision_note=str(e)[:2000])
            except PendingAnswer:
                raise  # replay mode: stop here until the answer file exists
            except Exception as e:  # a render or I/O failure in one clip must not stop the others
                self.log(f"    ERROR: {type(e).__name__}: {str(e)[:300]}")
                self.store.update_clip(clip_id, status="error", decision_note=f"{type(e).__name__}: {str(e)[:2000]}")

        summary = self.summarise(job_id, time.time() - t0)
        self.store.update_job(job_id, status="done", summary=summary)
        (job_dir / "report.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
        return summary

    # ---- one clip ----
    def process_clip(self, job_id: str, clip_id: str, cand: Candidate, source: SourceRecord, tr: Transcript, job_dir: Path) -> None:
        work = job_dir / "clips" / clip_id
        feedback, previous = "", None
        recent = self.store.recent_hooks(exclude_clip=clip_id)
        recent_texts = sorted({t for r in recent for t in (r["hook_text"] or {}).values()})
        for revision in range(MAX_REVISIONS + 1):
            tag = f"r{revision}"
            self.log(f"    [Edición] {'EDL' if revision == 0 else 'revisión pedida por el Revisor'}")
            edl = self.step(job_id, clip_id, "editing", f"edit:{tag}", EDL, lambda: self.editing.run(
                self.editing.prepare(clip_id, source, cand, tr, self.s.languages, recent_texts, job_dir,
                                     feedback=feedback, previous=previous, revision=revision)))
            self.store.update_clip(clip_id, status="edited", edl=edl.model_dump())

            caption_tr = tr
            job_options = self.store.get_job(job_id)["options"]
            if source.has_audio and not self.ctx.llm.offline and not job_options.get("transcript"):
                from ..media.transcribe import transcribe_clip
                self.log("    [Sincronización] alineando la voz del recorte seleccionado")
                caption_tr = transcribe_clip(source.path, work / tag / "alignment.wav",
                                             edl.source.start, edl.source.end, self.s, tr.language)

            # Captions and Titles/metadata do not depend on each other: run them side by side.
            self.log("    [Subtítulos] + [Títulos y metadata] en paralelo")
            with ThreadPoolExecutor(max_workers=2) as pool:
                f_cap = pool.submit(self.step, job_id, clip_id, "captions", f"captions:{tag}", CaptionResult,
                                    lambda: self.captions.run(edl, caption_tr))
                f_meta = pool.submit(self.step, job_id, clip_id, "metadata", f"metadata:{tag}", MetadataSet,
                                     lambda: self.metadata.run(edl, tr, source.credit))
                caps, meta = f_cap.result(), f_meta.result()

            self.log(f"    [Render] {', '.join(edl.languages)}")
            renders = self.step(job_id, clip_id, "render", f"render:{tag}", RenderResult,
                                lambda: self.render(edl, source, caps, work / tag),
                                valid=lambda r: all(Path(x.path).exists() for x in r.renders.values()))
            self.store.update_clip(clip_id, status="rendered")

            self.log("    [Revisor] guards y QA")
            review = self.step(job_id, clip_id, "reviewer", f"review:{tag}", ReviewVerdict,
                               lambda: self.reviewer.run(edl, source, tr, caps, meta, renders, recent, work / tag))
            self.store.update_clip(clip_id, review=review.model_dump())
            self.log(f"      veredicto: {review.status}" + (f" · {review.feedback[:160]}" if review.feedback else ""))
            if review.status == "rejected" and review.fixable and revision < MAX_REVISIONS:
                feedback, previous = review.feedback, edl.model_dump()
                continue
            break

        if review.status == "rejected":
            self.store.update_clip(clip_id, status="rejected", decision_note=review.feedback[:2000], hook_text=edl.hook.text)
            return
        self.log("    [Output] paquete y cola de aprobación")
        pkg = self.step(job_id, clip_id, "output", "output", PackageInfo, lambda: (
            output.build_package(self.s, self.store, job_id, clip_id, edl, source, caps, meta, renders, review,
                                 offline=self.ctx.llm.offline), StepMeta(model="deterministic")),
            valid=lambda p: Path(p.dir).exists())
        self.store.update_clip(clip_id, status=pkg.status, package_dir=pkg.dir, hook_text=edl.hook.text)

    def render(self, edl: EDL, source: SourceRecord, caps: CaptionResult, out_dir: Path) -> tuple[RenderResult, StepMeta]:
        reports = {lang: renderer.render_clip(edl, source, caps.tracks.get(lang), lang, out_dir, self.s) for lang in edl.languages}
        return RenderResult(renders=reports), StepMeta(model="ffmpeg")

    # ---- report ----
    def summarise(self, job_id: str, seconds: float) -> dict:
        clips = self.store.list_clips(job_id)
        steps = self.store.list_steps(job_id)
        usage = {"input_tokens": 0, "output_tokens": 0}
        per_agent: dict[str, dict] = {}
        for st in steps:
            a = per_agent.setdefault(st["agent"], {"runs": 0, "failed": 0, "first_try": 0, "seconds": 0.0})
            a["runs"] += 1
            a["failed"] += st["status"] == "failed"
            a["first_try"] += st["status"] == "done" and st["attempts"] == 1
            a["seconds"] = round(a["seconds"] + (st["seconds"] or 0), 1)
            for k in usage:
                usage[k] += int((st["usage"] or {}).get(k) or 0)
        counts: dict[str, int] = {}
        for c in clips:
            counts[c["status"]] = counts.get(c["status"], 0) + 1
        return {"job_id": job_id, "seconds": round(seconds, 1), "clips": counts, "agents": per_agent, "usage": usage,
                "packages": [{"clip_id": c["id"], "status": c["status"], "dir": c["package_dir"],
                              "hook": c["hook_text"], "note": c["decision_note"]} for c in clips]}
