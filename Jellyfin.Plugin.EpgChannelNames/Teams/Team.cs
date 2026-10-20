using System.Collections.Generic;

namespace Jellyfin.Plugin.EpgChannelNames.Teams;

/// <summary>
/// A team in the team database.
/// </summary>
/// <param name="League">Key of the team's <see cref="League"/>.</param>
/// <param name="ShortName">Name shown in program titles, e.g. "Flyers".</param>
/// <param name="Aliases">Names that identify the team in a description, e.g. "Philadelphia Flyers", "Flyers".</param>
/// <param name="WeakAliases">Names that identify the team only when the text names its league, e.g. "Philadelphia".</param>
/// <param name="Logo">Logo URL, if known.</param>
public sealed record Team(string League, string ShortName, IReadOnlyList<string> Aliases, IReadOnlyList<string> WeakAliases, string? Logo);
