"""Panel web (FastAPI). Usa los mismos Store, Orchestrator y reglas de derechos que la CLI:
no añade lógica de negocio, solo la expone. Pensado para uso local: no tiene usuarios, así
que escucha en localhost (ver docker-compose.yml). No lo expongas a internet sin proxy con login."""
from __future__ import annotations

import re
import json
from copy import deepcopy
import shutil
import threading
import time
import uuid
from collections import deque
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Optional

from fastapi import Body, FastAPI, File, Form, HTTPException, UploadFile
from fastapi.encoders import jsonable_encoder
from fastapi.responses import FileResponse, HTMLResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

from .. import ai_settings as ai
from .. import editor
from ..agents import output
from ..agents.base import Context
from ..agents.orchestrator import Orchestrator
from ..config import VALID_RIGHTS, Settings
from ..llm import make_llm
from .. import __version__
from ..media import captions as cap
from ..media import download
from ..rights import RightsError, register_source
from ..schemas import SourceRecord, Transcript
from ..store import Store

STATIC = Path(__file__).parent / "static"
LANG_RE = re.compile(r"^[a-z]{2,5}$")
VIDEO_EXT = {".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v"}
JOB_RE = re.compile(r"Trabajo (job_\w+) ")


class KeyBody(BaseModel):
    key: str
    provider: Optional[str] = None      # default: the active provider


class ProviderBody(BaseModel):
    provider: str
    base_url: Optional[str] = None
    models: Optional[dict] = None       # {"fast": id, "standard": id, "superior": id}
    prices: Optional[dict] = None       # {"fast": [input, output], ...} USD per million tokens
    activate: bool = True


class BudgetBody(BaseModel):
    usd: Optional[float] = None


class JobRequest(BaseModel):
    source_id: str
    offline: bool = False
    max_clips: Optional[int] = None
    languages: Optional[str] = None
    review: bool = True                    # True: analyse only, then the person picks the moments
    analysis_model: Optional[str] = None   # fast | standard | superior
    model: Optional[str] = None            # model for everything after the analysis (only when review is False)
    layout: Optional[str] = None
    caption_style: Optional[str] = None


class GenerateRequest(BaseModel):
    candidates: list[str]
    model: str
    layout: str = "auto"
    caption_style: str = "auto"
    languages: Optional[list[str]] = None


class Decision(BaseModel):
    by: str
    note: str = ""


class CreativeRequest(BaseModel):
    model: str = "standard"
    layout: str = "auto"


class ProjectBody(BaseModel):
    name: str
    description: str = ""


class MoveProjectBody(BaseModel):
    project_id: str


class Runs:
    """Trabajos en ejecución. Un solo hilo a la vez: Whisper y FFmpeg ya usan todos los núcleos."""

    def __init__(self) -> None:
        self.pool = ThreadPoolExecutor(max_workers=1)
        self.items: dict[str, dict] = {}
        self.lock = threading.Lock()

    def start(self, label: str, fn, project_id: str | None = None) -> str:
        run_id = uuid.uuid4().hex[:10]
        item = {"id": run_id, "label": label, "status": "queued", "log": deque(maxlen=400), "job_id": None,
                "error": None, "started": time.time(), "project_id": project_id}
        with self.lock:
            self.items[run_id] = item

        def log(line: str) -> None:
            item["log"].append(line)
            m = JOB_RE.search(line)
            if m and not item["job_id"]:
                item["job_id"] = m.group(1)

        def work() -> None:
            item["status"] = "running"
            try:
                fn(log)
                item["status"] = "done"
            except Exception as e:  # shown in the panel; the job row also records failures
                item["status"] = "failed"
                item["error"] = f"{type(e).__name__}: {e}"
                log(f"ERROR: {item['error']}")

        self.pool.submit(work)
        return run_id

    def public(self, item: dict) -> dict:
        return {k: (list(v) if k == "log" else v) for k, v in item.items()}


def create_app(settings: Settings | None = None) -> FastAPI:
    s = settings or Settings.from_env()
    from clipper.media import hardware
    render_file = s.data_dir / "render-settings.json"
    try:
        render_preferences = json.loads(render_file.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        render_preferences = {}
    if not isinstance(render_preferences, dict):
        render_preferences = {}
    render_device = render_preferences.get("device", "cpu" if s.encoder == "libx264" else s.encoder)
    if not isinstance(render_device, str):
        render_device = "cpu"
    s.encoder = hardware.resolve(render_device)
    s.fps = render_preferences.get("fps", s.fps) if render_preferences.get("fps", s.fps) in (30, 60) else 30
    quality = render_preferences.get("quality", 19)
    s.render_quality = quality if type(quality) is int and 14 <= quality <= 30 else 19
    render_lock = threading.RLock()
    store = Store(s.database_url)
    runs = Runs()
    uploads = s.data_dir / "uploads"
    uploads.mkdir(parents=True, exist_ok=True)
    downloads = s.data_dir / "downloads"
    input_dir = Path(__import__("os").environ.get("CLIPPER_INPUT_DIR", "/input"))
    app = FastAPI(title="Kairos Studio", version=__version__, docs_url="/api/docs", redoc_url=None)
    import secrets
    from .security import LocalSecurity
    session_token = secrets.token_hex(32)
    app.add_middleware(LocalSecurity, token=session_token,
                       key_reader=lambda: [ai.conf(s.data_dir, p["id"])["key"] for p in ai.catalog()])
    from . import library_delete
    library_delete.mount(app, s, store, runs)

    @app.get("/healthz")
    def health():
        return {"ok": True}


    # Jobs left 'running' by a stopped process cannot still be running: say so, so they can be resumed.
    for j in store.list_jobs():
        if j["status"] == "running":
            store.update_job(j["id"], status="interrupted")

    def settings_for(options: dict) -> Settings:
        """Settings for one job: the environment plus the choices stored with the job."""
        try:
            return deepcopy(s).apply_options(options)
        except ValueError as e:
            raise HTTPException(400, str(e))

    def launch(label: str, cfg: Settings, call, project_id: str | None = None) -> str:
        if cfg.llm_provider not in ("offline", "replay") and cfg.ai_missing:
            raise HTTPException(400, f"{cfg.ai_missing} También puedes usar el modo de prueba sin IA.")

        def fn(log):
            ctx = Context(settings=cfg, store=store, log=log,
                          llm=make_llm(cfg.llm_provider, cfg.api_key, cfg.data_dir / "replay", on_usage=ai.Ledger(cfg.data_dir, cfg.ai_provider).add,
                                       settings=cfg, log=log))
            call(Orchestrator(ctx))
        return runs.start(label, fn, project_id)

    def public(clip: dict) -> dict:
        """A clip row for the browser: the draft can be large, so lists only say whether one exists."""
        return {**{k: v for k, v in clip.items() if k != "draft"}, "has_draft": bool(clip.get("draft"))}

    def require_project(project_id: str) -> dict:
        project = store.get_project(project_id)
        if not project:
            raise HTTPException(404, "No existe el proyecto.")
        return project

    from . import project_resources
    project_resources.mount(app, s, store, require_project)

    @app.get("/api/projects")
    def list_projects():
        return jsonable_encoder([{**p, "source_count": len(store.list_sources(p["id"])), "job_count": len(store.list_jobs(p["id"])),
                                  "clip_count": len(store.list_clips(project_id=p["id"])), "resource_count": len(store.list_resources(p["id"]))} for p in store.list_projects()])

    @app.post("/api/projects")
    def add_project(body: ProjectBody):
        if not 1 <= len(body.name.strip()) <= 80 or len(body.description) > 600:
            raise HTTPException(400, "Usa un nombre de 1–80 caracteres y una descripción de hasta 600.")
        return jsonable_encoder(store.create_project(body.name.strip(), body.description.strip()))

    @app.put("/api/projects/{project_id}")
    def edit_project(project_id: str, body: ProjectBody):
        require_project(project_id)
        if not 1 <= len(body.name.strip()) <= 80 or len(body.description) > 600:
            raise HTTPException(400, "Usa un nombre de 1–80 caracteres y una descripción de hasta 600.")
        store.update_project(project_id, body.name.strip(), body.description.strip())
        return store.get_project(project_id)

    @app.put("/api/sources/{source_id}/project")
    def move_source(source_id: str, body: MoveProjectBody):
        require_project(body.project_id)
        if not store.get_source(source_id):
            raise HTTPException(404, "No existe el video.")
        source_project = store.get_source(source_id)["project_id"]
        with runs.lock:
            active = any(r["project_id"] == source_project and r["status"] in ("queued", "running") for r in runs.items.values())
        if any(j["source_id"] == source_id and j["status"] == "running" for j in store.list_jobs()) or active:
            raise HTTPException(409, "Espera a que termine el trabajo de este video para moverlo.")
        store.move_source(source_id, body.project_id)
        return {"ok": True}

    # ---------- pages ----------
    app.mount("/static", StaticFiles(directory=STATIC), name="static")

    @app.get("/", response_class=HTMLResponse)
    def index():
        # Scripts and styles carry the newest file time, so a rebuilt panel is never served from cache.
        version = str(int(max(p.stat().st_mtime for p in STATIC.iterdir())))
        html = (STATIC / "index.html").read_text(encoding="utf-8").replace("{{v}}", version).replace("{{csrf}}", session_token)
        return HTMLResponse(html, headers={"Cache-Control": "no-store"})

    @app.get("/api/config")
    def config():
        return {"version": __version__, "llm": s.llm_provider, "api_key_set": s.ai_missing is None, "ai": True, "languages": s.languages,
                "provider": {"id": s.ai_provider, "name": s.ai_name, "missing": s.ai_missing},
                "models": {"fast": s.model_fast, "standard": s.model_standard, "superior": s.model_superior},
                "whisper": f"{s.whisper_model} ({s.whisper_device})", "rights": list(VALID_RIGHTS),
                "max_clips": s.max_clips, "publish_slots": s.publish_slots,
                "tiers": tiers(),
                "devices": hardware.devices(),
                "render": {"device": render_device, "encoder": s.encoder, "resolved_encoder": s.encoder, "fps": s.fps, "quality": s.render_quality},
                "input_dir": str(input_dir) if input_dir.is_dir() else None}

    @app.put("/api/settings")
    def render_settings(body: dict = Body(...)):
        nonlocal render_device
        device = body.get("device", render_device)
        if device not in ("auto", "cpu", *(d["id"] for d in hardware.devices())):
            raise HTTPException(400, "El dispositivo no está disponible dentro del contenedor.")
        fps, quality = body.get("fps", s.fps), body.get("quality", s.render_quality)
        if fps not in (30, 60) or not isinstance(quality, int) or not 14 <= quality <= 30:
            raise HTTPException(400, "Usa 30/60 fps y calidad entre 14 y 30.")
        prefs = {"device": device, "fps": fps, "quality": quality}
        render_file.parent.mkdir(parents=True, exist_ok=True)
        with render_lock:
            temp = render_file.with_suffix(".tmp")
            temp.write_text(json.dumps(prefs), encoding="utf-8")
            temp.replace(render_file)
            render_device, s.encoder, s.fps, s.render_quality = device, hardware.resolve(device), fps, quality
        return {"ok": True}

    # ---------- AI provider connection and usage ----------
    def tiers() -> list[dict]:
        c = ai.conf(s.data_dir)
        names, notes = ai.tier_names(c), {"fast": "Rápido y barato", "standard": "Equilibrado", "superior": "Máxima calidad"}
        price = lambda t: f"${c['prices'][t][0]:g} / ${c['prices'][t][1]:g}" if c["prices"].get(t) else "precio no configurado"
        return [{"id": t, "name": names[t], "model": c["models"].get(t) or "", "price": price(t), "note": notes[t]} for t in ai.TIERS]

    def ai_state() -> dict:
        return ai.public_state(s)

    def provider_id(pid: str | None) -> str:
        pid = pid or ai.active_id(s.data_dir)
        if not ai.meta(pid):
            raise HTTPException(400, "Proveedor desconocido.")
        return pid

    def refresh() -> None:
        ai.apply(s)

    @app.get("/api/settings/ai")
    def get_ai():
        return ai_state()

    @app.get("/api/settings/ai/providers")
    def get_providers():
        return ai.catalog()

    @app.put("/api/settings/ai/provider")
    def set_provider(body: ProviderBody):
        pid = provider_id(body.provider)
        m = ai.meta(pid)
        if body.base_url is not None and body.base_url.strip() and not re.match(r"^https?://", body.base_url.strip()):
            raise HTTPException(400, "La dirección del servidor debe empezar por http:// o https://.")
        if body.base_url is not None and not m.get("editable_url") and body.base_url.strip() not in ("", m["base_url"]):
            raise HTTPException(400, f"La dirección de {m['name']} es fija.")
        if body.models is not None and any(len(str(v or "")) > 200 for v in body.models.values()):
            raise HTTPException(400, "Nombre de modelo demasiado largo.")
        prices = None
        if body.prices is not None:
            try:
                prices = {t: [float(v[0]), float(v[1])] for t, v in body.prices.items() if t in ai.TIERS and v is not None}
            except (TypeError, ValueError, IndexError):
                raise HTTPException(400, "Precios no válidos: usa [entrada, salida] en USD por millón de tokens.")
            if any(not (0 <= x <= 10_000) for v in prices.values() for x in v):
                raise HTTPException(400, "Los precios deben estar entre 0 y 10.000 USD por millón de tokens.")
        ai.set_provider(s.data_dir, pid, base_url=body.base_url if m.get("editable_url") else None,
                        models=body.models, prices=prices, activate=body.activate)
        refresh()
        return ai_state()

    @app.get("/api/settings/ai/models")
    def get_models(provider: str | None = None):
        pid = provider_id(provider)
        c = ai.conf(s.data_dir, pid)
        if c.get("needs_key") and not c["key"]:
            raise HTTPException(400, f"Guarda primero la API key de {c['name']}.")
        try:
            return {"models": ai.list_models(c["base_url"], c["key"], c["name"], "anthropic" if c["kind"] == "anthropic" else c.get("dialect") or "")}
        except ai.KeyError_ as e:
            raise HTTPException(400, str(e))

    @app.put("/api/settings/ai/key")
    def set_key(body: KeyBody):
        pid = provider_id(body.provider)
        name, key = ai.meta(pid)["name"], body.key.strip()
        if not ai.valid_key_format(key, pid):
            hint = "empieza por «sk-ant-» y es larga. Cópiala completa desde console.anthropic.com" if pid == "anthropic" else "cópiala completa, sin espacios"
            raise HTTPException(400, f"Esa no parece una API key de {name}: {hint}.")
        try:
            result = ai.verify(s.data_dir, pid, key, ai.Ledger(s.data_dir, pid))
        except ai.KeyError_ as e:
            raise HTTPException(400, str(e))
        ai.set_key(s.data_dir, pid, key)
        if pid == "anthropic":
            ai.save(s.data_dir, {"limits": result["limits"]})
        refresh()
        return {**ai_state(), "warning": result["warning"], "models": result.get("models")}

    @app.post("/api/settings/ai/test")
    def test_key(provider: str | None = None):
        pid = provider_id(provider)
        c = ai.conf(s.data_dir, pid)
        if c.get("needs_key") and not c["key"]:
            raise HTTPException(400, "Todavía no hay una API key.")
        try:
            result = ai.verify(s.data_dir, pid, c["key"], ai.Ledger(s.data_dir, pid))
        except ai.KeyError_ as e:
            raise HTTPException(400, str(e))
        if pid == "anthropic" and result["limits"]:
            ai.save(s.data_dir, {"limits": result["limits"]})
        return {**ai_state(), "warning": result["warning"], "models": result.get("models")}

    @app.delete("/api/settings/ai/key")
    def delete_key(provider: str | None = None):
        pid = provider_id(provider)
        ai.set_key(s.data_dir, pid, None)
        if pid == "anthropic":
            ai.save(s.data_dir, {"limits": None})
        refresh()
        return ai_state()

    @app.put("/api/settings/ai/budget")
    def set_budget(body: BudgetBody):
        if body.usd is not None and not (0 <= body.usd <= 1_000_000):
            raise HTTPException(400, "El crédito debe estar entre 0 y 1.000.000 USD.")
        ai.save(s.data_dir, {"budget": {"usd": body.usd, "since": ai.now_iso()}})
        return ai_state()

    @app.get("/api/usage")
    def usage(days: int = 30):
        return ai.summarize(s, max(1, min(days, 365)))

    # ---------- sources ----------
    @app.get("/api/sources")
    def sources(project_id: str | None = None):
        return jsonable_encoder(list(reversed(store.list_sources(project_id))))

    @app.get("/api/inputs")
    def inputs():
        if not input_dir.is_dir():
            return []
        return sorted(str(p.relative_to(input_dir)) for p in input_dir.rglob("*") if p.is_file() and p.suffix.lower() in VIDEO_EXT)

    def _register(path: Path, rights: str, evidence: str, credit: str, language: str, project_id: str = "project_default") -> dict:
        require_project(project_id)
        try:
            rec = register_source(store, path, rights_status=rights, evidence=evidence, credit=credit or None, language=language or None, project_id=project_id)
        except RightsError as e:
            raise HTTPException(400, str(e))
        except Exception as e:
            raise HTTPException(400, f"No se pudo leer el video: {e}")
        return jsonable_encoder(rec.model_dump())

    @app.post("/api/sources/upload")
    def upload(file: UploadFile = File(...), rights: str = Form(...), evidence: str = Form(...),
               credit: str = Form(""), language: str = Form(""), project_id: str = Form("project_default")):
        require_project(project_id)
        if Path(file.filename or "").suffix.lower() not in VIDEO_EXT:
            raise HTTPException(400, f"Formato no admitido. Usa: {', '.join(sorted(VIDEO_EXT))}")
        # Validate rights before writing a multi-GB file to disk.
        if rights not in VALID_RIGHTS:
            raise HTTPException(400, f"Estado de derechos inválido. Valores: {', '.join(VALID_RIGHTS)}")
        if len(evidence.strip()) < 8:
            raise HTTPException(400, "Falta la evidencia de derechos (al menos 8 caracteres).")
        safe = re.sub(r"[^A-Za-z0-9._-]+", "_", Path(file.filename).name)[:80]
        project_uploads = uploads / project_id
        project_uploads.mkdir(parents=True, exist_ok=True)
        dest = project_uploads / f"{uuid.uuid4().hex[:8]}_{safe}"
        with open(dest, "wb") as out:
            shutil.copyfileobj(file.file, out, 1 << 20)
        rec = _register(dest, rights, evidence, credit, language, project_id)
        if Path(rec["path"]) != dest.resolve():  # the same video was registered before
            dest.unlink(missing_ok=True)
        return rec

    @app.post("/api/sources/from-input")
    def from_input(rel: str = Form(...), rights: str = Form(...), evidence: str = Form(...),
                   credit: str = Form(""), language: str = Form(""), project_id: str = Form("project_default")):
        path = (input_dir / rel).resolve()
        if input_dir.resolve() not in path.parents:
            raise HTTPException(400, "Ruta fuera de la carpeta de entrada.")
        return _register(path, rights, evidence, credit, language, project_id)

    @app.post("/api/sources/from-url")
    def from_url(url: str = Form(...), rights: str = Form(...), evidence: str = Form(...),
                 credit: str = Form(""), language: str = Form(""), project_id: str = Form("project_default")):
        require_project(project_id)
        # Validate everything before queueing a (possibly multi-GB) download.
        try:
            url = download.check_url(url)
        except download.DownloadError as e:
            raise HTTPException(400, str(e))
        if rights not in VALID_RIGHTS:
            raise HTTPException(400, f"Estado de derechos inválido. Valores: {', '.join(VALID_RIGHTS)}")
        if len(evidence.strip()) < 8:
            raise HTTPException(400, "Falta la evidencia de derechos (al menos 8 caracteres).")

        def fn(log):
            path, meta = download.fetch(url, downloads / project_id, log)
            try:
                rec = register_source(store, path, rights_status=rights, evidence=evidence,
                                      credit=credit or meta.get("uploader"), language=language or None, project_id=project_id)
            except Exception:
                path.unlink(missing_ok=True)
                raise
            if Path(rec.path) != path.resolve():  # the same video was registered before
                path.unlink(missing_ok=True)
            log(f"Video registrado: {rec.id}")
        return {"run_id": runs.start(f"Descargar {url}", fn, project_id)}

    # ---------- jobs ----------
    @app.post("/api/jobs")
    def start_job(req: JobRequest):
        if not store.get_source(req.source_id):
            raise HTTPException(404, "Fuente no registrada.")
        langs = [x.strip() for x in (req.languages or "").split(",") if x.strip()]
        # Finding moments is always Haiku's job: it is cheap, and the person picks the model for the real work afterwards.
        options = {"llm": "offline" if req.offline else None, "languages": langs, "analysis_model": req.analysis_model or ("fast" if req.review else None),
                   "model": req.model, "layout": req.layout, "caption_style": req.caption_style}
        cfg = settings_for(options)
        label = f"{'Analizar' if req.review else 'Procesar'} {req.source_id}"
        run_id = launch(label, cfg, lambda orch: orch.run(req.source_id, max_clips=req.max_clips, review=req.review), store.get_source(req.source_id)["project_id"])
        return {"run_id": run_id}

    def _analysis(job_id: str) -> tuple[dict, dict, Transcript | None]:
        job = store.get_job(job_id)
        if not job:
            raise HTTPException(404, "No existe el trabajo.")
        st = store.get_done_step(job_id, None, "analysis", "analysis")
        if not st:
            raise HTTPException(409, "Este trabajo todavía no tiene el análisis terminado.")
        return job, st["output"], editor.transcript_for(store, job_id)

    @app.get("/api/jobs/{job_id}/candidates")
    def job_candidates(job_id: str):
        """The moments the Analysis subagent suggests, with what is said in each and whether a clip already exists."""
        job, analysis, tr = _analysis(job_id)
        src = store.get_source(job["source_id"]) or {}
        clips = {c["idx"]: c for c in store.list_clips(job_id)}
        out = []
        for i, c in enumerate(analysis["candidates"]):
            clip = clips.get(i)
            text = " ".join(sg.text for sg in tr.segments if sg.text and sg.start >= c["start"] - 0.01 and sg.end <= c["end"] + 0.01) if tr else ""
            out.append({**c, "duration": round(c["end"] - c["start"], 1), "text": text,
                        "clip_id": clip["id"] if clip else None, "clip_status": clip["status"] if clip else None})
        detected = tr.language if tr and tr.language and tr.language != "und" else None
        return jsonable_encoder({"job": {k: job[k] for k in ("id", "status", "source_id", "options")}, "source": src,
                                 "candidates": out, "languages": s.languages,
                                 "spoken": {"detected": detected, "declared": src.get("language")}})

    @app.post("/api/jobs/{job_id}/generate")
    def generate(job_id: str, req: GenerateRequest):
        job, analysis, _ = _analysis(job_id)
        if job["status"] == "running" or any(r["label"] == f"Generar {job_id}" and r["status"] in ("queued", "running") for r in runs.items.values()):
            raise HTTPException(409, "Este trabajo ya está en ejecución.")
        known = {c["id"] for c in analysis["candidates"]}
        picked = [c for c in req.candidates if c in known]
        if not picked:
            raise HTTPException(400, "Elige al menos un momento.")
        langs = [x for x in (req.languages or []) if LANG_RE.match(x)]
        options = {**job["options"], "model": req.model, "layout": req.layout, "caption_style": req.caption_style,
                   "languages": langs or job["options"].get("languages"), "selection": picked, "review": True}
        cfg = settings_for(options)          # validates model and styles before anything is stored
        store.update_job(job_id, options=options)   # the run reads its selection from the job
        try:
            return {"run_id": launch(f"Generar {job_id}", cfg, lambda orch: orch.run(job_id=job_id), job["project_id"])}
        except HTTPException:
            store.update_job(job_id, options=job["options"])
            raise

    @app.post("/api/clips/{clip_id}/creative-version")
    def creative_version(clip_id: str, req: CreativeRequest):
        clip = store.get_clip(clip_id)
        if not clip:
            raise HTTPException(404, "No existe el clip.")
        job, analysis, _ = _analysis(clip["job_id"])
        candidate = clip.get("candidate") or {}
        if candidate.get("id") not in {c["id"] for c in analysis["candidates"]}:
            raise HTTPException(400, "Este clip no tiene un momento guardado para reeditar.")
        label = f"Edición creativa {clip_id}"
        if any(r["label"] == label and r["status"] in ("queued", "running") for r in runs.items.values()):
            raise HTTPException(409, "Este clip ya tiene una edición en curso.")
        options = {**job["options"], "model": req.model, "layout": req.layout,
                   "languages": (clip.get("edl") or {}).get("languages") or job["options"].get("languages"),
                   "review": False, "selection": [candidate["id"]], "creative_from": clip_id}
        cfg = settings_for(options)
        if cfg.llm_provider not in ("offline", "replay") and cfg.ai_missing:
            raise HTTPException(400, cfg.ai_missing)
        new_job = store.create_job(job["source_id"], options)
        # Reuse the transcript and analysis; only the selected moment is edited again.
        store.record_step(job_id=new_job, clip_id=None, agent="analysis", key="analysis", status="done",
                          attempts=0, output=analysis, seconds=0)
        try:
            run_id = launch(label, cfg, lambda orch: orch.run(job_id=new_job), job["project_id"])
        except Exception:
            store.update_job(new_job, status="failed")
            raise
        return {"run_id": run_id, "job_id": new_job}

    @app.post("/api/jobs/{job_id}/resume")
    def resume_job(job_id: str):
        job = store.get_job(job_id)
        if not job:
            raise HTTPException(404, "No existe el trabajo.")
        if job["status"] == "running" and any(r["status"] in ("queued", "running") and r["job_id"] == job_id for r in runs.items.values()):
            raise HTTPException(409, "El trabajo ya está en ejecución.")
        return {"run_id": launch(f"Retomar {job_id}", settings_for(job["options"]), lambda orch: orch.run(job_id=job_id), job["project_id"])}

    @app.get("/api/runs")
    def list_runs(project_id: str | None = None):
        def belongs(r):
            job = store.get_job(r["job_id"]) if r["job_id"] else None
            return project_id is None or (job.get("project_id") if job else r.get("project_id")) == project_id
        return [runs.public(r) | {"log": list(r["log"])[-1:]} for r in reversed(list(runs.items.values()))
                if belongs(r)]

    @app.get("/api/runs/{run_id}")
    def get_run(run_id: str):
        r = runs.items.get(run_id)
        if not r:
            raise HTTPException(404, "No existe la ejecución.")
        return runs.public(r)

    @app.get("/api/jobs")
    def list_jobs(project_id: str | None = None):
        out = []
        for j in reversed(store.list_jobs(project_id)):
            clips = store.list_clips(j["id"])
            counts: dict[str, int] = {}
            for c in clips:
                counts[c["status"]] = counts.get(c["status"], 0) + 1
            out.append({**j, "clip_counts": counts})
        return jsonable_encoder(out)

    @app.get("/api/jobs/{job_id}")
    def get_job(job_id: str):
        job = store.get_job(job_id)
        if not job:
            raise HTTPException(404, "No existe el trabajo.")
        active = next((r for r in runs.items.values() if r["job_id"] == job_id), None)
        steps = [{k: v for k, v in st.items() if k != "output"} for st in store.list_steps(job_id)]
        return jsonable_encoder({"job": job, "clips": [public(c) for c in store.list_clips(job_id)], "steps": steps,
                                 "run": runs.public(active) if active else None})

    # ---------- clips ----------
    @app.get("/api/queue")
    def queue(project_id: str | None = None):
        return jsonable_encoder([public(c) for c in store.list_clips(status=output.QUEUE_STATES, project_id=project_id)])

    @app.get("/api/clips")
    def all_clips(project_id: str | None = None):
        return jsonable_encoder([public(c) for c in reversed(store.list_clips(project_id=project_id))])

    def _clip(clip_id: str) -> dict:
        c = store.get_clip(clip_id)
        if not c:
            raise HTTPException(404, "No existe el clip.")
        return c

    @app.get("/api/clips/{clip_id}")
    def get_clip(clip_id: str):
        c = _clip(clip_id)
        meta: dict = {}
        if c["package_dir"]:
            for lang in (c["edl"] or {}).get("languages", []):
                f = Path(c["package_dir"]) / lang / "metadata.json"
                if f.exists():
                    import json
                    meta[lang] = json.loads(f.read_text(encoding="utf-8"))
        return jsonable_encoder({**public(c), "metadata": meta})

    @app.get("/api/clips/{clip_id}/file/{lang}/{kind}")
    def clip_file(clip_id: str, lang: str, kind: str, download: bool = False):
        c = _clip(clip_id)
        names = {"video": ("clip.mp4", "video/mp4"), "srt": ("captions.srt", "text/plain; charset=utf-8")}
        if not LANG_RE.match(lang) or kind not in names or not c["package_dir"]:
            raise HTTPException(404, "Archivo no disponible.")
        path = Path(c["package_dir"]) / lang / names[kind][0]
        if not path.is_file():
            raise HTTPException(404, "Archivo no disponible.")
        return FileResponse(path, media_type=names[kind][1],
                            filename=f"{clip_id}_{lang}{path.suffix}" if download else None)

    @app.post("/api/clips/{clip_id}/approve")
    def approve(clip_id: str, d: Decision):
        try:
            return jsonable_encoder(output.decide(s, store, clip_id, approve=True, by=d.by, note=d.note))
        except ValueError as e:
            raise HTTPException(400, str(e))

    @app.post("/api/clips/{clip_id}/reject")
    def reject(clip_id: str, d: Decision):
        try:
            return jsonable_encoder(output.decide(s, store, clip_id, approve=False, by=d.by, note=d.note))
        except ValueError as e:
            raise HTTPException(400, str(e))

    # ---------- editor ----------
    def _edit(fn, *args):
        try:
            return fn(*args)
        except KeyError:
            raise HTTPException(404, "No existe el clip.")
        except (ValueError, TypeError) as e:  # EditError, and malformed values from the browser
            raise HTTPException(400, str(e))

    def _source(source_id: str) -> SourceRecord:
        rec = store.get_source(source_id)
        if not rec or not Path(rec["path"]).is_file():
            raise HTTPException(404, "No existe la fuente.")
        return SourceRecord(**rec)

    def _window(src: SourceRecord, a: float, b: float) -> tuple[float, float]:
        a, b = max(0.0, a), min(src.duration, b)
        if not (0 < b - a <= 2 * editor.PAD + editor.MAX_DUR + 5):
            raise HTTPException(400, "Tramo no válido.")
        return a, b

    @app.get("/api/clips/{clip_id}/editor")
    def editor_state(clip_id: str):
        return jsonable_encoder(_edit(editor.state, s, store, clip_id))

    @app.put("/api/clips/{clip_id}/draft")
    def editor_save(clip_id: str, payload: dict = Body(...)):
        return _edit(editor.save_draft, s, store, clip_id, payload)

    @app.delete("/api/clips/{clip_id}/draft")
    def editor_discard(clip_id: str):
        _edit(editor.discard_draft, store, clip_id)
        return {"ok": True}

    @app.post("/api/clips/{clip_id}/render")
    def editor_render(clip_id: str, payload: Optional[dict] = Body(None)):
        if payload:
            _edit(editor.save_draft, s, store, clip_id, payload)
        clip, _ = _edit(editor.load, store, clip_id)
        if not clip.get("draft"):
            raise HTTPException(400, "No hay cambios que renderizar.")
        label = f"Render manual {clip_id}"
        if any(r["label"] == label and r["status"] in ("queued", "running") for r in runs.items.values()):
            raise HTTPException(409, "Este clip ya se está renderizando.")
        return {"run_id": runs.start(label, lambda log: editor.render_draft(deepcopy(s), store, clip_id, log), clip["project_id"])}

    @app.get("/api/clips/{clip_id}/waveform")
    def editor_waveform(clip_id: str, a: float, b: float):
        _, src = _edit(editor.load, store, clip_id)
        return editor.waveform(src, *_window(src, a, b))

    @app.post("/api/clips/{clip_id}/proxy")
    def editor_proxy(clip_id: str, a: float, b: float):
        _, src = _edit(editor.load, store, clip_id)
        a, b = _window(src, a, b)
        path = editor.proxy(s, clip_id, src, a, b)
        return {"url": f"/api/proxy/{path.name}", "offset": a}

    @app.get("/api/proxy/{name}")
    def proxy_file(name: str):
        path = s.data_dir / "cache" / "proxy" / name
        if not re.fullmatch(r"[\w.-]+\.mp4", name) or not path.is_file():
            raise HTTPException(404, "Archivo no disponible.")
        return FileResponse(path, media_type="video/mp4")

    @app.get("/api/clips/{clip_id}/thumb")
    def clip_thumb(clip_id: str, lang: Optional[str] = None):
        path = editor.clip_thumb(s, _clip(clip_id), lang)
        if not path:
            raise HTTPException(404, "Sin miniatura.")
        return FileResponse(path, media_type="image/jpeg", headers={"Cache-Control": "max-age=86400"})

    @app.get("/api/sources/{source_id}/video")
    def source_video(source_id: str):
        return FileResponse(_source(source_id).path)  # answers Range requests, so the editor can seek freely

    @app.get("/api/sources/{source_id}/frame")
    def source_frame(source_id: str, t: float = 0.0, h: int = 72):
        src = _source(source_id)
        try:
            path = editor.frame(s, src, t, h)
        except Exception as e:
            raise HTTPException(500, f"No se pudo extraer el fotograma: {str(e)[:200]}")
        return FileResponse(path, media_type="image/jpeg", headers={"Cache-Control": "max-age=86400"})

    @app.get("/api/sfx/{sfx_id}")
    def sfx_file(sfx_id: str):
        entry = editor.audio_lib.load_library(s.sfx_dir).get(sfx_id)
        if not entry:
            raise HTTPException(404, "Sonido no disponible.")
        return FileResponse(Path(s.sfx_dir) / entry["file"])

    @app.get("/api/fonts/{which}")
    def font(which: str):
        """The fonts the renderer burns in, so the live preview measures and draws text the same way."""
        try:
            if which == "caption":
                path = cap.font_file(s.caption_font)
            elif which == "text":
                path = cap.font_file(s.text_font, True)
            else:
                path = editor.studio.fonts()[which]["path"]
        except Exception:
            raise HTTPException(404, "Fuente no disponible.")
        if not Path(path).is_file():
            raise HTTPException(404, "Fuente no disponible.")
        return FileResponse(path, headers={"Cache-Control": "max-age=86400"})

    @app.get("/api/studio/vignette.png")
    def vignette():
        path = s.studio_dir / "vignette.png"
        if not path.is_file():
            raise HTTPException(404, "Sin viñeta.")
        return FileResponse(path, media_type="image/png", headers={"Cache-Control": "max-age=86400"})

    return app


def main() -> None:
    import os

    import uvicorn
    uvicorn.run(create_app(), host=os.environ.get("CLIPPER_HOST", "127.0.0.1"), port=int(os.environ.get("CLIPPER_PORT", "8000")))


if __name__ == "__main__":
    main()
