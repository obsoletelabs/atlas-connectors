#!/usr/bin/env python3
"""Pin the files under ``contract/`` by sha256 (CONTRACT-C15 section 9.3).

    python tools/contract_pin.py check           # verify contract/SHA256SUMS; exit 1 on any difference
    python tools/contract_pin.py write           # regenerate contract/SHA256SUMS after a deliberate change
    python tools/contract_pin.py release-check   # check + refuse placeholders (used by the client-contract release)

Hashes are taken over LF-normalised bytes so a CRLF checkout (Windows ``core.autocrlf``) gives the same digest.
``contract/SHA256SUMS`` covers every file under ``contract/`` except any file named ``SHA256SUMS``. The files under
``contract/fixtures/`` and ``contract/openapi-client-v1.json`` are copied unchanged from Atlas (A24: ``contracts/``),
so the pins Atlas ships with them must keep agreeing too: ``fixtures/SHA256SUMS`` (Atlas's list for the fixture set)
and every ``*.json.sha256`` sidecar. ``release-check`` also refuses scaffold stand-ins (an ``"x-placeholder": true``
OpenAPI file, a ``scaffold`` fixture version, a hand-written ``_status`` draft) should one ever come back.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONTRACT = ROOT / "contract"
SUMS = "SHA256SUMS"
OPENAPI = "openapi-client-v1.json"


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()


def files(contract: Path) -> list[str]:
    return sorted(p.relative_to(contract).as_posix() for p in contract.rglob("*") if p.is_file() and p.name != SUMS)


def compute(contract: Path = CONTRACT) -> dict[str, str]:
    return {name: digest(contract / name) for name in files(contract)}


def read_sums(contract: Path = CONTRACT) -> dict[str, str]:
    sums: dict[str, str] = {}
    for line in (contract / SUMS).read_text(encoding="utf-8").splitlines():
        if line.strip() and not line.startswith("#"):
            value, _, name = line.partition("  ")
            sums[name.strip()] = value.strip()
    return sums


def write(contract: Path = CONTRACT) -> None:
    body = "".join(f"{value}  {name}\n" for name, value in compute(contract).items())
    header = "# sha256 of LF-normalised bytes. Regenerate: python tools/contract_pin.py write\n"
    (contract / SUMS).write_text(header + body, encoding="utf-8", newline="\n")


def check(contract: Path = CONTRACT) -> list[str]:
    """Return human-readable problems; empty means the pins hold."""
    if not (contract / SUMS).exists():
        return [f"{SUMS} is missing"]
    pinned, actual = read_sums(contract), compute(contract)
    problems = [f"changed: {n}" for n in sorted(pinned.keys() & actual.keys()) if pinned[n] != actual[n]]
    problems += [f"not pinned: {n}" for n in sorted(actual.keys() - pinned.keys())]
    problems += [f"missing: {n}" for n in sorted(pinned.keys() - actual.keys())]
    # Atlas's own pins (the resolve fixture and OpenAPI sidecars, the fixture set's SHA256SUMS) must keep agreeing.
    for sidecar in sorted(contract.rglob("*.json.sha256")):
        target = sidecar.with_suffix("")
        if target.exists() and sidecar.read_text().split()[0] != digest(target):
            problems.append(f"{sidecar.relative_to(contract).as_posix()} disagrees with {target.name}")
    for upstream in sorted(p for p in contract.rglob(SUMS) if p.parent != contract):
        folder = upstream.parent
        listed = read_sums(folder)
        where = folder.relative_to(contract).as_posix()
        for name, value in sorted(listed.items()):
            target = folder / name
            if not target.is_file():
                problems.append(f"{where}/{SUMS} lists a missing file: {name}")
            elif digest(target) != value:
                problems.append(f"{where}/{SUMS} disagrees with {name}")
        for name in sorted(set(files(folder)) - set(listed)):
            problems.append(f"{where}/{SUMS} does not list {name}")
    return problems


def placeholders(contract: Path = CONTRACT) -> list[str]:
    problems = []
    spec = json.loads((contract / OPENAPI).read_text(encoding="utf-8"))
    if spec.get("x-placeholder"):
        problems.append(f"{OPENAPI} is still a placeholder")
    if (contract / "fixtures" / "VERSION").read_text().strip().startswith("scaffold"):
        problems.append("fixtures/VERSION is still a scaffold version")
    for path in sorted((contract / "fixtures" / "books").glob("*.json")):
        if "_status" in json.loads(path.read_text(encoding="utf-8")):
            problems.append(f"fixtures/books/{path.name} is a hand-written draft")
    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("action", choices=["check", "write", "release-check"])
    args = parser.parse_args(argv)
    if args.action == "write":
        write()
        print(f"wrote contract/{SUMS} ({len(compute())} files)")
        return 0
    problems = check() + (placeholders() if args.action == "release-check" else [])
    for problem in problems:
        print(problem, file=sys.stderr)
    print("contract pins hold" if not problems else f"{len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
