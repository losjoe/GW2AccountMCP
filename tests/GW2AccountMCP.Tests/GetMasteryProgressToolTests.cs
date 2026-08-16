using GW2AccountMCP.Gw2;
using GW2AccountMCP.Tools;
using ModelContextProtocol;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class GetMasteryProgressToolTests
{
    [Fact]
    public async Task Returns_account_tracks_and_point_totals_in_canonical_order()
    {
        var client = new FakeGw2ApiClient
        {
            Sources = new Gw2AccountMasterySources(
                [new Gw2AccountMasteryTrack(2, 1), new Gw2AccountMasteryTrack(1, null)],
                [new Gw2MasteryPointTotal("Zeta", 6, 5), new Gw2MasteryPointTotal("Alpha", 1, 3)],
                DateTimeOffset.Parse("2026-08-16T12:00:00Z"), DateTimeOffset.Parse("2026-08-16T12:00:01Z")),
            Metadata = new Gw2PublicMasteries(
                [new Gw2PublicMastery(2, "Two", "", "Tyria", 4, [new Gw2PublicMasteryLevel("First", "", "", 1, 2), new Gw2PublicMasteryLevel("Second", "", "", 3, 4)])],
                [1])
        };

        var result = await new GetMasteryProgressTool(client, new SequenceTimeProvider()).GetMasteryProgressAsync(CancellationToken.None);

        Assert.Equal([1L, 2L], result.Tracks.Select(track => track.Id));
        Assert.Equal(["NoPublicMasteryResource", "Found"], result.Tracks.Select(track => track.MetadataStatus));
        Assert.Equal(1L, result.Tracks[1].SourceLevel);
        Assert.Equal(2L, result.Tracks[1].UnlockedLevelCount);
        Assert.Equal("Second", result.Tracks[1].CurrentLevel!.Name);
        Assert.Null(result.Tracks[1].NextLevel);
        Assert.Equal(["Alpha", "Zeta"], result.PointTotals.Select(total => total.Region));
        Assert.Equal((2L, (long?)null), (result.PointTotals[0].Available, result.PointTotals[1].Available));
        Assert.Equal("Partial", result.MetadataStatus);
        Assert.Equal([1L], result.MissingMetadataTrackIds);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), result.MetadataAsOf);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:01Z"), result.AsOf);
        Assert.Contains("zero-based", result.ScopeStatement, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["account", "metadata"], client.Calls);
    }

    [Fact]
    public async Task Preserves_essential_facts_when_metadata_is_unavailable_and_caps_warnings()
    {
        var tracks = Enumerable.Range(1, 33).Select(id => new Gw2AccountMasteryTrack(id, 0)).ToArray();
        var client = new FakeGw2ApiClient
        {
            Sources = new Gw2AccountMasterySources(tracks, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            MetadataError = new HttpRequestException("private metadata failure")
        };

        var result = await new GetMasteryProgressTool(client, TimeProvider.System).GetMasteryProgressAsync(CancellationToken.None);

        Assert.Equal("Unavailable", result.MetadataStatus);
        Assert.Null(result.MissingMetadataTrackIds);
        Assert.Single(result.Warnings);
        Assert.All(result.Tracks, track => Assert.Equal("PublicMasteriesUnavailable", track.MetadataStatus));
    }

    [Fact]
    public async Task Caps_level_context_warnings_in_ascending_track_order_without_changing_tracks()
    {
        var tracks = Enumerable.Range(1, 33).Select(id => new Gw2AccountMasteryTrack(id, 0)).ToArray();
        var metadata = tracks.Select(track => new Gw2PublicMastery(track.Id, "Track", "", "Tyria", 0, [])).ToArray();
        var client = new FakeGw2ApiClient { Sources = new Gw2AccountMasterySources(tracks, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), Metadata = new Gw2PublicMasteries(metadata, []) };

        var result = await new GetMasteryProgressTool(client, TimeProvider.System).GetMasteryProgressAsync(CancellationToken.None);

        Assert.Equal(32, result.Warnings.Count);
        Assert.Equal(Enumerable.Range(1, 32).Select(id => $"Mastery track {id} has public metadata with no levels; level context is unavailable."), result.Warnings);
        Assert.Equal(33, result.Tracks.Count);
        Assert.All(result.Tracks, track => Assert.Equal("Found", track.MetadataStatus));
    }

    [Fact]
    public async Task Maps_essential_failures_without_metadata_and_preserves_caller_cancellation()
    {
        foreach (var error in new Exception[] { new Gw2ConfigurationException("private account detail"), new HttpRequestException("private transport"), new OperationCanceledException() })
        {
            var client = new FakeGw2ApiClient { AccountError = error };
            await Assert.ThrowsAsync<McpException>(() => new GetMasteryProgressTool(client, TimeProvider.System).GetMasteryProgressAsync(CancellationToken.None));
            Assert.Equal(["account"], client.Calls);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GetMasteryProgressTool(new FakeGw2ApiClient { AccountError = new OperationCanceledException(cancellation.Token) }, TimeProvider.System).GetMasteryProgressAsync(cancellation.Token));
    }

    [Fact]
    public async Task Empty_essentials_skip_metadata_and_keep_source_timestamps()
    {
        var client = new FakeGw2ApiClient { Sources = new Gw2AccountMasterySources([], [], DateTimeOffset.Parse("2026-08-16T10:00:00Z"), DateTimeOffset.Parse("2026-08-16T10:00:01Z")) };
        var result = await new GetMasteryProgressTool(client, new SequenceTimeProvider()).GetMasteryProgressAsync(CancellationToken.None);

        Assert.Equal(("NotNeeded", true, DateTimeOffset.Parse("2026-08-16T10:00:00Z"), DateTimeOffset.Parse("2026-08-16T10:00:01Z")), (result.MetadataStatus, result.AreAllMetadataTracksResolved, result.AccountMasteriesAsOf, result.MasteryPointsAsOf));
        Assert.Empty(result.MissingMetadataTrackIds!);
        Assert.Null(result.MetadataAsOf);
        Assert.Equal(["account"], client.Calls);
    }

    [Fact]
    public async Task Projects_level_contexts_point_warnings_and_metadata_summary_in_required_order()
    {
        var tracks = new[] { new Gw2AccountMasteryTrack(5, 9), new Gw2AccountMasteryTrack(2, 1), new Gw2AccountMasteryTrack(3, 2), new Gw2AccountMasteryTrack(4, 0), new Gw2AccountMasteryTrack(1, 0) };
        var levels = new[] { new Gw2PublicMasteryLevel("Zero", "", "", 0, 0), new Gw2PublicMasteryLevel("One", "", "", 0, 0) };
        var client = new FakeGw2ApiClient
        {
            Sources = new Gw2AccountMasterySources(tracks, [new Gw2MasteryPointTotal("Zeta", 2, 1), new Gw2MasteryPointTotal("Alpha", 4, 3)], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            Metadata = new Gw2PublicMasteries([new Gw2PublicMastery(1, "One", "", "Future", long.MinValue, levels), new Gw2PublicMastery(2, "Two", "", "Tyria", long.MaxValue, levels), new Gw2PublicMastery(3, "Three", "", "Tyria", 0, levels), new Gw2PublicMastery(4, "Four", "", "Tyria", 0, []), new Gw2PublicMastery(5, "Five", "", "Tyria", 0, levels)], [99])
        };
        var result = await new GetMasteryProgressTool(client, TimeProvider.System).GetMasteryProgressAsync(CancellationToken.None);

        Assert.Equal([1L, 2L, 3L, 4L, 5L], result.Tracks.Select(track => track.Id));
        Assert.Equal(("Zero", "One"), (result.Tracks[0].CurrentLevel!.Name, result.Tracks[0].NextLevel!.Name));
        Assert.Null(result.Tracks[1].NextLevel);
        Assert.Null(result.Tracks[2].CurrentLevel);
        Assert.Null(result.Tracks[3].CurrentLevel);
        Assert.Null(result.Tracks[4].CurrentLevel);
        Assert.Equal(["Public mastery metadata is partial; some account mastery tracks have no public resource.", "Mastery point total for region Alpha reports spent points greater than earned points.", "Mastery point total for region Zeta reports spent points greater than earned points.", "Mastery track 3 sourceLevel 2 is outside the public level range; level context is unavailable.", "Mastery track 4 has public metadata with no levels; level context is unavailable.", "Mastery track 5 sourceLevel 9 is outside the public level range; level context is unavailable."], result.Warnings);
    }

    [Fact]
    public async Task Rejects_source_level_maximum_without_exposing_raw_metadata()
    {
        var client = new FakeGw2ApiClient { Sources = new Gw2AccountMasterySources([new Gw2AccountMasteryTrack(1, long.MaxValue)], [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), Metadata = new Gw2PublicMasteries([], [1]) };
        await Assert.ThrowsAsync<McpException>(() => new GetMasteryProgressTool(client, TimeProvider.System).GetMasteryProgressAsync(CancellationToken.None));
    }

    private sealed class FakeGw2ApiClient : IGw2ApiClient
    {
        public List<string> Calls { get; } = [];
        public Gw2AccountMasterySources Sources { get; set; } = new([], [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        public Gw2PublicMasteries Metadata { get; set; } = new([], []);
        public Exception? MetadataError { get; set; }
        public Exception? AccountError { get; set; }
        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Characters> GetCharactersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterBuild> GetCharacterBuildAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterEquipment> GetCharacterEquipmentAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterEquipmentTabs> GetCharacterEquipmentTabsAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterInventory> GetCharacterInventoryAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountStorage> GetAccountStorageAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterBags> GetCharacterBagsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2TradingPostDelivery> GetTradingPostDeliveryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CurrentSells> GetCurrentSellsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CurrentBuysPage> GetCurrentBuysPageAsync(int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CurrentSellsPage> GetCurrentSellsPageAsync(int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Items> GetItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicItems> GetPublicItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2MaterialCategories> GetPublicMaterialCategoriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicRecipes> GetPublicRecipesAsync(IReadOnlyList<long> recipeIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2RecipeSelector> SearchPublicRecipesByInputItemAsync(long itemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2RecipeSelector> SearchPublicRecipesByOutputItemAsync(long itemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountRecipeUnlocks> GetAccountRecipeUnlocksAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2LegendaryArmory> GetLegendaryArmoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountAchievementProgress> GetAccountAchievementProgressAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicAchievements> GetPublicAchievementsAsync(IReadOnlyList<long> achievementIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountMasterySources> GetAccountMasterySourcesAsync(CancellationToken cancellationToken) { Calls.Add("account"); return AccountError is null ? Task.FromResult(Sources) : Task.FromException<Gw2AccountMasterySources>(AccountError); }
        public Task<Gw2PublicMasteries> GetPublicMasteriesAsync(IReadOnlyList<long> masteryIds, CancellationToken cancellationToken) { Calls.Add("metadata"); return MetadataError is null ? Task.FromResult(Metadata) : Task.FromException<Gw2PublicMasteries>(MetadataError); }
    }

    private sealed class SequenceTimeProvider : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-16T12:00:00Z").AddSeconds(calls++);
    }
}
