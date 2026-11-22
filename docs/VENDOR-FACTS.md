# Vendor facts: what was checked, and what was not

Checked on **2026-11-09** against the live sources named below (fetched in this session). Anything marked
**unverified** is carried over from the design notes (CONTRACT-C15, labelled [L]/[U] there) and must be re-checked
before a connector relies on it. Re-verify when building each package.

## Jellyfin

| Fact (from the design notes) | Result | Evidence |
|---|---|---|
| Server licence is GPL-2.0 | **Confirmed** as GPL-2.0 by GitHub licence metadata of `jellyfin/jellyfin`. Whether individual files add "or later" is **unverified** | `https://api.github.com/repos/jellyfin/jellyfin/license` |
| Repository manifest is a JSON array of plugins with `guid`, `name`, `overview`, `owner`, `category`, `versions[]` | **Confirmed.** Real manifest keys per plugin: `category, description, guid, name, overview, owner, versions`. Real version keys: `changelog, checksum, sourceUrl, targetAbi, timestamp, version` | `https://repo.jellyfin.org/files/plugin/manifest.json` (36 plugins at the time) |
| `checksum` is MD5 | **Consistent**: the values are 32 hex characters. The algorithm is not stated in the docs read; treat as MD5 until a source says so | same manifest |
| `build.yaml` fields and framework per ABI | **Partly confirmed.** The plugin template's `build.yaml` has `name, guid, version, targetAbi, framework, overview, description, category, owner, artifacts, changelog` with `targetAbi: 10.11.0.0` and `framework: net9.0`. The 10.10 -> net8.0 pairing is **unverified** | `https://raw.githubusercontent.com/jellyfin/jellyfin-plugin-template/master/build.yaml` |
| Plugin package versions must match the server version | **Confirmed** by the template README: `Jellyfin.Controller` / `Jellyfin.Model` must match, else the plugin shows NotSupported (example 10.11.5). README also says plugins must compile to `net8.0` while its steps use .NET SDK 9 / `net9.0`; the discrepancy is unexplained | `https://raw.githubusercontent.com/jellyfin/jellyfin-plugin-template/master/README.md` |
| Book provider interfaces (`IRemoteMetadataProvider<Book, BookInfo>` etc.), `IExternalId`, `ProviderIds` | **Unverified** | to check in X2 |
| A headless container scan is practical in CI | **Unverified** | to check in X2 |

## Playnite

| Fact | Result | Evidence |
|---|---|---|
| SDK is MIT | **Confirmed** (`PackageLicenseExpression` MIT; GitHub metadata MIT) | `https://raw.githubusercontent.com/JosefNemec/Playnite/master/source/PlayniteSDK/Playnite.SDK.csproj` |
| SDK targets .NET Framework 4.6.2 | **Confirmed** for the current master SDK project: `TargetFramework` `net462`, SDK package version 6.18.0.0. Playnite 11's framework is **unverified** | same file |
| `extension.yaml` fields | **Confirmed.** Required: `Id`, `Name`, `Author`, `Version` (valid .NET version string), `Module` (dll), `Type` (`Script`, `GenericPlugin`, `GameLibrary`, `MetadataProvider`). Optional: `Icon`, `Links` (list of `Name` + `Url`) | `https://api.playnite.link/docs/tutorials/extensions/extensionsManifest.html` |
| Add-on database takes a PR with two manifests | **Confirmed in structure, with a blocker.** Addon manifest: `AddonId`, `Name`, `Type`, `Author`, `ShortDescription`, `SourceUrl`, `InstallerManifestUrl` (+ optional fields). Installer manifest: `AddonId`, `Packages[]` with `Version`, `RequiredApiVersion`, `ReleaseDate` (YYYY-MM-DD), `PackageUrl`, `Changelog` | `https://raw.githubusercontent.com/JosefNemec/PlayniteAddonDatabase/master/README.md` |
| **Add-on submissions are currently on hold** | **New finding, contradicts the design notes' assumption.** The database README says submissions resume through a new add-on portal after Playnite 11, with Playnite 10 backporting planned by the end of 2026; trusted community members may be accepted sooner. The Playnite release flow must therefore not assume an automatic PR to the database | same file |
| `ReleaseDate` supports partial dates (year only, year-month) | **Unverified** | to check in X1 |
| No headless Playnite exists | **Unverified** | to check in X1 |
| Toolbox `pack` and manifest validation | The database README says the Toolbox can validate manifests; `pack` itself **unverified** | same README |

## Emby and Plex

Not checked. The D15.17 spikes decide whether either is feasible.

## Open Library

Not relevant to this repository (Atlas side, PR A22).

## Atlas facts checked by running a real Atlas

Built from the local Atlas programme branch `programme/p7-atlas-metrics` (commit `1142dc7`, not pushed to the remote at
the time) and seeded through `tools/atlas_testkit.py`:

- The 19 cases of `resolve-contract-v1.json` plus a `/site` check and a read-scope check pass (21 tests in `tests/live`).
- `GET /games/{id}` sends an `ETag` header (`"<id>-<version>"`). `If-None-Match` handling was **not** tested.
- `GET /site` returns `contract: 1` and no `client_api` block yet.
- A read-scope token cannot mint tokens (403).
- `GET /games/{id}/relations` answers 404 on the plain `v2` line (commit `62c73f3`, image built for comparison) but is
  served on the programme branch.
- The seed fixture is flat (`developer` at the top level); the wire format nests `developer`, `publisher` and `series`
  under `facts`. `atlas_testkit.py` does the nesting.
