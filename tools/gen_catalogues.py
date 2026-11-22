#!/usr/bin/env python3
"""Generate the Jellyfin repository manifest (catalogues/jellyfin/manifest.json) from built plugin zips (CONTRACT-C15 8.3).

    python tools/gen_catalogues.py jellyfin --artifacts packages/jellyfin/dist --download-base URL \\
        [--existing OLD_MANIFEST.json] --out catalogues/jellyfin/manifest.json
    python tools/gen_catalogues.py verify  --manifest catalogues/jellyfin/manifest.json --artifacts packages/jellyfin/dist

The checksums are computed from the zips (MD5, the algorithm the Jellyfin manifest and jprm use); nothing is typed by hand. ``--existing``
keeps the versions of earlier releases so a new release adds to the manifest instead of replacing it. ``verify`` fails when a zip's MD5
disagrees with the manifest entry that points at it (a tampered or rebuilt artefact), and with ``--require-all`` also when an entry has no zip.

The manifest shape was checked against the live Jellyfin repository manifest on 2026-11-09 (docs/VENDOR-FACTS.md): a JSON array of plugins
with guid, name, description, overview, owner, category and versions[] (version, changelog, targetAbi, sourceUrl, checksum, timestamp).
The Playnite catalogue is not generated here: add-on submissions are on hold (docs/VENDOR-FACTS.md).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import zipfile
from pathlib import Path

PLUGIN_KEYS = ("guid", "name", "description", "overview", "owner", "category")
VERSION_KEYS = ("version", "changelog", "targetAbi", "timestamp")


def md5_of(path: Path) -> str:
    h = hashlib.md5()      # Jellyfin manifest checksum algorithm; integrity against corruption, not a security boundary
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def version_key(v: str) -> tuple[int, ...]:
    return tuple(int(p) for p in re.findall(r"\d+", v))


def read_meta(zip_path: Path) -> dict:
    with zipfile.ZipFile(zip_path) as z:
        return json.loads(z.read("meta.json"))


def build_manifest(artifacts: Path, download_base: str, existing: list | None) -> list:
    zips = sorted(artifacts.glob("*.zip"))
    if not zips:
        raise SystemExit(f"no *.zip in {artifacts}")
    plugins: dict[str, dict] = {p["guid"]: p for p in (existing or [])}
    for z in zips:
        meta = read_meta(z)
        plugin = plugins.setdefault(meta["guid"], {k: meta[k] for k in PLUGIN_KEYS} | {"versions": []})
        for k in PLUGIN_KEYS:             # the newest build describes the plugin
            plugin[k] = meta[k]
        entry = {k: meta[k] for k in VERSION_KEYS}
        entry["sourceUrl"] = f"{download_base.rstrip('/')}/{z.name}"
        entry["checksum"] = md5_of(z)
        plugin["versions"] = [v for v in plugin["versions"] if v["version"] != entry["version"]] + [entry]
    for plugin in plugins.values():
        plugin["versions"].sort(key=lambda v: version_key(v["version"]), reverse=True)
    return sorted(plugins.values(), key=lambda p: p["name"].lower())


def verify(manifest: list, artifacts: Path, require_all: bool = False) -> list[str]:
    errors: list[str] = []
    for plugin in manifest:
        for v in plugin.get("versions", []):
            name = v["sourceUrl"].rsplit("/", 1)[-1]
            zip_path = artifacts / name
            if not zip_path.is_file():
                if require_all:
                    errors.append(f"{plugin['name']} {v['version']}: artefact {name} not found")
                continue
            actual = md5_of(zip_path)
            if actual != v["checksum"]:
                errors.append(f"{plugin['name']} {v['version']}: checksum {v['checksum']} in the catalogue, {actual} for {name}")
            meta = read_meta(zip_path)
            for k in ("version", "targetAbi"):
                if meta.get(k) != v.get(k):
                    errors.append(f"{plugin['name']} {v['version']}: {k} {v.get(k)!r} in the catalogue, {meta.get(k)!r} in {name}")
    return errors


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    g = sub.add_parser("jellyfin")
    g.add_argument("--artifacts", type=Path, required=True)
    g.add_argument("--download-base", required=True, help="URL the zips are served from (no trailing file name)")
    g.add_argument("--existing", type=Path)
    g.add_argument("--out", type=Path, required=True)
    v = sub.add_parser("verify")
    v.add_argument("--manifest", type=Path, required=True)
    v.add_argument("--artifacts", type=Path, required=True)
    v.add_argument("--require-all", action="store_true")
    args = ap.parse_args(argv)
    if args.cmd == "jellyfin":
        existing = json.loads(args.existing.read_text(encoding="utf-8")) if args.existing and args.existing.is_file() else None
        manifest = build_manifest(args.artifacts, args.download_base, existing)
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")
        errors = verify(manifest, args.artifacts)
        for e in errors:
            print("error:", e, file=sys.stderr)
        print(f"wrote {args.out}: {sum(len(p['versions']) for p in manifest)} version(s)")
        return 1 if errors else 0
    errors = verify(json.loads(args.manifest.read_text(encoding="utf-8")), args.artifacts, args.require_all)
    for e in errors:
        print("error:", e, file=sys.stderr)
    print("catalogue matches the artefacts" if not errors else f"{len(errors)} problem(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
