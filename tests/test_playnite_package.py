"""Offline checks of the Playnite package metadata and packaging script (no dotnet, no Playnite needed)."""
from __future__ import annotations

import importlib.util
import re
import zipfile
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
PKG = ROOT / "packages" / "playnite"
spec = importlib.util.spec_from_file_location("playnite_pack", PKG / "pack.py")
pack = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pack)

GOOD_YAML = (PKG / "extension.yaml").read_text(encoding="utf-8")


def test_manifest_has_every_required_key_and_a_stable_id():
    m = pack.read_manifest(GOOD_YAML)
    assert pack.check_manifest(m) == []
    assert m["Type"] == "MetadataProvider" and m["Module"] == "Atlas.Playnite.dll"
    # the id embeds the plugin GUID that the C# code returns from Plugin.Id
    cs = (PKG / "Atlas.Playnite" / "AtlasMetadataPlugin.cs").read_text(encoding="utf-8")
    guid = re.search(r'PluginId = new Guid\("([0-9a-f-]{36})"\)', cs).group(1)
    assert m["Id"] == "Atlas_Metadata_" + guid.replace("-", "")


def test_versions_agree_in_manifest_project_changelog_and_readme():
    m = pack.read_manifest(GOOD_YAML)
    assert m["Version"] == pack.csproj_version() == pack.changelog_version() == "0.1.0"
    assert m["Version"] in (PKG / "README.md").read_text(encoding="utf-8")


def test_readme_has_the_compatibility_table():
    text = (PKG / "README.md").read_text(encoding="utf-8")
    assert "| Playnite SDK" in text and "min_client_api" in text and "6.18.0" in text


@pytest.mark.parametrize("change,expect", [
    ({"Version": "one"}, "Version"),
    ({"Type": "Plugin"}, "Type"),
    ({"Id": "has space"}, "Id"),
    ({"Module": ""}, "Module"),
])
def test_manifest_rules_reject_bad_values(change, expect):
    m = pack.read_manifest(GOOD_YAML)
    m.update(change)
    assert any(expect in e for e in pack.check_manifest(m))


def _zip(path: Path, files: dict[str, bytes]) -> Path:
    with zipfile.ZipFile(path, "w") as z:
        for name, data in files.items():
            z.writestr(name, data)
    return path


def _good_files():
    return {"extension.yaml": GOOD_YAML.encode(), "Atlas.Playnite.dll": b"MZ" + b"\0" * 10, "icon.png": b"x"}


def test_verify_accepts_a_good_package(tmp_path):
    assert pack.verify(_zip(tmp_path / "a.pext", _good_files())) == []


@pytest.mark.parametrize("mutate,expect", [
    (lambda f: f.pop("extension.yaml"), "extension.yaml"),
    (lambda f: f.pop("Atlas.Playnite.dll"), "Module"),
    (lambda f: f.update({"Newtonsoft.Json.dll": b"MZ"}), "must not be shipped"),
    (lambda f: f.update({"Playnite.SDK.dll": b"MZ"}), "must not be shipped"),
    (lambda f: f.update({"../evil.txt": b"x"}), "unsafe"),
    (lambda f: f.update({"Atlas.Playnite.dll": b"not an assembly"}), "not a Windows assembly"),
    (lambda f: f.pop("icon.png"), "Icon"),
])
def test_verify_rejects_bad_packages(tmp_path, mutate, expect):
    files = _good_files()
    mutate(files)
    errors = pack.verify(_zip(tmp_path / "b.pext", files))
    assert any(expect in e for e in errors), errors


def test_pack_from_a_fake_build_is_reproducible_and_checksummed(tmp_path):
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    (bin_dir / "Atlas.Playnite.dll").write_bytes(b"MZ" + b"\1" * 64)
    (bin_dir / "extension.yaml").write_bytes(GOOD_YAML.encode())
    (bin_dir / "icon.png").write_bytes(b"png")
    (bin_dir / "Playnite.SDK.dll").write_bytes(b"MZ-sdk")        # build output the package must NOT take
    (bin_dir / "Atlas.Playnite.pdb").write_bytes(b"pdb")
    first = pack.pack(tmp_path / "out1", bin_dir)
    second = pack.pack(tmp_path / "out2", bin_dir)
    assert first.read_bytes() == second.read_bytes()
    with zipfile.ZipFile(first) as z:
        assert sorted(z.namelist()) == ["Atlas.Playnite.dll", "CHANGELOG.md", "LICENSE", "extension.yaml", "icon.png"]
    sums = (first.parent / (first.name + ".sha256")).read_text(encoding="utf-8").split()
    import hashlib
    assert sums[0] == hashlib.sha256(first.read_bytes()).hexdigest() and sums[1] == first.name


def test_pack_refuses_an_unbuilt_tree(tmp_path):
    with pytest.raises(SystemExit) as e:
        pack.pack(tmp_path / "o", tmp_path / "empty")
    assert "not built" in str(e.value)
