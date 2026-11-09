#!/usr/bin/env python3
"""Start a real Atlas, seed synthetic fixtures, mint a read token (CONTRACT-C15 section 12, "live Atlas lane").

Standard library only. Needs Docker. Everything it creates is local and synthetic: the container binds
127.0.0.1 on a free port, the bootstrap admin token is generated fresh for every run, and the container is
removed afterwards (unless ``--keep``).

Sub-commands::

    atlas_testkit.py up     --atlas-source ../unnamed_tracking_metadata    # build image, start, print JSON
    atlas_testkit.py up     --image atlas-testkit:local --keep
    atlas_testkit.py seed   --url URL --bootstrap TOKEN                    # seed fixtures, print JSON
    atlas_testkit.py token  --url URL --bootstrap TOKEN                    # mint a read-only token
    atlas_testkit.py record --url URL --token TOKEN --ids ids.json --out DIR
    atlas_testkit.py down   --container NAME
    atlas_testkit.py run    --image IMG -- <command...>                    # up + seed + token + command + down

``run`` exports ATLAS_URL, ATLAS_READ_TOKEN and ATLAS_SEEDED (path of a JSON file mapping seed names to
canonical ids) to the command. Nothing here is a lasting secret: the values die with the container.

STATUS (scaffold, PR 1). Implemented against Atlas as it exists today (the ``v2`` line):
  * build / start / health wait / remove, via the Docker CLI;
  * seeding the games of ``contract/fixtures/resolve/resolve-contract-v1.json`` through the public HTTP API
    (``POST /observations`` with an admin token, ``POST /games/{id}/merge``);
  * minting a ``read`` token (``POST /tokens``);
  * recording ``/site``, ``/games``, ``/games/{id}``, ``/games/{id}/relations`` and ``/resolve`` responses.
STUBBED until Atlas ships them (CONTRACT-C15 section 14.1, PR A24 and the book PRs A21-A22):
  * ``atlas seed --fixture connectors-v1`` (``seed_with_atlas_cli`` reports "not available" and the HTTP
    seed is used instead);
  * books: seeded only when ``/site`` lists a ``book`` media type, otherwise skipped loudly;
  * ``client_api`` in ``/site``, ``POST /games/batch``, ``If-None-Match`` and pairing: not recorded.
"""
from __future__ import annotations

import argparse
import json
import os
import secrets
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RESOLVE_FIXTURE = ROOT / "contract" / "fixtures" / "resolve" / "resolve-contract-v1.json"
SOURCE_SCOPE = "connectors-testkit"
FACT_KEYS = ("developer", "publisher", "series")
TIMEOUT = 30


class TestkitError(RuntimeError):
    __test__ = False  # not a pytest class


# --- docker ----------------------------------------------------------------------------------------------------

def docker(*args: str, check: bool = True, **options) -> subprocess.CompletedProcess:
    return subprocess.run(["docker", *args], check=check, text=True, **options)


def docker_available() -> bool:
    try:
        return subprocess.run(["docker", "info"], capture_output=True).returncode == 0
    except FileNotFoundError:
        return False


def free_port() -> int:
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def build_image(source: Path, tag: str) -> str:
    docker("build", "-t", tag, str(source.resolve()))
    return tag


def up(image: str) -> dict[str, str]:
    """Start an Atlas container (role ``local``) and wait until it is healthy."""
    if not docker_available():
        raise TestkitError("Docker is not reachable. Start Docker and retry; this lane has no mocked fallback.")
    port = free_port()
    url = f"http://127.0.0.1:{port}"
    bootstrap = secrets.token_urlsafe(48)
    name = f"atlas-testkit-{os.getpid()}-{port}"
    docker("run", "-d", "--name", name, "-p", f"127.0.0.1:{port}:8000",
           "-e", "ATLAS_ROLE=local", "-e", f"ATLAS_PUBLIC_URL={url}", "-e", "ATLAS_STEAM_ENABLED=false",
           "-e", f"ATLAS_BOOTSTRAP_ADMIN_TOKEN={bootstrap}", image, capture_output=True)
    try:
        wait_healthy(url, name)
    except BaseException:
        down(name)
        raise
    return {"url": url, "bootstrap": bootstrap, "container": name, "image": image}


def down(container: str) -> None:
    docker("rm", "-f", container, check=False, capture_output=True)


def wait_healthy(url: str, container: str, seconds: int = 120) -> None:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(url + "/api/v2/health", timeout=3) as response:
                if response.status == 200:
                    return
        except OSError:
            pass
        time.sleep(1)
    logs = docker("logs", "--tail", "60", container, check=False, capture_output=True)
    raise TestkitError(f"Atlas did not become healthy within {seconds}s:\n{logs.stdout}{logs.stderr}")


# --- HTTP ------------------------------------------------------------------------------------------------------

class Atlas:
    def __init__(self, url: str) -> None:
        self.base = url.rstrip("/") + "/api/v2"

    def call(self, method: str, path: str, token: str | None = None, body=None) -> tuple[int, object]:
        data = json.dumps(body).encode() if body is not None else None
        headers = {"Accept": "application/json"}
        if data:
            headers["Content-Type"] = "application/json"
        if token:
            headers["Authorization"] = "Bearer " + token
        request = urllib.request.Request(self.base + path, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=TIMEOUT) as response:
                raw = response.read()
                return response.status, json.loads(raw) if raw else None
        except urllib.error.HTTPError as exc:
            raw = exc.read().decode("utf-8", "replace")
            try:
                return exc.code, json.loads(raw)
            except ValueError:
                return exc.code, raw

    def ok(self, method: str, path: str, token: str | None = None, body=None):
        status, payload = self.call(method, path, token, body)
        if not 200 <= status < 300:
            raise TestkitError(f"{method} {path} -> {status} {str(payload)[:300]}")
        return payload


def mint_token(atlas: Atlas, bootstrap: str, name: str, scopes: list[str]) -> str:
    return atlas.ok("POST", "/tokens", bootstrap, {"name": name, "scopes": scopes})["token"]


def mint_read_token(url: str, bootstrap: str, name: str = "connectors-testkit-read") -> str:
    """A token with the ``read`` scope only: what a connector is given."""
    return mint_token(Atlas(url), bootstrap, name, ["read"])


# --- seeding ---------------------------------------------------------------------------------------------------

def seed_with_atlas_cli(container: str | None) -> bool:
    """STUB. ``atlas seed --fixture connectors-v1`` is added by Atlas PR A24 (CONTRACT-C15 section 14.1).

    Returns False when the running Atlas has no such command, so the caller falls back to the HTTP seed. When
    A24 lands, replace the body with ``docker exec <container> atlas seed --fixture connectors-v1`` and make it
    the primary path; the HTTP seed then only remains for older Atlas releases in the matrix.
    """
    if not container:
        return False
    probe = docker("exec", container, "atlas", "seed", "--help", check=False, capture_output=True)
    if probe.returncode != 0:
        print("note: `atlas seed` is not available in this Atlas; using the HTTP seed", file=sys.stderr)
        return False
    raise NotImplementedError("`atlas seed` now exists: wire up --fixture connectors-v1 here (A24)")


def seed_resolve_fixture(atlas: Atlas, admin: str, fixture: Path = RESOLVE_FIXTURE) -> dict[str, str]:
    """Ingest the seeds of the C4 resolve contract (games only) and apply its merges. Returns name -> canonical id."""
    spec = json.loads(fixture.read_text(encoding="utf-8"))
    ids: dict[str, str] = {}
    for seed in spec["seeds"]:
        observation = {k: v for k, v in seed.items() if k not in {"name", "source", *FACT_KEYS}}
        facts = {k: seed[k] for k in FACT_KEYS if k in seed}
        if facts:  # the fixture is flat; the wire format nests these under `facts`
            observation["facts"] = facts
        result = atlas.ok("POST", "/observations", admin, {
            "connection": {"source_kind": "plugin", "source": seed["source"], "source_scope": SOURCE_SCOPE},
            "media_type": "game", "observations": [observation]})
        ids[seed["name"]] = result["results"][0]["atlas"]["canonical_id"]
    for source, target in spec.get("merges", []):
        atlas.ok("POST", f"/games/{ids[source.lstrip('$')]}/merge", admin, {"into": ids[target.lstrip('$')]})
    return ids


def seed_books(atlas: Atlas, admin: str) -> dict[str, str]:
    """STUB until Atlas has a ``book`` media type (A21). Skips loudly when it is missing."""
    site = atlas.ok("GET", "/site")
    if "book" not in site.get("media_types", []):
        print("note: this Atlas has no `book` media type; book fixtures were NOT seeded", file=sys.stderr)
        return {}
    # Draft shape only: the real seed (authors, ISBN, editions, series) comes with `atlas seed` in A24.
    result = atlas.ok("POST", "/observations", admin, {
        "connection": {"source_kind": "plugin", "source": "isbnsync", "source_scope": SOURCE_SCOPE},
        "media_type": "book",
        "observations": [{"external_id": "9990000000001", "title": "The Synthetic Lighthouse",
                          "release_date": "1999"}]})
    return {"synthetic_lighthouse": result["results"][0]["atlas"]["canonical_id"]}


def seed(url: str, bootstrap: str, container: str | None = None) -> dict[str, str]:
    atlas = Atlas(url)
    seed_with_atlas_cli(container)  # False today; see the stub
    admin = mint_token(atlas, bootstrap, "connectors-testkit-seed", ["read", "contribute", "curate"])
    ids = seed_resolve_fixture(atlas, admin)
    ids.update(seed_books(atlas, admin))
    return ids


# --- recording -------------------------------------------------------------------------------------------------

def record(url: str, token: str, out: Path, ids: dict[str, str]) -> list[str]:
    """Record real client responses to ``out``. Ids and timestamps are whatever this Atlas run produced."""
    atlas = Atlas(url)
    out.mkdir(parents=True, exist_ok=True)
    written: list[str] = []

    def save(name: str, payload) -> None:
        (out / name).write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
        written.append(name)

    save("site.json", atlas.ok("GET", "/site"))
    if "portal" in ids:
        cid = ids["portal"]
        save("search-portal.json", atlas.ok("GET", "/games?q=portal&limit=5", token))
        save("record-portal.json", atlas.ok("GET", f"/games/{cid}", token))
        status, relations = atlas.call("GET", f"/games/{cid}/relations", token)
        if status == 200:  # not served by every Atlas line
            save("relations-portal.json", relations)
        else:
            print(f"note: /games/{{id}}/relations answered {status}; not recorded", file=sys.stderr)
        save("resolve-by-atlas-id.json", atlas.ok("POST", "/resolve", token, {
            "items": [{"namespace": "atlas", "external_id": cid}]}))
    return written


# --- CLI -------------------------------------------------------------------------------------------------------

def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="cmd", required=True)
    for name in ("up", "run"):
        p = sub.add_parser(name)
        group = p.add_mutually_exclusive_group(required=True)
        group.add_argument("--atlas-source", type=Path, help="an Atlas checkout to build an image from")
        group.add_argument("--image", help="an existing Atlas image")
        p.add_argument("--tag", default="atlas-testkit:local")
        p.add_argument("--keep", action="store_true", help="leave the container running")
        if name == "run":
            p.add_argument("command", nargs=argparse.REMAINDER)
    for name in ("seed", "token", "record"):
        p = sub.add_parser(name)
        p.add_argument("--url", required=True)
        if name != "record":
            p.add_argument("--bootstrap", required=True)
        if name == "seed":
            p.add_argument("--container")
        if name == "record":
            p.add_argument("--token", required=True)
            p.add_argument("--ids", type=Path, help="JSON printed by `seed`")
            p.add_argument("--out", type=Path, required=True)
    sub.add_parser("down").add_argument("--container", required=True)
    return parser


def strip_separator(command: list[str]) -> list[str]:
    """Drop the leading ``--`` argparse leaves in REMAINDER; keep any later ``--`` (it belongs to the command)."""
    return command[1:] if command[:1] == ["--"] else list(command)


def run_command(node: dict[str, str], command: list[str], keep: bool) -> int:
    try:
        ids = seed(node["url"], node["bootstrap"], node["container"])
        token = mint_read_token(node["url"], node["bootstrap"])
        with tempfile.TemporaryDirectory() as tmp:
            seeded = Path(tmp) / "seeded.json"
            seeded.write_text(json.dumps(ids), encoding="utf-8")
            env = {**os.environ, "ATLAS_URL": node["url"], "ATLAS_READ_TOKEN": token, "ATLAS_SEEDED": str(seeded)}
            return subprocess.run(command, env=env).returncode
    finally:
        if not keep:
            down(node["container"])


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.cmd in {"up", "run"}:
            image = args.image or build_image(args.atlas_source, args.tag)
            if args.cmd == "run":
                command = strip_separator(args.command)
                if not command:
                    raise TestkitError("run needs a command after --")
            node = up(image)
            if args.cmd == "up":
                print(json.dumps(node))
                if not args.keep:
                    down(node["container"])
                return 0
            return run_command(node, command, args.keep)
        if args.cmd == "seed":
            print(json.dumps(seed(args.url, args.bootstrap, args.container)))
        elif args.cmd == "token":
            print(json.dumps({"token": mint_read_token(args.url, args.bootstrap)}))
        elif args.cmd == "record":
            ids = json.loads(args.ids.read_text()) if args.ids else {}
            print("\n".join(record(args.url, args.token, args.out, ids)))
        elif args.cmd == "down":
            down(args.container)
    except (TestkitError, NotImplementedError) as exc:
        print(f"atlas_testkit: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
