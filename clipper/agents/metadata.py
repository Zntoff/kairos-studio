"""Titles & metadata subagent: title, description and hashtags per platform and language."""
from __future__ import annotations

import re
from dataclasses import dataclass

from ..media import captions as cap
from ..schemas import EDL, MetadataSet, Transcript
from .base import ContractError, StepMeta, Subagent

PLATFORMS = ("youtube", "tiktok", "instagram")
CREDIT_LABEL = {"en": "Credit", "es": "Créditos"}


def load_spoiler_terms(path) -> list[str]:
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except FileNotFoundError:
        return []
    return [ln.strip().lower() for ln in lines if ln.strip() and not ln.strip().startswith("#")]


def find_terms(text: str, terms: list[str]) -> list[str]:
    low = text.lower()
    return [t for t in terms if re.search(rf"(?<!\w){re.escape(t)}(?!\w)", low)]


@dataclass
class MetadataInput:
    edl: EDL
    spoken_text: str
    credit: str | None
    spoiler_terms: list[str]


class MetadataAgent(Subagent):
    name = "metadata"
    tier = "standard"
    contract = MetadataSet
    max_tokens = 4000

    def run(self, edl: EDL, transcript: Transcript, credit: str | None) -> tuple[MetadataSet, StepMeta]:
        spoken = " ".join(w.w for w in cap.clip_words(transcript, edl.source.start, edl.source.end))
        inp = MetadataInput(edl=edl, spoken_text=spoken, credit=credit, spoiler_terms=load_spoiler_terms(self.s.spoilers_file))
        out, meta = self.decide(inp)
        # Deterministic clean-up: hashtag format and the credit line are not left to the model.
        for lang, lm in out.per_language.items():
            for platform in PLATFORMS:
                pm = getattr(lm, platform)
                tags, seen = [], set()
                for tag in pm.hashtags:
                    tag = "#" + re.sub(r"[^\w]", "", tag.lstrip("#"))
                    if len(tag) > 1 and tag.lower() not in seen:
                        seen.add(tag.lower())
                        tags.append(tag)
                pm.hashtags = tags[: self.s.limits.hashtags_max]
                if credit:
                    pm.description = f"{pm.description.rstrip()}\n\n{CREDIT_LABEL.get(lang, 'Credit')}: {credit}"
        return out, meta

    def build_user(self, inp: MetadataInput) -> str:
        e, lim = inp.edl, self.s.limits
        notes = "\n".join(f"- {' / '.join(n.text.values())}" for n in e.commentary.notes)
        reserve = len(inp.credit) + 20 if inp.credit else 0
        return (
            f"# Clip ({e.duration:.0f}s)\nHook: {' / '.join(e.hook.text.values())}\nCommentary notes:\n{notes}\n\n"
            f"What is said in the clip ({e.source_language}):\n{inp.spoken_text or '(no speech)'}\n\n"
            f"# Write for languages\n{', '.join(e.languages)}\n\n"
            "# Limits\n"
            f"- youtube.title: required, at most {lim.title_max} characters. tiktok.title and instagram.title: leave empty.\n"
            f"- description: at most {lim.description_max - reserve} characters on every platform. Do not put hashtags inside it.\n"
            f"- hashtags: at most {lim.hashtags_max} per platform.\n"
            + ("- A credit line for the original creator is appended automatically. Do not write one.\n" if inp.credit else "")
        )

    def check(self, inp: MetadataInput, out: MetadataSet) -> None:
        lim = self.s.limits
        reserve = len(inp.credit) + 20 if inp.credit else 0
        for lang in inp.edl.languages:
            lm = out.per_language.get(lang)
            if lm is None:
                raise ContractError(f"per_language is missing '{lang}'.")
            if not lm.youtube.title.strip():
                raise ContractError(f"per_language.{lang}.youtube.title is empty.")
            if len(lm.youtube.title) > lim.title_max:
                raise ContractError(f"per_language.{lang}.youtube.title has {len(lm.youtube.title)} characters; the limit is {lim.title_max}.")
            for platform in PLATFORMS:
                pm = getattr(lm, platform)
                if not pm.description.strip():
                    raise ContractError(f"per_language.{lang}.{platform}.description is empty.")
                if len(pm.description) > lim.description_max - reserve:
                    raise ContractError(f"per_language.{lang}.{platform}.description has {len(pm.description)} characters; the limit is {lim.description_max - reserve}.")
                hits = find_terms(f"{pm.title} {pm.description} {' '.join(pm.hashtags)}", inp.spoiler_terms)
                if hits:
                    raise ContractError(f"per_language.{lang}.{platform} contains spoiler-list terms {hits}. Rewrite without revealing them.")

    def offline(self, inp: MetadataInput) -> dict:
        def one(lang: str) -> dict:
            title = inp.edl.hook.text.get(lang, "Clip")[: self.s.limits.title_max]
            desc = "[offline] Descripción de prueba generada sin Claude."
            return {p: {"title": title if p == "youtube" else "", "description": desc, "hashtags": ["#shorts", "#clips"]} for p in PLATFORMS}

        return {"per_language": {lang: one(lang) for lang in inp.edl.languages}}
