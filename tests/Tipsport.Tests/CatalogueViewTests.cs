using System.Text.Json;
using Jellyfin.Plugin.Tipsport;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace Tipsport.Tests;
public sealed class CatalogueViewTests
{
    private static readonly PluginConfiguration Config = new() { FavouriteTeam = "Kometa", ShowUpcoming = true };
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T15:00:00Z");
    private static SportMatch Match(string id, string name, DateTimeOffset? start, bool live = false, bool ended = false) => new(id, name, "h", "Hokej", "Liga", live, start, "Hokej", ended);
    [Fact] public void FavouritesComeFirstThenKnownStartTimesUnknownLast()
    {
        SportMatch[] matches = [Match("1", "A - B", Now.AddHours(2)), Match("2", "C - D", null), Match("3", "Kometa - E", Now.AddHours(3)), Match("4", "F - G", Now)];
        Assert.Equal(new[] { "3", "4", "1", "2" }, CatalogueView.Select(matches, "sport:h", Config, Now).Select(m => m.Id));
    }
    [Fact] public void PrimaryFavouritePrecedesOtherFavouriteTeamsEvenWhenTheyStartEarlier()
    {
        var config = new PluginConfiguration { FavouriteTeam = "Kometa", AdditionalFavouriteTeams = "Zbrojovka", ShowUpcoming = true };
        SportMatch[] matches = [Match("1", "Zbrojovka - A", Now), Match("2", "Kometa - B", Now.AddHours(2))];
        Assert.Equal(new[] { "2", "1" }, CatalogueView.Select(matches, "favourites", config, Now).Select(m => m.Id));
        Assert.True(string.CompareOrdinal(CatalogueView.SortKey(matches[1], config), CatalogueView.SortKey(matches[0], config)) < 0);
    }
    [Fact] public void TodayUsesCzechCalendarDateAndIncludesLiveWithoutStart()
    {
        var now = DateTimeOffset.Parse("2026-11-01T23:30:00Z"); // Already 2 November in Prague.
        SportMatch[] matches = [Match("1", "A", now.AddMinutes(-20)), Match("2", "B", now.AddHours(-2)), Match("3", "C", null, true), Match("4", "D", null)];
        Assert.Equal(new[] { "1", "3" }, CatalogueView.Select(matches, "today", Config, now).Select(m => m.Id));
    }
    [Fact] public void EndedStreamsAreHiddenButDelayedUpcomingMatchesArePreserved()
    {
        SportMatch[] matches = [Match("1", "Kometa - A", Now.AddHours(-1), ended: true), Match("2", "Kometa - B", Now.AddMinutes(-10)), Match("3", "A - B", Now, true)];
        Assert.Equal("2", Assert.Single(CatalogueView.Select(matches, "favourites", Config, Now)).Id);
        Assert.Equal("3", Assert.Single(CatalogueView.Select(matches, "live", Config, Now)).Id);
    }
    [Fact] public void ExplicitProviderEndFlagIsReadWithoutInventingAnEndFromStartTime()
    {
        using var json = JsonDocument.Parse("""{"program":[{"id":"h","title":"Hokej","matchesByTimespans":[[{"id":1,"name":"A - B","finished":true},{"id":2,"name":"C - D","start":"2020-01-01T00:00:00Z"}]]}]}""");
        var matches = Catalogue.Parse(json.RootElement);
        Assert.True(matches.Single(m => m.Id == "1").Ended);
        Assert.False(matches.Single(m => m.Id == "2").Ended);
    }
    [Fact] public async Task InitialMetadataRefreshAppliesSortWithoutRenamingCard()
    {
        var item = new Video { Name = "ŽIVĚ · Kometa - A" };
        item.ProviderIds["TipsportSortKey"] = "01 20261101160000 Kometa - A";
        var provider = new CatalogueMetadataProvider();
        Assert.Equal(ItemUpdateType.MetadataEdit, await provider.FetchAsync(item, null!, CancellationToken.None));
        Assert.Equal(item.ProviderIds["TipsportSortKey"], item.ForcedSortName);
        Assert.Equal("ŽIVĚ · Kometa - A", item.Name);
        item.IsLocked = true;
        Assert.Equal(ItemUpdateType.None, CatalogueMetadataProvider.ApplySort(item, "different"));
    }
    [Fact] public void PlaybackExplainsUpcomingEndedAndAbsentStreams()
    {
        var future = Match("1", "A", DateTimeOffset.UtcNow.AddDays(1));
        var ended = Match("2", "B", null, ended: true);
        Assert.Contains("ještě nezačal", Assert.Throws<TipsportException>(() => Catalogue.Select([future], "1", "")).Message);
        Assert.Contains("už skončil", Assert.Throws<TipsportException>(() => Catalogue.Select([ended], "2", "")).Message);
        Assert.Contains("není v aktuální nabídce", Assert.Throws<TipsportException>(() => Catalogue.Select([], "3", "")).Message);
        Assert.Contains("nemá živý přenos", Assert.Throws<TipsportException>(() => Catalogue.Select([], "favourite", "Kometa")).Message);
    }
}
