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


def _vector_record(case):
    rel = case["record"]
    base = CONTRACT if rel.startswith("vectors/") else CONTRACT / "fixtures"
    return json.loads((base / rel).read_text(encoding="utf-8"))


def _expected_date(record):
    """An independent, tiny re-statement of the rule (never invent a month or a day) to cross-check the vector file."""
    first = record.get("first_release")
    if not isinstance(first, dict):
        return None
    text, precision = str(first.get("date") or ""), first.get("precision")
    parts = text.split("-")
    if not parts[0].isdigit() or len(parts[0]) != 4 or len(parts) > 3:
        return None
    out = {"Year": int(parts[0])}
    if len(parts) > 1 and precision in ("month", "day"):
        out["Month"] = int(parts[1])
    if len(parts) > 2 and precision == "day":
        out["Day"] = int(parts[2])
    return out


def test_playnite_vectors_agree_with_their_records():
    vectors = json.loads((CONTRACT / "vectors" / "playnite.json").read_text(encoding="utf-8"))
    assert vectors["vector_version"] == 1 and vectors["base_url"].endswith("/")
    seen_names = set()
    for case in vectors["cases"]:
        assert case["name"] not in seen_names
        seen_names.add(case["name"])
        record = _vector_record(case)
        assert record["title"] == case["expect"]["Name"], case["name"]
        if "ReleaseDate" in case["expect"]:
            assert case["expect"]["ReleaseDate"] == _expected_date(record), case["name"]
        if "ReleaseDate" in case.get("expect_absent", []):
            # either no first_release or an impossible date
            assert _expected_date(record) is None or record.get("first_release", {}).get("date") == "soon", case["name"]
        assert not set(case["expect"]) & set(case.get("expect_absent", [])), case["name"]


def test_vector_records_are_synthetic_and_contain_no_secrets():
    for path in (CONTRACT / "vectors" / "records").glob("*.json"):
        text = path.read_text(encoding="utf-8").lower()
        assert "token" not in text and "secret" not in text and "password" not in text, path
        assert json.loads(text)["title"].startswith("synthetic "), path


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
    assert kit.strip_separator(args.command) == ["echo", "hi"]
    inner = kit.build_parser().parse_args(["run", "--image", "x", "--", "pytest", "--", "-k", "a"])
    assert kit.strip_separator(inner.command) == ["pytest", "--", "-k", "a"]


def test_testkit_reports_missing_docker(monkeypatch, capsys):
    monkeypatch.setattr(kit, "docker_available", lambda: False)
    assert kit.main(["up", "--image", "x"]) == 1
    assert "Docker is not reachable" in capsys.readouterr().err
