"""Curated Docker build context: never collect the developer's working directory wholesale."""
from pathlib import Path
import argparse
import json
import zipfile
import hashlib
import re

ROOT = Path(__file__).resolve().parents[1]


def package(destination: Path, version: str):
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-beta\.[1-9]\d*)?", version):
        raise ValueError("Expected a stable or beta semantic version")
    destination.mkdir(parents=True, exist_ok=True)
    output = destination / f"KairosStudio-{version}-Docker.zip"
    prefix = f"KairosStudio-{version}-Docker/"
    files = {}
    for directory in ("clipper", "assets", "config"):
        for source in (ROOT / directory).rglob("*"):
            if not source.is_file() or "__pycache__" in source.parts or source.suffix == ".pyc":
                continue
            if source.is_symlink():
                raise ValueError(f"Symlink forbidden in package: {source.name}")
            files[source.relative_to(ROOT).as_posix()] = source.read_bytes()
    for name in ("Dockerfile", ".dockerignore", "pyproject.toml", "LICENSE", "NOTICE", ".env.example"):
        files[name] = (ROOT / name).read_bytes()
    for source in (ROOT / "distribution/docker").iterdir():
        if source.is_file():
            files[source.name] = source.read_bytes().replace(b"@VERSION@", version.encode())
    for name in files:
        basename = Path(name).name
        if (basename.startswith(".env") and basename != ".env.example") or basename in ("settings.ai.json", "usage.jsonl", "ui-language.json") or Path(name).suffix.lower() in (".db", ".sqlite", ".sqlite3", ".pfx", ".p12", ".pem", ".key") or (basename == "library.json" and name != "assets/sfx/library.json"):
            raise ValueError(f"Private file forbidden in package: {name}")
    files["package-manifest.json"] = json.dumps({"version": version, "format": "docker-build-context", "files": {name: hashlib.sha256(data).hexdigest() for name, data in files.items()}}, indent=2).encode()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for name, data in sorted(files.items()):
            archive.writestr(prefix + name, data)
    return output


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", required=True)
    arguments = parser.parse_args()
    print(package(arguments.output, arguments.version))
