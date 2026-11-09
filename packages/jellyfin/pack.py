#!/usr/bin/env python3
"""Build the Atlas Jellyfin plugin once per supported ABI and pack one zip per ABI (the layout jprm writes).

    python packages/jellyfin/pack.py                       # dotnet build -c Release per ABI, then pack into packages/jellyfin/dist/
    python packages/jellyfin/pack.py --no-build            # pack what is already in Jellyfin.Plugin.Atlas/bin/<abi>/
    python packages/jellyfin/pack.py --abi 12              # only one ABI key from abis.json
    python packages/jellyfin/pack.py --verify FILE.zip     # check a zip and print its MD5

Per ABI (abis.json) it writes ``atlas_<version>.zip`` containing ``Jellyfin.Plugin.Atlas.dll`` and ``meta.json``, plus
``atlas_<version>.zip.meta.json`` (a copy) and ``atlas_<version>.zip.md5sum``. This follows what the Jellyfin plugin repository manager
(jprm) does, read from its source on 2026-11-09 (docs/VENDOR-FACTS.md): slug of the name + four-part version, meta.json with
guid/name/description/overview/owner/category/version/changelog/targetAbi/timestamp, MD5 of the zip. jprm itself is NOT run here.

Why two versions per release: each ABI is a separate build, so the manifest needs distinct ``version`` values. The four-part version is
``<semver>.<n>`` with n from ``version_suffix`` in abis.json (0 = 10.11, 1 = 12). Which entry a server picks when several match is
unverified; distinct versions avoid the question.

Only the plugin dll is shipped. Jellyfin.Controller, Jellyfin.Model and System.Text.Json come from the server (ExcludeAssets=runtime).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

PKG = Path(__file__).resolve().parent
PROJECT = PKG / "Jellyfin.Plugin.Atlas" / "Jellyfin.Plugin.Atlas.csproj"
DIST = PKG / "dist"
DLL = "Jellyfin.Plugin.Atlas.dll"
FIXED_TIME = (2026, 1, 1, 0, 0, 0)       # reproducible archives
SLUG = "atlas"
META_KEYS = ("guid", "name", "description", "overview", "owner", "category", "version", "changelog", "targetAbi", "timestamp")


def read_yaml_scalars(text: str) -> dict:
    """Top-level ``key: "value"`` pairs and the ``artifacts`` list of build.yaml (all values are plain one-line scalars here)."""
    out: dict = {}
    current = None
    for line in text.splitlines():
        m = re.match(r'^([A-Za-z]+):\s*(?:"(.*)"|(.*?))\s*$', line)
        if m and not line.startswith("-"):
            key = m.group(1)
            value = m.group(2) if m.group(2) is not None else m.group(3)
            if value == "":
                out[key] = []
                current = key
            else:
                out[key] = value
                current = None
            continue
        item = re.match(r'^-\s*"?(.*?)"?\s*$', line)
        if item and current:
            out[current].append(item.group(1))
    return out


def load_abis() -> list[dict]:
    return json.loads((PKG / "abis.json").read_text(encoding="utf-8"))["abis"]


def build_config(abi: dict) -> dict:
    """build.yaml (10.11 line) or build-abi12.yaml, whichever carries this ABI."""
    for name in ("build.yaml", "build-abi12.yaml"):
        cfg = read_yaml_scalars((PKG / name).read_text(encoding="utf-8"))
        if cfg.get("targetAbi") == abi["target_abi"]:
            return cfg
    raise SystemExit(f"no build yaml has targetAbi {abi['target_abi']}")


def csproj_version() -> str:
    m = re.search(r"<Version>([^<]+)</Version>", PROJECT.read_text(encoding="utf-8"))
    return m.group(1) if m else ""


def md5_of(path: Path) -> str:
    h = hashlib.md5()      # the Jellyfin manifest checksum is MD5 (jprm checksum_file(..., 'md5')); not a security boundary
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def timestamp() -> str:
    epoch = os.environ.get("SOURCE_DATE_EPOCH")
    now = datetime.fromtimestamp(int(epoch), timezone.utc) if epoch else datetime.now(timezone.utc)
    return now.strftime("%Y-%m-%dT%H:%M:%SZ")


def build(abi: dict) -> None:
    subprocess.run(["dotnet", "build", str(PROJECT), "-c", "Release", f"-p:JellyfinAbi={abi['key']}"], check=True)


def meta_for(abi: dict, stamp: str) -> dict:
    cfg = build_config(abi)
    meta = {k: cfg[k] for k in ("guid", "name", "description", "overview", "owner", "category", "version", "changelog", "targetAbi")}
    meta["timestamp"] = stamp
    return meta


def pack(abi: dict, stamp: str) -> Path:
    dll = PKG / "Jellyfin.Plugin.Atlas" / "bin" / abi["key"] / DLL
    if not dll.is_file():
        raise SystemExit(f"{dll} missing: build ABI {abi['key']} first (or drop --no-build)")
    meta = meta_for(abi, stamp)
    DIST.mkdir(parents=True, exist_ok=True)
    zip_path = DIST / f"{SLUG}_{meta['version']}.zip"
    meta_text = json.dumps(meta, sort_keys=True, indent=4) + "\n"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in ((DLL, dll.read_bytes()), ("meta.json", meta_text.encode("utf-8"))):
            info = zipfile.ZipInfo(name, FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            z.writestr(info, data)
    Path(str(zip_path) + ".meta.json").write_text(meta_text, encoding="utf-8")
    Path(str(zip_path) + ".md5sum").write_text(f"{md5_of(zip_path)} *{zip_path.name}\n", encoding="utf-8")
    return zip_path


def verify(path: Path) -> list[str]:
    errors: list[str] = []
    try:
        with zipfile.ZipFile(path) as z:
            names = z.namelist()
            if sorted(names) != sorted([DLL, "meta.json"]):
                errors.append(f"unexpected contents {names}; expected exactly {DLL} and meta.json (the server provides every other assembly)")
            if "meta.json" in names:
                meta = json.loads(z.read("meta.json"))
                errors += [f"meta.json lacks {k}" for k in META_KEYS if not meta.get(k)]
                if meta.get("version") and not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", meta["version"]):
                    errors.append(f"version {meta['version']!r} is not four-part")
                if meta.get("targetAbi") and not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", meta["targetAbi"]):
                    errors.append(f"targetAbi {meta['targetAbi']!r} is not four-part")
                if meta.get("version") and not path.name == f"{SLUG}_{meta['version']}.zip":
                    errors.append(f"file name {path.name} does not match {SLUG}_{meta['version']}.zip")
            if DLL in names and not z.read(DLL).startswith(b"MZ"):
                errors.append(f"{DLL} is not a .NET assembly")
    except zipfile.BadZipFile:
        errors.append("not a zip file")
    return errors


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--abi", help="only this ABI key from abis.json")
    ap.add_argument("--verify", type=Path, help="verify an existing zip and print its MD5")
    args = ap.parse_args(argv)
    if args.verify:
        errors = verify(args.verify)
        for e in errors:
            print("error:", e, file=sys.stderr)
        if not errors:
            print(f"ok {md5_of(args.verify)}  {args.verify.name}")
        return 1 if errors else 0
    abis = [a for a in load_abis() if not args.abi or a["key"] == args.abi]
    if not abis:
        print("no such ABI key", file=sys.stderr)
        return 2
    stamp = timestamp()
    for abi in abis:
        if not args.no_build:
            build(abi)
        z = pack(abi, stamp)
        errors = verify(z)
        if errors:
            for e in errors:
                print("error:", e, file=sys.stderr)
            return 1
        print(f"packed {z.relative_to(PKG.parents[1]).as_posix()}  md5 {md5_of(z)}  targetAbi {abi['target_abi']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
