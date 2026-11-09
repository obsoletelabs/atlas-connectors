using System;
using System.Globalization;
using System.Linq;
using Atlas.Client;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Atlas;

/// <summary>
/// Atlas record to Jellyfin item fields (the expected values are in contract/vectors/jellyfin.json).
/// Rules (CONTRACT-C15 8.4, CLIENT-CONTRACT 6): <c>PremiereDate</c> only at day precision, otherwise <c>ProductionYear</c> only, a day is
/// never invented; unknown vocabulary and unknown fields are ignored; subjects fill Genres first (10) and Tags after (D15.12).
/// </summary>
public static class ItemMapper
{
    public const int GenreLimit = 10;
    public const string AtlasKey = "Atlas";
    public const string IsbnKey = "Isbn";

    public static MetadataResult<Book> ToBook(AtlasRecord r)
    {
        var book = new Book();
        var result = new MetadataResult<Book> { Item = book, HasMetadata = true };
        ApplyCommon(book, r);
        if (r.Series != null && !string.IsNullOrWhiteSpace(r.Series.Name))
        {
            book.SeriesName = r.Series.Name.Trim();

            // only a whole number is an index; "2.5" or "II" is left unset rather than rounded or guessed
            if (int.TryParse(r.Series.Position, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
            {
                book.IndexNumber = n;
            }
        }

        if (r.ProviderIds.TryGetValue("isbn", out var isbn) && !string.IsNullOrWhiteSpace(isbn))
        {
            book.SetProviderId(IsbnKey, isbn.Trim());
        }

        foreach (var a in r.Authors.Where(a => !string.IsNullOrWhiteSpace(a.Name)))
        {
            result.AddPerson(new PersonInfo { Name = a.Name.Trim(), Type = PersonKind.Author });
        }

        return result;
    }

    public static MetadataResult<Movie> ToMovie(AtlasRecord r)
    {
        var movie = new Movie();
        ApplyCommon(movie, r);
        return new MetadataResult<Movie> { Item = movie, HasMetadata = true };
    }

    public static MetadataResult<Series> ToSeries(AtlasRecord r)
    {
        var series = new Series();
        ApplyCommon(series, r);
        return new MetadataResult<Series> { Item = series, HasMetadata = true };
    }

    public static void ApplyCommon(BaseItem item, AtlasRecord r)
    {
        if (r == null)
        {
            throw new ArgumentNullException(nameof(r));
        }

        item.Name = r.Title;
        if (!string.IsNullOrWhiteSpace(r.Description))
        {
            item.Overview = r.Description;
        }

        var d = r.FirstRelease;
        if (d.HasValue && d.Value.Year >= 1 && d.Value.Year <= 9999)
        {
            item.ProductionYear = d.Value.Year;
            var day = DayOf(d.Value);
            if (day.HasValue)
            {
                item.PremiereDate = day;
            }
        }

        var subjects = r.Subjects.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (subjects.Count > 0)
        {
            item.Genres = subjects.Take(GenreLimit).ToArray();
        }

        if (subjects.Count > GenreLimit)
        {
            item.Tags = subjects.Skip(GenreLimit).ToArray();
        }

        var studios = new[] { r.Developer, r.Publisher }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (studios.Length > 0)
        {
            item.Studios = studios;
        }

        if (!string.IsNullOrEmpty(r.Id))
        {
            item.SetProviderId(AtlasKey, r.Id);
        }
    }

    /// <summary>A date only when Atlas proved the day and the day exists. Otherwise null: the year alone is carried by ProductionYear.</summary>
    public static DateTime? DayOf(PartialDate d)
    {
        if (!d.Month.HasValue || !d.Day.HasValue || d.Year < 1 || d.Year > 9999)
        {
            return null;
        }

        var m = d.Month.Value;
        if (m < 1 || m > 12 || d.Day.Value < 1 || d.Day.Value > DateTime.DaysInMonth(d.Year, m))
        {
            return null;
        }

        return new DateTime(d.Year, m, d.Day.Value, 0, 0, 0, DateTimeKind.Utc);
    }

    public static RemoteSearchResult ToSearchResult(AtlasRecord r, string imageUrl)
    {
        var s = new RemoteSearchResult
        {
            Name = r.Title,
            SearchProviderName = "Atlas",
            ImageUrl = imageUrl,
            ProductionYear = r.FirstRelease.HasValue ? r.FirstRelease.Value.Year : (int?)null,
            Overview = r.Description,
            PremiereDate = r.FirstRelease.HasValue ? DayOf(r.FirstRelease.Value) : null,
        };
        s.SetProviderId(AtlasKey, r.Id);
        return s;
    }
}
