# Atlas for Playnite

A Playnite **metadata provider** that fills game fields and images from your own [Atlas](https://github.com/obsoletelabs)
node. Version **0.1.0** (see `CHANGELOG.md`; tags are `playnite-v<version>`). MIT, like the rest of this repository.

It only reads. It never writes to Atlas.

## Compatibility

| Item | Value | Status |
|---|---|---|
| Playnite SDK it is built against | **PlayniteSDK 6.18.0** (NuGet), .NET Framework **4.6.2** (`net462`) | restored and built here; the SDK's own project targets `net462` |
| Playnite versions | the Playnite 10 line (SDK 6.x) | **not run in Playnite yet.** SDK minor versions are backwards compatible within a major (Playnite docs), but the exact Playnite versions that load a 6.18.0 build are unverified |
| Playnite 11 | unknown | SDK and runtime may change; re-check when it ships |
| `min_client_api` | **1.0** (`client-api` v1, `contract/CLIENT-CONTRACT.md`) | the connector refuses an Atlas whose `client_api` major differs; an Atlas without the block (before Atlas PR A24) still works, and the settings test says so |
| Required Atlas capabilities | none for games | ETag 304 and `batch` are used when Atlas has them |
| JSON | Newtonsoft.Json **10.0.3**, compiled against, **not shipped** | Playnite loads its own copy and cannot load two (Playnite docs); 10.0.3 is read from Playnite's `Playnite.csproj` |
| Windows | 10 / 11 (DPAPI token storage) | tests run on .NET Framework 4.8, Windows |

## Install and set up

1. Download `Atlas_Metadata-<version>.pext` from the release, check the `.sha256` next to it, and open the file with Playnite
   installed (installing a `.pext` into Playnite has not been tried yet, see the limits below).
2. Playnite > Add-ons > Extensions settings > **Atlas metadata**:
   - **Atlas address**: `https://your-atlas.example.org/`. Plain `http` is accepted for `localhost`; for another address you must tick the
     box (the token would travel unencrypted).
   - **Pair with Atlas**: the settings show a code and an address. Open the address on a device where you are signed in to Atlas and
     approve the code (only approve a code you see in Playnite). Or paste a read token made under your Atlas account.
   - **Test connection** shows the node name and client API version.
3. Playnite > Settings > Metadata: choose "Atlas" as a source for the fields you want. **The order between providers is Playnite's own
   setting**, there is no separate order inside this extension.

## What it does

| Playnite field | From Atlas | Note |
|---|---|---|
| Name | title | |
| Release date | `first_release` | exactly as precise as Atlas proves: year, year-month or full date. **A month or day is never invented.** An unparseable date gives no date |
| Developers, Publishers | developer, publisher | |
| Series | series name | |
| Genres, Tags | subjects | first 10 genres, the rest tags (decision D15.12) |
| Description | description | when Atlas has one |
| Links | an **Atlas** link | also how the connector remembers the Atlas id (Playnite has no hidden custom field) |
| Cover, Background, Icon | artwork | Atlas's `primary` per role, else the one marked preferred, else the first of that kind. An unknown kind is never guessed into a role |

Expected values for 9 records are in `contract/vectors/playnite.json` and are run by the unit tests.

**Identity first.** A Steam game is sent to Atlas by its Steam app id (`steamsync` namespace). Other libraries are sent as
title evidence plus Playnite's own game id. The connector applies an answer by itself only for the identity reasons `id`, `redirect`
and `crosswalk` (`CLIENT-CONTRACT` section 5); `exact` is Atlas's exact *title* match and is treated as a candidate. A title-based answer is **never applied by a background (bulk) download**; in a manual download
(game edit window) you pick from the candidates or search Atlas yourself.
Only Steam is wired because it is the only library whose Atlas namespace was checked (`resolve-contract-v1.json`); other libraries
get an id mapping when their namespace is confirmed.

**Images** are downloaded by the extension and handed to Playnite as bytes. The token is only ever in the `Authorization` header,
never in a URL. A redirect to another host (Atlas answers 307 for an image it has not cached) is followed once, without the token,
https only, images only, within a size limit. The public `media_url` is preferred when Atlas gives one.

**Offline.** A fetched record is cached under the plugin's data folder. When Atlas cannot be reached (network error, 5xx, 429), a
record younger than the configured number of days (default 7) is used. A merged Atlas id is followed and the Atlas link is
rewritten to the new id (the old link stays; delete it by hand if you like).

**Token at rest.** Stored with Windows DPAPI for the current user (`ProtectedToken` in the plugin settings file is not readable on
another account or machine). Never logged, never shown again after saving.

## Build, test, package

Windows, .NET SDK 8+ (developed with 10.0.402), Python 3.

```
dotnet build packages/playnite/Atlas.Playnite -c Release
dotnet test  packages/playnite/Atlas.Playnite.Tests -c Release     # runs on .NET Framework 4.8, no network
python packages/playnite/pack.py                                    # builds and writes packages/playnite/dist/Atlas_Metadata-<version>.pext
python packages/playnite/pack.py --verify packages/playnite/dist/Atlas_Metadata-0.1.0.pext
python -m pytest tests/test_playnite_package.py
```

`Atlas.Client` is compiled in as source (`dotnet/README.md`) with the Newtonsoft adapter. Layout:

| Path | What |
|---|---|
| `Atlas.Playnite/` | the extension: plugin, provider, `AtlasLookup` (resolve, cache, images), `GameMapper`, settings and WPF view |
| `Atlas.Playnite.Tests/` | xUnit on net48: vectors, provider and lookup against a fake HTTP handler over `contract/fixtures`, settings, DPAPI |
| `extension.yaml`, `icon.png`, `CHANGELOG.md` | the manifest and package files |
| `pack.py` | builds and packs the `.pext`, then re-opens it and checks it |

## Limits of version 0.1.0

- Not run inside a real Playnite. There is no headless Playnite, so the plugin class itself (`GetSettings`, `GetSettingsView`, the manual
  picker dialog) was compiled but not exercised; the provider is driven the way Playnite drives it, with a fake HTTP handler.
- The pairing endpoints are provisional until Atlas PR A24 (`docs/CLIENT-NOTES.md`). Pairing is tested in the shared client, not
  end to end against an Atlas, and not through the settings view model.
- No QR code is drawn; the settings show the code and the address (the QR in the design is rendered by Atlas).
- The `.pext` was packed by `pack.py`, not by Playnite's Toolbox, and has not been installed into Playnite.
- Add-on database listing is not automated: submissions are on hold (`docs/VENDOR-FACTS.md`).
