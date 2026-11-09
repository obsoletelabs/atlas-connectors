using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Atlas.Client;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Atlas;

/// <summary>Shared behaviour of the three metadata providers: settings gate, lookup, mapping, search.</summary>
public abstract class AtlasProviderBase<TItem, TInfo>
    where TItem : BaseItem, IHasLookupInfo<TInfo>
    where TInfo : ItemLookupInfo, new()
{
    private readonly Func<AtlasRuntime> _runtime;

    protected AtlasProviderBase(Func<AtlasRuntime> runtime)
    {
        _runtime = runtime ?? (() => AtlasRuntime.Current);
    }

    public string Name => "Atlas";

    protected abstract ItemKind Kind { get; }

    protected abstract bool Enabled(Configuration.PluginConfiguration c);

    protected abstract MetadataResult<TItem> Map(AtlasRecord record);

    public async Task<MetadataResult<TItem>> GetMetadata(TInfo info, CancellationToken cancellationToken)
    {
        var none = new MetadataResult<TItem> { HasMetadata = false };
        var rt = _runtime();
        var lookup = rt != null && Enabled(rt.Config) ? rt.GetLookup() : null;
        if (lookup == null)
        {
            return none;
        }

        var result = await lookup.FindAsync(Query(info), cancellationToken).ConfigureAwait(false);
        if (result.Outcome != LookupOutcome.Found || result.Record == null)
        {
            if (result.Outcome == LookupOutcome.Unavailable)
            {
                rt.Log("Atlas: " + result.Message);
            }

            return none;
        }

        // Always carries the (possibly merged) current Atlas id: the old id is replaced silently.
        return Map(result.Record);
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(TInfo searchInfo, CancellationToken cancellationToken)
    {
        var rt = _runtime();
        var lookup = rt != null && Enabled(rt.Config) ? rt.GetLookup() : null;
        if (lookup == null)
        {
            return Array.Empty<RemoteSearchResult>();
        }

        var records = await lookup.SearchAsync(Query(searchInfo), cancellationToken).ConfigureAwait(false);
        return records.Select(r => ItemMapper.ToSearchResult(r, null)).ToList();
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => AtlasImageProvider.FetchAsync(_runtime(), url, cancellationToken);

    private ItemQuery Query(TInfo info)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (info.ProviderIds != null)
        {
            foreach (var kv in info.ProviderIds)
            {
                ids[kv.Key] = kv.Value;
            }
        }

        return new ItemQuery { Kind = Kind, Name = info.Name, Year = info.Year, ProviderIds = ids };
    }
}

public sealed class AtlasBookProvider : AtlasProviderBase<Book, BookInfo>, IRemoteMetadataProvider<Book, BookInfo>
{
    public AtlasBookProvider()
        : base(null)
    {
    }

    internal AtlasBookProvider(Func<AtlasRuntime> runtime)
        : base(runtime)
    {
    }

    protected override ItemKind Kind => ItemKind.Book;

    protected override bool Enabled(Configuration.PluginConfiguration c) => c.EnableBooks;

    protected override MetadataResult<Book> Map(AtlasRecord record) => ItemMapper.ToBook(record);
}

public sealed class AtlasMovieProvider : AtlasProviderBase<Movie, MovieInfo>, IRemoteMetadataProvider<Movie, MovieInfo>
{
    public AtlasMovieProvider()
        : base(null)
    {
    }

    internal AtlasMovieProvider(Func<AtlasRuntime> runtime)
        : base(runtime)
    {
    }

    protected override ItemKind Kind => ItemKind.Movie;

    protected override bool Enabled(Configuration.PluginConfiguration c) => c.EnableMovies;

    protected override MetadataResult<Movie> Map(AtlasRecord record) => ItemMapper.ToMovie(record);
}

public sealed class AtlasSeriesProvider : AtlasProviderBase<Series, MediaBrowser.Controller.Providers.SeriesInfo>, IRemoteMetadataProvider<Series, MediaBrowser.Controller.Providers.SeriesInfo>
{
    public AtlasSeriesProvider()
        : base(null)
    {
    }

    internal AtlasSeriesProvider(Func<AtlasRuntime> runtime)
        : base(runtime)
    {
    }

    protected override ItemKind Kind => ItemKind.Series;

    protected override bool Enabled(Configuration.PluginConfiguration c) => c.EnableSeries;

    protected override MetadataResult<Series> Map(AtlasRecord record) => ItemMapper.ToSeries(record);
}

/// <summary>Images for books, movies and series. The bytes are fetched by the plugin; the token is only ever in a header.</summary>
public sealed class AtlasImageProvider : IRemoteImageProvider
{
    private readonly Func<AtlasRuntime> _runtime;

    public AtlasImageProvider()
        : this(null)
    {
    }

    internal AtlasImageProvider(Func<AtlasRuntime> runtime)
    {
        _runtime = runtime ?? (() => AtlasRuntime.Current);
    }

    public string Name => "Atlas";

    public bool Supports(BaseItem item) => item is Book || item is Movie || item is Series;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        => new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Logo };

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var list = new List<RemoteImageInfo>();
        var rt = _runtime();
        var lookup = rt?.GetLookup();
        var baseUri = rt?.BaseUri;
        var id = item.ProviderIds == null ? null : AtlasLookup.AtlasIdOf(item.ProviderIds);
        if (lookup == null || baseUri == null || id == null)
        {
            return list;
        }

        var art = await lookup.ArtworkAsync(id, cancellationToken).ConfigureAwait(false);
        if (art == null)
        {
            return list;
        }

        Add(list, AtlasLookup.Pick(art, "cover", "poster"), ImageType.Primary, baseUri);
        Add(list, AtlasLookup.Pick(art, "background"), ImageType.Backdrop, baseUri);
        Add(list, AtlasLookup.Pick(art, "logo"), ImageType.Logo, baseUri);
        return list;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => FetchAsync(_runtime(), url, cancellationToken);

    /// <summary>
    /// Download an image listed by <see cref="GetImages"/>. The URL is checked against the configured Atlas origin by the client
    /// (a foreign host is refused, so the token cannot be sent elsewhere); <c>/media/</c> URLs are public and sent without the token.
    /// </summary>
    internal static async Task<HttpResponseMessage> FetchAsync(AtlasRuntime rt, string url, CancellationToken ct)
    {
        var lookup = rt?.GetLookup();
        if (lookup == null || string.IsNullOrWhiteSpace(url))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var isMedia = url.IndexOf("/media/", StringComparison.Ordinal) >= 0;
        var art = new Artwork { MediaUrl = isMedia ? url : null, ImageUrl = isMedia ? null : url };
        try
        {
            var data = await lookup.DownloadAsync(art, ct).ConfigureAwait(false);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data.Bytes) };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(data.ContentType, out var ctype) ? ctype : new MediaTypeHeaderValue("image/jpeg");
            return response;
        }
        catch (AtlasException e)
        {
            rt.Log("Atlas image not downloaded: " + e.Message);
            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        }
    }

    private static void Add(List<RemoteImageInfo> list, Artwork a, ImageType type, Uri baseUri)
    {
        if (a == null)
        {
            return;
        }

        list.Add(new RemoteImageInfo
        {
            ProviderName = "Atlas",
            Type = type,
            Url = AtlasLookup.PublicUrlFor(a, baseUri),
            Width = a.Width.HasValue ? (int?)Math.Min(a.Width.Value, int.MaxValue) : null,
            Height = a.Height.HasValue ? (int?)Math.Min(a.Height.Value, int.MaxValue) : null,
        });
    }
}

/// <summary>"Atlas" appears as an external id on books, movies and series (stored in <c>ProviderIds["Atlas"]</c>).</summary>
public abstract class AtlasExternalIdBase : IExternalId
{
    public string ProviderName => "Atlas";

    public string Key => ItemMapper.AtlasKey;

    public ExternalIdMediaType? Type => null;

    public abstract bool Supports(IHasProviderIds item);
}

public sealed class AtlasBookExternalId : AtlasExternalIdBase
{
    public override bool Supports(IHasProviderIds item) => item is Book;
}

public sealed class AtlasMovieExternalId : AtlasExternalIdBase
{
    public override bool Supports(IHasProviderIds item) => item is Movie;
}

public sealed class AtlasSeriesExternalId : AtlasExternalIdBase
{
    public override bool Supports(IHasProviderIds item) => item is Series;
}

/// <summary>
/// The link from an item to its record page on the configured Atlas node (CONTRACT-C15 8.4: "an IExternalId with a link to the record
/// page"; since Jellyfin 10.10 the link comes from an <see cref="IExternalUrlProvider"/>, not from the IExternalId). The page path
/// <c>/game/{id}</c> is the one the Playnite package links to; it is Atlas's web page for every media type (PROVISIONAL until checked
/// against a live Atlas). The URL carries no token.
/// </summary>
public sealed class AtlasExternalUrlProvider : IExternalUrlProvider
{
    private readonly Func<AtlasRuntime> _runtime;

    public AtlasExternalUrlProvider()
        : this(null)
    {
    }

    internal AtlasExternalUrlProvider(Func<AtlasRuntime> runtime)
    {
        _runtime = runtime ?? (() => AtlasRuntime.Current);
    }

    public string Name => "Atlas";

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        var baseUri = _runtime()?.BaseUri;
        var id = item is Book || item is Movie || item is Series ? AtlasLookup.AtlasIdOf(item.ProviderIds) : null;
        if (baseUri == null || id == null)
        {
            yield break;
        }

        yield return RecordPage(baseUri, id);
    }

    public static string RecordPage(Uri baseUri, string atlasId)
        => baseUri.AbsoluteUri.TrimEnd('/') + "/game/" + Uri.EscapeDataString(atlasId).Replace("%3A", ":", StringComparison.Ordinal);
}
