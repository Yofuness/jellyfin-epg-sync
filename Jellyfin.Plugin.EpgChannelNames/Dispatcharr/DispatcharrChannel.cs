namespace Jellyfin.Plugin.EpgChannelNames.Dispatcharr;

/// <summary>
/// A Dispatcharr channel as the plugin needs it.
/// </summary>
/// <param name="Number">Channel number as it appears in the M3U <c>tvg-chno</c> (e.g. "220", "2.5").</param>
/// <param name="Name">Dispatcharr channel name.</param>
/// <param name="Group">Channel group name, whitespace-normalized; null when the channel has no group.</param>
/// <param name="StreamName">Name of the channel's first (playing) stream; null when it has none.</param>
public sealed record DispatcharrChannel(string Number, string Name, string? Group, string? StreamName);
