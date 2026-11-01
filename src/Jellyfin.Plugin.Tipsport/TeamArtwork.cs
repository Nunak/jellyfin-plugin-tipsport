namespace Jellyfin.Plugin.Tipsport;

internal static class TeamArtwork
{
    // Club-hosted asset, not redistributed as part of this plugin's GPL package.
    internal const string KometaLogo = "https://www.hc-kometa.cz/znaky/2013/kom.png";
    internal static string? ForTeam(string team) => team.Trim().Equals("Kometa", StringComparison.OrdinalIgnoreCase)
        || team.Trim().Equals("HC Kometa Brno", StringComparison.OrdinalIgnoreCase)
        ? KometaLogo : null;
}
