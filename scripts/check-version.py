"""Check that the three version declarations agree, and optionally match a tag and the changelog.

    python scripts/check-version.py                      # the three declarations match
    python scripts/check-version.py --tag v0.11.0        # ...and equal the release tag
    python scripts/check-version.py --changelog          # ...and CHANGELOG.md has a ## [X.Y.Z] section
    python scripts/check-version.py --notes notes.md     # ...and write that section to notes.md

Prints the version on success; exits with an error message otherwise.
"""
import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCES = [
    ("pyproject.toml", r'(?m)^version = "([^"]+)"'),
    ("clipper/__init__.py", r'__version__ = "([^"]+)"'),
    ("desktop/ClipperStudio/ClipperStudio.csproj", r"<Version>([^<]+)</Version>"),
]


def declared_versions() -> dict[str, str]:
    found = {}
    for name, pattern in SOURCES:
        matches = re.findall(pattern, (ROOT / name).read_text(encoding="utf-8"))
        if len(matches) != 1:
            raise SystemExit(f"Expected exactly one version in {name}, found {len(matches)}.")
        found[name] = matches[0]
    return found


def changelog_section(version: str) -> str | None:
    text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    match = re.search(rf"(?ms)^## \[{re.escape(version)}\][^\n]*\n(.*?)(?=^## |^\[[^\]]+\]: |\Z)", text)
    return match.group(1).strip() if match else None


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--tag", help="release tag that must match, e.g. v0.11.0")
    parser.add_argument("--changelog", action="store_true", help="require a CHANGELOG.md section for the version")
    parser.add_argument("--notes", type=Path, help="write the CHANGELOG.md section to this file (implies --changelog)")
    args = parser.parse_args()

    found = declared_versions()
    versions = set(found.values())
    if len(versions) != 1:
        details = ", ".join(f"{name}={value}" for name, value in found.items())
        raise SystemExit(f"Version declarations differ: {details}. Run scripts/set-version.py.")
    version = versions.pop()
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-beta\.[1-9]\d*)?", version):
        raise SystemExit(f"{version} is not a supported stable or beta semantic version.")

    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    block = re.search(r"(?s)<!-- downloads:start -->.*?<!-- downloads:end -->", readme)
    for suffix in ("Setup-win-x64.exe", "Portable-win-x64.zip", "Docker.zip"):
        expected = f"https://github.com/Zntoff/kairos-studio/releases/download/v{version}/KairosStudio-{version}-{suffix}"
        if not block or expected not in block[0]:
            raise SystemExit("README download links must match the version. Run scripts/set-version.py.")

    if args.tag is not None and args.tag != f"v{version}":
        raise SystemExit(f"Tag {args.tag} does not match version {version}; expected v{version}.")

    if args.changelog or args.notes:
        section = changelog_section(version)
        if not section:
            raise SystemExit(f"CHANGELOG.md has no entries under ## [{version}].")
        if args.notes:
            args.notes.write_text(section + "\n", encoding="utf-8")

    print(version)


if __name__ == "__main__":
    sys.exit(main())
