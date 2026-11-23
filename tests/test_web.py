"""Panel web: las rutas usan el mismo Store y las mismas reglas que la CLI."""
import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from clipper.agents.orchestrator import Orchestrator
from clipper.web import app as webapp


@pytest.fixture
def client(tmp_path, monkeypatch, demo):
    monkeypatch.setenv("CLIPPER_DATA_DIR", str(tmp_path / "data"))
    monkeypatch.setenv("DATABASE_URL", f"sqlite:///{tmp_path / 'web.db'}")
    monkeypatch.setenv("CLIPPER_LANGUAGES", "en")
    monkeypatch.setenv("CLIPPER_CLIP_MIN_S", "10")
    monkeypatch.setenv("CLIPPER_CLIP_MAX_S", "22")
    monkeypatch.setenv("CLIPPER_MAX_CLIPS", "1")
    monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
    monkeypatch.setenv("CLIPPER_INPUT_DIR", str(tmp_path / "input"))
    real_run = Orchestrator.run
    # Whisper cannot be downloaded in tests: hand the orchestrator the prepared transcript.
    monkeypatch.setattr(Orchestrator, "run", lambda self, *a, **k: real_run(self, *a, transcript=str(demo[1]), **k))
    from session import authorize
    return authorize(TestClient(webapp.create_app(), base_url="http://127.0.0.1"))


def upload(client, demo, **over):
    data = {"rights": "own", "evidence": "video sintético de prueba", "language": "en"} | over
    with open(demo[0], "rb") as f:
        return client.post("/api/sources/upload", data=data, files={"file": ("demo.mp4", f, "video/mp4")})


def wait(client, run_id, timeout=240):
    t0 = time.time()
    while time.time() - t0 < timeout:
        r = client.get(f"/api/runs/{run_id}").json()
        if r["status"] in ("done", "failed"):
            return r
        time.sleep(1)
    raise AssertionError("el trabajo no terminó")


def test_page_and_config(client):
    assert "Kairos Studio" in client.get("/").text
    cfg = client.get("/api/config").json()
    assert cfg["api_key_set"] is False and "own" in cfg["rights"]


def test_upload_requires_rights_and_evidence(client, demo):
    assert upload(client, demo, rights="free_for_all").status_code == 400
    assert upload(client, demo, evidence="x").status_code == 400
    assert client.get("/api/sources").json() == []  # nothing registered, nothing left in uploads
    assert list((Path(webapp.Settings.from_env().data_dir) / "uploads").iterdir()) == []


def test_upload_registers_once(client, demo):
    a, b = upload(client, demo).json(), upload(client, demo).json()
    assert a["id"] == b["id"] and len(client.get("/api/sources").json()) == 1
    assert len(list((Path(webapp.Settings.from_env().data_dir) / "uploads").iterdir())) == 1  # duplicate upload removed


def from_url(client, **over):
    data = {"url": "https://www.twitch.tv/videos/123", "rights": "own", "evidence": "canal propio, VOD 123"} | over
    return client.post("/api/sources/from-url", data=data)


def test_from_url_validates_before_downloading(client, monkeypatch):
    monkeypatch.setattr(webapp.download, "fetch", lambda *a, **k: pytest.fail("no debe descargar"))
    assert from_url(client, url="https://example.com/video.mp4").status_code == 400
    assert from_url(client, url="file:///etc/passwd").status_code == 400
    assert from_url(client, url="https://evil.com/?u=youtube.com").status_code == 400
    assert from_url(client, rights="free_for_all").status_code == 400
    assert from_url(client, evidence="x").status_code == 400


def test_from_url_downloads_and_registers(client, demo, monkeypatch):
    def fake_fetch(url, dest, log):
        dest.mkdir(parents=True, exist_ok=True)
        p = dest / "twitch_123.mp4"
        p.write_bytes(Path(demo[0]).read_bytes())
        return p, {"uploader": "@creador"}
    monkeypatch.setattr(webapp.download, "fetch", fake_fetch)
    r = from_url(client)
    assert r.status_code == 200
    assert wait(client, r.json()["run_id"])["status"] == "done"
    src = client.get("/api/sources").json()
    assert len(src) == 1 and src[0]["credit"] == "@creador" and src[0]["rights_status"] == "own"


def test_job_needs_key_unless_offline(client, demo):
    sid = upload(client, demo).json()["id"]
    r = client.post("/api/jobs", json={"source_id": sid})
    assert r.status_code == 400 and "API key" in r.json()["detail"] and "Ajustes" in r.json()["detail"]
    assert client.post("/api/jobs", json={"source_id": "src_nope", "offline": True}).status_code == 404


def test_offline_job_then_queue_and_decision(client, demo):
    sid = upload(client, demo).json()["id"]
    run = wait(client, client.post("/api/jobs", json={"source_id": sid, "offline": True, "review": False}).json()["run_id"])
    assert run["status"] == "done", run["log"][-5:]
    job = client.get(f"/api/jobs/{run['job_id']}").json()
    assert job["job"]["status"] == "done" and job["clips"]
    # Offline output is never auto-approved: it lands in the queue marked for review.
    queue = client.get("/api/queue").json()
    assert queue and queue[0]["status"] == "needs_human"
    cid = queue[0]["id"]
    clip = client.get(f"/api/clips/{cid}").json()
    assert "en" in clip["metadata"]
    video = client.get(f"/api/clips/{cid}/file/en/video")
    assert video.status_code == 200 and video.headers["content-type"] == "video/mp4"
    assert client.get(f"/api/clips/{cid}/file/..%2f/video").status_code == 404
    assert client.get(f"/api/clips/{cid}/file/en/passwd").status_code == 404

    assert client.post(f"/api/clips/{cid}/approve", json={"by": ""}).status_code == 400
    ok = client.post(f"/api/clips/{cid}/approve", json={"by": "reviewer", "note": "bien"})
    assert ok.status_code == 200 and ok.json()["status"] == "approved" and ok.json()["schedule"]
    assert client.get("/api/queue").json() == []
    assert client.post(f"/api/clips/{cid}/reject", json={"by": "reviewer"}).status_code == 400  # already decided
    project = client.post("/api/projects", json={"name": "Proyecto terminado"}).json()
    assert client.put(f"/api/sources/{sid}/project", json={"project_id": project["id"]}).status_code == 200
    assert client.get("/api/clips?project_id=project_default").json() == []
    assert client.get(f"/api/clips?project_id={project['id']}").json()[0]["id"] == cid
    assert client.get("/api/runs?project_id=project_default").json() == []
    assert client.get(f"/api/runs?project_id={project['id']}").json()[0]["id"] == run["id"]


def test_creative_version_preserves_original_and_reuses_analysis(client, demo):
    sid = upload(client, demo).json()["id"]
    run = wait(client, client.post("/api/jobs", json={"source_id": sid, "offline": True, "review": False}).json()["run_id"])
    original = client.get(f"/api/jobs/{run['job_id']}").json()["clips"][0]
    cid = original["id"]
    before = client.get(f"/api/clips/{cid}").json()
    assert client.post(f"/api/clips/{cid}/creative-version", json={"model": "invalid"}).status_code == 400
    assert client.post("/api/clips/missing/creative-version", json={}).status_code == 404
    response = client.post(f"/api/clips/{cid}/creative-version", json={"model": "standard", "layout": "center_crop"})
    assert response.status_code == 200, response.text
    result = response.json()
    assert wait(client, result["run_id"])["status"] == "done"
    job = client.get(f"/api/jobs/{result['job_id']}").json()
    assert len(job["clips"]) == 1 and job["clips"][0]["id"] != cid
    assert job["job"]["project_id"] == original["project_id"]
    assert job["clips"][0]["edl"]["layout"] == "center_crop"
    analysis = next(step for step in job["steps"] if step["agent"] == "analysis")
    assert analysis["attempts"] == 0 and analysis["seconds"] == 0
    assert client.get(f"/api/clips/{cid}").json() == before


def test_editor_draft_render_and_media(client, demo):
    sid = upload(client, demo).json()["id"]
    run = wait(client, client.post("/api/jobs", json={"source_id": sid, "offline": True, "review": False}).json()["run_id"])
    assert run["status"] == "done", run["log"][-5:]
    cid = client.get("/api/queue").json()[0]["id"]

    st = client.get(f"/api/clips/{cid}/editor").json()
    edl, tracks = st["edl"], st["tracks"]
    assert not st["has_draft"] and st["words"] and st["library"] and "en" in st["renders"]
    assert st["window"]["a"] <= edl["source"]["start"] < edl["source"]["end"] <= st["window"]["b"]
    assert set(st["geometry"]) == {"gameplay_blur_bg", "center_crop"} and st["styles"]["bold"]["active"].startswith("#")

    # Media the editor needs: seekable source, frames, waveform, sounds.
    part = client.get(f"/api/sources/{sid}/video", headers={"Range": "bytes=0-99"})
    assert part.status_code == 206 and len(part.content) == 100
    frame = client.get(f"/api/sources/{sid}/frame?t=3&h=72")
    assert frame.status_code == 200 and frame.content[:2] == b"\xff\xd8"
    assert client.get("/api/sources/src_nope/frame").status_code == 404
    wave = client.get(f"/api/clips/{cid}/waveform?a={st['window']['a']}&b={st['window']['b']}").json()
    assert wave["peaks"] and max(wave["peaks"]) <= 1
    assert client.get(f"/api/sfx/{st['library'][0]['id']}").status_code == 200
    assert client.get(f"/api/clips/{cid}/thumb").content[:2] == b"\xff\xd8"

    # A draft is validated and does not touch the packaged clip.
    edl["source"]["start"] += 2.0
    edl["hook"]["text"]["en"] = "Edited by hand"
    edl["hook"].update(x=0.5, y=0.5)
    edl["captions"]["x"] = 0.5
    edl["layout"], edl["crop_x"] = "center_crop", 0.3
    edl["source"]["id"], edl["languages"] = "src_other", ["en", "xx"]       # not editable: ignored
    edl["audio"]["sfx"].append({"t": 1.0, "id": "not_in_library"})          # dropped
    edl["commentary"]["notes"] = [{"t": 3.0, "duration": 2.5, "text": {"en": "My own note about this moment of the video"}}]
    cues = [{"start": 0.5, "end": 1.5, "words": [{"w": "hello", "start": 0.5, "end": 1.0}, {"w": "there", "start": 1.0, "end": 1.5, "hl": True}]},
            {"start": 1.2, "end": 2.4, "words": [{"w": "overlap", "start": 1.2, "end": 2.4}]}]
    payload = {"edl": edl, "tracks": {"en": {"cues": cues}}}
    too_short = {"edl": {**edl, "source": {**edl["source"], "end": edl["source"]["start"] + 1}}, "tracks": {}}
    assert client.put(f"/api/clips/{cid}/draft", json=too_short).status_code == 400
    assert client.put(f"/api/clips/{cid}/draft", json=payload).status_code == 200
    st2 = client.get(f"/api/clips/{cid}/editor").json()
    assert st2["edl"]["hook"]["x"] == 0.5 and st2["edl"]["hook"]["y"] == 0.5
    assert st2["edl"]["captions"]["x"] == 0.5
    from clipper.media.captions import build_ass
    from clipper.schemas import EDL
    ass = build_ass(EDL.model_validate(st2["edl"]), None, "en", 1280, 720, webapp.Settings.from_env())
    assert "\\pos(540,960)" in ass
    assert st2["has_draft"] and st2["edl"]["hook"]["text"]["en"] == "Edited by hand"
    assert st2["edl"]["source"]["id"] == sid and st2["edl"]["languages"] == ["en"]
    assert all(x["id"] != "not_in_library" for x in st2["edl"]["audio"]["sfx"])
    saved = st2["tracks"]["en"]["cues"]
    assert len(saved) == 2 and saved[1]["start"] >= saved[0]["end"]         # the overlap was closed
    before = client.get(f"/api/clips/{cid}").json()
    assert before["has_draft"] and before["edl"]["hook"]["text"]["en"] != "Edited by hand"

    # Discard, save again, render: the draft becomes the clip and goes back to the queue.
    assert client.delete(f"/api/clips/{cid}/draft").status_code == 200
    assert not client.get(f"/api/clips/{cid}/editor").json()["has_draft"]
    assert client.post(f"/api/clips/{cid}/render").status_code == 400      # nothing to render

    # Studio tools: every effect, a colour grade, text layers, transitions, progress bar, audio levels.
    cat = st["catalog"]
    assert {"anton", "caption"} <= set(st["fonts"]) and len(cat["looks"]) > 5 and client.get("/api/fonts/anton").status_code == 200
    assert client.get("/api/studio/vignette.png").content[:4] == b"\x89PNG"
    edl["effects"] = [{"t": 0.5 + i * 0.7, "type": e["id"], "duration": 0.5, "intensity": 1.2} for i, e in enumerate(cat["effects"])]
    edl["effects"].append({"t": 1.0, "type": "sparkles", "duration": 1})                       # unknown: dropped
    edl["color"] = {"look": "cinema", "look_amount": 0.8, "saturation": 0.2, "temperature": -0.1, "brightness": 9}
    edl["texts"] = [
        {"t": 0.4, "duration": 2.0, "text": {"en": "Big title\nsecond line"}, "font": "anton", "size": 110, "upper": True,
         "anim_in": "pop", "anim_out": "shrink", "rotate": -5, "y": 0.3},
        {"t": 1.0, "duration": 2.0, "text": {"en": "boxed label"}, "font": "nope", "box": "#FFE500", "color": "#000000",
         "anim_in": "slide_up", "y": 0.45, "glow": 3},
        {"t": 2.0, "duration": 2.0, "text": {"en": "   "}},                                    # empty: dropped
    ]
    edl["transitions"] = {"intro": "flash", "intro_d": 0.3, "outro": "fade", "outro_d": 0.5}
    edl["progress"] = {"on": True, "color": "#22D3EE", "position": "bottom", "height": 10}
    edl["captions"] = {"style": "neon", "y": 0.62, "scale": 1.2}
    edl["audio"].update(voice_gain=0.8, voice_enhance=True, music_id="music_chill_01", music_gain=1.5,
                        sfx=[{"t": 0.5, "id": "glitch_01", "gain": 0.5}])
    bad = {"edl": {**edl, "texts": [{**edl["texts"][0], "color": "red"}]}, "tracks": payload["tracks"]}
    assert client.put(f"/api/clips/{cid}/draft", json=bad).status_code == 400
    run = wait(client, client.post(f"/api/clips/{cid}/render", json=payload).json()["run_id"])
    assert run["status"] == "done", run["log"][-5:]
    clip = client.get(f"/api/clips/{cid}").json()
    assert clip["status"] == "needs_human" and not clip["has_draft"]
    assert clip["edl"]["hook"]["text"]["en"] == "Edited by hand" and clip["edl"]["layout"] == "center_crop"
    assert abs((clip["edl"]["source"]["end"] - clip["edl"]["source"]["start"]) - (edl["source"]["end"] - edl["source"]["start"])) < 0.01
    assert any(c["name"] == "manual_edit" for c in clip["review"]["checks"])
    assert client.get(f"/api/clips/{cid}/file/en/video").status_code == 200
    assert len(client.get(f"/api/clips/{cid}/editor").json()["tracks"]["en"]["cues"]) == 2
    out = clip["edl"]
    assert [e["type"] for e in out["effects"]] == [e["id"] for e in cat["effects"]] and out["effects"][0]["intensity"] == 1.2
    assert out["color"]["look"] == "cinema" and out["color"]["brightness"] == 1.0
    assert len(out["texts"]) == 2 and out["texts"][1]["font"] == "anton" and out["texts"][0]["text"]["en"] == "Big title\nsecond line"
    assert out["transitions"]["outro"] == "fade" and out["progress"]["on"] and out["captions"]["style"] == "neon"
    assert out["audio"]["music_id"] == "music_chill_01" and out["audio"]["sfx"][0]["gain"] == 0.5


def store_options(client, jid):
    return client.get(f"/api/jobs/{jid}").json()["job"]["options"]


def test_review_flow_select_style_and_model(client, demo, monkeypatch):
    monkeypatch.setenv("ANTHROPIC_API_KEY", "sk-test")
    sid = upload(client, demo).json()["id"]
    cfg = client.get("/api/config").json()
    assert [t["id"] for t in cfg["tiers"]] == ["fast", "standard", "superior"]

    # 1. Analysis only: nothing is generated.
    r = client.post("/api/jobs", json={"source_id": sid, "offline": True})   # analysis defaults to Haiku
    run = wait(client, r.json()["run_id"])
    assert run["status"] == "done" and "Analizar" in run["label"], run["log"][-5:]
    jid = run["job_id"]
    job = client.get(f"/api/jobs/{jid}").json()
    assert job["job"]["status"] == "awaiting_selection" and job["clips"] == []

    # 2. The suggested moments, with what is said in each.
    d = client.get(f"/api/jobs/{jid}/candidates").json()
    cands = d["candidates"]
    assert d["spoken"]["detected"] == "en" and d["spoken"]["declared"] == "en"
    assert store_options(client, jid)["analysis_model"] == "fast"
    assert cands and all(c["clip_id"] is None and c["end"] > c["start"] and "text" in c for c in cands)

    # 3. Bad requests are refused before anything runs.
    gen = lambda **kw: client.post(f"/api/jobs/{jid}/generate", json={"candidates": [cands[0]["id"]], "model": "fast", **kw})
    assert gen(model="gpt-4").status_code == 400 and gen(layout="wide").status_code == 400
    assert client.post(f"/api/jobs/{jid}/generate", json={"candidates": ["nope"], "model": "fast"}).status_code == 400
    assert client.post("/api/jobs/job_nope/generate", json={"candidates": ["x"], "model": "fast"}).status_code == 404
    assert client.get(f"/api/jobs/{jid}").json()["job"]["status"] == "awaiting_selection"   # untouched

    # 4. Generate one moment with chosen styles and model (offline here: no key spent in tests).
    ok = gen(layout="center_crop", caption_style="bounce", languages=["en"])
    assert ok.status_code == 200
    run = wait(client, ok.json()["run_id"])
    assert run["status"] == "done", run["log"][-5:]
    job = client.get(f"/api/jobs/{jid}").json()
    assert job["job"]["status"] == "done" and len(job["clips"]) == 1
    assert job["job"]["options"]["model"] == "fast" and job["job"]["options"]["selection"] == [cands[0]["id"]]
    edl = client.get(f"/api/clips/{job['clips'][0]['id']}").json()["edl"]
    assert edl["layout"] == "center_crop" and edl["captions"]["style"] == "bounce"
    d2 = client.get(f"/api/jobs/{jid}/candidates").json()["candidates"]
    assert d2[0]["clip_id"] and all(c["clip_id"] is None for c in d2[1:])      # only the picked one exists


def test_editor_refuses_unknown_and_unfinished_clips(client):
    assert client.get("/api/clips/nope/editor").status_code == 404
    assert client.put("/api/clips/nope/draft", json={}).status_code == 404
    store = webapp.Store(webapp.Settings.from_env().database_url)
    jid = store.create_job("src_x", {})
    cid = store.upsert_clip(jid, 0, {"start": 0, "end": 10})
    assert client.get(f"/api/clips/{cid}/editor").status_code == 400         # still a candidate, no EDL yet


def test_interrupted_jobs_are_marked_on_start(client, tmp_path):
    store = webapp.Store(webapp.Settings.from_env().database_url)
    jid = store.create_job("src_x", {})
    webapp.create_app()
    assert store.get_job(jid)["status"] == "interrupted"


def test_api_key_budget_and_usage(client, tmp_path, monkeypatch):
    from clipper import ai_settings as ai

    assert client.get("/api/settings/ai").json()["key_set"] is False
    assert client.put("/api/settings/ai/key", json={"key": "hola"}).status_code == 400          # not a key
    key = "sk-ant-api03-" + "x" * 40

    def rejected(k, model, ledger=None):
        raise ai.KeyError_("Anthropic rechazó esta API key.")
    monkeypatch.setattr(ai, "verify_key", rejected)
    r = client.put("/api/settings/ai/key", json={"key": key})
    assert r.status_code == 400 and "rechazó" in r.json()["detail"] and not client.get("/api/settings/ai").json()["key_set"]

    limits = {"checked_at": "2026-11-14T00:00:00Z", "requests": {"limit": 50, "remaining": 49, "reset": None}}
    monkeypatch.setattr(ai, "verify_key", lambda k, model, ledger=None: {"ok": True, "warning": None, "limits": limits})
    st = client.put("/api/settings/ai/key", json={"key": key}).json()
    assert st["key_set"] and st["source"] == "panel" and key not in str(st) and st["key_hint"].endswith("xxxx")
    assert client.get("/api/config").json()["api_key_set"] is True
    assert ai.stored_key(tmp_path / "data") == key                                              # kept on disk for the next start

    # Spending: tokens per call become dollars through the tier prices; the budget counts from when it is set.
    assert client.put("/api/settings/ai/budget", json={"usd": -5}).status_code == 400
    assert client.put("/api/settings/ai/budget", json={"usd": 10}).json()["budget"]["usd"] == 10
    ledger = ai.Ledger(tmp_path / "data")
    ledger.add({"agent": "analysis", "model": "claude-haiku-5-5", "input": 1_000_000, "output": 1_000_000})   # 0.10 + 0.50
    ledger.add({"agent": "editing", "model": "claude-opus-5-5", "input": 500_000, "output": 100_000})        # 2.00 + 2.00
    u = client.get("/api/usage?days=7").json()
    assert u["totals"]["calls"] == 2 and abs(u["totals"]["cost"] - 4.6) < 1e-9 and len(u["by_day"]) == 7
    assert [m["model"] for m in u["by_model"]] == ["claude-opus-5-5", "claude-haiku-5-5"]
    assert abs(u["budget"]["spent"] - 4.6) < 1e-9 and abs(u["budget"]["remaining"] - 5.4) < 1e-9
    assert u["by_day"][-1]["calls"] == 2 and u["limits"]["requests"]["remaining"] == 49

    assert client.delete("/api/settings/ai/key").json()["key_set"] is False
    assert ai.stored_key(tmp_path / "data") is None
