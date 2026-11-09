"""Offline checks of the Jellyfin package metadata, packaging script and catalogue generator (no dotnet, no Jellyfin needed)."""
from __future__ import annotations

import hashlib
import importlib.util
import json
import re
import sys
import zipfile
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
PKG = ROOT / "packages" / "jellyfin"


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    sys.modules[name] = mod
    spec.loader.exec_module(mod)
    return mod


pack = _load("jellyfin_pack", PKG / "pack.py")
gen = _load("gen_catalogues", ROOT / "tools" / "gen_catalogues.py")


def test_build_yaml_files_match_abis_json_and_the_template_fields():
    abis = pack.load_abis()
    assert [a["key"] for a in abis] == ["10.11", "12"]
    for abi in abis:
        cfg = pack.build_config(abi)
        assert cfg["framework"] == abi["framework"]
        for key in ("name", "guid", "version", "targetAbi", "framework", "overview", "description", "category", "owner", "artifacts", "changelog"):
            assert cfg.get(key), f"{key} missing from the build yaml of ABI {abi['key']}"
        assert cfg["artifacts"] == ["Jellyfin.Plugin.Atlas.dll"]


def test_versions_agree_in_csproj_build_yaml_changelog_and_readme():
    semver = pack.csproj_version()
    assert semver == "0.1.0"
    for abi in pack.load_abis():
        cfg = pack.build_config(abi)
        assert cfg["version"] == f"{semver}.{abi['version_suffix']}"
    assert re.search(r"^## " + re.escape(semver), (PKG / "CHANGELOG.md").read_text(encoding="utf-8"), re.M)
    assert semver in (PKG / "README.md").read_text(encoding="utf-8")


def test_the_guid_in_build_yaml_is_the_one_the_code_returns():
    cs = (PKG / "Jellyfin.Plugin.Atlas" / "Plugin.cs").read_text(encoding="utf-8")
    guid = re.search(r'PluginId = Guid\.Parse\("([0-9a-f-]{36})"\)', cs).group(1)
    for abi in pack.load_abis():
        assert pack.build_config(abi)["guid"] == guid


def test_csproj_pins_the_packages_and_frameworks_that_abis_json_names():
    text = (PKG / "Jellyfin.Plugin.Atlas" / "Jellyfin.Plugin.Atlas.csproj").read_text(encoding="utf-8")
    tests = (PKG / "Jellyfin.Plugin.Atlas.Tests" / "Jellyfin.Plugin.Atlas.Tests.csproj").read_text(encoding="utf-8")
    for abi in pack.load_abis():
        key = abi["key"]
        assert re.search(rf"<PropertyGroup Condition=\"'\$\(JellyfinAbi\)' == '{re.escape(key)}'\">\s*<PluginTfm>{re.escape(abi['framework'])}</PluginTfm>\s*"
                         rf"<JellyfinPackages>{re.escape(abi['jellyfin_packages'])}</JellyfinPackages>", text)
        assert f"== '{key}'\">{abi['jellyfin_packages']}<" in tests
    assert 'ExcludeAssets="runtime"' in text           # the server provides the Jellyfin assemblies


def test_readme_has_the_compatibility_table():
    readme = (PKG / "README.md").read_text(encoding="utf-8")
    assert "| Jellyfin" in readme and "targetAbi" in readme and "min_client_api" in readme
    for abi in pack.load_abis():
        assert abi["target_abi"] in readme and abi["jellyfin_packages"] in readme


def test_config_page_is_an_embedded_resource_and_never_asks_for_the_token_back():
    csproj = (PKG / "Jellyfin.Plugin.Atlas" / "Jellyfin.Plugin.Atlas.csproj").read_text(encoding="utf-8")
    assert "EmbeddedResource Include=\"Configuration\\configPage.html\"" in csproj
    html = (PKG / "Jellyfin.Plugin.Atlas" / "Configuration" / "configPage.html").read_text(encoding="utf-8")
    assert "getUrl('Atlas/' + path)" in html and "getPluginConfiguration" not in html


def _fake_zip(path: Path, version: str, abi: str, dll: bytes = b"MZ\0\0") -> Path:
    cfg = pack.build_config({"target_abi": abi})
    meta = {k: cfg[k] for k in ("guid", "name", "description", "overview", "owner", "category", "changelog")}
    meta.update(version=version, targetAbi=abi, timestamp="2026-01-01T00:00:00Z")
    with zipfile.ZipFile(path, "w") as z:
        z.writestr(pack.DLL, dll)
        z.writestr("meta.json", json.dumps(meta))
    return path


def test_verify_accepts_a_good_zip_and_rejects_bad_ones(tmp_path):
    good = _fake_zip(tmp_path / "atlas_0.1.0.0.zip", "0.1.0.0", "10.11.0.0")
    assert pack.verify(good) == []

    extra = tmp_path / "atlas_0.1.0.0.zip"
    with zipfile.ZipFile(extra, "a") as z:
        z.writestr("System.Text.Json.dll", b"MZ")
    assert any("unexpected contents" in e for e in pack.verify(extra))

    bad_name = _fake_zip(tmp_path / "other.zip", "0.1.0.0", "10.11.0.0")
    assert any("file name" in e for e in pack.verify(bad_name))
    not_dll = _fake_zip(tmp_path / "atlas_0.1.0.1.zip", "0.1.0.1", "12.0.0.0", dll=b"text")
    assert any("not a .NET assembly" in e for e in pack.verify(not_dll))
    junk = tmp_path / "junk.zip"
    junk.write_bytes(b"nope")
    assert pack.verify(junk) == ["not a zip file"]


# ---- the catalogue generator ---------------------------------------------------------------------------------------

BASE = "https://example.invalid/releases/download/jellyfin-v0.1.0"


@pytest.fixture
def artefacts(tmp_path):
    d = tmp_path / "dist"
    d.mkdir()
    _fake_zip(d / "atlas_0.1.0.0.zip", "0.1.0.0", "10.11.0.0")
    _fake_zip(d / "atlas_0.1.0.1.zip", "0.1.0.1", "12.0.0.0")
    return d


def test_manifest_has_the_real_jellyfin_shape_and_checksums_computed_from_the_zips(artefacts):
    manifest = gen.build_manifest(artefacts, BASE, None)
    assert len(manifest) == 1
    plugin = manifest[0]
    assert set(plugin) == {"guid", "name", "description", "overview", "owner", "category", "versions"}
    assert [v["version"] for v in plugin["versions"]] == ["0.1.0.1", "0.1.0.0"]          # newest first
    for v in plugin["versions"]:
        assert set(v) == {"version", "changelog", "targetAbi", "timestamp", "sourceUrl", "checksum"}
        name = v["sourceUrl"].rsplit("/", 1)[-1]
        assert v["checksum"] == hashlib.md5((artefacts / name).read_bytes()).hexdigest()
        assert re.fullmatch(r"[0-9a-f]{32}", v["checksum"])
    assert {v["targetAbi"] for v in plugin["versions"]} == {"10.11.0.0", "12.0.0.0"}
    assert gen.verify(manifest, artefacts, require_all=True) == []


def test_a_rebuilt_or_tampered_artefact_fails_the_checksum_test(artefacts):
    manifest = gen.build_manifest(artefacts, BASE, None)
    _fake_zip(artefacts / "atlas_0.1.0.0.zip", "0.1.0.0", "10.11.0.0", dll=b"MZ\0\0 different build")
    errors = gen.verify(manifest, artefacts)
    assert len(errors) == 1 and "checksum" in errors[0] and "0.1.0.0" in errors[0]


def test_a_missing_artefact_only_fails_when_all_are_required(artefacts):
    manifest = gen.build_manifest(artefacts, BASE, None)
    (artefacts / "atlas_0.1.0.1.zip").unlink()
    assert gen.verify(manifest, artefacts) == []
    assert any("not found" in e for e in gen.verify(manifest, artefacts, require_all=True))


def test_a_new_release_keeps_the_versions_of_earlier_ones(artefacts, tmp_path):
    first = gen.build_manifest(artefacts, BASE, None)
    newer = tmp_path / "new"
    newer.mkdir()
    _fake_zip(newer / "atlas_0.2.0.0.zip", "0.2.0.0", "10.11.0.0")
    merged = gen.build_manifest(newer, BASE.replace("0.1.0", "0.2.0"), first)
    assert [v["version"] for v in merged[0]["versions"]] == ["0.2.0.0", "0.1.0.1", "0.1.0.0"]
    again = gen.build_manifest(newer, BASE.replace("0.1.0", "0.2.0"), merged)       # re-running a release does not duplicate it
    assert len(again[0]["versions"]) == 3


def test_cli_writes_the_file_and_verify_command_agrees(artefacts, tmp_path):
    out = tmp_path / "cat" / "manifest.json"
    assert gen.main(["jellyfin", "--artifacts", str(artefacts), "--download-base", BASE, "--out", str(out)]) == 0
    assert json.loads(out.read_text(encoding="utf-8"))[0]["name"] == "Atlas"
    assert gen.main(["verify", "--manifest", str(out), "--artifacts", str(artefacts), "--require-all"]) == 0


def test_the_committed_workflow_publishes_through_the_generator_not_by_hand():
    wf = (ROOT / ".github" / "workflows" / "release-jellyfin.yml").read_text(encoding="utf-8")
    assert "gen_catalogues.py" in wf and "pack.py" in wf
    assert "Refusing to publish an empty release" not in wf
