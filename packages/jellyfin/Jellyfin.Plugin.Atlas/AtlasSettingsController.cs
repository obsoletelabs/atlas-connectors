using System;
using System.Threading;
using System.Threading.Tasks;
using Atlas.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Atlas;

/// <summary>
/// Admin-only endpoints for the config page, under <c>/Atlas/...</c>. They exist so the page never needs the whole plugin
/// configuration API, and so the token goes in (paste or pairing) and never comes back out.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Atlas")]
[Produces("application/json")]
public class AtlasSettingsController : ControllerBase
{
    private static SettingsService Service()
    {
        var rt = AtlasRuntime.Current ?? throw new InvalidOperationException("The Atlas plugin is not loaded.");
        return new SettingsService(rt, () => Plugin.Instance.SaveConfiguration());
    }

    [HttpGet("Settings")]
    public ActionResult<SettingsView> GetSettings() => Service().Get();

    [HttpPost("Settings")]
    public ActionResult SaveSettings([FromBody] SettingsUpdate update)
    {
        var errors = Service().Save(update ?? new SettingsUpdate());
        return errors.Count == 0 ? NoContent() : BadRequest(new { errors });
    }

    [HttpPost("Test")]
    public async Task<ActionResult<object>> Test(CancellationToken ct) => new { message = await Service().TestAsync(ct).ConfigureAwait(false) };

    [HttpPost("Pairing/Start")]
    public async Task<ActionResult<PairingStartView>> StartPairing(CancellationToken ct)
    {
        try
        {
            return await Service().StartPairingAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new { errors = new[] { e.Message } });
        }
        catch (AtlasException e)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { errors = new[] { "Pairing failed: " + e.Message } });
        }
    }

    [HttpPost("Pairing/Poll")]
    public async Task<ActionResult<object>> PollPairing(CancellationToken ct)
    {
        try
        {
            return new { status = await Service().PollPairingAsync(ct).ConfigureAwait(false) };
        }
        catch (AtlasException e)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { errors = new[] { "Pairing failed: " + e.Message } });
        }
    }
}
