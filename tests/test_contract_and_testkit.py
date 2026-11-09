"""Offline tests for contract pinning, fixtures, vectors and the pure parts of the Atlas testkit."""
import json
import shutil
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import atlas_testkit as kit  # noqa: E402
import contract_pin as pin  # noqa: E402

CONTRACT = ROOT / "contract"


# --- the committed contract directory ---------------------------------------------------------------------------

def test_committed_pins_hold():
    assert pin.check() == []


def test_every_json_fixture_parses():
    for path in CONTRACT.rglob("*.json"):
        json.loads(path.read_text(encoding="utf-8"))


def test_resolve_fixture_matches_the_digest_atlas_pins():
    fixture = CONTRACT / "fixtures" / "resolve" / "resolve-contract-v1.json"
    assert pin.digest(fixture) == (fixture.parent / (fixture.name + ".sha256")).read_text().split()[0]


def test_release_check_refuses_scaffold_stand_ins():
    problems = pin.placeholders()
    assert any("placeholder" in p for p in problems)
    assert any("scaffold" in p for p in problems)
    assert any("hand-written draft" in p for p in problems)


def test_fixtures_hold_no_credentials():
    for path in CONTRACT.rglob("*.json"):
        text = path.read_text(encoding="utf-8").lower()
        assert "bearer " not in text and '"token"' not in text and "secret" not in text, path


def test_playnite_vectors_agree_with_their_records():
    vectors = json.loads((CONTRACT / "vectors" / "playnite.json").read_text(encoding="utf-8"))
    assert vectors["vector_version"] == 1
    for case in vectors["cases"]:
        record = json.loads((CONTRACT / "fixtures" / case["record"]).read_text(encoding="utf-8"))
        assert record["title"] == case["expect"]["Name"]
        first = record["first_release"]
        assert first["precision"] == "day"
        year, month, day = (int(x) for x in first["date"].split("-"))
        assert case["expect"]["ReleaseDate"] == {"Year": year, "Month": month, "Day": day}


def test_forward_compat_record_really_has_unknown_members():
    record = json.loads((CONTRACT / "fixtures" / "forward-compat" / "record-unknown-members.json").read_text("utf-8"))
    assert "a_field_added_in_a_later_minor" in record and record["platform_family"] == "holographic"


# --- pin mechanics on a scratch copy ----------------------------------------------------------------------------

@pytest.fixture
def scratch(tmp_path):
    target = tmp_path / "contract"
    shutil.copytree(CONTRACT, target)
    return target


def test_pin_detects_change_addition_and_removal(scratch):
    assert pin.check(scratch) == []
    (scratch / "fixtures" / "games" / "site.json").write_text("{}\n", encoding="utf-8")
    (scratch / "fixtures" / "games" / "new.json").write_text("{}\n", encoding="utf-8")
    (scratch / "vectors" / "playnite.json").unlink()
    problems = pin.check(scratch)
    assert "changed: fixtures/games/site.json" in problems
    assert "not pinned: fixtures/games/new.json" in problems
    assert "missing: vectors/playnite.json" in problems


def test_pin_write_then_check_is_clean(scratch):
    (scratch / "fixtures" / "games" / "new.json").write_text("{}\n", encoding="utf-8")
    pin.write(scratch)
    assert pin.check(scratch) == []


def test_pin_is_line_ending_independent(scratch):
    target = scratch / "fixtures" / "games" / "site.json"
    target.write_bytes(target.read_bytes().replace(b"\n", b"\r\n"))
    assert pin.check(scratch) == []


def test_pin_reports_a_missing_sums_file(scratch):
    (scratch / "SHA256SUMS").unlink()
    assert pin.check(scratch) == ["SHA256SUMS is missing"]


def test_sidecar_digest_disagreement_is_reported(scratch):
    (scratch / "fixtures" / "resolve" / "resolve-contract-v1.json.sha256").write_text("0" * 64 + "  x\n")
    pin.write(scratch)
    assert any("disagrees" in p for p in pin.check(scratch))


def test_release_check_passes_once_stand_ins_are_replaced(scratch):
    (scratch / "openapi-client-v1.json").write_text('{"openapi": "3.1.0", "paths": {}}\n', encoding="utf-8")
    (scratch / "fixtures" / "VERSION").write_text("1.0.0\n", encoding="utf-8")
    shutil.rmtree(scratch / "fixtures" / "books")
    assert pin.placeholders(scratch) == []


def test_pin_cli(capsys):
    assert pin.main(["check"]) == 0
    assert pin.main(["release-check"]) == 1
    assert "contract pins hold" in capsys.readouterr().out


# --- the testkit, without Docker or a network --------------------------------------------------------------------

def test_testkit_seed_mapping_nests_facts_and_posts_in_order(monkeypatch):
    calls = []

    class Fake(kit.Atlas):
        def __init__(self):
            self.n = 0

        def ok(self, method, path, token=None, body=None):
            calls.append((method, path, body))
            if path == "/observations":
                self.n += 1
                return {"results": [{"atlas": {"canonical_id": f"atlas:{self.n}"}}]}
            return {}

    ids = kit.seed_resolve_fixture(Fake(), "admin")
    assert ids["portal"] == "atlas:1" and len(ids) == 11
    first = calls[0][2]
    assert first["media_type"] == "game" and first["observations"][0]["facts"] == {"developer": "Valve"}
    assert "developer" not in first["observations"][0] and "name" not in first["observations"][0]
    merges = [c for c in calls if c[1].endswith("/merge")]
    assert [(m[1], m[2]) for m in merges] == [
        (f"/games/{ids['merge_a']}/merge", {"into": ids["merge_b"]}),
        (f"/games/{ids['merge_b']}/merge", {"into": ids["merge_c"]})]


def test_testkit_books_skipped_loudly_without_a_book_type(capsys):
    class NoBooks(kit.Atlas):
        def __init__(self):
            pass

        def ok(self, *a, **k):
            return {"media_types": ["game"]}

    assert kit.seed_books(NoBooks(), "admin") == {}
    assert "NOT seeded" in capsys.readouterr().err


def test_testkit_cli_requires_a_command_and_a_source(capsys):
    with pytest.raises(SystemExit):
        kit.build_parser().parse_args(["up"])
    args = kit.build_parser().parse_args(["run", "--image", "x", "--", "echo", "hi"])
    assert [c for c in args.command if c != "--"] == ["echo", "hi"]


def test_testkit_reports_missing_docker(monkeypatch, capsys):
    monkeypatch.setattr(kit, "docker_available", lambda: False)
    assert kit.main(["up", "--image", "x"]) == 1
    assert "Docker is not reachable" in capsys.readouterr().err
