using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Atlas.Client;
using Atlas.Playnite;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Xunit;

namespace Atlas.Playnite.Tests
{
    /// <summary>
    /// The provider and lookup against a fake HTTP handler. There is no headless Playnite (CONTRACT-C15 12), so the plugin class is not
    /// started here; the provider is driven the way Playnite drives it (a Game, MetadataRequestOptions, GetMetadataFieldArgs).
    /// </summary>
    public class ProviderTests : IDisposable
    {
        private readonly string _dir = TestAtlas.TempDir();
        private static readonly GetMetadataFieldArgs Args = new GetMetadataFieldArgs();

        public void Dispose() { try { System.IO.Directory.Delete(_dir, true); } catch (System.IO.IOException) { } }

        private static Game SteamPortal() { return new Game("Portal 2") { PluginId = LibraryIds.Steam, GameId = "620" }; }

        private AtlasMetadataProvider Provider(FakeHandler h, Game g, bool background = true, Func<IReadOnlyList<Candidate>, Candidate> chooser = null, AtlasServices services = null)
        {
            return new AtlasMetadataProvider(services ?? TestAtlas.Services(h, _dir), new MetadataRequestOptions(g, background), chooser);
        }

        [Fact]
        public void Steam_id_is_sent_as_identity_with_the_token_in_a_header_only()
        {
            var h = TestAtlas.RecordHandler();
            var p = Provider(h, SteamPortal());

            Assert.Equal(TestAtlas.Title, p.GetName(Args));
            var resolve = h.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/resolve"));
            Assert.Contains("\"steamsync\"", resolve.Body);
            Assert.Contains("\"620\"", resolve.Body);
            Assert.Contains("\"game\"", resolve.Body);
            Assert.Equal("Bearer " + TestAtlas.Token, resolve.Header("Authorization"));
            Assert.StartsWith("atlas-connector-playnite/", resolve.Header("User-Agent"));
            Assert.All(h.Requests, r => Assert.DoesNotContain(TestAtlas.Token, r.Uri.ToString()));
            Assert.Equal(LookupOutcome.Found, p.Result.Outcome);
        }

        [Fact]
        public void Fields_come_from_the_record_and_a_day_precision_date_stays_a_day()
        {
            var p = Provider(TestAtlas.RecordHandler(), SteamPortal());
            var date = p.GetReleaseDate(Args).Value;
            Assert.Equal(new[] { 2019, 3, 14 }, new[] { date.Year, date.Month.Value, date.Day.Value });
            Assert.Equal("Glowmoth Studio", ((MetadataNameProperty)p.GetDevelopers(Args).Single()).Name);
            Assert.Equal("Harbour Lights Interactive", ((MetadataNameProperty)p.GetPublishers(Args).Single()).Name);
            Assert.Equal(new[] { "Puzzle", "Adventure" }, p.GetGenres(Args).Select(g => ((MetadataNameProperty)g).Name).ToArray());
            Assert.Equal("https://atlas.example.org/game/" + TestAtlas.RecordId, p.GetLinks(Args).Single().Url);
            Assert.Contains(MetadataField.Name, p.AvailableFields);
            Assert.Contains(MetadataField.Links, p.AvailableFields);
            Assert.Contains(MetadataField.Publishers, p.AvailableFields);
            Assert.DoesNotContain(MetadataField.Series, p.AvailableFields);   // the record has none
            Assert.Null(p.GetSeries(Args));
        }

        [Fact]
        public void Title_only_never_assigns_in_a_background_download()
        {
            var h = TestAtlas.RecordHandler();
            h.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, FuzzyAnswer()));
            var g = new Game("portal two") { PluginId = Guid.NewGuid(), GameId = "x" };
            var chooserCalled = false;
            var p = Provider(h, g, background: true, chooser: c => { chooserCalled = true; return c[0]; });

            Assert.Null(p.GetName(Args));
            Assert.Empty(p.AvailableFields);
            Assert.False(chooserCalled);
            Assert.Equal(LookupOutcome.Candidates, p.Result.Outcome);
            var resolve = h.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/resolve"));
            Assert.Contains("\"playnite\"", resolve.Body);                       // unmapped library: Playnite's own id, plus the title
            Assert.Contains("portal two", resolve.Body);
        }

        [Fact]
        public void Title_only_in_a_manual_download_offers_candidates_and_applies_only_the_users_choice()
        {
            var h = TestAtlas.RecordHandler();
            h.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, FuzzyAnswer()));
            IReadOnlyList<Candidate> shown = null;
            var g = new Game("portal two") { PluginId = Guid.NewGuid(), GameId = "x" };
            var p = Provider(h, g, background: false, chooser: c => { shown = c; return c[0]; });

            Assert.Equal(TestAtlas.Title, p.GetName(Args));
            Assert.Equal(TestAtlas.RecordId, shown.Single().Id);
            Assert.Equal(TestAtlas.Title, shown.Single().Title);
        }

        [Fact]
        public void The_user_can_decline_every_candidate()
        {
            var h = TestAtlas.RecordHandler();
            h.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, FuzzyAnswer()));
            var p = Provider(h, new Game("portal two") { PluginId = Guid.NewGuid(), GameId = "x" }, false, c => null);
            Assert.Null(p.GetName(Args));
        }

        // Atlas answers reason "exact", assigned true, for an exact TITLE match (atlas/resolve.py title evidence). C15 8.4: a title
        // is never an automatic match, so a background download applies nothing, and a manual one offers that id to the user.
        private static string ExactTitleAnswer()
        {
            return "{\"results\":[{\"assigned\":true,\"canonical_id\":\"" + TestAtlas.RecordId + "\",\"reason\":\"exact\",\"method\":\"exact\","
                 + "\"confidence\":1.0,\"candidates\":[]}]}";
        }

        [Fact]
        public void An_exact_title_match_is_never_applied_by_a_background_download()
        {
            var h = TestAtlas.RecordHandler();
            h.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, ExactTitleAnswer()));
            var chooserCalled = false;
            var p = Provider(h, new Game("Portal 2") { PluginId = Guid.NewGuid(), GameId = "x" }, background: true, chooser: c => { chooserCalled = true; return c[0]; });

            Assert.Null(p.GetName(Args));
            Assert.Empty(p.AvailableFields);
            Assert.False(chooserCalled);
            Assert.Equal(LookupOutcome.Candidates, p.Result.Outcome);
        }

        [Fact]
        public void An_exact_title_match_is_offered_to_the_user_in_a_manual_download()
        {
            var h = TestAtlas.RecordHandler();
            h.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, ExactTitleAnswer()));
            IReadOnlyList<Candidate> shown = null;
            var p = Provider(h, new Game("Portal 2") { PluginId = Guid.NewGuid(), GameId = "x" }, background: false, chooser: c => { shown = c; return c[0]; });

            Assert.Equal(TestAtlas.Title, p.GetName(Args));
            Assert.Equal(TestAtlas.RecordId, shown.Single().Id);
        }

        [Fact]
        public void A_cancelled_download_gives_no_fields_and_no_exception()
        {
            var h = TestAtlas.RecordHandler();
            var p = Provider(h, SteamPortal());
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                // Playnite sets CancelToken itself (non-public setter); the test sets it the same way.
                var cancelled = new GetMetadataFieldArgs();
                typeof(GetMetadataFieldArgs).GetProperty("CancelToken").GetSetMethod(true).Invoke(cancelled, new object[] { cts.Token });
                Assert.Null(p.GetName(cancelled));
                Assert.Null(p.GetCoverImage(cancelled));
                Assert.Empty(p.AvailableFields);
                Assert.Equal(LookupOutcome.Unavailable, p.Result.Outcome);
            }
        }

        private static string FuzzyAnswer()
        {
            return "{\"results\":[{\"assigned\":false,\"canonical_id\":null,\"reason\":\"fuzzy\",\"method\":\"title\",\"confidence\":0.6,"
                 + "\"candidates\":[{\"id\":\"" + TestAtlas.RecordId + "\",\"score\":0.6}]}]}";
        }

        [Fact]
        public void A_known_atlas_link_skips_resolve_and_a_second_run_sends_the_etag()
        {
            var h = TestAtlas.RecordHandler();
            var g = SteamPortal();
            g.Links = new System.Collections.ObjectModel.ObservableCollection<Link>(new[] { AtlasLinks.RecordLink(TestAtlas.BaseUrl, TestAtlas.RecordId) });
            var services = TestAtlas.Services(h, _dir);

            Assert.Equal(TestAtlas.Title, Provider(h, g, services: services).GetName(Args));
            Assert.Equal(TestAtlas.Title, Provider(h, g, services: services).GetName(Args));

            Assert.DoesNotContain(h.Requests, r => r.Uri.AbsolutePath.EndsWith("/resolve"));
            var gets = h.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/games/" + TestAtlas.RecordId)).ToList();
            Assert.Equal(2, gets.Count);
            Assert.Null(gets[0].Header("If-None-Match"));
            Assert.Equal("\"v2\"", gets[1].Header("If-None-Match"));    // 304 answered from the cached copy
        }

        [Fact]
        public void A_merged_id_is_followed_and_the_new_id_goes_into_the_link()
        {
            var h = TestAtlas.RecordHandler();
            // the recorded answer for the merged duplicate: the survivor, with redirected_from
            h.On("GET", "/api/v2/games/" + TestAtlas.DuplicateId, s => FakeHandler.Json(200, Repo.Text("contract", "fixtures", "games", "record-redirected.json")));
            var g = SteamPortal();
            g.Links = new System.Collections.ObjectModel.ObservableCollection<Link>(new[] { AtlasLinks.RecordLink(TestAtlas.BaseUrl, TestAtlas.DuplicateId) });
            var p = Provider(h, g);

            Assert.Equal("https://atlas.example.org/game/" + TestAtlas.RecordId, p.GetLinks(Args).Single().Url);
            Assert.True(p.Result.IdChanged);
        }

        [Fact]
        public void When_atlas_is_down_a_record_within_the_ttl_is_served_and_nothing_older()
        {
            var h = TestAtlas.RecordHandler();
            var services = TestAtlas.Services(h, _dir);
            Assert.Equal(TestAtlas.Title, Provider(h, SteamPortal(), services: services).GetName(Args));

            h.Offline = true;
            var offline = Provider(h, SteamPortal(), services: TestAtlas.Services(h, _dir, settings: new AtlasSettings { BaseUrl = TestAtlas.BaseUrl, CacheTtlDays = 7 }));
            Assert.Equal(TestAtlas.Title, offline.GetName(Args));
            Assert.True(offline.Result.FromCache);

            var expired = Provider(h, SteamPortal(), services: TestAtlas.Services(h, _dir, settings: new AtlasSettings { BaseUrl = TestAtlas.BaseUrl, CacheTtlDays = 0 }));
            Assert.Null(expired.GetName(Args));
            Assert.Equal(LookupOutcome.Unavailable, expired.Result.Outcome);
        }

        [Fact]
        public void An_unconfigured_plugin_provides_nothing_and_does_not_throw()
        {
            var services = new AtlasServices(new AtlasSettings(), new ReverseProtector(), _dir);
            var p = new AtlasMetadataProvider(services, new MetadataRequestOptions(SteamPortal(), true), null);
            Assert.Null(p.GetName(Args));
            Assert.Empty(p.AvailableFields);
            Assert.Equal(LookupOutcome.Unavailable, p.Result.Outcome);
        }

        [Fact]
        public void A_rejected_token_gives_a_message_not_an_exception()
        {
            var h = TestAtlas.RecordHandler();
            h.On("POST", "/api/v2/resolve", s => FakeHandler.Json(401, "{\"error\":{\"code\":\"unauthorized\",\"message\":\"no\"}}"));
            var p = Provider(h, SteamPortal());
            Assert.Null(p.GetName(Args));
            Assert.Contains("Pair Playnite again", p.Result.Message);
        }

        [Fact]
        public void A_non_game_record_is_never_applied_to_a_game()
        {
            var h = TestAtlas.RecordHandler();
            var movie = TestAtlas.ResolveAnswer().Replace("\"media_type\":\"game\"", "\"media_type\":\"movie\"");
            Assert.Contains("\"movie\"", movie);
            h.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, movie));
            var p = Provider(h, SteamPortal());
            Assert.Null(p.GetName(Args));
        }

        // ---- images ----------------------------------------------------------------------------------------------

        private const string ArtworkJson =
            "{\"id\":\"" + TestAtlas.RecordId + "\",\"items\":["
            + "{\"id\":\"a1\",\"kind\":\"cover\",\"preferred\":true,\"cached\":true,\"media_url\":\"/media/abc123\"},"
            + "{\"id\":\"a2\",\"kind\":\"background\",\"preferred\":true,\"image_url\":\"/api/v2/artwork/a2/image\"},"
            + "{\"id\":\"a3\",\"kind\":\"holo-poster-from-the-future\",\"preferred\":true}],"
            + "\"primary\":{\"cover\":\"a1\"}}";

        [Fact]
        public void Cover_comes_from_the_public_media_url_without_the_token()
        {
            var h = TestAtlas.RecordHandler();
            h.On("GET", "/api/v2/games/" + TestAtlas.RecordId + "/artwork", s => FakeHandler.Json(200, ArtworkJson));
            h.On("GET", "/media/abc123", s => FakeHandler.Bytes(new byte[] { 1, 2, 3, 4 }, "image/png"));
            var p = Provider(h, SteamPortal());

            var cover = p.GetCoverImage(Args);
            Assert.Equal("atlas-cover.png", cover.FileName);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, cover.Content);
            Assert.Null(h.Requests.Single(r => r.Uri.AbsolutePath == "/media/abc123").Header("Authorization"));
            Assert.Contains(MetadataField.CoverImage, p.AvailableFields);
            Assert.DoesNotContain(MetadataField.Icon, p.AvailableFields);       // an unknown artwork kind is never guessed into a role
            Assert.Null(p.GetIcon(Args));
        }

        [Fact]
        public void A_307_to_another_host_never_carries_the_authorization_header()
        {
            var h = TestAtlas.RecordHandler();
            h.On("GET", "/api/v2/games/" + TestAtlas.RecordId + "/artwork", s => FakeHandler.Json(200, ArtworkJson));
            h.On("GET", "/api/v2/artwork/a2/image", s => FakeHandler.Redirect(307, "https://images.other.example/bg.jpg"));
            h.OnHost("images.other.example", s => FakeHandler.Bytes(new byte[] { 9, 9 }, "image/jpeg"));
            var p = Provider(h, SteamPortal());

            var bg = p.GetBackgroundImage(Args);
            Assert.Equal("atlas-background.jpg", bg.FileName);
            var atlasHop = h.Requests.Single(r => r.Uri.AbsolutePath == "/api/v2/artwork/a2/image");
            Assert.Equal("Bearer " + TestAtlas.Token, atlasHop.Header("Authorization"));
            var foreign = h.Requests.Single(r => r.Uri.Host == "images.other.example");
            Assert.False(foreign.Headers.ContainsKey("Authorization"), "the token leaked to the redirect target");
        }

        [Fact]
        public void A_failed_image_download_gives_no_image_and_no_exception()
        {
            var h = TestAtlas.RecordHandler();
            h.On("GET", "/api/v2/games/" + TestAtlas.RecordId + "/artwork", s => FakeHandler.Json(200, ArtworkJson));
            h.On("GET", "/media/abc123", s => FakeHandler.Json(404, "{}"));
            Assert.Null(Provider(h, SteamPortal()).GetCoverImage(Args));
        }

        [Fact]
        public void The_recorded_artwork_answer_picks_by_role_not_by_kind()
        {
            var h = TestAtlas.RecordHandler();
            h.On("GET", "/api/v2/games/" + TestAtlas.RecordId + "/artwork", s => FakeHandler.Json(200, Repo.Text("contract", "fixtures", "games", "artwork-lanternfall.json")));
            h.On("GET", "/api/v2/artwork/01KDVDNAE2000000000000001D/image", s => FakeHandler.Bytes(new byte[] { 7 }, "image/png"));   // key_art, ranked cover
            h.On("GET", "/api/v2/artwork/01KDVDNAEP000000000000001F/image", s => FakeHandler.Bytes(new byte[] { 8 }, "image/png"));   // icon
            var p = Provider(h, SteamPortal());

            Assert.Equal(new byte[] { 7 }, p.GetCoverImage(Args).Content);
            Assert.Equal(new byte[] { 8 }, p.GetIcon(Args).Content);
            Assert.Contains(MetadataField.BackgroundImage, p.AvailableFields);              // the hero, ranked background
            Assert.All(h.Requests.Where(r => r.Uri.AbsolutePath.Contains("/artwork/") && r.Uri.AbsolutePath.EndsWith("/image")),
                r => Assert.Equal("Bearer " + TestAtlas.Token, r.Header("Authorization")));
        }

        [Fact]
        public void Artwork_pick_prefers_primary_then_preferred_then_first_and_tries_kinds_in_order()
        {
            var list = AtlasReader.ReadArtwork(new NewtonsoftJsonAdapter().Parse(
                "{\"items\":[{\"id\":\"p1\",\"kind\":\"poster\"},{\"id\":\"c1\",\"kind\":\"cover\"},{\"id\":\"c2\",\"kind\":\"cover\",\"preferred\":true}]}"));
            Assert.Equal("c2", AtlasLookup.Pick(list, "cover", "poster").Id);
            Assert.Equal("p1", AtlasLookup.Pick(list, "poster").Id);
            Assert.Null(AtlasLookup.Pick(list, "icon"));
            var withPrimary = AtlasReader.ReadArtwork(new NewtonsoftJsonAdapter().Parse(
                "{\"items\":[{\"id\":\"c1\",\"kind\":\"cover\"},{\"id\":\"c2\",\"kind\":\"cover\",\"preferred\":true}],\"primary\":{\"cover\":\"c1\"}}"));
            Assert.Equal("c1", AtlasLookup.Pick(withPrimary, "cover").Id);
            Assert.Null(AtlasLookup.Pick(null, "cover"));
        }

        [Theory]
        [InlineData("image/png", ".png")]
        [InlineData("image/jpeg; charset=binary", ".jpg")]
        [InlineData("IMAGE/WEBP", ".webp")]
        [InlineData("image/avif", ".img")]
        [InlineData(null, ".img")]
        public void File_extension_follows_the_content_type(string type, string ext)
        {
            Assert.Equal(ext, AtlasMetadataProvider.Extension(type));
        }

        [Fact]
        public void Library_mapping_knows_steam_app_ids_only()
        {
            Assert.Equal("steamsync", LibraryIds.Map(LibraryIds.Steam, "620").Item1);
            Assert.Null(LibraryIds.Map(LibraryIds.Steam, "90071992547409921"));   // a Steam mod GameID, not an app id
            Assert.Null(LibraryIds.Map(LibraryIds.Steam, "12ab"));
            Assert.Null(LibraryIds.Map(LibraryIds.Steam, "0"));
            Assert.Null(LibraryIds.Map(Guid.NewGuid(), "620"));
        }
    }
}
