using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Atlas.Client;
using Jellyfin.Plugin.Atlas.Configuration;

namespace Jellyfin.Plugin.Atlas;

/// <summary>What the config page may read. The token is never in it: only whether one is stored.</summary>
public sealed class SettingsView
{
    public string BaseUrl { get; set; }

    public bool AllowPlainHttp { get; set; }

    public int CacheTtlDays { get; set; }

    public bool EnableBooks { get; set; }

    public bool EnableMovies { get; set; }

    public bool EnableSeries { get; set; }

    public bool HasToken { get; set; }

    public string PluginVersion { get; set; }

    public string MinClientApi { get; set; } = "1.0";
}

/// <summary>What the config page sends. <see cref="Token"/> null or empty keeps the stored token; <see cref="ForgetToken"/> removes it.</summary>
public sealed class SettingsUpdate
{
    public string BaseUrl { get; set; }

    public bool AllowPlainHttp { get; set; }

    public int CacheTtlDays { get; set; } = 7;

    public bool EnableBooks { get; set; } = true;

    public bool EnableMovies { get; set; } = true;

    public bool EnableSeries { get; set; } = true;

    public string Token { get; set; }

    public bool ForgetToken { get; set; }
}

public sealed class PairingStartView
{
    public string Code { get; set; }

    public string VerifyUrl { get; set; }

    public int ExpiresInSeconds { get; set; }

    public int IntervalSeconds { get; set; }
}

/// <summary>The logic behind the admin-only settings endpoints, free of ASP.NET so the tests can call it directly.</summary>
public sealed class SettingsService
{
    private readonly AtlasRuntime _rt;
    private readonly Action _save;

    public SettingsService(AtlasRuntime runtime, Action save)
    {
        _rt = runtime;
        _save = save;
    }

    public SettingsView Get()
    {
        var c = _rt.Config;
        return new SettingsView
        {
            BaseUrl = c.BaseUrl,
            AllowPlainHttp = c.AllowPlainHttp,
            CacheTtlDays = c.CacheTtlDays,
            EnableBooks = c.EnableBooks,
            EnableMovies = c.EnableMovies,
            EnableSeries = c.EnableSeries,
            HasToken = _rt.Tokens.Has,
            PluginVersion = SettingsRules.Version,
        };
    }

    /// <summary>Validate, then save. Returns the errors; nothing is saved when there are any.</summary>
    public IReadOnlyList<string> Save(SettingsUpdate u)
    {
        var errors = new List<string>();
        Uri uri = null;
        if (!string.IsNullOrWhiteSpace(u.BaseUrl))
        {
            uri = SettingsRules.NormalizeBaseUrl(u.BaseUrl, u.AllowPlainHttp, out var err);
            if (uri == null)
            {
                errors.Add(err);
            }
        }

        if (u.CacheTtlDays < 0 || u.CacheTtlDays > 365)
        {
            errors.Add("Keep cached answers for 0 to 365 days.");
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        var c = _rt.Config;
        c.BaseUrl = uri == null ? string.Empty : uri.AbsoluteUri;
        c.AllowPlainHttp = u.AllowPlainHttp;
        c.CacheTtlDays = u.CacheTtlDays;
        c.EnableBooks = u.EnableBooks;
        c.EnableMovies = u.EnableMovies;
        c.EnableSeries = u.EnableSeries;
        if (u.ForgetToken)
        {
            _rt.Tokens.Clear();
        }
        else if (!string.IsNullOrWhiteSpace(u.Token))
        {
            _rt.Tokens.Write(u.Token);
        }

        _save();
        return errors;
    }

    public async Task<PairingStartView> StartPairingAsync(CancellationToken ct)
    {
        var s = await _rt.StartPairingAsync("Jellyfin on " + Environment.MachineName, ct).ConfigureAwait(false);
        return new PairingStartView { Code = s.UserCode, VerifyUrl = s.VerifyUrlComplete ?? s.VerifyUrl, ExpiresInSeconds = s.ExpiresInSeconds, IntervalSeconds = Math.Max(5, s.IntervalSeconds) };
    }

    /// <summary>pending, slow_down, approved, denied, expired or none (nothing started). The token is stored, never returned.</summary>
    public async Task<string> PollPairingAsync(CancellationToken ct)
    {
        var status = await _rt.PollPairingAsync(ct).ConfigureAwait(false);
        if (!status.HasValue)
        {
            return "none";
        }

        switch (status.Value)
        {
            case PairingStatus.Approved: return "approved";
            case PairingStatus.Denied: return "denied";
            case PairingStatus.Expired: return "expired";
            case PairingStatus.SlowDown: return "slow_down";
            default: return "pending";
        }
    }

    public Task<string> TestAsync(CancellationToken ct) => _rt.TestConnectionAsync(ct);
}
