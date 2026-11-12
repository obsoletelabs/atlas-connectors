using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Atlas.Playnite;
using Xunit;

namespace Atlas.Playnite.Tests
{
    public class SettingsTests : IDisposable
    {
        private readonly string _dir = TestAtlas.TempDir();
        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        [Theory]
        [InlineData("https://atlas.example.org", true)]
        [InlineData("https://atlas.example.org/prefix/", true)]
        [InlineData("http://localhost:8000", true)]
        [InlineData("http://127.0.0.1:8000/", true)]
        [InlineData("http://192.168.1.5:8000", false)]        // plain http to the LAN needs the opt-in
        [InlineData("ftp://atlas.example.org", false)]
        [InlineData("atlas.example.org", false)]
        [InlineData("", false)]
        [InlineData("https://user:pw@atlas.example.org/", false)]
        [InlineData("https://atlas.example.org/?token=abc", false)]
        public void Base_url_rules(string url, bool ok)
        {
            string err;
            var uri = SettingsRules.NormalizeBaseUrl(url, false, out err);
            Assert.Equal(ok, uri != null);
            Assert.Equal(!ok, err != null);
            if (ok) Assert.EndsWith("/", uri.AbsoluteUri);
        }

        [Fact]
        public void Plain_http_to_a_lan_address_is_allowed_only_with_the_opt_in()
        {
            string err;
            Assert.Null(SettingsRules.NormalizeBaseUrl("http://192.168.1.5:8000", false, out err));
            Assert.NotNull(SettingsRules.NormalizeBaseUrl("http://192.168.1.5:8000", true, out err));
        }

        private AtlasSettingsViewModel Vm(out AtlasServices services, out List<AtlasSettings> saved, AtlasSettings start = null)
        {
            var list = new List<AtlasSettings>();
            saved = list;
            services = new AtlasServices(start ?? new AtlasSettings(), new ReverseProtector(), _dir);
            return new AtlasSettingsViewModel(services, s => list.Add(s));
        }

        [Fact]
        public void Saving_stores_the_token_protected_and_never_in_clear_text()
        {
            AtlasServices svc; List<AtlasSettings> saved;
            var vm = Vm(out svc, out saved);
            vm.BeginEdit();
            vm.BaseUrl = "https://atlas.example.org";
            vm.SetPastedToken("  tok-secret-1234  ");
            List<string> errors;
            Assert.True(vm.VerifySettings(out errors));
            vm.EndEdit();

            var s = saved.Single();
            Assert.Equal("https://atlas.example.org/", s.BaseUrl);
            Assert.NotEqual("tok-secret-1234", s.ProtectedToken);
            Assert.Equal("tok-secret-1234", svc.Token);
        }

        [Fact]
        public void Cancel_saves_nothing_and_restores_the_fields()
        {
            AtlasServices svc; List<AtlasSettings> saved;
            var vm = Vm(out svc, out saved, new AtlasSettings { BaseUrl = "https://a.example.org/" });
            vm.BeginEdit();
            vm.BaseUrl = "https://changed.example.org/";
            vm.SetPastedToken("new");
            vm.CancelEdit();
            Assert.Empty(saved);
            Assert.Equal("https://a.example.org/", vm.BaseUrl);
            Assert.Null(svc.Token);
        }

        [Fact]
        public void Forget_token_clears_it_on_ok_only()
        {
            var prot = new ReverseProtector();
            AtlasServices svc; List<AtlasSettings> saved;
            var vm = Vm(out svc, out saved, new AtlasSettings { BaseUrl = "https://a.example.org/", ProtectedToken = prot.Protect("keepme") });
            vm.BeginEdit();
            vm.ForgetTokenCommand.Execute(null);
            Assert.Equal("keepme", svc.Token);                    // not yet
            vm.EndEdit();
            Assert.Equal(string.Empty, saved.Single().ProtectedToken);
            Assert.Null(svc.Token);
        }

        [Fact]
        public void Invalid_input_is_reported_before_save()
        {
            AtlasServices svc; List<AtlasSettings> saved;
            var vm = Vm(out svc, out saved);
            vm.BeginEdit();
            vm.BaseUrl = "http://192.168.1.5:8000";
            vm.CacheTtlDays = 4000;
            List<string> errors;
            Assert.False(vm.VerifySettings(out errors));
            Assert.Equal(2, errors.Count);
            vm.BaseUrl = string.Empty; vm.CacheTtlDays = 7;       // empty address = provider off, allowed
            Assert.True(vm.VerifySettings(out errors));
        }

        [Fact]
        public async System.Threading.Tasks.Task Test_connection_reports_the_site_and_never_changes_settings()
        {
            var h = TestAtlas.RecordHandler();
            var list = new List<AtlasSettings>();
            var svc = new AtlasServices(new AtlasSettings(), new ReverseProtector(), _dir, () => h);
            var vm = new AtlasSettingsViewModel(svc, s => list.Add(s));
            vm.BeginEdit();
            vm.BaseUrl = "https://atlas.example.org/";
            await vm.TestAsync();
            Assert.StartsWith("Connected to Atlas", vm.Status);
            Assert.Empty(list);
        }

        // ---- DPAPI (real, Windows) ------------------------------------------------------------------------------

        [Fact]
        public void Dpapi_round_trips_and_does_not_store_the_token_readably()
        {
            var p = new DpapiTokenProtector();
            var stored = p.Protect("tok-secret-1234");
            Assert.NotEqual("tok-secret-1234", stored);
            Assert.DoesNotContain("tok-secret", stored);
            Assert.Equal("tok-secret-1234", p.Unprotect(stored));
            Assert.Null(p.Unprotect("not base64 !!"));
            Assert.Null(p.Unprotect(Convert.ToBase64String(new byte[] { 1, 2, 3 })));
            Assert.Null(p.Unprotect(string.Empty));
            Assert.Equal(string.Empty, p.Protect(string.Empty));
        }

        // ---- the package files agree -----------------------------------------------------------------------------

        [Fact]
        public void Plugin_id_version_and_module_match_extension_yaml_and_the_project()
        {
            var yaml = File.ReadAllText(Path.Combine(Repo.Root, "packages", "playnite", "extension.yaml"));
            Assert.Contains("Id: Atlas_Metadata_" + AtlasMetadataPlugin.PluginId.ToString("N"), yaml);
            Assert.Contains("Type: MetadataProvider", yaml);
            Assert.Contains("Module: Atlas.Playnite.dll", yaml);
            var version = Regex.Match(yaml, @"^Version: (\S+)", RegexOptions.Multiline).Groups[1].Value;
            Assert.Equal(version, SettingsRules.Version);
        }
    }
}
