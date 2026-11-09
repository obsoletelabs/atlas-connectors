# Atlas for Jellyfin

A Jellyfin **plugin** (`Jellyfin.Plugin.Atlas`) that fills metadata, ids and images for **books, movies and series** from your own
[Atlas](https://github.com/obsoletelabs) node. Version **0.1.0** (see `CHANGELOG.md`; tags are `jellyfin-v<version>`). MIT, like the
rest of this repository.

It only reads. It never writes to Atlas. It does not cover games (Jellyfin has no game library).

**Honest status:** the plugin compiles against the real Jellyfin packages for two ABIs and its logic is tested against the real
Jellyfin entity classes, but it has **never been loaded by a running Jellyfin server** (see "Not verified" below).

## Compatibility

| Jellyfin | `targetAbi` | Plugin version in the manifest | Framework | `Jellyfin.Controller` / `Jellyfin.Model` built against | Status |
|---|---|---|---|---|---|
| 10.11.x | `10.11.0.0` | `0.1.0.0` | `net9.0` | `10.11.11` | builds; 36 tests pass (see below). **Not loaded in a server** |
| 12.x | `12.0.0.0` | `0.1.0.1` | `net10.0` | `12.4.0` | builds; 36 tests pass. **Not loaded in a server** |
| 10.10 and older | not built | | | | `net8.0`, not attempted: the current official plugin template and every recent plugin in the official repository manifest target 10.11 or newer |

| Item | Value |
|---|---|
| `min_client_api` | **1.0** (`client-api` v1, `contract/CLIENT-CONTRACT.md`). An Atlas without the `client_api` block (before Atlas PR A24) still works |
| Atlas data needed | books: Atlas `book` records (Atlas books PRs); movies/series: only where the node has such records (Atlas has game providers only today, CONTRACT-C15 8.1) |
| JSON | `System.Text.Json`, provided by the server; `Atlas.Client` is compiled in as source (`dotnet/README.md`) |
| What is shipped in the zip | `Jellyfin.Plugin.Atlas.dll` and `meta.json` only |

Jellyfin warns that the package version of `Jellyfin.Controller`/`Jellyfin.Model` must match the server or the plugin shows
"NotSupported" (plugin template README). Each ABI therefore pins the newest package of its line (`abis.json`); a server at a
different patch level of the same line is **not verified**.

## Install

1. Dashboard > Plugins > Repositories > add `https://<owner>.github.io/atlas-provider-adapters/jellyfin/manifest.json` (this URL exists only
   after the first `jellyfin-v*` release has been published). Or download a zip from the release, check its `.md5sum`, and unpack it into
   the server's `plugins/Atlas_<version>/` folder (manual install, see the Jellyfin docs). Restart the server.
2. Dashboard > Plugins > **Atlas**: enter the Atlas address, press **Pair with Atlas** (a code and an address appear; open the address where
   you are signed in to Atlas, and approve only a code shown on this page) or paste a read token, press **Test connection**, **Save**.
3. Library settings > metadata downloaders for Books, Movies and Shows: tick **Atlas** and order it relative to other providers.
   **The order between providers is Jellyfin's own per-library setting**; there is no separate order inside the plugin.

## What it does

| Jellyfin field | From Atlas | Note |
|---|---|---|
| `Name` | title | |
| `PremiereDate` | `first_release` | **only at day precision**; an impossible day (30 February) gives none. A day is never invented |
| `ProductionYear` | `first_release` year | set at every precision |
| `Overview` | description | when Atlas has one |
| `Genres`, `Tags` | subjects | first 10 genres, the rest tags (decision D15.12), duplicates dropped |
| `Studios` | developer, publisher | de-duplicated |
| Book `SeriesName`, `IndexNumber` | series name and position | the index only when the position is a whole number (`2`), never for `2.5` or `II` |
| Book authors | `authors[]` | as `Author` people; blank names dropped |
| `ProviderIds["Atlas"]` | Atlas id | shown as an external id on books, movies and series; a merged id is followed and replaced |
| Book `ProviderIds["Isbn"]` | `provider_ids.isbn` | |
| Primary / Backdrop / Logo images | artwork | Atlas's `primary` per role, else the preferred one, else the first of that kind (`cover`/`poster` -> Primary). An unknown kind is never guessed into a role |

Expected values for 8 cases (books, movie, series, anime, partial dates, unknown members) are in `contract/vectors/jellyfin.json` and are run by the unit
tests against the real `Book`, `Movie` and `Series` classes.

**Identity first.** `Isbn`, `Tmdb`, `Imdb`, `Tvdb` and `AniList` provider ids are sent to Atlas `POST /resolve` as `isbn`, `tmdb`, `imdb`, `tvdb`, `anilist`
(**the namespace names are provisional**: Atlas has not published them). Atlas assigns by itself only for the reasons `id`, `redirect`,
`crosswalk` and `exact`. **A title alone never applies a record**: a scan of an item with no known id gets nothing from this plugin. Use
**Identify** on the item: the plugin searches Atlas (right media type only) and writes the Atlas id you pick, which later scans then use.

**Series provider scope.** Atlas `tv_show`, `series` and `anime` records are offered to Jellyfin *series*; `movie` records to *movies*. An anime
film is therefore not matched (Atlas `anime` is ambiguous between the two).

**Images** are downloaded by the plugin (`GetImageResponse`); the token is only in the `Authorization` header, never in a URL. The image list uses
Atlas's public `media_url` when present. A redirect to another host (Atlas answers 307 for an image it has not cached) is followed once, without the
token, https only, images only, within a size limit. A URL on any host other than your Atlas is refused. For an image Atlas has not cached, the
URL in Jellyfin's image picker is the Atlas image path, which a browser cannot open without the token, so the thumbnail in the picker may be blank
while the download itself works (unverified in the web UI).

**Offline.** Fetched records are cached under the server's plugin configuration folder (`Jellyfin.Plugin.Atlas.cache`). When Atlas cannot be
reached (network error, 5xx, 429), a record younger than the configured days (default 7) is used. Nothing here throws into a library scan.

**Token at rest.** The token is **not** in the plugin configuration XML, so the `Plugins/{id}/Configuration` API never returns it. It is a file
`Jellyfin.Plugin.Atlas.token` next to the configuration files (mode `0600` on Unix; Windows uses the folder's ACL). It is **not encrypted**: any
process that can read the server's configuration folder can read it. The settings page never shows it again after saving; the endpoints it calls
(`/Atlas/Settings`, `/Atlas/Test`, `/Atlas/Pairing/*`) require the server's admin policy `RequiresElevation` (name taken from other plugins; **not
verified** against a running server).

## Build, test, pack

```
dotnet test packages/jellyfin/Jellyfin.Plugin.Atlas.Tests -c Release -p:JellyfinAbi=10.11     # net9.0 ABI
dotnet test packages/jellyfin/Jellyfin.Plugin.Atlas.Tests -c Release -p:JellyfinAbi=12        # net10.0 ABI
python packages/jellyfin/pack.py                                                              # one zip per ABI into dist/
python tools/gen_catalogues.py jellyfin --artifacts packages/jellyfin/dist --download-base <url> --out catalogues/jellyfin/manifest.json
```

`build.yaml` (10.11) and `build-abi12.yaml` follow the official plugin template. `pack.py` writes the zip layout `jprm` writes, but `jprm`
itself is not used or run. Release: tag `jellyfin-v0.1.0`; `.github/workflows/release-jellyfin.yml` builds, packs, regenerates the manifest from
the zips, attaches them to a GitHub release and deploys `manifest.json` to Pages.

## Not verified

- **No Jellyfin server has loaded this plugin.** Plugin registration, the config page rendering inside the dashboard, the admin policy name, how
  Jellyfin orders providers and picks among two manifest entries, the Identify dialog and the image picker are all untested. The CI plan
  in CONTRACT-C15 section 12 (start the official Jellyfin container per ABI, scan a fixture tree, assert `ProviderIds.Atlas`) is **not done**:
  neither Docker nor a Jellyfin server was used here.
- **No real Atlas was contacted.** Tests use a fake HTTP handler over `contract/fixtures`. The `isbn/tmdb/imdb/tvdb/anilist` namespaces, the pairing
  wire names and book/movie/series records are provisional until Atlas ships client-api (PR A24) and generates the fixtures.
- Local test runs: the 10.11 (`net9.0`) tests ran on the **.NET 10** runtime through roll-forward, because the ASP.NET Core 9 runtime is not
  installed on the author's machine. CI installs .NET 9 and 10 and runs each ABI on its own runtime; that has not run yet either.
- The release workflow has not run on GitHub. `upload-pages-artifact@v4` and `deploy-pages@v4` and the Pages setting are unchecked.
- Jellyfin 10.10 and older (`net8.0`) are not built.
- StyleCop/analyzer rules of the official template are not enabled (compiler warnings are errors, analyzers are not installed).
