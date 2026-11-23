"""Speech to text with word timestamps (faster-whisper), plus helpers to load a transcript
made elsewhere and to represent a video with no speech."""
from __future__ import annotations

import json
import re
from pathlib import Path

from ..config import Settings
from ..schemas import Segment, Transcript, Word
from . import ffmpeg

SENTENCE_END = re.compile(r"[.!?…]+[\"')\]]*$")


def extract_audio(video: str | Path, out_wav: Path) -> Path:
    ffmpeg.run(["-i", str(video), "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", str(out_wav)])
    return out_wav


def transcribe_clip(video: str | Path, out_wav: Path, start: float, end: float,
                    settings: Settings, language: str | None = None) -> Transcript:
    """Align against the actual cut, retaining source coordinates for caption generation.

    A little context protects words cut at either edge. Never rescale timestamps to
    fit a clip: pauses and variable speech rate must retain their acoustic timing.
    """
    origin = max(0.0, start - 0.4)
    out_wav.parent.mkdir(parents=True, exist_ok=True)
    ffmpeg.run(["-ss", f"{origin:.3f}", "-i", str(video), "-t", f"{end + .4 - origin:.3f}",
                "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", str(out_wav)])
    transcript = transcribe(out_wav, settings, language)
    for segment in transcript.segments:
        segment.start += origin
        segment.end += origin
        for word in segment.words:
            word.start += origin
            word.end += origin
    transcript.duration += origin
    return transcript


def resegment(words: list[Word], *, max_pause: float = 0.7, max_len: float = 14.0) -> list[Segment]:
    """Group words into sentence-like segments so that segment edges are natural cut points:
    the end of a sentence, a pause, or a length cap."""
    segments: list[Segment] = []
    cur: list[Word] = []

    def flush() -> None:
        if cur:
            segments.append(Segment(id=len(segments), start=cur[0].start, end=cur[-1].end,
                                    text=" ".join(w.w for w in cur).strip(), words=list(cur)))
            cur.clear()

    for i, w in enumerate(words):
        cur.append(w)
        nxt = words[i + 1] if i + 1 < len(words) else None
        pause = (nxt.start - w.end) if nxt else 0.0
        too_long = (w.end - cur[0].start) >= max_len
        if nxt is None or SENTENCE_END.search(w.w) or pause > max_pause or too_long:
            flush()
    return segments


def silent_transcript(duration: float, language: str = "und", step: float = 5.0) -> Transcript:
    """A video without speech still needs cut points: fixed windows with empty one-second marks."""
    segments: list[Segment] = []
    t = 0.0
    while t < duration - 0.5:
        end = min(duration, t + step)
        marks, m = [], t
        while m < end - 0.25:
            marks.append(Word(w="", start=round(m, 3), end=round(min(end, m + 1.0), 3)))
            m += 1.0
        segments.append(Segment(id=len(segments), start=round(t, 3), end=round(end, 3), text="", words=marks))
        t = end
    return Transcript(language=language, duration=duration, segments=segments)


def transcribe(audio_wav: str | Path, settings: Settings, language: str | None = None) -> Transcript:
    """Run faster-whisper. The first run downloads the model named in CLIPPER_WHISPER_MODEL."""
    from faster_whisper import WhisperModel

    model = WhisperModel(settings.whisper_model, device=settings.whisper_device, compute_type=settings.whisper_compute)
    segments, info = model.transcribe(str(audio_wav), language=language, word_timestamps=True, vad_filter=True)
    words: list[Word] = []
    for seg in segments:
        for w in seg.words or []:
            text = w.word.strip()
            if text:
                words.append(Word(w=text, start=round(float(w.start), 3), end=round(float(w.end), 3)))
    duration = float(getattr(info, "duration", 0.0) or (words[-1].end if words else 0.0))
    lang = language or getattr(info, "language", None) or "und"
    if not words:
        return silent_transcript(duration, lang)
    return Transcript(language=lang, duration=duration, segments=resegment(words))


def load_transcript(path: str | Path, duration: float | None = None) -> Transcript:
    """Accepts this project's transcript.json, or a flat {"language", "words": [{w,start,end}]} file."""
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    if "segments" in raw:
        words = [Word(**w) for s in raw["segments"] for w in s["words"]]
    else:
        words = [Word(**w) for w in raw["words"]]
    dur = float(raw.get("duration") or duration or (words[-1].end if words else 0.0))
    lang = raw.get("language") or "und"
    if not any(w.w for w in words):
        return silent_transcript(dur, lang)
    return Transcript(language=lang, duration=dur, segments=resegment(words))
