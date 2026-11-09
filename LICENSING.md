# Licensing

| What | Licence |
|---|---|
| All code in this repository (`dotnet/`, `packages/`, `tools/`, workflows) | **MIT** (`LICENSE`) |
| Data in `contract/` (`fixtures/`, `vectors/`, `openapi-client-v1.json`) | **CC0 1.0** (`contract/LICENSE-CC0.md`) |
| Documentation (`*.md`, including `contract/CLIENT-CONTRACT.md`) | MIT, like the code |

## Why this differs from the AGPL repositories

The Tracking app, its plugins and Atlas (default branches) are AGPL-3.0. This repository is deliberately **not**.
This is design reasoning, not legal advice; the owner may want a lawyer's view for the Jellyfin case.

- **The connectors are not derivative works of Atlas.** They talk to Atlas over HTTP and copy no Atlas code. AGPL
  section 13 (network use) binds whoever runs a *modified Atlas*, not its API clients.
- **They load into other people's programs.** A Jellyfin plugin runs inside the Jellyfin server process and links its
  assemblies. Jellyfin is GPL-2.0 (verified on its GitHub licence metadata on 2026-11-09; whether individual files say
  "or later" was **not** checked). AGPL-3.0 is compatible with GPL-3.0 only, and not with GPL-2.0-only, so an AGPL
  Jellyfin plugin is risky. MIT is safe. A Playnite extension loads into Playnite, whose SDK is MIT. The Playnite
  add-on database maintainers may not welcome copyleft (not verified).
- **The shared C# client** (`dotnet/Atlas.Client`) is compiled into every package, so its licence must be compatible
  with every target. Only a permissive licence satisfies all of them.
- **Emby and Plex** are closed hosts. Only our own code is licensed here; a curated catalogue may refuse copyleft
  (not verified).
- **Fixtures** are synthetic, so CC0 is possible and keeps them reusable by anyone.

If copyleft is wanted later, the recommendation in the design notes is GPL-3.0-or-later for the Playnite package
only, keeping Jellyfin and the shared client permissive. That is an owner decision.

## Files copied from Atlas

`contract/openapi-client-v1.json` (once generated), the recorded fixtures and
`contract/fixtures/resolve/resolve-contract-v1.json` come out of the Atlas repository, whose code is AGPL-3.0 (or MIT
on the programme branches that have not yet picked up the AGPL `LICENSE`). Atlas's `DATA-LICENSE.md` dedicates
user-contributed *data* to CC0. Data and a generated interface description are unlikely to carry AGPL obligations,
but to remove doubt the **copyright holder is expected to state in Atlas's `DATA-LICENSE.md`** that `contracts/` and
`tests/fixtures/*contract*` are also offered under CC0, and Atlas's CONTRIBUTING should require the same grant for
outside contributions to those files.

**Status: that statement is not yet made.** Until the owner makes it, the Atlas-origin files here rest on the
reasoning above only. Only synthetic data is included: no provider data (Steam, IGDB, RAWG, Open Library) and no real
copyrighted content.

## Contributions

By contributing you agree your code is MIT-licensed and your fixture/vector data is dedicated under CC0 1.0. Do not
contribute data you cannot dedicate (anything fetched from a provider, or copied from a real catalogue).
