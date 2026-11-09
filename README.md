# atlas-provider-adapters (the Atlas connectors monorepo)

Bring [Atlas](https://github.com/obsoletelabs) metadata into the tools people already use. Atlas is a semi-distributed
media metadata server (games first, books next). This repository holds the **connectors** that read from Atlas
inside other programs, built on one versioned, documented HTTP contract.

> The design notes call this repository `atlas-connectors` (decision D15.13). The repository was created as
> `atlas-provider-adapters`; the layout and contract are the same.

**Status: scaffold.** There is no connector code yet. This repository currently contains the contract, the fixtures,
the CI plumbing and the test tooling that the connectors will be built on.

## Packages

| Package | Host | Media from Atlas | Tag prefix | Status |
|---|---|---|---|---|
| `packages/playnite` | Playnite (Windows game launcher), `MetadataPlugin` packaged as `.pext` | games | `playnite-v*` | planned (PR X1) |
| `packages/jellyfin` | Jellyfin media server plugin (`IRemoteMetadataProvider`, `IExternalId`, image provider) | books, movies, TV, anime (not games: Jellyfin has no game library) | `jellyfin-v*` | 0.1.0 in review (PR X2): builds and tests for Jellyfin 10.11 and 12; never loaded in a Jellyfin server |
| `packages/emby` | Emby plugin | movies, TV, books | `emby-v*` | undecided: a one-day spike decides (D15.17) |
| `packages/plex` | Plex | movies, TV | `plex-v*` | undecided: a one-day spike decides (D15.17) |
| `dotnet/Atlas.Client` | shared C# client, compiled into each package as source (not a NuGet dependency) | n/a | not released | implemented (PR 2): models, tolerant reader, HTTP retry, wire negotiation, pairing; see `dotnet/README.md` |
| `contract/` | the Atlas client contract: `CLIENT-CONTRACT.md`, pinned OpenAPI, fixtures, vectors | n/a | `client-contract-v*` | draft (this PR) |

Every package has its own SemVer version and CHANGELOG, declares the `client-api` version it needs (`min_client_api`)
and its host range, and is released by its own tag.

## How Atlas fits

```
 Playnite / Jellyfin  --(HTTPS, read token)-->  Atlas  <--federation-->  other Atlas nodes
   connector in this repo     client-api v1      authority / local / mirror
```

- A connector sends the ids the host already knows (Steam app id, ISBN, TMDB id, ...) to `POST /api/v2/resolve`, writes the
  Atlas id back to the host, and maps the Atlas record to the host's metadata fields.
- The connector **never writes to Atlas**, never puts its token in a URL, and never follows a redirect with the token.
- Everything a connector may rely on is in [`contract/CLIENT-CONTRACT.md`](contract/CLIENT-CONTRACT.md). Anything else in
  Atlas can change without notice.

## Repository layout

```
contract/        the client contract (no code): CLIENT-CONTRACT.md, openapi-client-v1.json (placeholder until Atlas
                 generates it), fixtures/, vectors/, SHA256SUMS
dotnet/          Atlas.Client (shared C# client, compiled in as source), Atlas.Client.Tests, Atlas.Client.SourceCheck
packages/        one directory per host (placeholders)
tools/           ci_changes.py (what a change touches), atlas_testkit.py (real Atlas, seed, read token),
                 contract_pin.py (sha256 pins)
catalogues/      generated: Jellyfin manifest.json, Playnite add-on database entries (placeholder)
docs/            VENDOR-FACTS.md: what was checked against vendor documentation, and what was not
tests/           offline tests, plus tests/live (needs a real Atlas)
.github/         path-filtered CI, tag-prefix release workflows, templates
```

## Working here

```
python -m pip install pytest
python -m pytest tests -q                 # offline, no network
python tools/contract_pin.py check
dotnet test dotnet/Atlas.Client.Tests       # the shared client against contract/fixtures (needs the .NET 10 SDK)
python tools/atlas_testkit.py run --image <atlas-image> -- python -m pytest tests/live -q   # needs Docker
```

See [`CONTRIBUTING.md`](CONTRIBUTING.md). Licensing differs from the AGPL Atlas repositories on purpose:
code MIT, fixtures CC0. See [`LICENSING.md`](LICENSING.md).
