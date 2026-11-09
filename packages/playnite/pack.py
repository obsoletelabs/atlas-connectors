#!/usr/bin/env python3
"""Build the Atlas Playnite extension and pack it into a .pext (a zip of the built extension folder).

    python packages/playnite/pack.py                 # dotnet build -c Release, then pack into packages/playnite/dist/
    python packages/playnite/pack.py --no-build      # pack what is already in Atlas.Playnite/bin/Release
    python packages/playnite/pack.py --verify FILE   # check an existing .pext and print its checksum

Why not Playnite's own Toolbox (`Toolbox.exe pack <folder> <target>`)? It ships inside a Playnite install, which a CI runner and
this repository do not have. A .pext is a zip with extension.yaml at its root next to the module; this script writes exactly that
and then re-opens the result and checks it against the manifest rules (docs/VENDOR-FACTS.md). It has NOT been compared byte for
byte with Toolbox output and no Playnite has loaded the result yet: that is the first thing to do with a release candidate
(Playnite > Add-ons > Install from file), and `Toolbox.exe verify` is meant for the add-on database manifests, not the package.

The package carries only what Playnite does not already provide: Atlas.Playnite.dll, extension.yaml, icon.png, LICENSE and
CHANGELOG.md. Playnite.SDK.dll and Newtonsoft.Json.dll are deliberately absent (Playnite loads its own; shipping a second copy
of Newtonsoft is the conflict the Playnite docs warn about).
"""
from __future__ import annotations

import argparse
import hashlib
import re
import subprocess
import sys
import zipfile
from pathlib import Path

PKG = Path(__file__).resolve().parent
ROOT = PKG.parents[1]
PROJECT = PKG / "Atlas.Playnite" / "Atlas.Playnite.csproj"
BIN = PKG / "Atlas.Playnite" / "bin" / "Release"
DIST = PKG / "dist"

CONTENT = ["Atlas.Playnite.dll", "extension.yaml", "icon.png"]
EXTRA = [(ROOT / "LICENSE", "LICENSE"), (PKG / "CHANGELOG.md", "CHANGELOG.md")]
FORBIDDEN = re.compile(r"^(Playnite\..*|Newtonsoft\..*|System\..*|Microsoft\..*|.*\.pdb|.*\.xml)$", re.IGNORECASE)
REQUIRED_KEYS = ("Id", "Name", "Author", "Version", "Module", "Type")
TYPES = {"Script", "GenericPlugin", "GameLibrary", "MetadataProvider"}
FIXED_TIME = (2026, 1, 1, 0, 0, 0)       # reproducible archives


def read_manifest(text: str) -> dict[str, str]:
    """Top-level `Key: value` pairs of extension.yaml (all the keys this package uses are plain scalars)."""
    out: dict[str, str] = {}
    for line in text.splitlines():
        m = re.match(r"^([A-Za-z]+):\s*(.*?)\s*$", line)
        if m and m.group(2):
            out[m.group(1)] = m.group(2)
    return out


def check_manifest(m: dict[str, str]) -> list[str]:
    errors = [f"extension.yaml lacks {k}" for k in REQUIRED_KEYS if not m.get(k)]
    if m.get("Type") and m["Type"] not in TYPES:
        errors.append(f"Type {m['Type']!r} is not one of {sorted(TYPES)}")
    if m.get("Version") and not re.fullmatch(r"\d+(\.\d+){1,3}", m["Version"]):
        errors.append(f"Version {m['Version']!r} is not a .NET version string")
    if m.get("Id") and not re.fullmatch(r"[A-Za-z0-9_]+", m["Id"]):
        errors.append(f"Id {m['Id']!r} should be letters, digits and underscores")
    return errors


def csproj_version() -> str:
    m = re.search(r"<Version>([^<]+)</Version>", PROJECT.read_text(encoding="utf-8"))
    if not m:
        raise SystemExit("no <Version> in Atlas.Playnite.csproj")
    return m.group(1).strip()


def changelog_version() -> str | None:
    m = re.search(r"^## (\d+\.\d+\.\d+)", (PKG / "CHANGELOG.md").read_text(encoding="utf-8"), re.MULTILINE)
    return m.group(1) if m else None


def build() -> None:
    subprocess.run(["dotnet", "build", str(PROJECT), "-c", "Release", "-nologo"], check=True)


def pack(out_dir: Path = DIST, bin_dir: Path = BIN) -> Path:
    manifest = read_manifest((PKG / "extension.yaml").read_text(encoding="utf-8"))
    problems = check_manifest(manifest)
    version = manifest.get("Version", "")
    if version != csproj_version():
        problems.append(f"extension.yaml Version {version} differs from Atlas.Playnite.csproj Version {csproj_version()}")
    if changelog_version() != version:
        problems.append(f"CHANGELOG.md top version {changelog_version()} differs from {version}")
    missing = [n for n in CONTENT if not (bin_dir / n).is_file()]
    if missing:
        problems.append(f"not built: {', '.join(missing)} missing from {bin_dir}")
    if problems:
        raise SystemExit("cannot pack:\n  " + "\n  ".join(problems))

    out_dir.mkdir(parents=True, exist_ok=True)
    target = out_dir / f"Atlas_Metadata-{version}.pext"
    members = [(bin_dir / n, n) for n in CONTENT] + [(src, name) for src, name in EXTRA if src.is_file()]
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
        for src, name in sorted(members, key=lambda p: p[1]):
            info = zipfile.ZipInfo(name, FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            z.writestr(info, src.read_bytes())
    errors = verify(target)
    if errors:
        target.unlink()
        raise SystemExit("the packed file failed its own check:\n  " + "\n  ".join(errors))
    digest = hashlib.sha256(target.read_bytes()).hexdigest()
    (target.parent / (target.name + ".sha256")).write_text(f"{digest}  {target.name}\n", encoding="utf-8", newline="\n")
    return target


def verify(path: Path) -> list[str]:
    errors: list[str] = []
    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        if z.testzip() is not None:
            errors.append("zip is damaged")
        if "extension.yaml" not in names:
            return errors + ["extension.yaml is not at the root of the package"]
        for n in names:
            if "\\" in n or n.startswith("/") or ".." in n.split("/"):
                errors.append(f"unsafe member name {n!r}")
            if FORBIDDEN.match(n.split("/")[-1]):
                errors.append(f"{n} must not be shipped (Playnite provides it, or it is a debug file)")
        manifest = read_manifest(z.read("extension.yaml").decode("utf-8-sig"))
        errors += check_manifest(manifest)
        module = manifest.get("Module")
        if module and module not in names:
            errors.append(f"Module {module} is not in the package")
        icon = manifest.get("Icon")
        if icon and icon not in names:
            errors.append(f"Icon {icon} is not in the package")
        if module and module in names and z.read(module)[:2] != b"MZ":
            errors.append(f"{module} is not a Windows assembly")
    return errors


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--no-build", action="store_true", help="pack the existing build output")
    ap.add_argument("--out", type=Path, default=DIST, help="output folder (default packages/playnite/dist)")
    ap.add_argument("--verify", type=Path, metavar="PEXT", help="only check an existing package")
    args = ap.parse_args(argv)
    if args.verify:
        errors = verify(args.verify)
        for e in errors:
            print("ERROR:", e, file=sys.stderr)
        if not errors:
            print(f"{args.verify.name}: ok  sha256={hashlib.sha256(args.verify.read_bytes()).hexdigest()}")
        return 1 if errors else 0
    if not args.no_build:
        build()
    target = pack(args.out)
    print(f"packed {target}")
    print((target.parent / (target.name + ".sha256")).read_text(encoding="utf-8").strip())
    with zipfile.ZipFile(target) as z:
        for i in z.infolist():
            print(f"  {i.file_size:>9}  {i.filename}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
