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
            var h = TestAtlas.PortalHandler();
            var p = Provider(h, SteamPortal());

            Assert.Equal("Portal 2", p.GetName(Args));
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
            var p = Provider(TestAtlas.PortalHandler(), SteamPortal());
            var date = p.GetReleaseDate(Args).Value;
            Assert.Equal(new[] { 2011, 4, 19 }, new[] { date.Year, date.Month.Value, date.Day.Value });
            Assert.Equal("Valve", ((MetadataNameProperty)p.GetDevelopers(Args).Single()).Name);
            Assert.Equal("https://atlas.example.org/game/" + TestAtlas.PortalId, p.GetLinks(Args).Single().Url);
            Assert.Contains(MetadataField.Name, p.AvailableFields);
            Assert.Contains(MetadataField.Links, p.AvailableFields);
            Assert.DoesNotContain(MetadataField.Publishers, p.AvailableFields);
            Assert.Null(p.GetPublishers(Args));
        }

        [Fact]
        public void Title_only_never_assigns_in_a_background_download()
        {
            var h = TestAtlas.PortalHandler();
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
            var h = TestAtlas.PortalHandler();
            h.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, FuzzyAnswer()));
            h.On("POST", "/api/v2/games/batch", s => FakeHandler.Json(404, "{}"));   // no batch capability on this fixture site
            IReadOnlyList<Candidate> shown = null;
            var g = new Game("portal two") { PluginId = Guid.NewGuid(), GameId = "x" };
            var p = Provider(h, g, background: false, chooser: c => { shown = c; return c[0]; });

            Assert.Equal("Portal 2", p.GetName(Args));
            Assert.Equal(TestAtlas.PortalId, shown.Single().Id);
            Assert.Equal("Portal 2", shown.Single().Title);
        }

        [Fact]
        public void The_user_can_decline_every_candidate()
        {
            var h = TestAtlas.PortalHandler();
            h.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, FuzzyAnswer()));
            var p = Provider(h, new Game("portal two") { PluginId = Guid.NewGuid(), GameId = "x" }, false, c => null);
            Assert.Null(p.GetName(Args));
        }

        private static string FuzzyAnswer()
        {
            return "{\"results\":[{\"assigned\":false,\"canonical_id\":null,\"reason\":\"fuzzy\",\"method\":\"title\",\"confidence\":0.6,"
                 + "\"candidates\":[{\"id\":\"" + TestAtlas.PortalId + "\",\"score\":0.6}]}]}";
        }

        [Fact]
        public void A_known_atlas_link_skips_resolve_and_a_second_run_sends_the_etag()
        {
            var h = TestAtlas.PortalHandler();
            var g = SteamPortal();
            g.Links = new System.Collections.ObjectModel.ObservableCollection<Link>(new[] { AtlasLinks.RecordLink(TestAtlas.BaseUrl, TestAtlas.PortalId) });
            var services = TestAtlas.Services(h, _dir);

            Assert.Equal("Portal 2", Provider(h, g, services: services).GetName(Args));
            Assert.Equal("Portal 2", Provider(h, g, services: services).GetName(Args));

            Assert.DoesNotContain(h.Requests, r => r.Uri.AbsolutePath.EndsWith("/resolve"));
            var gets = h.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/games/" + TestAtlas.PortalId)).ToList();
            Assert.Equal(2, gets.Count);
            Assert.Null(gets[0].Header("If-None-Match"));
            Assert.Equal("\"v2\"", gets[1].Header("If-None-Match"));    // 304 answered from the cached copy
        }

        [Fact]
        public void A_merged_id_is_followed_and_the_new_id_goes_into_the_link()
        {
            var h = TestAtlas.PortalHandler();
            var merged = Repo.Text("contract", "fixtures", "games", "record-portal.json")
                .Replace("\"redirected_from\": null", "\"redirected_from\": \"atlas:0old\"");
            h.On("GET", "/api/v2/games/atlas:0old", s => FakeHandler.Json(200, merged));
            var g = SteamPortal();
            g.Links = new System.Collections.ObjectModel.ObservableCollection<Link>(new[] { AtlasLinks.RecordLink(TestAtlas.BaseUrl, "atlas:0old") });
            var p = Provider(h, g);

            Assert.Equal("https://atlas.example.org/game/" + TestAtlas.PortalId, p.GetLinks(Args).Single().Url);
            Assert.True(p.Result.IdChanged);
        }

        [Fact]
        public void When_atlas_is_down_a_record_within_the_ttl_is_served_and_nothing_older()
        {
            var h = TestAtlas.PortalHandler();
            var services = TestAtlas.Services(h, _dir);
            Assert.Equal("Portal 2", Provider(h, SteamPortal(), services: services).GetName(Args));

            h.Offline = true;
            var offline = Provider(h, SteamPortal(), services: TestAtlas.Services(h, _dir, settings: new AtlasSettings { BaseUrl = TestAtlas.BaseUrl, CacheTtlDays = 7 }));
            Assert.Equal("Portal 2", offline.GetName(Args));
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
            var h = TestAtlas.PortalHandler();
            h.On("POST", "/api/v2/resolve", s => FakeHandler.Json(401, "{\"error\":{\"code\":\"unauthorized\",\"message\":\"no\"}}"));
            var p = Provider(h, SteamPortal());
            Assert.Null(p.GetName(Args));
            Assert.Contains("Pair Playnite again", p.Result.Message);
        }

        [Fact]
        public void A_non_game_record_is_never_applied_to_a_game()
        {
            var h = TestAtlas.PortalHandler();
            var movie = Repo.Text("contract", "fixtures", "games", "resolve-by-atlas-id.json").Replace("\"media_type\": \"game\"", "\"media_type\": \"movie\"");
            h.On("POST", "/api/v2/resolve", s => FakeHandler.Json(200, movie));
            var p = Provider(h, SteamPortal());
            Assert.Null(p.GetName(Args));
        }

        // ---- images ----------------------------------------------------------------------------------------------

        private const string ArtworkJson =
            "{\"id\":\"" + TestAtlas.PortalId + "\",\"items\":["
            + "{\"id\":\"a1\",\"kind\":\"cover\",\"preferred\":true,\"cached\":true,\"media_url\":\"/media/abc123\"},"
            + "{\"id\":\"a2\",\"kind\":\"background\",\"preferred\":true,\"image_url\":\"/api/v2/artwork/a2/image\"},"
            + "{\"id\":\"a3\",\"kind\":\"holo-poster-from-the-future\",\"preferred\":true}],"
            + "\"primary\":{\"cover\":\"a1\"}}";

        [Fact]
        public void Cover_comes_from_the_public_media_url_without_the_token()
        {
            var h = TestAtlas.PortalHandler();
            h.On("GET", "/api/v2/games/" + TestAtlas.PortalId + "/artwork", s => FakeHandler.Json(200, ArtworkJson));
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
            var h = TestAtlas.PortalHandler();
            h.On("GET", "/api/v2/games/" + TestAtlas.PortalId + "/artwork", s => FakeHandler.Json(200, ArtworkJson));
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
            var h = TestAtlas.PortalHandler();
            h.On("GET", "/api/v2/games/" + TestAtlas.PortalId + "/artwork", s => FakeHandler.Json(200, ArtworkJson));
            h.On("GET", "/media/abc123", s => FakeHandler.Json(404, "{}"));
            Assert.Null(Provider(h, SteamPortal()).GetCoverImage(Args));
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
