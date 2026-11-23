"""Captions: phrases, word-by-word cues, and the ASS / SRT files the renderer burns in.

All on-screen text (captions, hook, commentary notes) goes into one ASS file per language,
positioned inside the safe zone of a 1080x1920 frame."""
from __future__ import annotations

import functools
import re
import subprocess
from pathlib import Path

from PIL import ImageFont

from ..config import Settings
from ..schemas import EDL, CaptionTrack, CaptionWord, Cue, Transcript, Word
from . import studio
from .studio import ass_color

PUNCT_BREAK = re.compile(r"[,.;:!?…]+[\"')\]]*$")
SENTENCE_END = re.compile(r"[.!?…]+[\"')\]]*$")

# ASS colours are &HAABBGGRR
WHITE, BLACK = "&H00FFFFFF", "&H00000000"
YELLOW = "&H0000E5FF"
NOTE_BOX = "&H38181818"
TEXT_MARGIN = 60          # free text layers wrap this far from each side of the frame
TEXT_BOX_PAD = 14


# ---------- words, phrases, cues ----------
def clip_words(transcript: Transcript, start: float, end: float) -> list[Word]:
    """Spoken words inside the clip, with times relative to the clip start."""
    out = []
    for w in transcript.words():
        mid = (w.start + w.end) / 2
        if w.w and start <= mid <= end:
            out.append(Word(w=w.w, start=round(max(0.0, w.start - start), 3), end=round(min(end, w.end) - start, 3)))
    return out


def build_phrases(words: list[Word], max_words: int = 9) -> list[list[Word]]:
    phrases, cur = [], []
    for i, w in enumerate(words):
        cur.append(w)
        nxt = words[i + 1] if i + 1 < len(words) else None
        pause = nxt.start - w.end if nxt else 0.0
        if nxt is None or SENTENCE_END.search(w.w) or pause > 0.6 or len(cur) >= max_words \
                or (len(cur) >= 5 and PUNCT_BREAK.search(w.w)):
            phrases.append(cur)
            cur = []
    return phrases


def norm(token: str) -> str:
    return re.sub(r"[^\w']+", "", token.lower())


def chunk(tokens: list[str], max_words: int = 3, max_chars: int = 16) -> list[list[int]]:
    """Split a phrase into short on-screen groups. Returns groups of token indexes."""
    groups, cur, chars = [], [], 0
    for i, tok in enumerate(tokens):
        if cur and (len(cur) >= max_words or chars + len(tok) + 1 > max_chars):
            groups.append(cur)
            cur, chars = [], 0
        cur.append(i)
        chars += len(tok) + 1
        if SENTENCE_END.search(tok) or (len(cur) >= 2 and PUNCT_BREAK.search(tok)):
            groups.append(cur)
            cur, chars = [], 0
    if cur:
        groups.append(cur)
    return groups


def timed_cues(phrase: list[Word], tokens: list[str] | None, highlights: set[str]) -> list[Cue]:
    """Cues for the spoken language: each word keeps its real timing. `tokens` are the
    corrected spellings; they are used only when they map one-to-one onto the spoken words."""
    texts = tokens if tokens and len(tokens) == len(phrase) else [w.w for w in phrase]
    cues = []
    # Do not leave a word highlighted across a silence inside a short group.
    groups = []
    for group in chunk(texts):
        current = []
        for i in group:
            if current and phrase[i].start - phrase[current[-1]].end > 0.25:
                groups.append(current)
                current = []
            current.append(i)
        if current:
            groups.append(current)
    for group in groups:
        words = [CaptionWord(w=texts[i], start=phrase[i].start, end=phrase[i].end, hl=norm(texts[i]) in highlights) for i in group]
        cues.append(Cue(start=words[0].start, end=words[-1].end, words=words))
    return cues


def spread_cues(text: str, start: float, end: float, highlights: set[str]) -> list[Cue]:
    """Cues for a translated phrase: its words share the phrase's time span in proportion to length."""
    tokens = text.split()
    if not tokens or end <= start:
        return []
    weights = [len(t) + 1 for t in tokens]
    total, t, times = sum(weights), start, []
    for wgt in weights:
        dur = (end - start) * wgt / total
        times.append((round(t, 3), round(t + dur, 3)))
        t += dur
    cues = []
    for group in chunk(tokens):
        words = [CaptionWord(w=tokens[i], start=times[i][0], end=times[i][1], hl=norm(tokens[i]) in highlights) for i in group]
        cues.append(Cue(start=words[0].start, end=words[-1].end, words=words))
    return cues


def close_gaps(cues: list[Cue], clip_duration: float, hold: float = 0.3) -> list[Cue]:
    """Keep each cue on screen briefly after its last word, never past the next cue or the clip end."""
    cues = sorted(cues, key=lambda c: c.start)
    for i, cue in enumerate(cues):
        limit = cues[i + 1].start if i + 1 < len(cues) else clip_duration
        cue.end = round(max(cue.words[-1].end, min(cue.words[-1].end + hold, limit)), 3)
    return cues


# ---------- text measurement ----------
@functools.lru_cache(maxsize=16)
def font_file(family: str, bold: bool = False) -> str:
    bundled = studio.bundled_font_file(family, bold)
    if bundled:
        return bundled
    pattern = f"{family}:bold" if bold else family
    return subprocess.run(["fc-match", "-f", "%{file}", pattern], capture_output=True, text=True, check=True).stdout.strip()


@functools.lru_cache(maxsize=64)
def _font(path: str, size: float) -> ImageFont.FreeTypeFont:
    """`size` is an ASS font size (the font's line height), as everywhere in this module."""
    return ImageFont.truetype(path, max(1.0, size * studio.font_scale(path)))


def text_width(text: str, family: str, size: int, bold: bool = False) -> float:
    return _font(font_file(family, bold), size).getlength(text)


def wrap(text: str, family: str, size: int, max_width: float, bold: bool = False) -> list[str]:
    """Wrap to max_width, then narrow the block as far as the line count allows, so the
    last line is not a lone short word."""
    lines = _wrap_greedy(text, family, size, max_width, bold)
    if len(lines) < 2:
        return lines
    lo, hi = max_width * 0.5, max_width
    for _ in range(8):
        mid = (lo + hi) / 2
        trial = _wrap_greedy(text, family, size, mid, bold)
        if len(trial) <= len(lines) and all(text_width(l, family, size, bold) <= max_width for l in trial):
            hi, lines = mid, trial
        else:
            lo = mid
    return lines


def _wrap_greedy(text: str, family: str, size: int, max_width: float, bold: bool = False) -> list[str]:
    lines, cur = [], ""
    for word in text.split():
        trial = f"{cur} {word}".strip()
        if cur and text_width(trial, family, size, bold) > max_width:
            lines.append(cur)
            cur = word
        else:
            cur = trial
    if cur:
        lines.append(cur)
    return lines


# ---------- layout geometry ----------
def geometry(edl: EDL, src_w: int, src_h: int, s: Settings) -> dict:
    """Where the picture and each text block sit in the frame."""
    W, H, safe = s.width, s.height, s.safe
    top_safe, bottom_safe = safe.top, H - safe.bottom
    if edl.layout == "gameplay_blur_bg":
        fg_h = min(H, int(round(W * src_h / src_w / 2)) * 2)
        fg_y = (H - fg_h) // 2
        caption_y = min(bottom_safe - 70, fg_y + fg_h + max(70, (bottom_safe - fg_y - fg_h) // 2))
        top_y = max(top_safe + 110, top_safe + (fg_y - top_safe) // 2)
    else:
        fg_h, fg_y = H, 0
        caption_y, top_y = bottom_safe - 160, top_safe + 200
    plan = getattr(edl, "captions", None)
    if plan is not None and getattr(plan, "y", None) is not None:
        caption_y = int(round(plan.y * H))
    return {"fg_h": fg_h, "fg_y": fg_y, "caption_y": caption_y, "top_y": top_y,
            "safe_width": W - 2 * safe.side, "top_safe": top_safe, "bottom_safe": bottom_safe}


# Caption styles are data: assets/studio/catalog.json (colours there are #RRGGBB).
STYLE_SPECS: dict[str, dict] = studio.catalog()["caption_styles"]


def caption_font(style: str, s: Settings) -> tuple[str, bool]:
    font_id = STYLE_SPECS[style]["font"]
    if font_id == "caption":
        return s.caption_font, False
    if font_id == "text":
        return s.text_font, True
    f = studio.font(font_id)
    return f["family"], bool(f["bold"])


def cue_font_size(cue_text: str, style: str, s: Settings, safe_width: float, scale: float = 1.0) -> int:
    spec = STYLE_SPECS[style]
    family, bold = caption_font(style, s)
    text = cue_text.upper() if spec["upper"] else cue_text
    base = int(round(spec["size"] * scale))
    # 1.2 leaves room for the active word's pop and the outline.
    width = text_width(text, family, base, bold) * 1.2 + 2 * spec["outline"]
    return base if width <= safe_width else max(40, int(base * safe_width / width))


# ---------- ASS ----------
def _ts(t: float) -> str:
    t = max(0.0, t)
    cs = int(round(t * 100))
    return f"{cs // 360000}:{cs // 6000 % 60:02d}:{cs // 100 % 60:02d}.{cs % 100:02d}"


def _esc(text: str) -> str:
    return text.replace("\\", "/").replace("{", "(").replace("}", ")").replace("\n", " ").strip()


def _alpha(opacity: float) -> str:
    return f"&H{int(round((1 - max(0.0, min(1.0, opacity))) * 255)):02X}&"


def _bgr(hex_color: str) -> str:
    return f"&H{ass_color(hex_color)[4:]}&"


def _rect(w: int, h: int) -> str:
    return f"\\p1}}m 0 0 l {w} 0 {w} {h} 0 {h}{{\\p0}}"


def text_lines(raw: str, family: str, size: int, bold: bool, upper: bool, max_width: float) -> list[str]:
    """Lines of a free text layer: the person's own line breaks, then wrapped to the frame."""
    lines: list[str] = []
    for para in raw.replace("\r", "").split("\n"):
        para = _esc(para.upper() if upper else para)
        if para:
            lines += wrap(para, family, size, max_width, bold)
    return lines


def text_events(edl: EDL, lang: str, s: Settings) -> list[str]:
    """Free text layers, with their entrance and exit animations."""
    A = studio.catalog()["anim"]
    W, H, ev = s.width, s.height, []
    for tx in edl.texts:
        t0, t1 = max(0.0, tx.t), min(edl.duration, tx.t + tx.duration)
        f = studio.font(tx.font)
        fs = int(round(tx.size / studio.font_scale(f["path"])))   # tx.size is the em size; libass wants the line height
        lines = text_lines(tx.text.get(lang, ""), f["family"], fs, bool(f["bold"]), tx.upper, W - 2 * TEXT_MARGIN)
        if not lines or t1 - t0 < 0.05:
            continue
        x, y, d_ms = int(round(tx.x * W)), int(round(tx.y * H)), int(round((t1 - t0) * 1000))
        tags = [f"\\an5\\fn{f['family']}\\b{1 if f['bold'] else 0}\\fs{fs}\\shad0\\c{_bgr(tx.color)}\\1a{_alpha(tx.opacity)}"]
        if tx.box:
            tags.append(f"\\bord{TEXT_BOX_PAD}\\3c{_bgr(tx.box)}\\3a{_alpha(tx.box_opacity * tx.opacity)}")
        else:
            tags.append(f"\\bord{tx.stroke_w:g}\\3c{_bgr(tx.stroke)}\\3a{_alpha(tx.opacity)}")
            if tx.glow:
                tags.append(f"\\blur{tx.glow:g}")
        if tx.rotate:
            tags.append(f"\\frz{-tx.rotate:g}")
        px = A["slide_px"]
        start = {"slide_up": (x, y + px), "slide_down": (x, y - px), "slide_left": (x + px, y), "slide_right": (x - px, y)}.get(tx.anim_in)
        tags.append(f"\\move({start[0]},{start[1]},{x},{y},0,{int(A['slide'] * 1000)})" if start else f"\\pos({x},{y})")
        fade_in = 0 if tx.anim_in == "none" else int((0.08 if tx.anim_in == "pop" else A["fade"]) * 1000)
        fade_out = int(A["fade"] * 1000) if tx.anim_out == "fade" else 0
        if fade_in or fade_out:
            tags.append(f"\\fad({fade_in},{fade_out})")
        if tx.anim_in == "pop":
            a, b = int(A["pop_a"] * 1000), int(A["pop_b"] * 1000)
            tags.append(f"\\fscx40\\fscy40\\t(0,{a},\\fscx112\\fscy112)\\t({a},{b},\\fscx100\\fscy100)")
        elif tx.anim_in == "zoom":
            tags.append(f"\\fscx160\\fscy160\\t(0,{int(A['zoom'] * 1000)},\\fscx100\\fscy100)")
        if tx.anim_out == "shrink":
            tags.append(f"\\t({max(0, d_ms - int(A['shrink'] * 1000))},{d_ms},\\fscx0\\fscy0)")
        ev.append(f"Dialogue: 3,{_ts(t0)},{_ts(t1)},{'TxtB' if tx.box else 'Txt'},,0,0,0,,{{{''.join(tags)}}}" + "\\N".join(lines))
    return ev


def shape_events(edl: EDL, s: Settings) -> list[str]:
    """Full-frame shapes drawn by libass: white flashes and the progress bar."""
    W, H, FX, ev = s.width, s.height, studio.catalog()["fx"], []
    flashes = [(fx.t, fx.duration, fx.intensity) for fx in edl.effects if fx.type == "flash"]
    if edl.transitions.intro == "flash":
        flashes.append((0.0, edl.transitions.intro_d, 1.2))
    for t, d, k in flashes:
        if d > 0 and t < edl.duration:
            ev.append(f"Dialogue: 9,{_ts(t)},{_ts(min(edl.duration, t + d))},Shape,,0,0,0,,"
                      f"{{\\pos(0,0)\\c&HFFFFFF&\\alpha{_alpha(FX['flash'] * k)}\\t(\\alpha&HFF&){_rect(W, H)}")
    if edl.progress.on:
        h = edl.progress.height
        y = 0 if edl.progress.position == "top" else H - h
        ev.append(f"Dialogue: 8,{_ts(0)},{_ts(edl.duration)},Shape,,0,0,0,,"
                  f"{{\\pos(0,{y})\\c{_bgr(edl.progress.color)}\\clip(0,{y},0,{y + h})\\t(\\clip(0,{y},{W},{y + h})){_rect(W, h)}")
    return ev


def build_ass(edl: EDL, track: CaptionTrack | None, lang: str, src_w: int, src_h: int, s: Settings) -> str:
    g = geometry(edl, src_w, src_h, s)
    cx = s.width // 2
    style = edl.captions.style
    spec = STYLE_SPECS[style]
    family, bold = caption_font(style, s)
    base, active, keyword = ass_color(spec["color"]), ass_color(spec["active"]), ass_color(spec["keyword"])
    glow = f"\\blur{spec['glow']}" if spec["glow"] else ""
    head = [
        "[Script Info]", "ScriptType: v4.00+", f"PlayResX: {s.width}", f"PlayResY: {s.height}",
        "WrapStyle: 2", "ScaledBorderAndShadow: yes", "",
        "[V4+ Styles]",
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, "
        "Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding",
        f"Style: Cap,{family},{spec['size']},{base},{base},{ass_color(spec['outline_color'])},&H80000000,{-1 if bold else 0},0,0,0,100,100,0,0,1,{spec['outline']},{0 if spec['glow'] else 2},5,20,20,20,1",
        "Style: Txt,Inter,60,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,5,0,0,0,1",
        "Style: TxtB,Inter,60,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,3,14,0,5,0,0,0,1",
        "Style: Shape,Inter,20,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1",
        f"Style: Hook,{s.caption_font},86,{BLACK},{BLACK},{YELLOW},{YELLOW},0,0,0,0,100,100,0,0,3,16,0,5,20,20,20,1",
        f"Style: Note,{s.text_font},46,{WHITE},{WHITE},{NOTE_BOX},{NOTE_BOX},-1,0,0,0,100,100,0,0,3,14,0,5,20,20,20,1",
        "", "[Events]",
        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text",
    ]
    ev: list[str] = []

    def block(o, width, height, default_y):
        hw, hh = min((s.width-2*s.safe.side)/2, width/2+4), min((s.height-s.safe.top-s.safe.bottom)/2, height/2+4)
        x = cx if o.x is None else round(o.x*s.width)
        y = default_y if o.y is None else round(o.y*s.height)
        return round(min(s.width-s.safe.side-hw,max(s.safe.side+hw,x))), round(min(s.height-s.safe.bottom-hh,max(s.safe.top+hh,y)))

    # Hook: big label at the top for the first seconds.
    hook = _esc(edl.hook.text.get(lang, "")).upper()
    if hook:
        size = 86
        lines = wrap(hook, s.caption_font, size, g["safe_width"] - 40)
        while len(lines) > 3 and size > 56:
            size -= 8
            lines = wrap(hook, s.caption_font, size, g["safe_width"] - 40)
        x,y = block(edl.hook,max(text_width(l,s.caption_font,size) for l in lines)+32,len(lines)*size+36,g["top_y"])
        ev.append(f"Dialogue: 2,{_ts(0)},{_ts(edl.hook.duration)},Hook,,0,0,0,,"
                  f"{{\\pos({x},{y})\\fs{size}\\fad(80,160)}}" + "\\N".join(lines))

    # Commentary notes: the channel's own analysis, in a box at the top.
    for note in edl.commentary.notes:
        text = _esc(note.text.get(lang, ""))
        if not text or note.duration <= 0:
            continue
        lines = wrap(text, s.text_font, 46, g["safe_width"] - 40, bold=True)
        x,y = block(note,max(text_width(l,s.text_font,46,True) for l in lines)+28,len(lines)*46+32,g["top_y"])
        ev.append(f"Dialogue: 1,{_ts(note.t)},{_ts(note.t + note.duration)},Note,,0,0,0,,"
                  f"{{\\pos({x},{y})\\fad(120,160)}}" + "\\N".join(lines))

    # Captions: one event per spoken word, with that word highlighted.
    for cue in (track.cues if track else []):
        size = cue_font_size(cue.text, style, s, g["safe_width"], edl.captions.scale)
        x,y = block(edl.captions,text_width(cue.text,family,size,bold)*1.2+2*spec["outline"],size*1.2,g["caption_y"])
        for k, spoken in enumerate(cue.words):
            t0 = cue.start if k == 0 else spoken.start
            t1 = cue.words[k + 1].start if k + 1 < len(cue.words) else cue.end
            if t1 <= t0:
                continue
            parts = []
            for j, w in enumerate(cue.words):
                word = _esc(w.w.upper() if spec["upper"] else w.w)
                if j == k:
                    pop = "\\fscx86\\fscy86\\t(0,80,\\fscx116\\fscy116)\\t(80,170,\\fscx106\\fscy106)" if spec["pop"] else "\\fscx106\\fscy106"
                    parts.append(f"{{\\c{active}{pop}}}{word}{{\\c{base}\\fscx100\\fscy100}}")
                elif w.hl:
                    parts.append(f"{{\\c{keyword}}}{word}{{\\c{base}}}")
                else:
                    parts.append(word)
            ev.append(f"Dialogue: 0,{_ts(t0)},{_ts(t1)},Cap,,0,0,0,,{{\\pos({x},{y})\\fs{size}{glow}}}" + " ".join(parts))
    ev += text_events(edl, lang, s) + shape_events(edl, s)
    return "\n".join(head + ev) + "\n"


# ---------- SRT ----------
def _srt_ts(t: float) -> str:
    ms = int(round(max(0.0, t) * 1000))
    return f"{ms // 3600000:02d}:{ms // 60000 % 60:02d}:{ms // 1000 % 60:02d},{ms % 1000:03d}"


def build_srt(track: CaptionTrack | None) -> str:
    if not track:
        return ""
    return "\n".join(f"{i}\n{_srt_ts(c.start)} --> {_srt_ts(c.end)}\n{c.text}\n" for i, c in enumerate(track.cues, 1))
