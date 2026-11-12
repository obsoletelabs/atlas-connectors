# dotnet/

`Atlas.Client` is the shared C# client for Atlas `client-api` v1 (see `../contract/CLIENT-CONTRACT.md`). It is **compiled
into each package as source**, never released and never a NuGet dependency (CONTRACT-C15 section 8.2): Playnite and
Jellyfin load every extension into one process with shared assemblies, and a separately versioned `Atlas.Client.dll`
would conflict between extensions.

| Project | What it is |
|---|---|
| `Atlas.Client/` | the sources. `netstandard2.0`, **C# 7.3** (consumers compile with their own, possibly old, language version), no dependency in `Core/` beyond the BCL |
| `Atlas.Client.Tests/` | xUnit, `net10.0`. A fake HTTP handler over `../contract/fixtures`; no sockets, no network |
| `Atlas.Client.SourceCheck/` | a `net462` library that takes the sources through the props file with **Newtonsoft only**. Build-only; proves the "compiled in as source" promise on the Playnite SDK's framework |

## Using it from a package

```xml
<!-- packages/jellyfin/Jellyfin.Plugin.Atlas.csproj -->
<PropertyGroup><AtlasJson>SystemTextJson</AtlasJson></PropertyGroup>   <!-- or Newtonsoft -->
<Import Project="..\..\dotnet\Atlas.Client\Atlas.Client.Source.props" />
```

The props file compiles `Core/**` plus the one adapter you pick, and defines `ATLAS_JSON_STJ` or `ATLAS_JSON_NEWTONSOFT`.
The package then needs only the serializer the host already ships. Types live in the `Atlas.Client` namespace and are
public; each package is its own assembly, so two packages in one host process do not clash.

```csharp
var options = new AtlasClientOptions
{
    BaseUri = new Uri("https://atlas.example.org/"),
    TokenProvider = () => myProtectedStore.ReadToken(),   // DPAPI on Playnite, plugin config on Jellyfin; never stored here
    HostName = "playnite", HostVersion = "0.3.1",          // User-Agent: atlas-connector-playnite/0.3.1
    Wire = new WireRange(1, 1),                            // Atlas-Wire: min=1, max=1
};
options.RequiredCapabilities.Add("books");
using var atlas = new AtlasClient(options, new NewtonsoftJsonAdapter());

var answers = await atlas.ResolveAsync(items);             // identity first, 200 per request, input order
foreach (var a in answers.Where(a => a.CanAutoAssign)) { /* store a.CanonicalId as ProviderIds["Atlas"] */ }
```

## What is in it

| Area | Where | Behaviour |
|---|---|---|
| Models | `Core/Models.cs`, `Core/AtlasReader.cs` | typed records for site, search, record, resolve, relations, artwork, books (authors, editions, series, subjects) and games. `PartialDate` is as precise as Atlas proves and never invents a day |
| Tolerant reader | `Core/AtlasReader.cs` | built on a neutral `JsonNode` tree, not on serializer attributes: unknown members are ignored (kept in `Raw`), unknown vocabulary values stay as text (`MediaTypeKind.Unknown`), a member of the wrong type reads as absent, nothing throws on a shape Atlas may add (client-api guarantees 1 and 2) |
| JSON adapters | `Core/Json.cs`, `Json/*` | `IJsonAdapter` = `Parse` / `Write` over the tree. `SystemTextJsonAdapter`, `NewtonsoftJsonAdapter`. Invalid JSON surfaces as `AtlasProtocolException`, never a serializer type |
| HTTP | `Core/AtlasClient*.cs` | bounded exponential backoff with jitter; `Retry-After` (seconds or date) honoured; a `Retry-After` above `MaxRetryAfter` is thrown (`AtlasRateLimitedException.RetryAfter`) instead of stalling a UI thread; only GET and the read-only POSTs (`/resolve`, `/games/batch`) are retried; every `await` is `ConfigureAwait(false)`; per-attempt timeout; response size cap; redirects are never followed on API calls |
| Version negotiation | `Core/Wire.cs` | sends `Atlas-Wire: min=, max=`; reads `use=`; no header in the answer = v0 Atlas = `WireInfo.IsDegraded` (works, show a notice); a `use` outside our range or a malformed header = `AtlasProtocolException`; 409 `wire_unsupported` = `AtlasWireUnsupportedException` with `Detail` to show verbatim and both ranges (C16 sections 3.2, 6, 10.1) |
| client-api check | `EnsureCompatibleAsync` | `/site` once per session; refuses a different `client_api` major or a missing required capability, naming it |
| Conditional GET | `GetRecordAsync(id, etag)` | sends `If-None-Match`; `RecordResponse.NotModified` on 304 (`ETag: "<id>-<version>"`, capability `etag`; an older Atlas returns the record) |
| Many records | `GetRecordsAsync` | `POST /games/batch` (200 per call) with the `batch` capability, reading `{requested, status, game}` items (`not_found` skipped), else one GET per id |
| Images | `DownloadArtworkAsync` | prefers public `media_url`; otherwise token in a header; a 307/308 is followed once, **without** `Authorization`, https only, `image/*` only, within `MaxImageBytes`; artwork URLs on another origin are refused |
| Pairing | `AtlasClient.Pairing.cs` | `StartPairingAsync`, `PollPairingAsync`, `PairAsync` (polls no faster than 5 s, +5 s on `slow_down`, expires locally). **Provisional wire names**, see `../docs/CLIENT-NOTES.md` |
| Safety | | token only in the `Authorization` header via a callback; `ToString()` of pairing objects omits secrets; plain `http` refused unless loopback or `AllowPlainHttp` |

## Running

```
dotnet build dotnet/Atlas.Client -c Release
dotnet build dotnet/Atlas.Client.SourceCheck -c Release
dotnet test  dotnet/Atlas.Client.Tests -c Release
```

A change under `dotnet/` or `contract/` selects the `dotnet` CI job (and every package, as before).
