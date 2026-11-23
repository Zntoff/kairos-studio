import datetime as dt
from types import SimpleNamespace

import pytest
from pydantic import BaseModel

from clipper.agents import output
from clipper.agents.analysis import AnalysisAgent, AnalysisInput
from clipper.agents.base import AgentFailed, ContractError, Subagent
from clipper.agents.metadata import find_terms
from clipper.llm import AnthropicLLM, LLMError, PendingAnswer, ReplayLLM, ScriptedLLM, make_llm
from clipper.media import captions as cap
from clipper.media import transcribe as tr
from clipper.rights import RightsError, register_source
from clipper.schemas import AnalysisProposal, Signals, Word


def words(text, start=0.0, step=0.4):
    return [Word(w=w, start=round(start + i * step, 2), end=round(start + i * step + 0.3, 2)) for i, w in enumerate(text.split())]


# ---- transcript ----
def test_resegment_breaks_on_sentences_and_pauses():
    ws = words("Hello there. This is a test") + words("after a long pause", start=10.0)
    segs = tr.resegment(ws)
    assert [s.text for s in segs] == ["Hello there.", "This is a test", "after a long pause"]
    assert segs[1].end < segs[2].start


def test_silent_transcript_gives_cut_points():
    t = tr.silent_transcript(23.0, "und")
    assert t.segments[-1].end == 23.0 and all(not w.w for w in t.words())


def test_faster_whisper_adapter(monkeypatch, settings):
    import faster_whisper

    class FakeModel:
        def __init__(self, *a, **k):
            pass

        def transcribe(self, path, **kw):
            assert kw["word_timestamps"] is True
            seg = SimpleNamespace(words=[SimpleNamespace(word=" Hola", start=0.5, end=0.8), SimpleNamespace(word=" mundo.", start=0.9, end=1.3)])
            return iter([seg]), SimpleNamespace(language="es", duration=5.0)

    monkeypatch.setattr(faster_whisper, "WhisperModel", FakeModel)
    t = tr.transcribe("x.wav", settings)
    assert t.language == "es" and [w.w for w in t.words()] == ["Hola", "mundo."]


# ---- captions ----
def test_chunk_keeps_groups_short():
    toks = "Three, two, one, go! That was the craziest thing ever".split()
    groups = cap.chunk(toks)
    assert all(len(g) <= 3 for g in groups)
    assert [toks[i] for i in groups[0]] == ["Three,", "two,"]


def test_timed_cues_keep_real_word_times_and_highlights():
    phrase = words("the car flew over the bridge")
    cues = cap.close_gaps(cap.timed_cues(phrase, None, {"bridge"}), clip_duration=10.0)
    flat = [w for c in cues for w in c.words]
    assert [(w.start, w.end) for w in flat] == [(w.start, w.end) for w in phrase]
    assert [w.w for w in flat if w.hl] == ["bridge"]
    assert all(a.end <= b.start for a, b in zip(cues, cues[1:]))


def test_corrected_text_only_used_when_word_count_matches():
    phrase = words("the kar flew")
    assert cap.timed_cues(phrase, "The car flew".split(), set())[0].text == "The car flew"
    assert cap.timed_cues(phrase, "The car just flew".split(), set())[0].text == "the kar flew"


def test_caption_groups_do_not_highlight_a_word_through_a_pause():
    phrase = [Word(w="Hola", start=.4, end=.7), Word(w="mundo", start=1.2, end=1.6)]
    cues = cap.close_gaps(cap.timed_cues(phrase, None, set()), 2)
    assert len(cues) == 2
    assert cues[0].end <= 1.0
    assert cues[1].start == 1.2


def test_clip_alignment_preserves_source_origin_and_pauses(settings, tmp_path, monkeypatch):
    from clipper.media import transcribe as tr
    from clipper.schemas import Transcript, Segment
    commands = []
    monkeypatch.setattr(tr.ffmpeg, "run", lambda args: commands.append(args))
    monkeypatch.setattr(tr, "transcribe", lambda *args: Transcript(language="es", duration=5, segments=[
        Segment(id=0, start=.6, end=2.8, text="Hola mundo", words=[
            Word(w="Hola", start=.6, end=.9), Word(w="mundo", start=2.4, end=2.8)])]))
    transcript = tr.transcribe_clip("video.mp4", tmp_path / "clip.wav", 120, 124, settings, "es")
    assert commands[0][1] == "119.600"
    relative = cap.clip_words(transcript, 120, 124)
    assert [(w.start, w.end) for w in relative] == [(.2, .5), (2, 2.4)]


def test_spread_cues_cover_the_phrase_span():
    cues = cap.spread_cues("el auto voló sobre todo el puente", 2.0, 5.0, set())
    assert cues[0].start == 2.0 and abs(cues[-1].words[-1].end - 5.0) < 0.01


def test_wrap_respects_width():
    lines = cap.wrap("The ramp angle matters more than speed here: watch where he lifts off.", "Inter", 46, 800, True)
    assert all(cap.text_width(l, "Inter", 46, True) <= 800 for l in lines) and len(lines[-1].split()) > 1


# ---- rights ----
def test_rights_registry_refuses_without_status_or_evidence(store, demo):
    with pytest.raises(RightsError):
        register_source(store, demo[0], rights_status="downloaded", evidence="found it online")
    with pytest.raises(RightsError):
        register_source(store, demo[0], rights_status="written_permission", evidence=" ")
    rec = register_source(store, demo[0], rights_status="written_permission", evidence="email del creador, 2026-11-10", credit="@creator")
    assert register_source(store, demo[0], rights_status="own", evidence="otra vez el mismo archivo").id == rec.id


# ---- spoiler terms ----
def test_find_terms_matches_whole_words_only():
    assert find_terms("The final mission is wild", ["final mission", "leak"]) == ["final mission"]
    assert find_terms("bleak weather", ["leak"]) == []


# ---- analysis contract ----
def test_analysis_contract_rejects_bad_ranges(make_ctx, source, demo, settings):
    transcript = tr.load_transcript(demo[1])
    agent = AnalysisAgent(make_ctx())
    inp = AnalysisInput(source=source, transcript=transcript, signals=Signals(hop_s=0.5, energy=[], scene_cuts=[]), target_count=2)
    mk = lambda *spans: AnalysisProposal.model_validate({"candidates": [
        {"start_segment": a, "end_segment": b, "score": 50, "category": "x", "reason": "y"} for a, b in spans]})
    with pytest.raises(ContractError, match="must last"):
        agent.check(inp, mk((0, 0)))
    with pytest.raises(ContractError, match="invalid"):
        agent.check(inp, mk((0, 999)))
    good = agent.offline(inp)["candidates"][0]
    span = (good["start_segment"], good["end_segment"])
    agent.check(inp, mk(span))
    with pytest.raises(ContractError, match="overlap"):
        agent.check(inp, mk(span, span))


# ---- retry loop ----
class Answer(BaseModel):
    n: int


class Probe(Subagent):
    name, contract = "analysis", Answer  # reuses an existing prompt file

    def build_user(self, inp):
        return "give n"

    def check(self, inp, out):
        if out.n < 3:
            raise ContractError("n must be at least 3")


def test_retry_feeds_the_error_back_and_succeeds(make_ctx):
    llm = ScriptedLLM({"analysis": [{"n": "x"}, {"n": 1}, {"n": 5}]})
    out, meta = Probe(make_ctx(llm)).decide(None)
    assert out.n == 5 and meta.attempts == 3 and meta.prompt_version.startswith("analysis@v2#")
    assert "n must be at least 3" in llm.calls[2]["user"]


def test_escalates_after_two_retries(make_ctx):
    llm = ScriptedLLM({"analysis": [{"n": 0}]})
    with pytest.raises(AgentFailed) as e:
        Probe(make_ctx(llm)).decide(None)
    assert e.value.attempts == 3 and len(llm.calls) == 3


# ---- Anthropic client: request shape and answer parsing, against a fake SDK client ----
def test_anthropic_request_shape_and_parsing(tmp_path):
    seen = {}

    class FakeMessages:
        def create(self, **kw):
            seen.update(kw)
            block = SimpleNamespace(type="tool_use", name="submit_probe", input={"n": 7})
            return SimpleNamespace(content=[block], stop_reason="tool_use", model="claude-test",
                                   usage=SimpleNamespace(input_tokens=11, output_tokens=5))

    img = tmp_path / "f.jpg"
    img.write_bytes(b"\xff\xd8\xff")
    res = AnthropicLLM(None, client=SimpleNamespace(messages=FakeMessages())).complete(
        agent="probe", model="m", system="sys", user="hi", contract=Answer, images=[img])
    assert res.data == {"n": 7} and res.usage == {"input_tokens": 11, "output_tokens": 5}
    assert seen["tool_choice"] == {"type": "auto"}  # forced tool_choice is a 400 on Opus/Sonnet 5.5
    assert "submit_probe" in seen["system"]
    assert seen["tools"][0]["input_schema"]["properties"]["n"]["type"] == "integer"
    assert [b["type"] for b in seen["messages"][0]["content"]] == ["image", "text"]


def test_missing_api_key_is_a_clear_error():
    with pytest.raises(LLMError, match="ANTHROPIC_API_KEY"):
        make_llm("anthropic", None)


# ---- scheduling ----
def test_next_slots_one_clip_per_slot_per_language(settings, store):
    after = dt.datetime(2026, 11, 20, 9, 0)
    first = output.next_slots(settings, store, ["en", "es"], after)
    assert first == {"en": "2026-11-20T12:00:00", "es": "2026-11-20T12:00:00"}
    job = store.create_job("src", {})
    clip = store.upsert_clip(job, 0, {})
    store.update_clip(clip, schedule=first)
    assert output.next_slots(settings, store, ["en"], after) == {"en": "2026-11-20T19:00:00"}


# ---- replay mode ----
def test_replay_writes_the_prompt_then_reads_the_answer(tmp_path):
    llm = ReplayLLM(tmp_path)
    call = dict(agent="probe", model="m", system="SYS", user="what is n?", contract=Answer)
    with pytest.raises(PendingAnswer):
        llm.complete(**call)
    prompt = next(tmp_path.glob("probe_*.prompt.md"))
    assert "SYS" in prompt.read_text() and "what is n?" in prompt.read_text()
    prompt.with_name(prompt.name.replace(".prompt.md", ".answer.json")).write_text('{"n": 4}')
    assert llm.complete(**call).data == {"n": 4}


def test_color_grade_is_one_affine_transform():
    from clipper.media import studio

    assert studio.color_affine(studio.color_params({})) is None and studio.color_filter({"look": "none"}) is None
    bw = studio.color_affine(studio.color_params({"saturation": -1}))
    assert bw[0][:3] == bw[1][:3] == bw[2][:3] and abs(sum(bw[0][:3]) - 1) < 1e-3          # grey: every channel is the luma
    p = studio.color_params({"look": "cinema", "look_amount": 0.5, "vignette": 0.9})
    assert abs(p["contrast"] - 0.1) < 1e-6 and p["vignette"] == 1.0                           # look scaled, then clamped
    brighter = studio.color_affine(studio.color_params({"brightness": 0.4}))
    assert brighter[0][3] == 0.1 and brighter[0][0] == 1.0
    f = studio.color_filter({"look": "vivid"})
    assert f.startswith("format=rgba,colorchannelmixer=rr=") and ":ba=" in f and studio.ass_color("#FFE500") == "&H0000E5FF"


def test_motion_and_text_events(settings):
    from clipper.media import captions as cap
    from clipper.media import render
    from clipper.schemas import EDL

    edl = EDL.model_validate({
        "clip_id": "c", "source": {"id": "s", "start": 10, "end": 20}, "source_language": "en", "languages": ["en"],
        "layout": "center_crop", "hook": {"text": {"en": ""}, "duration": 0}, "commentary": {"mode": "onscreen", "notes": []},
        "captions": {"style": "bold"}, "audio": {"normalize_lufs": -14}, "risk": {"spoiler": False, "rights_ok": True},
        "effects": [{"t": 1, "type": "shake", "duration": 0.5}, {"t": 2, "type": "flash", "duration": 0.3},
                    {"t": 3, "type": "glitch", "duration": 0.4}],
        "texts": [{"t": 1, "duration": 2, "text": {"en": "hello\nworld"}, "anim_in": "slide_left", "box": "#FFE500"}],
        "progress": {"on": True}})
    z, dx, dy = render.motion(edl)
    assert "between(it,1.000,1.500)" in z and "sin(" in dx and "cos(" in dy
    assert render.timed_filters(edl) == ["rgbashift=rh=-14:bh=14:enable='between(t,3.000,3.400)'"]
    ass = cap.build_ass(edl, None, "en", 1920, 1080, settings)
    text = next(l for l in ass.splitlines() if ",TxtB," in l)
    assert r"\move(680,576,540,576,0,300)" in text and text.endswith(r"hello\Nworld") and r"\3c&H00E5FF&" in text
    assert sum(",Shape," in l for l in ass.splitlines()) == 2                                # flash + progress bar
