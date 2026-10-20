using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.EpgChannelNames.Dispatcharr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EpgChannelNames.Api;

/// <summary>
/// Lists Dispatcharr groups for the config page's group table.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("EpgChannelNames")]
public class GroupsController : ControllerBase
{
    private readonly IDispatcharrClient _dispatcharr;

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupsController"/> class.
    /// </summary>
    /// <param name="dispatcharr">Instance of the <see cref="IDispatcharrClient"/> interface.</param>
    public GroupsController(IDispatcharrClient dispatcharr)
    {
        _dispatcharr = dispatcharr;
    }

    /// <summary>
    /// Gets the Dispatcharr groups that have channels, with their channel count and automatic row.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The groups, or 502 with a message when Dispatcharr can't be read.</returns>
    [HttpGet("Groups")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<IEnumerable<GroupInfo>>> GetGroups(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(config.DispatcharrUrl))
        {
            return BadRequest("Set the Dispatcharr URL and API key, then Save.");
        }

        try
        {
            var channels = await _dispatcharr.GetChannelsAsync(config.DispatcharrUrl, config.DispatcharrApiKey, cancellationToken).ConfigureAwait(false);
            return Ok(Summarize(channels));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return StatusCode(StatusCodes.Status502BadGateway, "Could not read Dispatcharr: " + ex.Message);
        }
    }

    /// <summary>
    /// Groups channels by Dispatcharr group, sorted by name.
    /// </summary>
    /// <param name="channels">Dispatcharr channels.</param>
    /// <returns>One entry per group with channels.</returns>
    internal static IReadOnlyList<GroupInfo> Summarize(IEnumerable<DispatcharrChannel> channels)
        => channels
            .Where(c => !string.IsNullOrEmpty(c.Group))
            .GroupBy(c => c.Group!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GroupInfo(g.Key, g.Count(), CategoryRules.Guess(g.Key)?.ToString()))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
