using System.Text.Json;
using Jellyfin.Plugin.Tipsport;
using Xunit;
namespace Tipsport.Tests;
public sealed class DiagnosticsTests
{
    [Fact] public void ExportExcludesSecretsUrlsMatchHistoryAndFreeText()
    {
        var secret = "NEVER-EXPORT-THIS";
        var config = new PluginConfiguration { Region = secret, FavouriteTeam = secret, AdditionalFavouriteTeams = secret, SportIds = secret, MaximumHeight = 0 };
        var json = JsonSerializer.Serialize(Diagnostics.Create(config, true, new(true, secret, 2, 1), "854x480 https://private.test/?JSESSIONID=" + secret, true, null, null));
        Assert.DoesNotContain(secret, json);
        Assert.DoesNotContain("private.test", json); Assert.DoesNotContain("JSESSIONID", json);
        Assert.DoesNotContain("MatchId", json); Assert.DoesNotContain("FavouriteTeam\"", json);
        Assert.Contains("854x480", json); Assert.Contains("PluginVersion", json); Assert.Contains("Readers", json);
    }
    [Fact] public void DefaultQualityRemainsAutomaticBestAvailable()
    {
        Assert.Equal(0, new PluginConfiguration().MaximumHeight);
    }
}
