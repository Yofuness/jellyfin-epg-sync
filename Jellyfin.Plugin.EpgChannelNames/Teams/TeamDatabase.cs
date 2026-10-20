using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpgChannelNames.Teams;

/// <summary>
/// Downloads teams from ESPN's public site API and caches them in the plugin's data folder.
/// </summary>
public sealed class TeamDatabase : ITeamDatabase, IDisposable
{
    private const string CacheFileName = "teams.json";

    // Without a cache, a failed download is retried at most this often (the rename task runs every 15 min).
    private static readonly TimeSpan _retryAfterFailure = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TeamDatabase> _logger;
    private readonly Func<string> _getDataFolder;
    private readonly Func<string> _getBaseUrl;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<Team>? _teams;
    private DateTime _lastFailure = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="TeamDatabase"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TeamDatabase}"/> interface.</param>
    public TeamDatabase(IHttpClientFactory httpClientFactory, ILogger<TeamDatabase> logger)
        : this(httpClientFactory, logger, () => Plugin.Instance!.DataFolderPath, () => Plugin.Instance!.Configuration.TeamDatabaseUrl)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TeamDatabase"/> class with explicit paths. Used by tests.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TeamDatabase}"/> interface.</param>
    /// <param name="getDataFolder">Returns the folder for the cache file.</param>
    /// <param name="getBaseUrl">Returns the ESPN site API base URL.</param>
    internal TeamDatabase(IHttpClientFactory httpClientFactory, ILogger<TeamDatabase> logger, Func<string> getDataFolder, Func<string> getBaseUrl)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _getDataFolder = getDataFolder;
        _getBaseUrl = getBaseUrl;
    }

    private string CachePath => Path.Combine(_getDataFolder(), CacheFileName);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Team>> GetTeamsAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _teams ??= await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
            if (_teams is null && DateTime.UtcNow - _lastFailure > _retryAfterFailure)
            {
                _teams = await DownloadAsync(null, cancellationToken).ConfigureAwait(false);
            }

            return WithBuiltIn(_teams);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> RefreshAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = _teams ?? await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
            _teams = await DownloadAsync(cached, cancellationToken).ConfigureAwait(false) ?? cached;
            return WithBuiltIn(_teams).Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    /// <summary>
    /// Converts an ESPN team to a <see cref="Team"/>.
    /// </summary>
    /// <param name="league">The team's league.</param>
    /// <param name="displayName">e.g. "Philadelphia Flyers", "Wolverhampton Wanderers".</param>
    /// <param name="shortDisplayName">e.g. "Flyers", "Wolves", "C Palace".</param>
    /// <param name="name">e.g. "Flyers", "Wolverhampton Wanderers".</param>
    /// <param name="location">e.g. "Philadelphia"; only used for leagues with <see cref="League.ShortNicknames"/>.</param>
    /// <param name="logo">Logo URL.</param>
    /// <returns>The team.</returns>
    internal static Team ToTeam(League league, string displayName, string? shortDisplayName, string? name, string? location, string? logo)
    {
        var aliases = new[] { displayName, shortDisplayName, name, StripClubWords(displayName) }
            .OfType<string>()
            .Where(a => a.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // "C Palace" and "Nottm Forest" are fine to match but read badly in a title.
        var shortName = shortDisplayName is { Length: > 0 } s && !s.Split(' ').Any(w => w.Length == 1) ? s : displayName;
        var weak = league.ShortNicknames && !string.IsNullOrWhiteSpace(location) ? new[] { location } : Array.Empty<string>();
        return new Team(league.Key, shortName, aliases, weak, logo);
    }

    /// <summary>
    /// Removes weak aliases that several teams of one league share ("New York" for the Rangers and the Islanders).
    /// </summary>
    /// <param name="teams">Teams to clean.</param>
    /// <returns>The teams with only unambiguous weak aliases.</returns>
    internal static IReadOnlyList<Team> DropSharedWeakAliases(IEnumerable<Team> teams)
    {
        var list = teams.ToList();
        var shared = list
            .SelectMany(t => t.WeakAliases.Select(a => (t.League, Alias: a.ToUpperInvariant())))
            .GroupBy(x => x)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();
        return list
            .Select(t => t with { WeakAliases = t.WeakAliases.Where(a => !shared.Contains((t.League, a.ToUpperInvariant()))).ToList() })
            .ToList();
    }

    // "Bath Rugby" -> "Bath", "Atlanta United FC" -> "Atlanta United".
    private static string? StripClubWords(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Equals("FC", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("AFC", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("CF", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("Rugby", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return words.Count == 0 ? null : string.Join(' ', words);
    }

    private static IReadOnlyList<Team> WithBuiltIn(IReadOnlyList<Team>? teams)
        => (teams ?? Array.Empty<Team>()).Concat(League.GaaCounties).ToList();

    private async Task<IReadOnlyList<Team>?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            var stream = File.OpenRead(CachePath);
            await using (stream.ConfigureAwait(false))
            {
                var cache = await JsonSerializer.DeserializeAsync<CacheFile>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                return cache?.Teams;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the team cache {Path}", CachePath);
            return null;
        }
    }

    // Returns null when nothing could be downloaded. A league that fails keeps its teams from cached.
    private async Task<IReadOnlyList<Team>?> DownloadAsync(IReadOnlyList<Team>? cached, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(nameof(TeamDatabase));
        client.BaseAddress = new Uri(_getBaseUrl().TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(30);

        var teams = new List<Team>();
        var failed = new List<string>();
        foreach (var league in League.All.Where(l => l.EspnPath is not null))
        {
            try
            {
                var response = await client.GetFromJsonAsync<EspnResponse>(league.EspnPath + "/teams", cancellationToken).ConfigureAwait(false);
                var downloaded = (response?.Sports ?? new())
                    .SelectMany(s => s.Leagues ?? new())
                    .SelectMany(l => l.Teams ?? new())
                    .Select(t => t.Team)
                    .Where(t => !string.IsNullOrWhiteSpace(t?.DisplayName))
                    .Select(t => ToTeam(league, t!.DisplayName!, t.ShortDisplayName, t.Name, t.Location, t.Logos?.FirstOrDefault()?.Href))
                    .ToList();
                if (downloaded.Count == 0)
                {
                    throw new JsonException("No teams in the response");
                }

                teams.AddRange(downloaded);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException
                || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "Could not download {League} teams", league.Label);
                failed.Add(league.Label);
                teams.AddRange(cached?.Where(t => t.League == league.Key) ?? Enumerable.Empty<Team>());
            }
        }

        if (failed.Count == League.All.Count(l => l.EspnPath is not null))
        {
            _lastFailure = DateTime.UtcNow;
            return null;
        }

        var result = DropSharedWeakAliases(teams);
        try
        {
            Directory.CreateDirectory(_getDataFolder());
            var stream = File.Create(CachePath);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, new CacheFile { Updated = DateTime.UtcNow, Teams = result.ToList() }, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the team cache {Path}", CachePath);
        }

        _logger.LogInformation("Team database updated: {Count} teams{Failed}", result.Count, failed.Count > 0 ? "; kept cached teams for " + string.Join(", ", failed) : string.Empty);
        return result;
    }

    private sealed class CacheFile
    {
        public DateTime Updated { get; set; }

        public List<Team> Teams { get; set; } = new();
    }

    private sealed class EspnResponse
    {
        [JsonPropertyName("sports")]
        public List<EspnSport>? Sports { get; set; }
    }

    private sealed class EspnSport
    {
        [JsonPropertyName("leagues")]
        public List<EspnLeague>? Leagues { get; set; }
    }

    private sealed class EspnLeague
    {
        [JsonPropertyName("teams")]
        public List<EspnTeamWrapper>? Teams { get; set; }
    }

    private sealed class EspnTeamWrapper
    {
        [JsonPropertyName("team")]
        public EspnTeam? Team { get; set; }
    }

    private sealed class EspnTeam
    {
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("shortDisplayName")]
        public string? ShortDisplayName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("location")]
        public string? Location { get; set; }

        [JsonPropertyName("logos")]
        public List<EspnLogo>? Logos { get; set; }
    }

    private sealed class EspnLogo
    {
        [JsonPropertyName("href")]
        public string? Href { get; set; }
    }
}
