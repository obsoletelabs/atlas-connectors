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

## Facts checked for the shared C# client (PR 2), 2026-11-09

| Fact | Result | Evidence |
|---|---|---|
| `netstandard2.0` is the right target to share code with .NET Framework | **Confirmed.** .NET Standard 2.0 is supported by .NET Framework 4.6.1 and later and by .NET Core 2.0+ / .NET 5-10. Microsoft advises `netstandard2.0` to share code between .NET Framework and the rest | `https://learn.microsoft.com/en-us/dotnet/standard/net-standard` |
| .NET Framework 4.6.1/4.6.2 consuming .NET Standard 2.0 has issues | **Confirmed caveat, new.** The same page says NuGet accepts 4.6.1 for .NET Standard 1.5-2.0 but "there are several issues" consuming such libraries from 4.6.1 projects and recommends 4.7.2 or higher. Playnite's SDK is `net462`, so a Playnite package should **compile the client in as source** (as planned) rather than reference a netstandard DLL. `Atlas.Client.SourceCheck` proves the sources build for `net462` | same page; `Atlas.Client.SourceCheck` build |
| Playnite SDK targets .NET Framework 4.6.2 | **Reconfirmed** (`net462`; the SDK project has no `PackageReference` at all) | `https://raw.githubusercontent.com/JosefNemec/Playnite/master/source/PlayniteSDK/Playnite.SDK.csproj` |
| "Playnite loads every extension with its own Newtonsoft.Json" (design notes) | **Not confirmed, and doubtful.** Old SDK packages (5.2.0) depended on `Newtonsoft.Json >= 10.0.0`; the current SDK project has no package reference, the SDK exposes its own `Serialization` class, and the Playnite 11 developer manual says the SDK dropped some non-Playnite features such as serialization APIs and that .NET's built-in JSON is good enough. Which serializer a Playnite package may rely on is therefore **open**; this is why the client takes a serializer adapter (`NewtonsoftJsonAdapter` or `SystemTextJsonAdapter`) and a package picks one with `AtlasJson`. Decide in PR X1 against the Playnite version it targets | `https://api.playnite.link/docs/api/Playnite.SDK.Data.Serialization.html`, `https://packages.nuget.org/packages/PlayniteSDK/5.2.0`, `https://api.playnite.link/docs/wpfdevel11/manual/plugins.html` (search results; page bodies of the last two not read in full) |
| Playnite refuses plugins that reference non-SDK Playnite assemblies | **Seen in a search snippet** (SDK 5.3.0 onward); not re-read from the source page | same search |
| Jellyfin plugin targets `net9.0` against `Jellyfin.Controller`/`Jellyfin.Model` 10.11.5 | **Confirmed** for the current template (no `PackageReference` to System.Text.Json: the host provides it). Matches the 10.11 -> net9.0 pairing; the 10.10 -> net8.0 pairing is still **unverified** | `https://raw.githubusercontent.com/jellyfin/jellyfin-plugin-template/master/Jellyfin.Plugin.Template/Jellyfin.Plugin.Template.csproj` |
| `System.Text.Json` is what the Jellyfin host uses | **Not verified.** The template referencing no JSON package is consistent with it but does not prove it | to check in X2 |

What is **not** verified by running anything: the client has never been run on .NET Framework 4.6.2 or inside Playnite or
Jellyfin. Its tests run on `net10.0` against a fake HTTP handler.


## Facts checked for the Playnite package (PR 3), 2026-11-09

| Fact | Result | Evidence |
|---|---|---|
| The SDK package restores and compiles here | **Confirmed.** `PlayniteSDK` **6.18.0** (latest on nuget.org at the time, from the flat-container index) restores for `net462` with the .NET SDK 10.0.402; the extension builds against it | `dotnet build packages/playnite/Atlas.Playnite`; `https://api.nuget.org/v3-flatcontainer/playnitesdk/index.json` |
| Metadata plugin shape | **Confirmed by reflecting over the restored DLL and by the docs**: `MetadataPlugin` (abstract `Name`, `SupportedFields`, `GetMetadataProvider(MetadataRequestOptions)`), `Plugin.Id` abstract `Guid`, `OnDemandMetadataProvider` (`AvailableFields` plus `Get*` per field, `IDisposable`), `MetadataRequestOptions.IsBackgroundDownload` distinguishes bulk from manual | `https://api.playnite.link/docs/tutorials/extensions/metadataPlugins.html`, SDK 6.18.0 assembly |
| Partial release dates | **Confirmed** (was unverified). `ReleaseDate` has constructors for year, year+month and year+month+day, with nullable `Month` and `Day` | `https://raw.githubusercontent.com/JosefNemec/Playnite/master/source/PlayniteSDK/Models/ReleaseDate.cs` |
| Images as bytes | **Confirmed.** `MetadataFile(string name, byte[] content)` exists | SDK 6.18.0 assembly |
| Playnite's Newtonsoft version | **Confirmed for master**: `Newtonsoft.Json` **10.0.3** is a package reference of the `Playnite` project. This settles the open point of PR 2 (the design note "Playnite's own Newtonsoft" was right, but the version is 10.0.3, not 13). The Playnite docs say a plugin must use the version Playnite already references, because two versions cannot be loaded. The extension therefore compiles against 10.0.3 and does not ship it | `https://raw.githubusercontent.com/JosefNemec/Playnite/master/source/Playnite/Playnite.csproj`; `https://api.playnite.link/docs/tutorials/extensions/plugins.html` |
| Newtonsoft 10.0.3 has a known DoS advisory (fixed in 13.0.1) | **Accepted and mitigated**: the running copy is Playnite's own, and the adapter caps depth at 64 (`MaxDepth`). The advisory warning is suppressed in the two projects, with a comment | NuGet audit warning NU1903 on restore |
| Toolbox `pack` | **Documented, not run.** `Toolbox.exe pack <extensionfolder> <targetfolder>` ships in a Playnite install, which is absent here. `packages/playnite/pack.py` writes the zip itself and re-checks it. A `.pext` being a zip with `extension.yaml` at the root is the assumption; it is **unverified** against a Toolbox-made file | `https://api.playnite.link/docs/tutorials/toolbox.html` |
| `Toolbox.exe verify` | Validates **add-on database manifests** (`addon`, `installer`), not the package itself | same page |
| Steam library plugin id and `GameId` | **Confirmed.** Plugin id `CB91DFC9-B977-43BF-8E70-55F46E410FAB`; `GameId` is the Steam GameID as a number string (it is a 64-bit id that also covers mods, so the connector sends only values that fit a 32-bit app id). GOG is `AEBE8B7C-6DC3-4A66-AF31-E7375C6B5E9E`, Epic `00000002-DBD1-46C6-B5D0-B1BA559D10E4` (read, not used: Atlas namespaces for them are unknown) | `PlayniteExtensions/source/Libraries/SteamLibrary/SteamLibrary.cs`, `Services/SteamLocalService.cs`, `GameExtension.cs` |
| Atlas namespaces | Only `steamsync` and `igdbsync` appear in `resolve-contract-v1.json`. Namespace `playnite` (for unmapped libraries) is **not confirmed to be accepted by a live Atlas**; the Atlas docs mention "a Playnite identity" without a wire name | `contract/fixtures/resolve/resolve-contract-v1.json`; Atlas `docs/authority-modes.md` |
| Atlas web page for a record | `/game/{cid}` exists in the Atlas programme clone (`atlas/web/routes.py`); the link written into Playnite uses it | read-only look at the local Atlas clone |
| Settings and ISettings | **Confirmed.** `ISettings : IEditableObject` plus `VerifySettings`; view is a WPF `UserControl` with the settings object as data context; plugin data folder via `GetPluginUserDataPath()`; `Properties.HasSettings = true` | `https://api.playnite.link/docs/tutorials/extensions/pluginSettings.html`, SDK assembly |
| Plugin loads only SDK assemblies | **Confirmed** by the plugin docs ("DO NOT reference non-SDK Playnite assemblies"); linking source is the suggested route (as `Atlas.Client` does) | `https://api.playnite.link/docs/tutorials/extensions/plugins.html` |
| No headless Playnite | still **unverified**; none was looked for beyond the docs, which describe attaching a debugger to a running Playnite | plugins.html |

What was **not** done: the extension was never loaded by a running Playnite; no Atlas (real or local) was contacted by the
extension; `Toolbox.exe pack` was never run; Playnite 11 was not looked at.


## Facts checked for the Jellyfin package (PR 4), 2026-11-09

| Fact | Result | Evidence |
|---|---|---|
| Supported ABIs / frameworks (design notes: 10.10 -> net8.0, 10.11 -> net9.0) | **Corrected.** Current stable Jellyfin is **12.4** (released 2026-10-26; 12.0 on 2026-09-08). NuGet `Jellyfin.Controller` 10.11.x targets **net9.0**, 12.x targets **net10.0**. The live plugin manifest lists `targetAbi` values 10.9.0.0, 10.10.x, 10.11.x and **12.0.0.0, 12.1.0.0, 12.2.0.0**. So the package builds for 10.11 (net9.0) and 12 (net10.0); 10.10/net8.0 is not built. The plugin template on `master` still shows 10.11.5 / net9.0 and a README that says net8.0: it lags | `https://api.github.com/repos/jellyfin/jellyfin/releases`; `https://api.nuget.org/v3-flatcontainer/jellyfin.controller/<ver>/jellyfin.controller.nuspec` (10.11.11, 12.0.0, 12.4.0); `https://lon1.mirror.jellyfin.org/files/plugin/manifest.json` (the `repo.jellyfin.org` URL answers a 302 to this mirror; first 100000 of 201833 characters read) |
| Server licence | **Mixed signals, unresolved.** The repository `LICENSE` is GPL v2 (text read). The NuGet packages `Jellyfin.Controller` 10.11.11 / 12.x declare **GPL-3.0-only** in their nuspec. Whether the server is "2 only" or "3" for plugin purposes is therefore unclear. A plugin under MIT is compatible with either; AGPL would not be with GPL-2.0-only. The `LICENSING.md` conclusion stands, but its "GPL-2.0" statement is incomplete | `https://raw.githubusercontent.com/jellyfin/jellyfin/master/LICENSE`; nuspec files above |
| Interfaces `IRemoteMetadataProvider<T,TInfo>`, `IRemoteImageProvider`, `IExternalId` | **Confirmed** from the `release-10.11.z` source and by compiling against the 10.11.11 and 12.4.0 packages. `IExternalId` has `ProviderName`, `Key`, `Type` (`ExternalIdMediaType?`), `Supports(IHasProviderIds)` and no URL member in 10.11. Series lookup type is `MediaBrowser.Controller.Providers.SeriesInfo` (it clashes by name with `Atlas.Client.SeriesInfo`) | `https://raw.githubusercontent.com/jellyfin/jellyfin/release-10.11.z/MediaBrowser.Controller/Providers/*.cs`; `dotnet build` of both ABIs |
| Package versions must match the server | Re-read from the template README ("otherwise the plugin will show as NotSupported"). Behaviour at a different patch level of the same line is **unverified** | template README |
| Manifest entry keys and zip layout | **Confirmed** from the `jprm` source: zip `<slug>_<four-part version>.zip` holding the artifacts and `meta.json` (`guid, name, description, overview, owner, category, version, changelog, targetAbi, timestamp`), MD5 of the zip as `checksum`, manifest `versions[]` with `sourceUrl, checksum, timestamp, targetAbi, version, changelog`. Plugin entries may also carry `imageUrl`. `jprm` itself was **not run** | `https://raw.githubusercontent.com/oddstr13/jellyfin-plugin-repository-manager/master/jprm/__init__.py`, via a page summary |
| `build.yaml` fields | Same as PR 2 (template); `category` value `Metadata` is not checked against a list | template `build.yaml` |
| Which entry a server picks when several versions match | **Unverified.** The package uses distinct version numbers per ABI (`0.1.0.0` for 10.11, `0.1.0.1` for 12) to avoid the question | |
| Config page markup (`emby-input`, `Dashboard.showLoadingMsg`, `ApiClient.ajax`) and admin policy name `RequiresElevation` | **Unverified**: written from memory of the template/other plugins, never rendered in a dashboard | |
| Headless container scan in CI (CONTRACT-C15 12) | **Not attempted**: no Docker/Jellyfin server was used in this step | |
| Jellyfin uses `System.Text.Json` | The packages compile and the adapter works under the test host; which serializer the server's own API uses was not checked. The plugin only needs that the runtime ships it, which both target frameworks do | |

What was **not** done: no Jellyfin server loaded the plugin; no real Atlas was contacted; `jprm` was not run; the release workflow has not run on GitHub.

## Facts checked for the client-api 1.0 adoption (PR 5 fix pass), 2026-11-12

| Fact | Result | Evidence |
|---|---|---|
| Jellyfin `PersonKind` has `Author`, `Translator`, `Illustrator`, `Editor` and `Unknown` (used for book creator roles) | **Confirmed** in both supported ABIs: the names are in the `Jellyfin.Data` assemblies 10.11.11 (`net9.0`) and 12.4.0 (`net10.0`), and `ItemMapper.PersonKindOf` compiles against both in CI. There is no `Narrator` kind, so `narrator` (and any role Atlas adds later) maps to `Unknown`. Display of these kinds on a book page in a real server is **unverified** | `~/.nuget/packages/jellyfin.data/{10.11.11,12.4.0}/lib/*/Jellyfin.Data.dll`; `contract/vectors/jellyfin.json` creator-roles case |
| Atlas resolves an ISBN through `/resolve` with namespace `isbn` | **Confirmed in source, not live**: `isbn` is Atlas's crosswalk namespace of ISBN-13s (`atlas/books.py` `ISBN_NAMESPACE`, C15 2.5) and `/resolve` looks provider ids up in the crosswalk by `(namespace, external_id)` (`atlas/resolve.py` -> `matching.match`), so only the bare ISBN-13 matches; the connector normalises first. `GET /games?q=<isbn>` answers the work alone (`service._isbn_hit`, OpenAPI `q`, `books/search-isbn.json`) | Atlas `a6bbc2a` source; recorded fixture |
| Atlas `PairBody.label` "Printable" | **Confirmed in source**: `pairing.clean_label` refuses a label that is empty, longer than 64 code points or not `str.isprintable()` (which is false for control, format, private-use, unassigned, surrogate and every separator except the ASCII space) | Atlas `a6bbc2a` `atlas/pairing.py`, `atlas/api/routes.py` `PairBody` |
| Atlas has `tmdb` / `imdb` / `tvdb` / `anilist` namespaces | **No**: no provider in Atlas `a6bbc2a` writes them; the Jellyfin namespace names for movies and series stay provisional | `grep` of Atlas `a6bbc2a` `atlas/` |
