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
    [Fact] public void TimingExportContainsOnlyNumericMeasurements()
    {
        var json = JsonSerializer.Serialize(Diagnostics.Create(new(), true,
            new(true, "private-match", 2, 1, false, 0, 123, 456, 7, 1024), null, false, null, null, 800));
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.Equal(123, root.GetProperty("Relay").GetProperty("ResolveMs").GetInt64());
        Assert.Equal(456, root.GetProperty("Relay").GetProperty("FirstByteMs").GetInt64());
        Assert.Equal(800, root.GetProperty("ProbeMs").GetInt64());
        Assert.DoesNotContain("private-match", json);
    }
    [Fact] public void DefaultQualityRemainsAutomaticBestAvailable()
    {
        Assert.Equal(0, new PluginConfiguration().MaximumHeight);
        Assert.False(new PluginConfiguration().LowLatencyStart);
        Assert.False(new PluginConfiguration().ShortHlsSegments);
        Assert.False(new PluginConfiguration().IosTransportStream);
    }
}
