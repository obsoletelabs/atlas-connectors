"""Live lane: client-api 1.0 against a real Atlas seeded with ``atlas seed --fixture connectors-v1``.

Run through the testkit (see test_resolve_contract_live.py). Skipped without a live Atlas, and skipped (with the
reason) when that Atlas predates client-api 1.0, so the CLI seed did not run. The recorded answers in
contract/fixtures come from the same seed; these tests check the shapes a connector relies on, not byte equality
(ids and timestamps differ per run).
"""
import json
import os
import urllib.error
import urllib.request
from pathlib import Path

import pytest

URL = os.environ.get("ATLAS_URL")
pytestmark = pytest.mark.skipif(not URL, reason="needs a live Atlas: run through tools/atlas_testkit.py run")


@pytest.fixture(scope="module")
def ids():
    seeded = json.loads(Path(os.environ["ATLAS_SEEDED"]).read_text())
    if "lanternfall" not in seeded:
        pytest.skip("this Atlas has no `atlas seed --fixture connectors-v1` (older than client-api 1.0)")
    return seeded


def call(method, path, body=None, headers=None, token=True):
    sent = {"Content-Type": "application/json", "User-Agent": "atlas-connector-other/0.0.0-live", **(headers or {})}
    if token:
        sent["Authorization"] = "Bearer " + os.environ["ATLAS_READ_TOKEN"]
    request = urllib.request.Request(URL + "/api/v2" + path, method=method, headers=sent,
                                     data=json.dumps(body).encode() if body is not None else None)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            raw = response.read()
            return response.status, dict(response.headers), json.loads(raw) if raw else None
    except urllib.error.HTTPError as exc:
        raw = exc.read()
        return exc.code, dict(exc.headers), json.loads(raw) if raw else None


def test_site_offers_client_api_1(ids):
    status, _, site = call("GET", "/site", token=False)
    assert status == 200
    api = site["client_api"]
    assert api["version"].split(".")[0] == "1" and api["min_supported"] == "1.0"
    assert {"books", "pairing", "etag", "batch"} <= set(api["capabilities"])


def test_record_etag_and_304(ids):
    status, headers, record = call("GET", f"/games/{ids['lanternfall']}")
    assert status == 200 and record["id"] == ids["lanternfall"]
    etag = headers.get("ETag") or headers.get("etag")
    assert etag == f'"{record["id"]}-{record["version"]}"'
    status, _, body = call("GET", f"/games/{ids['lanternfall']}", headers={"If-None-Match": etag})
    assert status == 304 and body is None


def test_merged_id_answers_the_survivor_and_its_etag(ids):
    status, headers, record = call("GET", f"/games/{ids['lanternfall-duplicate']}")
    assert status == 200
    assert record["id"] == ids["lanternfall"] and record["redirected_from"] == ids["lanternfall-duplicate"]
    assert (headers.get("ETag") or headers.get("etag")).startswith(f'"{ids["lanternfall"]}-')


def test_batch_shape_in_request_order(ids):
    wanted = [ids["lanternfall"], ids["lanternfall-duplicate"], "atlas:00000000000000000000000000", ids["copper-tide"]]
    status, _, body = call("POST", "/games/batch", {"ids": wanted})
    assert status == 200
    results = body["results"]
    assert [r["requested"] for r in results] == wanted
    assert [r["status"] for r in results] == ["ok", "ok", "not_found", "ok"]
    assert results[1]["game"]["id"] == ids["lanternfall"] and results[2]["game"] is None


def test_records_alias_answers_like_games(ids):
    _, _, games = call("GET", f"/games/{ids['the-lamp-keeper']}")
    status, _, records = call("GET", f"/records/{ids['the-lamp-keeper']}")
    assert status == 200 and records == games
    status, _, batch = call("POST", "/records/batch", {"ids": [ids["the-lamp-keeper"]]})
    assert status == 200 and batch["results"][0]["status"] == "ok"


def test_book_shape(ids):
    _, _, book = call("GET", f"/games/{ids['the-lamp-keeper']}")
    assert book["media_type"] == "book"
    assert {a["role"] for a in book["authors"]} >= {"author"}
    assert all(e.get("format") for e in book["editions"]) and any(e.get("isbn_13") for e in book["editions"])
    series = [m for m in book["relations"]["members_of"] if m["type"] == "series_member"]
    assert series and series[0]["id"] == ids["series-lamp-cycle"]


def test_artwork_primary_by_role(ids):
    status, _, art = call("GET", f"/games/{ids['lanternfall']}/artwork")
    assert status == 200
    assert set(art["primary_by_role"]) == {"cover", "background", "icon", "logo"}
    known = {a["id"] for a in art["results"]}
    assert all(v is None or v in known for v in art["primary_by_role"].values())


def test_pairing_start_and_poll_are_public_and_always_200(ids):
    status, _, started = call("POST", "/client/pair", {"label": "atlas-connectors live lane"}, token=False)
    assert status == 200
    assert started["verify_url_complete"].endswith("?code=" + started["code"])
    assert started["qr_svg"].lstrip().startswith("<") and started["interval"] >= 5
    status, _, poll = call("POST", "/client/pair/poll", {"poll_token": started["poll_token"]}, token=False)
    assert status == 200 and poll["status"] in {"pending", "slow_down"}
    status, _, unknown = call("POST", "/client/pair/poll", {"poll_token": "not-a-real-poll-token"}, token=False)
    assert status == 200 and unknown["status"] == "expired"
    status, _, _ = call("POST", "/client/pair", {"label": "x", "client": "jellyfin"}, token=False)
    assert status == 422  # PairBody refuses unknown members: the connectors send `label` only


def test_wrong_token_is_401_even_for_public_paths(ids):
    status, _, _ = call("GET", f"/games/{ids['lanternfall']}", headers={"Authorization": "Bearer wrong"}, token=False)
    assert status == 401
