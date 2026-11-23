"""Analysis subagent: video -> transcript, signals and ranked clip candidates."""
from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path

from ..media import signals as sig
from ..media import transcribe as tr
from ..schemas import AnalysisProposal, AnalysisResult, Candidate, Signals, SourceRecord, Transcript
from .base import ContractError, StepMeta, Subagent

EXCITED = re.compile(r"(!|\?!|\bno way\b|\binsane\b|\bcrazy\b|\bwhat\b|\bwow\b|\boh my\b|\bincre[ií]ble\b|\bno puede ser\b|\blocura\b)", re.I)


@dataclass
class AnalysisInput:
    source: SourceRecord
    transcript: Transcript
    signals: Signals
    target_count: int


class AnalysisAgent(Subagent):
    name = "analysis"
    tier = "standard"
    contract = AnalysisProposal
    max_tokens = 6000

    # ---- full step: tools + decision ----
    def run(self, source: SourceRecord, job_dir: Path, transcript_override: str | None = None) -> tuple[AnalysisResult, StepMeta]:
        wav = job_dir / "audio_16k.wav"
        if source.has_audio:
            tr.extract_audio(source.path, wav)
        if transcript_override:
            transcript = tr.load_transcript(transcript_override, duration=source.duration)
        elif source.has_audio:
            self.ctx.log(f"    transcribiendo con faster-whisper ({self.s.whisper_model}, {self.s.whisper_device})...")
            transcript = tr.transcribe(wav, self.s, language=source.language)
        else:
            transcript = tr.silent_transcript(source.duration, source.language or "und")
        if transcript.duration <= 0:
            transcript.duration = source.duration
        signals = sig.compute(source.path, wav if source.has_audio else None, scene_mode=self.s.scene_mode)

        t_path, s_path = job_dir / "transcript.json", job_dir / "signals.json"
        t_path.write_text(transcript.model_dump_json(indent=1), encoding="utf-8")
        s_path.write_text(signals.model_dump_json(), encoding="utf-8")

        target = max(1, min(self.s.max_clips, int(source.duration // (self.s.clip_min_s * 2))))
        inp = AnalysisInput(source=source, transcript=transcript, signals=signals, target_count=target)
        proposal, meta = self.decide(inp)

        ranked = sorted(proposal.candidates, key=lambda c: -c.score)
        segs = transcript.segments
        candidates = [Candidate(id=f"cand{i:02d}", start_segment=c.start_segment, end_segment=c.end_segment,
                                start=segs[c.start_segment].start, end=segs[c.end_segment].end, score=c.score,
                                category=c.category, reason=c.reason, hook_idea=c.hook_idea)
                      for i, c in enumerate(ranked)]
        return AnalysisResult(transcript_path=str(t_path), signals_path=str(s_path), candidates=candidates), meta

    # ---- prompt input ----
    def build_user(self, inp: AnalysisInput) -> str:
        has_speech = any(s.text for s in inp.transcript.segments)
        lines = []
        for s in inp.transcript.segments:
            mean, peak, cuts = sig.segment_features(s, inp.signals)
            lines.append(f"[{s.id}] {s.start:.1f}-{s.end:.1f}s | energy {mean:.2f}/{peak:.2f} | cuts {cuts} | {s.text or '(no speech)'}")
        return (
            "# Video\n"
            f"Duration: {inp.source.duration:.0f}s. Spoken language: {inp.transcript.language}. Speech present: {'yes' if has_speech else 'no'}.\n\n"
            "# Constraints\n"
            f"- Propose up to {inp.target_count} candidates, best first. Fewer is fine if the video does not have that many good moments.\n"
            f"- A candidate spans start_segment..end_segment inclusive and must last between {self.s.clip_min_s:.0f} and {self.s.clip_max_s:.0f} seconds.\n"
            "- Candidates must not overlap.\n\n"
            "# Segments\n"
            "Format: [id] start-end | energy mean/peak (0-1, relative to the loudest moment) | hard cuts | text\n"
            + "\n".join(lines)
        )

    # ---- validation ----
    def check(self, inp: AnalysisInput, out: AnalysisProposal) -> None:
        segs = inp.transcript.segments
        if not out.candidates:
            raise ContractError("No candidates. Propose at least one.")
        spans = []
        for i, c in enumerate(out.candidates):
            if c.end_segment >= len(segs) or c.start_segment > c.end_segment:
                raise ContractError(f"candidates[{i}]: segment range {c.start_segment}..{c.end_segment} is invalid (valid ids: 0..{len(segs) - 1}).")
            dur = segs[c.end_segment].end - segs[c.start_segment].start
            if not (self.s.clip_min_s <= dur <= self.s.clip_max_s):
                raise ContractError(f"candidates[{i}]: lasts {dur:.1f}s; it must last {self.s.clip_min_s:.0f}-{self.s.clip_max_s:.0f}s. Adjust the segment range.")
            spans.append((c.start_segment, c.end_segment, i))
        spans.sort()
        for (a1, b1, i1), (a2, b2, i2) in zip(spans, spans[1:]):
            if a2 <= b1:
                raise ContractError(f"candidates[{i1}] and candidates[{i2}] overlap (segments {a1}..{b1} and {a2}..{b2}).")

    # ---- offline heuristic (plumbing tests only) ----
    def offline(self, inp: AnalysisInput) -> dict:
        segs = inp.transcript.segments
        feats = [sig.segment_features(s, inp.signals) for s in segs]
        target_len = min(self.s.clip_max_s, max(self.s.clip_min_s + 5, 35.0))
        windows = []
        for i in range(len(segs)):
            j = i
            while j + 1 < len(segs) and segs[j].end - segs[i].start < target_len and segs[j + 1].end - segs[i].start <= self.s.clip_max_s:
                j += 1
            dur = segs[j].end - segs[i].start
            if dur < self.s.clip_min_s:
                continue
            mean = sum(f[0] for f in feats[i:j + 1]) / (j - i + 1)
            peak = max(f[1] for f in feats[i:j + 1])
            cuts = sum(f[2] for f in feats[i:j + 1])
            excited = sum(len(EXCITED.findall(s.text)) for s in segs[i:j + 1])
            score = min(100.0, 35 * mean + 25 * peak + min(20, 6 * excited) + min(15, 3 * cuts) + 5)
            windows.append((score, i, j))
        windows.sort(reverse=True)
        picked: list[tuple[float, int, int]] = []
        for score, i, j in windows:
            if all(j < a or i > b for _, a, b in picked):
                picked.append((score, i, j))
            if len(picked) >= inp.target_count:
                break
        return {"candidates": [
            {"start_segment": i, "end_segment": j, "score": round(score, 1), "category": "heuristic",
             "reason": "Heurística offline: energía de audio, cortes y marcas de emoción.", "hook_idea": ""}
            for score, i, j in picked]}
