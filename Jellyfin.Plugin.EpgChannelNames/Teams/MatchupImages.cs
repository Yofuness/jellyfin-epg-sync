using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.EpgChannelNames.Teams;

/// <summary>
/// Draws "logo vs logo" cards with SkiaSharp (shipped with Jellyfin) and caches them in the plugin's data folder.
/// </summary>
public sealed class MatchupImages : IMatchupImages, IDisposable
{
    /// <summary>
    /// Image width in pixels (16:9, like Jellyfin's program cards).
    /// </summary>
    internal const int Width = 960;

    /// <summary>
    /// Image height in pixels.
    /// </summary>
    internal const int Height = 540;

    // Logos and finished cards are kept; unused cards are removed after this long.
    private static readonly TimeSpan _keepUnused = TimeSpan.FromDays(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MatchupImages> _logger;
    private readonly Func<string> _getDataFolder;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _skiaUnavailable;
    private bool _cleaned;

    /// <summary>
    /// Initializes a new instance of the <see cref="MatchupImages"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{MatchupImages}"/> interface.</param>
    public MatchupImages(IHttpClientFactory httpClientFactory, ILogger<MatchupImages> logger)
        : this(httpClientFactory, logger, () => Plugin.Instance!.DataFolderPath)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MatchupImages"/> class with an explicit folder. Used by tests.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{MatchupImages}"/> interface.</param>
    /// <param name="getDataFolder">Returns the folder for logos and cards.</param>
    internal MatchupImages(IHttpClientFactory httpClientFactory, ILogger<MatchupImages> logger, Func<string> getDataFolder)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _getDataFolder = getDataFolder;
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(Team first, Team second, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(first.Logo) || string.IsNullOrEmpty(second.Logo) || _skiaUnavailable)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = Path.Combine(_getDataFolder(), "matchups");
            Directory.CreateDirectory(folder);
            CleanOnce(folder);

            var path = Path.Combine(folder, Hash(first.Logo + "|" + second.Logo) + ".png");
            if (File.Exists(path))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // still in use
                return path;
            }

            var a = await GetLogoAsync(first.Logo, cancellationToken).ConfigureAwait(false);
            var b = await GetLogoAsync(second.Logo, cancellationToken).ConfigureAwait(false);
            if (a is null || b is null)
            {
                return null;
            }

            await File.WriteAllBytesAsync(path, Render(a, b), cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException)
        {
            // Jellyfin always ships SkiaSharp, but a broken native library must not fail the task every run.
            _skiaUnavailable = true;
            _logger.LogWarning(ex, "SkiaSharp is not available; matchup images are off until Jellyfin restarts");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the matchup image for {First} vs {Second}", first.ShortName, second.ShortName);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    /// <summary>
    /// Draws two logos side by side with a divider between them on a dark 960x540 card.
    /// </summary>
    /// <param name="first">Encoded image of the left logo.</param>
    /// <param name="second">Encoded image of the right logo.</param>
    /// <returns>The card as PNG.</returns>
    internal static byte[] Render(byte[] first, byte[] second)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        using (var background = new SKPaint())
        {
            background.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, Height),
                new[] { new SKColor(0x2A, 0x2E, 0x38), new SKColor(0x12, 0x14, 0x19) },
                SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, Width, Height, background);
        }

        DrawLogo(canvas, first, Width * 0.25f);
        DrawLogo(canvas, second, Width * 0.75f);

        // A divider instead of "VS" text: servers often have no fonts installed.
        using (var line = new SKPaint { IsAntialias = true, StrokeWidth = 4 })
        {
            line.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, Height * 0.2f),
                new SKPoint(0, Height * 0.8f),
                new[] { SKColors.Transparent, new SKColor(0xFF, 0xFF, 0xFF, 0x90), SKColors.Transparent },
                SKShaderTileMode.Clamp);
            canvas.DrawLine(Width / 2f, Height * 0.2f, Width / 2f, Height * 0.8f, line);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Fits a logo into a 340px square centered at (centerX, middle).
    private static void DrawLogo(SKCanvas canvas, byte[] encoded, float centerX)
    {
        using var logo = SKImage.FromEncodedData(encoded);
        if (logo is null)
        {
            return;
        }

        const float Box = 340;
        var scale = Math.Min(Box / logo.Width, Box / logo.Height);
        var w = logo.Width * scale;
        var h = logo.Height * scale;
        var rect = SKRect.Create(centerX - (w / 2), (Height - h) / 2, w, h);
        canvas.DrawImage(logo, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20].ToLowerInvariant();

    // Logos are cached by URL; ESPN changes the URL when a team changes its logo.
    private async Task<byte[]?> GetLogoAsync(string url, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(_getDataFolder(), "logos");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Hash(url) + ".png");
        if (File.Exists(path))
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(nameof(MatchupImages));
            client.Timeout = TimeSpan.FromSeconds(30);
            var bytes = await client.GetByteArrayAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Could not download team logo {Url}", url);
            return null;
        }
    }

    private void CleanOnce(string folder)
    {
        if (_cleaned)
        {
            return;
        }

        _cleaned = true;
        foreach (var file in Directory.EnumerateFiles(folder, "*.png"))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > _keepUnused)
            {
                File.Delete(file);
            }
        }
    }
}
