namespace Jellyfin.Plugin.EpgChannelNames.Api;

/// <summary>
/// A Dispatcharr group for the config page.
/// </summary>
/// <param name="Name">Normalized group name.</param>
/// <param name="Channels">Number of channels in the group.</param>
/// <param name="Automatic">Row picked from the name, or null for none.</param>
public sealed record GroupInfo(string Name, int Channels, string? Automatic);
