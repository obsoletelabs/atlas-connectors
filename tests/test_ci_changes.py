import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import ci_changes as cc  # noqa: E402

ALL = ["emby", "jellyfin", "plex", "playnite"]
ALL_SORTED = sorted(ALL)


def pick(*files):
    return cc.classify(list(files), ALL)


def test_package_change_selects_only_that_package():
    r = pick("packages/playnite/src/Foo.cs", "packages/playnite/CHANGELOG.md")
    assert r["packages"] == ["playnite"] and r["live"] is True
    assert r["python"] is False and r["contract"] is False


def test_two_packages():
    assert pick("packages/jellyfin/a.cs", "packages/playnite/b.cs")["packages"] == ["jellyfin", "playnite"]


@pytest.mark.parametrize("path", [
    "dotnet/Atlas.Client/Client.cs", "dotnet/Atlas.Client.Tests/T.cs", "contract/fixtures/games/site.json",
    "contract/CLIENT-CONTRACT.md", "tools/atlas_testkit.py", ".github/workflows/ci.yml", "catalogues/jellyfin/manifest.json",
    "LICENSE", ".gitattributes", "Directory.Build.props",
])
def test_shared_paths_select_all(path):
    r = pick(path)
    assert r["packages"] == ALL_SORTED and r["python"] is True and r["contract"] is True


def test_unknown_path_selects_all():
    assert pick("something/new/thing.bin")["packages"] == ALL_SORTED


def test_docs_only_selects_nothing():
    r = pick("README.md", "docs/VENDOR-FACTS.md", "CONTRIBUTING.md", ".github/ISSUE_TEMPLATE/bug.yml",
             ".github/PULL_REQUEST_TEMPLATE.md")
    assert r == {"python": False, "contract": False, "live": False, "packages": []}


def test_package_readme_at_packages_root_is_docs_like():
    assert pick("packages/README.md")["packages"] == []


def test_repo_python_tests_select_python_lane_only():
    r = pick("tests/test_ci_changes.py")
    assert r["python"] is True and r["packages"] == [] and r["live"] is False


def test_docs_plus_package_still_only_package():
    assert pick("README.md", "packages/jellyfin/x.cs")["packages"] == ["jellyfin"]


def test_shared_beats_package():
    assert pick("packages/jellyfin/x.cs", "tools/ci_changes.py")["packages"] == ALL_SORTED


def test_unknown_change_set_selects_all():
    r = cc.classify(None, ALL)
    assert r["packages"] == ALL_SORTED and r["python"] and r["contract"] and r["live"]


def test_windows_separators_and_blank_lines():
    assert pick("packages\\playnite\\a.cs", "", "  ")["packages"] == ["playnite"]


def test_empty_change_set_selects_nothing():
    assert cc.classify([], ALL)["packages"] == []


def test_available_packages_includes_known_and_new(tmp_path):
    (tmp_path / "packages" / "newhost").mkdir(parents=True)
    assert cc.available_packages(tmp_path) == sorted({"newhost", *cc.KNOWN_PACKAGES})


def test_new_package_directory_is_selected_by_name():
    assert cc.classify(["packages/newhost/a.cs"], ALL)["packages"] == ["newhost"]


@pytest.mark.parametrize("tag,expected", [
    ("playnite-v0.3.1", ("package", "playnite", "0.3.1")),
    ("refs/tags/jellyfin-v1.0.0", ("package", "jellyfin", "1.0.0")),
    ("jellyfin-v1.0.0-rc.1", ("package", "jellyfin", "1.0.0-rc.1")),
    ("client-contract-v1.2", ("contract", "", "1.2")),
])
def test_release_target(tag, expected):
    r = cc.release_target(tag)
    assert (r["kind"], r["package"], r["version"]) == expected


@pytest.mark.parametrize("tag", ["v1.0.0", "playnite-0.3.1", "playnite-v1", "playnite-v1.2", "client-contract-v1",
                                 "client-contract-v1.2.3", "nope-v1.0.0", "playnite-v1.0.0.0"])
def test_bad_tags_rejected(tag):
    with pytest.raises(ValueError):
        cc.release_target(tag)


def run_cli(*args, env=None):
    import os
    return subprocess.run([sys.executable, str(Path(cc.__file__)), *args], capture_output=True, text=True,
                          env={**os.environ, "GITHUB_OUTPUT": "", **(env or {})})


def test_cli_main_ref_selects_everything():
    r = run_cli("--ref", "main")
    assert r.returncode == 0 and "python=true" in r.stdout and "playnite" in r.stdout


def test_cli_tag_ref_selects_everything():
    assert "jellyfin" in run_cli("--ref", "refs/tags/playnite-v1.0.0").stdout


def test_cli_unknown_event_selects_everything():
    r = run_cli("--event", "workflow_dispatch")
    assert "live=true" in r.stdout and "jellyfin" in r.stdout


def test_cli_tag_output_and_error():
    assert "package=playnite" in run_cli("--tag", "playnite-v0.3.1").stdout
    bad = run_cli("--tag", "bogus")
    assert bad.returncode == 2 and "not a release tag" in bad.stderr


def test_cli_writes_github_output(tmp_path):
    out = tmp_path / "out"
    run_cli("--tag", "client-contract-v1.0", env={"GITHUB_OUTPUT": str(out)})
    assert "kind=contract" in out.read_text()


def test_git_diff_integration(tmp_path):
    def git(*a):
        subprocess.run(["git", *a], cwd=tmp_path, check=True, capture_output=True)
    git("init", "-q", "-b", "main")
    git("config", "user.email", "t@example.invalid")
    git("config", "user.name", "t")
    (tmp_path / "a.txt").write_text("1")
    git("add", "."), git("commit", "-qm", "one")
    base = subprocess.run(["git", "rev-parse", "HEAD"], cwd=tmp_path, capture_output=True, text=True).stdout.strip()
    (tmp_path / "packages" / "playnite").mkdir(parents=True)
    (tmp_path / "packages" / "playnite" / "x.cs").write_text("x")
    git("add", "."), git("commit", "-qm", "two")
    head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=tmp_path, capture_output=True, text=True).stdout.strip()
    old = cc.REPOSITORY
    cc.REPOSITORY = tmp_path
    try:
        files = cc.changed_files("push", base, head)
    finally:
        cc.REPOSITORY = old
    assert files == ["packages/playnite/x.cs"]
    assert cc.changed_files("push", cc.ZERO_SHA, head) is None
    assert cc.changed_files("workflow_dispatch", base, head) is None
