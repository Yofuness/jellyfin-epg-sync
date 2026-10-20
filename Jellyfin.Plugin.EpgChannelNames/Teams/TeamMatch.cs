namespace Jellyfin.Plugin.EpgChannelNames.Teams;

/// <summary>
/// The two teams found in a program, in the order the text names them.
/// </summary>
/// <param name="League">Their league.</param>
/// <param name="First">The first team named (usually home in "X v Y", away in "X @ Y").</param>
/// <param name="Second">The second team named.</param>
/// <param name="Title">Program title, e.g. "Live NHL Hockey: Flyers vs Avalanche".</param>
public sealed record TeamMatch(League League, Team First, Team Second, string Title);
