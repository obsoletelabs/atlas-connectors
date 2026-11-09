"""Decide which CI areas a change needs (CONTRACT-C15 section 12).

Rules, in order:

* ``main`` and any tag select everything.
* An unknown change set (no base commit, git failure) selects everything.
* ``packages/<p>/...`` selects package ``<p>`` only.
* ``dotnet/``, ``contract/``, ``tools/``, ``catalogues/``, ``.github/`` and the repository root files that
  configure the build select **all** packages. So does any path we do not recognise (fail safe).
* Documentation-only changes (``docs/``, root ``*.md``, issue and PR templates) select nothing.
* ``tests/`` (repository-level Python tests) selects the Python lane; ``tests/live/`` also selects the live lane,
  so a change to the live tests is actually exercised against a real Atlas.

Output is ``key=value`` lines (also appended to ``$GITHUB_OUTPUT``)::

    python=true|false     run the Python lane (tools, contract pin check)
    contract=true|false   verify contract/ pins
    dotnet=true|false     build and test dotnet/Atlas.Client (the shared client + its tests against contract/fixtures)
    live=true|false       the live Atlas lane is worth running
    packages=a,b          package directories to build and test (empty when none)

``--tag <name>`` instead prints the release target for a tag such as ``playnite-v0.3.1``.
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parents[1]
ZERO_SHA = "0" * 40
KNOWN_PACKAGES = ("playnite", "jellyfin", "emby", "plex")
SHARED_ROOTS = ("dotnet", "contract", "tools", "catalogues", ".github")
DOC_ROOTS = ("docs",)
DOC_TEMPLATES = (".github/ISSUE_TEMPLATE/", ".github/PULL_REQUEST_TEMPLATE.md")
TAG = re.compile(r"^(?P<target>playnite|jellyfin|emby|plex|client-contract)-v(?P<version>\d+(?:\.\d+){1,2}(?:[-+][0-9A-Za-z.-]+)?)$")


def available_packages(root: Path = REPOSITORY) -> list[str]:
    """Package directories that exist now, plus the known ones that are still placeholders."""
    found = {child.name for child in (root / "packages").iterdir() if child.is_dir()} if (root / "packages").is_dir() else set()
    return sorted(found | set(KNOWN_PACKAGES))


def classify(files: list[str] | None, packages: list[str] | None = None) -> dict[str, object]:
    """Map changed paths to CI areas. ``None`` means the change set is unknown: everything runs."""
    every = packages if packages is not None else available_packages()
    if files is None:
        return everything(every)
    selected: set[str] = set()
    shared = python = live_tests = False
    for raw in files:
        path = raw.strip().replace("\\", "/")
        if not path:
            continue
        parts = path.split("/")
        if path.startswith(DOC_TEMPLATES) or parts[0] in DOC_ROOTS or (len(parts) == 1 and path.endswith(".md")):
            continue
        if parts[0] == "packages" and len(parts) > 2:
            selected.add(parts[1])
        elif parts[0] == "packages" and len(parts) == 2:
            continue  # packages/README.md and similar
        elif parts[0] == "tests":
            python = True
            live_tests = live_tests or (len(parts) > 2 and parts[1] == "live")
        elif parts[0] in SHARED_ROOTS or len(parts) == 1:
            shared = True  # includes workflows, root config, LICENSE
        else:
            shared = True  # unknown path: fail safe
    chosen = sorted(every if shared else selected)
    return {
        "python": shared or python,
        "contract": shared,
        "dotnet": shared,
        "live": bool(chosen) or live_tests,
        "packages": chosen,
    }


def everything(every: list[str] | None = None) -> dict[str, object]:
    return {"python": True, "contract": True, "dotnet": True, "live": True,
            "packages": sorted(every if every is not None else available_packages())}


def release_target(tag: str) -> dict[str, str]:
    """``playnite-v0.3.1`` -> package playnite, version 0.3.1. ``client-contract-v1.2`` -> the contract release."""
    name = tag.removeprefix("refs/tags/")
    match = TAG.match(name)
    if not match:
        raise ValueError(f"not a release tag: {name!r} (expected <package>-v<semver> or client-contract-v<major.minor>)")
    target, version = match["target"], match["version"]
    if target == "client-contract":
        if not re.fullmatch(r"\d+\.\d+", version):
            raise ValueError(f"client-contract tags are major.minor, got {version!r}")
        return {"kind": "contract", "package": "", "version": version}
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?", version):
        raise ValueError(f"package tags are full SemVer, got {version!r}")
    return {"kind": "package", "package": target, "version": version}


def changed_files(event: str, base: str, head: str) -> list[str] | None:
    if event not in {"pull_request", "push"} or not base or base == ZERO_SHA or not head:
        return None
    spec = f"{base}...{head}" if event == "pull_request" else f"{base}..{head}"
    result = subprocess.run(["git", "diff", "--name-only", spec], capture_output=True, text=True, cwd=REPOSITORY)
    return result.stdout.splitlines() if result.returncode == 0 else None


def render(selection: dict[str, object]) -> list[str]:
    return [f"{key}={','.join(value) if isinstance(value, list) else str(value).lower()}" for key, value in selection.items()]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--event", default=os.environ.get("EVENT", ""))
    parser.add_argument("--base", default=os.environ.get("BASE_SHA", ""))
    parser.add_argument("--head", default=os.environ.get("HEAD_SHA", ""))
    parser.add_argument("--ref", default=os.environ.get("REF", ""), help="main and tags always run everything")
    parser.add_argument("--tag", help="print the release target for this tag instead")
    args = parser.parse_args(argv)
    if args.tag:
        try:
            lines = [f"{k}={v}" for k, v in release_target(args.tag).items()]
        except ValueError as exc:
            print(f"error: {exc}", file=sys.stderr)
            return 2
    else:
        ref = args.ref.removeprefix("refs/heads/")
        selection = everything() if ref == "main" or args.ref.startswith("refs/tags/") \
            else classify(changed_files(args.event, args.base, args.head))
        lines = render(selection)
    print("\n".join(lines))
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as handle:
            handle.write("\n".join(lines) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
