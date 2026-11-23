"""Editing subagent: one candidate -> one Edit Decision List (EDL)."""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path

from ..media import audio as audio_lib
from ..media import ffmpeg
from ..schemas import (EDL, Candidate, EditDecision, EdlAudio, EdlCommentary, EdlEffect, EdlHook, EdlNote,
                       EdlRisk, EdlSfx, EdlSource, SourceRecord, Transcript, Word)
from .base import ContractError, StepMeta, Subagent, dumps

HOOK_MAX_CHARS = 60
NOTE_MAX_CHARS = 120
MIN_COMMENTARY_WORDS = 8
LEAD_IN, TAIL = 0.15, 0.30


@dataclass
class EditingInput:
    clip_id: str
    source: SourceRecord
    candidate: Candidate
    words: list[Word]            # candidate words plus one segment of context on each side
    core: tuple[int, int]        # index range of the candidate inside `words`
    source_language: str
    languages: list[str]
    recent_hooks: list[str]
    library: dict[str, dict]
    feedback: str = ""
    previous: dict | None = None
    revision: int = 0
    frames: list[Path] = field(default_factory=list)


def clip_bounds(inp: EditingInput, start_word: int, end_word: int) -> tuple[float, float]:
    w = inp.words
    start = w[start_word].start - LEAD_IN
    floor = w[start_word - 1].end + 0.02 if start_word > 0 else 0.0
    start = max(0.0, floor, start) if floor <= w[start_word].start else max(0.0, start)
    end = w[end_word].end + TAIL
    if end_word + 1 < len(w):
        end = min(end, max(w[end_word].end, w[end_word + 1].start - 0.02))
    end = min(end, inp.source.duration)
    return round(start, 3), round(end, 3)


class EditingAgent(Subagent):
    name = "editing"
    tier = "standard"
    contract = EditDecision
    max_tokens = 4000

    def prepare(self, clip_id: str, source: SourceRecord, candidate: Candidate, transcript: Transcript,
                languages: list[str], recent_hooks: list[str], job_dir: Path, feedback: str = "",
                previous: dict | None = None, revision: int = 0) -> EditingInput:
        segs = transcript.segments
        a, b = max(0, candidate.start_segment - 1), min(len(segs) - 1, candidate.end_segment + 1)
        words, core_start, core_end = [], 0, 0
        for s in segs[a:b + 1]:
            if s.id == candidate.start_segment:
                core_start = len(words)
            words.extend(s.words)
            if s.id == candidate.end_segment:
                core_end = len(words) - 1
        frames: list[Path] = []
        if not self.ctx.llm.offline:
            frames = sample_source_frames(source.path, candidate.start, candidate.end, job_dir / "frames" / clip_id)
        return EditingInput(clip_id=clip_id, source=source, candidate=candidate, words=words, core=(core_start, core_end),
                            source_language=transcript.language, languages=languages, recent_hooks=recent_hooks,
                            library=audio_lib.load_library(self.s.sfx_dir), feedback=feedback, previous=previous,
                            revision=revision, frames=frames)

    def run(self, inp: EditingInput) -> tuple[EDL, StepMeta]:
        decision, meta = self.decide(inp)
        return self.to_edl(inp, decision), meta

    # ---- prompt input ----
    def images(self, inp: EditingInput) -> list[Path]:
        return inp.frames

    def build_user(self, inp: EditingInput) -> str:
        c = inp.candidate
        word_lines, line = [], []
        for i, w in enumerate(inp.words):
            line.append(f"{i}:{w.w or '·'}@{w.start:.1f}")
            if len(line) == 10:
                word_lines.append("  ".join(line))
                line = []
        if line:
            word_lines.append("  ".join(line))
        sfx = [f"- {e['id']} ({e['kind']}, {e['duration']:.1f}s): {e['description']}" for e in inp.library.values()]
        parts = [
            "# Source",
            f"{inp.source.width}x{inp.source.height}, spoken language: {inp.source_language}. "
            f"{len(inp.frames)} frames from this moment are attached, in order." if inp.frames else
            f"{inp.source.width}x{inp.source.height}, spoken language: {inp.source_language}.",
            "\n# Candidate picked by the Analysis subagent",
            f"Category: {c.category}. Why: {c.reason} Hook idea: {c.hook_idea or '(none)'}",
            f"It covers words {inp.core[0]}..{inp.core[1]}. You may trim it, or extend it into the context words around it.",
            "\n# Words (index:word@second-in-source; '·' marks a second with no speech)",
            "\n".join(word_lines),
            "\n# Requirements",
            f"- Final clip length: {self.s.clip_min_s * 0.6:.0f}-{self.s.clip_max_s:.0f} seconds.",
            f"- Write every text field in each of these languages: {', '.join(inp.languages)}.",
            f"- Hook: at most {HOOK_MAX_CHARS} characters. Each commentary note: at most {NOTE_MAX_CHARS} characters.",
            f"- Commentary: at least one note and {MIN_COMMENTARY_WORDS}+ words in total per language. Notes start after the hook ends and do not overlap.",
            "- commentary.mode must be \"onscreen\" (voice-over is not enabled yet).",
            *self.fixed_choices(),
            "- layout: \"gameplay_blur_bg\" keeps the whole 16:9 picture over a blurred background; \"center_crop\" fills the frame and crops the sides (crop_x 0=left, 1=right).",
            "\n# Sound library (use only these ids; music_id may be null)",
            "\n".join(sfx) or "(empty)",
            "\n# Hooks used recently on this channel (do not repeat or closely imitate)",
            "\n".join(f"- {h}" for h in inp.recent_hooks) or "(none yet)",
        ]
        if inp.previous and inp.feedback:
            parts += ["\n# Revision requested by the Reviewer", inp.feedback,
                      "\nYour previous edit, as executed (times in seconds from the clip start):", dumps(inp.previous)]
        return "\n".join(parts)

    def fixed_choices(self) -> list[str]:
        """What the channel owner already decided for this job: the model does not choose it."""
        out = []
        if self.s.layout_choice != "auto":
            out.append(f'- The owner fixed layout = "{self.s.layout_choice}" for this job. Use it.')
        if self.s.caption_choice != "auto":
            out.append(f'- The owner fixed captions.style = "{self.s.caption_choice}" for this job. Use it.')
        return out

    # ---- validation ----
    def check(self, inp: EditingInput, d: EditDecision) -> None:
        n = len(inp.words)
        if d.end_word >= n or d.start_word >= d.end_word:
            raise ContractError(f"start_word/end_word {d.start_word}..{d.end_word} invalid (valid indexes: 0..{n - 1}, start < end).")
        start, end = clip_bounds(inp, d.start_word, d.end_word)
        dur = end - start
        lo, hi = self.s.clip_min_s * 0.6, self.s.clip_max_s
        if not (lo <= dur <= hi):
            raise ContractError(f"Clip would last {dur:.1f}s; it must last {lo:.0f}-{hi:.0f}s. Change start_word/end_word.")
        if d.commentary.mode != "onscreen":
            raise ContractError('commentary.mode must be "onscreen".')
        if not d.commentary.notes:
            raise ContractError("commentary.notes is empty. Add at least one note with original commentary.")

        def need_langs(label: str, text: dict[str, str], limit: int) -> None:
            for lang in inp.languages:
                value = (text.get(lang) or "").strip()
                if not value:
                    raise ContractError(f"{label}: missing text for language '{lang}'.")
                if len(value) > limit:
                    raise ContractError(f"{label}[{lang}] has {len(value)} characters; the limit is {limit}.")

        need_langs("hook.text", d.hook.text, HOOK_MAX_CHARS)
        for i, note in enumerate(d.commentary.notes):
            need_langs(f"commentary.notes[{i}].text", note.text, NOTE_MAX_CHARS)
            if not (d.start_word <= note.at_word <= d.end_word):
                raise ContractError(f"commentary.notes[{i}].at_word={note.at_word} is outside the clip ({d.start_word}..{d.end_word}).")
        for lang in inp.languages:
            total = sum(len(note.text[lang].split()) for note in d.commentary.notes)
            if total < MIN_COMMENTARY_WORDS:
                raise ContractError(f"Commentary in '{lang}' has {total} words; write at least {MIN_COMMENTARY_WORDS} of original commentary.")
            hook = _norm(d.hook.text[lang])
            if any(hook == _norm(h) for h in inp.recent_hooks):
                raise ContractError(f"hook.text[{lang}] repeats a recent hook. Write a different one.")
        for i, s in enumerate(d.audio.sfx):
            if s.id not in inp.library or inp.library[s.id]["kind"] != "sfx":
                raise ContractError(f"audio.sfx[{i}].id='{s.id}' is not a sound effect in the library ({', '.join(k for k, v in inp.library.items() if v['kind'] == 'sfx')}).")
            if not (d.start_word <= s.at_word <= d.end_word):
                raise ContractError(f"audio.sfx[{i}].at_word={s.at_word} is outside the clip.")
        if d.audio.music_id is not None and (d.audio.music_id not in inp.library or inp.library[d.audio.music_id]["kind"] != "music"):
            raise ContractError(f"audio.music_id='{d.audio.music_id}' is not a music track in the library. Use null if there is none.")
        for i, fx in enumerate(d.effects):
            if not (d.start_word <= fx.at_word <= d.end_word):
                raise ContractError(f"effects[{i}].at_word={fx.at_word} is outside the clip.")
        # Timing of notes, after the same clamping the EDL conversion applies.
        edl = self.to_edl(inp, d)
        for i, note in enumerate(edl.commentary.notes):
            if note.t < edl.hook.duration:
                raise ContractError(f"A commentary note starts at {note.t:.1f}s, before the hook ends ({edl.hook.duration:.1f}s). Move its at_word later.")
            if note.duration < 1.5:
                raise ContractError("Commentary notes are too close together or too close to the end: each needs 1.5s on screen. Space out their at_word values.")

    # ---- conversion: word indexes -> seconds ----
    def to_edl(self, inp: EditingInput, d: EditDecision) -> EDL:
        start, end = clip_bounds(inp, d.start_word, d.end_word)
        dur = end - start

        def t_of(i: int) -> float:
            return round(min(max(0.0, inp.words[i].start - start), max(0.0, dur - 0.1)), 3)

        raw = sorted(((t_of(n.at_word), n) for n in d.commentary.notes), key=lambda x: x[0])
        notes: list[EdlNote] = []
        for idx, (t, n) in enumerate(raw):
            limit = (raw[idx + 1][0] - 0.2) if idx + 1 < len(raw) else dur - 0.1
            notes.append(EdlNote(t=t, duration=round(max(0.0, min(n.duration, limit - t)), 3), text=n.text))
        return EDL(
            clip_id=inp.clip_id,
            source=EdlSource(id=inp.source.id, start=start, end=end),
            source_language=inp.source_language, languages=inp.languages,
            layout=self.s.layout_choice if self.s.layout_choice != "auto" else d.layout, crop_x=d.crop_x,
            hook=EdlHook(text=d.hook.text, duration=min(d.hook.duration, max(1.0, dur / 4))),
            commentary=EdlCommentary(mode=d.commentary.mode, notes=notes),
            captions=d.captions.model_copy(update={"style": self.s.caption_choice}) if self.s.caption_choice != "auto" else d.captions,
            audio=EdlAudio(normalize_lufs=self.s.target_lufs, music_id=d.audio.music_id, duck_music=d.audio.duck_music,
                           sfx=sorted((EdlSfx(t=t_of(s.at_word), id=s.id) for s in d.audio.sfx), key=lambda s: s.t)),
            effects=sorted((EdlEffect(t=t_of(fx.at_word), type=fx.type, duration=fx.duration) for fx in d.effects), key=lambda e: e.t),
            risk=EdlRisk(spoiler=d.risk.spoiler, rights_ok=True, notes=d.risk.notes),
            revision=inp.revision,
        )

    # ---- offline heuristic (plumbing tests only) ----
    def offline(self, inp: EditingInput) -> dict:
        a, b = inp.core
        span = b - a
        spoken = [w.w for w in inp.words[a:b + 1] if w.w]
        hook = re.sub(r"[^\w\s'¿?¡!]", "", " ".join(spoken[:6])).strip() or "Mira esto"
        hook = f"{hook[:HOOK_MAX_CHARS - 12]} #{inp.clip_id[-2:]}" if any(_norm(hook) == _norm(h) for h in inp.recent_hooks) else hook[:HOOK_MAX_CHARS]
        loc = lambda text: {lang: text for lang in inp.languages}
        n1, n2 = a + int(span * 0.35), a + int(span * 0.70)
        sfx_ids = [k for k, v in inp.library.items() if v["kind"] == "sfx"]
        sfx = []
        if sfx_ids:
            sfx = [{"at_word": a, "id": sfx_ids[0]}, {"at_word": n1, "id": sfx_ids[min(1, len(sfx_ids) - 1)]},
                   {"at_word": n2, "id": sfx_ids[min(1, len(sfx_ids) - 1)]}]
        return {
            "start_word": a, "end_word": b,
            "layout": "gameplay_blur_bg" if inp.source.width > inp.source.height else "center_crop",
            "hook": {"text": loc(hook), "duration": 2.0},
            "commentary": {"mode": "onscreen", "notes": [
                {"at_word": n1, "duration": 3.5, "text": loc("[offline] Aquí va el comentario original que escribe Claude sobre este momento")},
                {"at_word": n2, "duration": 3.5, "text": loc("[offline] Segunda nota de análisis: contexto que el video original no da")}]},
            "captions": {"style": ["bold", "bounce", "clean"][sum(map(ord, inp.clip_id)) % 3], "highlight_keywords": []},
            "audio": {"sfx": sfx, "music_id": None, "duck_music": True},
            "effects": [{"at_word": a + span // 2, "type": "punch_in", "duration": 0.8}],
            "risk": {"spoiler": False, "notes": "Sin revisar: modo offline."},
        }


def _norm(text: str) -> str:
    return re.sub(r"[\W_]+", " ", text.lower()).strip()


def sample_source_frames(video: str, start: float, end: float, out_dir: Path, count: int = 6, width: int = 480) -> list[Path]:
    out_dir.mkdir(parents=True, exist_ok=True)
    frames = []
    for i in range(count):
        t = start + (end - start) * (i + 0.5) / count
        out = out_dir / f"src_{i:02d}.jpg"
        ffmpeg.run(["-ss", f"{t:.3f}", "-i", str(video), "-frames:v", "1", "-vf", f"scale={width}:-2", "-q:v", "6", str(out)])
        frames.append(out)
    return frames
