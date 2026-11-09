"""Live lane: the C4 resolve contract fixture, replayed against a real Atlas.

Run through the testkit, which seeds the fixture and exports ATLAS_URL, ATLAS_READ_TOKEN and ATLAS_SEEDED::

    python tools/atlas_testkit.py run --image atlas-testkit:local -- python -m pytest tests/live -q

Without those variables the module is skipped (the default no-network unit run).
"""
import json
import os
import urllib.request
from pathlib import Path

import pytest

URL = os.environ.get("ATLAS_URL")
pytestmark = pytest.mark.skipif(not URL, reason="needs a live Atlas: run through tools/atlas_testkit.py run")

FIXTURE = Path(__file__).resolve().parents[2] / "contract" / "fixtures" / "resolve" / "resolve-contract-v1.json"
SPEC = json.loads(FIXTURE.read_text(encoding="utf-8"))
# The Atlas image under test may predate part of the contract; name a case here (with the reason) to xfail it.
KNOWN_GAPS: dict[str, str] = {}


def substitute(value, ids):
    if isinstance(value, dict):
        return {k: substitute(v, ids) for k, v in value.items()}
    if isinstance(value, list):
        return [substitute(v, ids) for v in value]
    if isinstance(value, str) and value.startswith("$"):
        return ids[value[1:]]
    return value


@pytest.fixture(scope="module")
def ids():
    return json.loads(Path(os.environ["ATLAS_SEEDED"]).read_text())


def call(path, body=None):
    request = urllib.request.Request(
        URL + "/api/v2" + path, data=json.dumps(body).encode() if body is not None else None,
        method="POST" if body is not None else "GET",
        headers={"Authorization": "Bearer " + os.environ["ATLAS_READ_TOKEN"], "Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.status, json.loads(response.read())


def test_site_is_public_and_names_the_contract():
    with urllib.request.urlopen(URL + "/api/v2/site", timeout=10) as response:
        site = json.loads(response.read())
    assert site["contract"] == 1 and "game" in site["media_types"]


def test_read_token_cannot_write():
    import urllib.error
    with pytest.raises(urllib.error.HTTPError) as err:
        call("/tokens", {"name": "x", "scopes": ["admin"]})
    assert err.value.code in {401, 403}


@pytest.mark.parametrize("case", SPEC["cases"], ids=[c["name"] for c in SPEC["cases"]])
def test_resolve_case(case, ids):
    if case["name"] in KNOWN_GAPS:
        pytest.xfail(KNOWN_GAPS[case["name"]])
    request = substitute(case["request"], ids)
    status, body = call("/resolve", {"items": [request], "include_view": True})
    assert status == 200
    got = body["results"][0]
    want = substitute(case["expect"], ids)
    for key in ("reason", "tier", "method", "canonical_id"):
        assert got[key] == want[key], (key, got)
    assert got["assigned"] is (want["canonical_id"] is not None)
    if "redirected_from" in want:
        assert got["redirected_from"] == want["redirected_from"]
    if "game_title" in want:
        assert got["game"]["id"] == want["canonical_id"] and got["game"]["title"] == want["game_title"]
    if want["canonical_id"] is None:
        assert got["game"] is None
    if want["reason"] == "conflict":
        assert got["conflict"]["own"] == want["conflict_own"]
        assert sorted(got["conflict"]["canonical_ids"]) == sorted(want["conflict_ids"])
    else:
        assert got["conflict"] is None
