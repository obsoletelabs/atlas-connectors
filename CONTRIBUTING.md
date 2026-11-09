# Contributing

Thanks for helping. This repository holds the connectors that bring [Atlas](https://github.com/obsoletelabs)
metadata into other tools. Read `README.md` and `contract/CLIENT-CONTRACT.md` first.

## Ground rules

- **Talk HTTP to the client contract.** Never copy Atlas code, never depend on a field outside the `client-api` subset.
- **Synthetic data only** (the scaffold's Atlas-origin game facts are a documented exception, see `LICENSING.md`).
  No real catalogue content, no provider data (Steam, IGDB, RAWG, Open Library), no
  credentials or tokens, no personal data. Fixtures and vectors are CC0; code is MIT (`LICENSING.md`).
- **Fixtures are generated, not hand-edited.** Record them with `tools/atlas_testkit.py record`; the final set comes from
  Atlas. After any change under `contract/`, run `python tools/contract_pin.py write` and commit `contract/SHA256SUMS`.
- **Read-only against Atlas.** Connectors never write to Atlas.
- **Vendor facts need evidence.** Playnite, Jellyfin, Emby and Plex rules change. Record what you checked, where and when
  in `docs/VENDOR-FACTS.md`; mark anything unchecked as unverified.

## Layout and ownership

`packages/<host>` is one releasable package with its own version, `CHANGELOG.md` and README compatibility table.
`dotnet/Atlas.Client` is shared source compiled into each package (not released). `tools/`, `contract/`, workflows and
`dotnet/` affect every package, so CI runs them all for such changes.

## Running the checks

```
python -m pip install pytest
python -m pytest tests -q                       # offline: change detection, pins, testkit logic
python tools/contract_pin.py check              # contract/ matches contract/SHA256SUMS
python tools/atlas_testkit.py run --image <atlas-image> -- python -m pytest tests/live -q   # needs Docker
```

## Pull requests

- One logical change per PR; pull requests form a linear stack while the scaffold is being built.
- Say what changed, why, and how you tested it, with real output. Say plainly what you could not run.
- Commit messages: imperative summary, details in the body.

## Releases

Tag `playnite-v<semver>`, `jellyfin-v<semver>` or `client-contract-v<major>.<minor>` on `main`. Each tag triggers only
its own workflow. See `.github/workflows/`.
