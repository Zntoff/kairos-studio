"""Descarga de videos desde una URL (YouTube, Twitch, Kick) con yt-dlp.

Descargar no da derechos: el video sigue pasando por el registro de derechos
(`rights.register_source`), que exige estado y evidencia antes de entrar al pipeline."""
from __future__ import annotations

import os
from pathlib import Path
from typing import Callable
from urllib.parse import urlparse

ALLOWED_HOSTS = ("youtube.com", "youtu.be", "twitch.tv", "kick.com")
MAX_HEIGHT = 1080


class DownloadError(ValueError):
    pass


def is_url(text: str) -> bool:
    return text.strip().lower().startswith(("http://", "https://"))


def check_url(url: str) -> str:
    url = url.strip()
    p = urlparse(url)
    host = (p.hostname or "").lower()
    if p.scheme not in ("http", "https") or not any(host == h or host.endswith("." + h) for h in ALLOWED_HOSTS):
        raise DownloadError(f"URL no admitida. Plataformas: {', '.join(ALLOWED_HOSTS)}")
    return url


def fetch(url: str, dest_dir: Path, log: Callable[[str], None] = lambda _: None) -> tuple[Path, dict]:
    """Descarga el video a `dest_dir` y devuelve (ruta, metadatos: title, uploader, id)."""
    url = check_url(url)
    try:
        import yt_dlp
    except ImportError as e:  # pragma: no cover
        raise DownloadError("Falta yt-dlp: pip install yt-dlp") from e

    dest_dir.mkdir(parents=True, exist_ok=True)
    state = {"pct": -10.0}

    def hook(d: dict) -> None:
        if d.get("status") == "downloading":
            total = d.get("total_bytes") or d.get("total_bytes_estimate")
            if total:
                pct = d["downloaded_bytes"] * 100 / total
                if pct - state["pct"] >= 10:  # log every ~10%, not every chunk
                    state["pct"] = pct
                    log(f"Descargando… {pct:.0f}%")
        elif d.get("status") == "finished":
            state["pct"] = -10.0

    class _Quiet:
        def debug(self, m): pass
        def info(self, m): pass
        def warning(self, m): pass
        def error(self, m): log(f"yt-dlp: {m}")

    opts = {
        "format": f"bv*[height<={MAX_HEIGHT}]+ba/b[height<={MAX_HEIGHT}]/b",
        # At the same resolution prefer H.264 + AAC: every browser plays it (the editor previews the
        # source directly) and FFmpeg decodes it much faster than AV1.
        "format_sort": [f"res:{MAX_HEIGHT}", "vcodec:h264", "acodec:aac"],
        "merge_output_format": "mp4",
        "outtmpl": str(dest_dir / "%(extractor)s_%(id)s.%(ext)s"),
        "noplaylist": True,
        "restrictfilenames": True,
        "progress_hooks": [hook],
        "logger": _Quiet(),
        "concurrent_fragment_downloads": 4,
    }
    cookies = os.environ.get("CLIPPER_COOKIES_FILE")  # YouTube a veces pide sesión para confirmar que no eres un bot
    if cookies:
        opts["cookiefile"] = cookies

    log(f"Descargando {url}")
    try:
        with yt_dlp.YoutubeDL(opts) as ydl:
            info = ydl.extract_info(url, download=True)
    except yt_dlp.utils.DownloadError as e:
        raise DownloadError(f"No se pudo descargar: {str(e).replace('ERROR: ', '')[:300]}") from e
    if not info:
        raise DownloadError("No se pudo descargar: yt-dlp no devolvió información.")

    downloads = info.get("requested_downloads") or []
    path = Path(downloads[0]["filepath"]) if downloads and downloads[0].get("filepath") else Path(ydl.prepare_filename(info))
    if not path.is_file():
        raise DownloadError("La descarga terminó pero no se encontró el archivo.")
    meta = {"title": info.get("title"), "uploader": info.get("uploader") or info.get("channel"), "id": info.get("id")}
    log(f"Descargado: {path.name}")
    return path, meta
