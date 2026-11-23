"""Archive privacy boundaries: never redistribute a developer's personal files."""
import importlib.util
import json
from pathlib import Path
import zipfile
import pytest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("docker_package", ROOT / "scripts/package-docker.py")
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)


def fixture_tree(tmp_path, monkeypatch):
    tree = tmp_path / "source"
    for name in ("clipper/app.py", "clipper/__pycache__/private.pyc", "assets/sfx/library.json", "config/providers.json", "distribution/docker/compose.yaml", "Dockerfile", ".dockerignore", "pyproject.toml", "LICENSE", "NOTICE", ".env.example", ".env", "data/settings.ai.json", "desktop/user.key"):
        file = tree / name
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text("@VERSION@" if name.endswith("compose.yaml") else "fixture", encoding="utf-8")
    monkeypatch.setattr(packager, "ROOT", tree)
    return tree


@pytest.mark.parametrize("version", ["0.10.0", "0.11.0-beta.1"])
def test_docker_archive_excludes_personal_files_and_substitutes_version(tmp_path, monkeypatch, version):
    fixture_tree(tmp_path, monkeypatch)
    file = packager.package(tmp_path / "output", version)
    with zipfile.ZipFile(file) as archive:
        names = archive.namelist()
        assert all("__pycache__" not in name and not name.endswith(("/.env", "/settings.ai.json", "/user.key")) for name in names)
        root = f"KairosStudio-{version}-Docker/"
        assert archive.read(root + "compose.yaml") == version.encode()
        assert root + ".dockerignore" in names
        manifest = json.loads(archive.read(root + "package-manifest.json"))
        assert "assets/sfx/library.json" in manifest["files"]


@pytest.mark.parametrize("name", ["assets/leaked.key", "assets/settings.ai.json", "config/library.json", "assets/.env.local", "config/private.sqlite3"])
def test_private_files_inside_allowed_folders_block_release(tmp_path, monkeypatch, name):
    tree = fixture_tree(tmp_path, monkeypatch)
    (tree / name).write_text("synthetic-test-only", encoding="utf-8")
    with pytest.raises(ValueError, match="Private file forbidden"):
        packager.package(tmp_path / "output", "0.10.0")
