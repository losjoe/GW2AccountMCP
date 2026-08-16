using System.Text.Json;
using GW2AccountMCP.Gw2;
using GW2AccountMCP.Tools;
using ModelContextProtocol;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class GetAchievementProgressToolTests
{
    [Fact]
    public async Task Returns_caller_ordered_account_and_public_facts_with_duplicate_completed_bits()
    {
        var client = new FakeGw2ApiClient
        {
            Account = new Gw2AccountAchievementProgress(
            [
                new Gw2AccountAchievementProgressEntry(2, 4, 6, true, 1, true, [1, 1, 4])
            ]),
            Definitions = new Gw2PublicAchievements(
            [
                new Gw2PublicAchievement(2, "Future achievement", "", "", "", "FutureType", ["Zeta", "Alpha"], [new Gw2AchievementBit("One", 10, "first"), new Gw2AchievementBit("", 11, "blank"), new Gw2AchievementBit("FutureBit", null, null), new Gw2AchievementBit("", null, null), new Gw2AchievementBit("FutureBit", 14, "fourth")])
            ],
            [1])
        };
        var result = await new GetAchievementProgressTool(client, new SequenceTimeProvider()).GetAchievementProgressAsync([1, 2], CancellationToken.None);
        using var document = JsonSerializer.SerializeToDocument(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal([1L, 2L], result.Rows.Select(row => row.Id));
        Assert.Equal(["NoAccountProgressRecord", "ReportedAccountProgress"], result.Rows.Select(row => row.AccountProgressStatus));
        Assert.Equal(["NoPublicAchievementResource", "Found"], result.Rows.Select(row => row.DefinitionStatus));
        Assert.All(new[] { "Current", "Max", "Done", "Repeated", "IsUnlocked", "CompletedBits" }, property => Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("rows")[0].GetProperty(char.ToLowerInvariant(property[0]) + property[1..]).ValueKind));
        var found = result.Rows[1];
        Assert.Equal((4L, 6L, true, 1L, true), (found.Current, found.Max, found.Done, found.Repeated, found.IsUnlocked));
        Assert.Equal(["Alpha", "Zeta"], found.Flags);
        Assert.Equal(5, found.BitCount);
        Assert.Equal([(1L, false, (string?)null), (1L, false, null), (4L, true, "FutureBit")], found.CompletedBits!.Select(bit => (bit.Index, bit.IsDefinitionResolved, bit.Type)));
        Assert.Equal([1L], result.MissingDefinitionIds);
        Assert.False(result.AreAllDefinitionsResolved);
        Assert.False(result.IsAtomicSnapshot);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), result.AccountProgressAsOf);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:01Z"), result.DefinitionsAsOf);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:02Z"), result.AsOf);
        Assert.Equal(["account", "definitions"], client.Calls);
    }

    [Fact]
    public async Task Maps_found_public_definition_when_the_account_has_no_progress_record()
    {
        var client = new FakeGw2ApiClient
        {
            Account = new Gw2AccountAchievementProgress([]),
            Definitions = new Gw2PublicAchievements(
                [new Gw2PublicAchievement(1, "Public name", "Public description", "Public requirement", "Public locked text", "FutureType", ["Zeta", "Alpha"], [new Gw2AchievementBit("FutureBit", null, null)])],
                [])
        };

        var row = Assert.Single((await new GetAchievementProgressTool(client, TimeProvider.System).GetAchievementProgressAsync([1], CancellationToken.None)).Rows);

        Assert.Equal("NoAccountProgressRecord", row.AccountProgressStatus);
        Assert.Equal("Found", row.DefinitionStatus);
        Assert.Null(row.Current);
        Assert.Null(row.Max);
        Assert.Null(row.Done);
        Assert.Null(row.Repeated);
        Assert.Null(row.IsUnlocked);
        Assert.Null(row.CompletedBits);
        Assert.Equal(("Public name", "Public description", "Public requirement", "Public locked text", "FutureType"), (row.Name, row.Description, row.Requirement, row.LockedText, row.Type));
        Assert.Equal(["Alpha", "Zeta"], row.Flags);
        Assert.Equal(1, row.BitCount);
    }

    [Fact]
    public async Task Validates_ids_before_any_source_work()
    {
        var client = new FakeGw2ApiClient();
        var tool = new GetAchievementProgressTool(client, TimeProvider.System);

        foreach (var ids in new IReadOnlyList<long>?[] { null, [], [0], [1, 1], Enumerable.Range(1, 21).Select(id => (long)id).ToArray() })
        {
            await Assert.ThrowsAsync<McpException>(() => tool.GetAchievementProgressAsync(ids!, CancellationToken.None));
        }

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Degrades_whole_public_failure_preserving_account_facts_and_propagates_caller_cancellation()
    {
        var client = new FakeGw2ApiClient
        {
            Account = new Gw2AccountAchievementProgress([new Gw2AccountAchievementProgressEntry(1, null, null, false, null, true, [])]),
            PublicError = new HttpRequestException("private definition route")
        };
        var result = await new GetAchievementProgressTool(client, new SequenceTimeProvider()).GetAchievementProgressAsync([1], CancellationToken.None);

        Assert.Equal("ReportedAccountProgress", Assert.Single(result.Rows).AccountProgressStatus);
        Assert.Equal("PublicDefinitionsUnavailable", result.Rows[0].DefinitionStatus);
        Assert.Null(result.MissingDefinitionIds);
        Assert.Null(result.DefinitionsAsOf);
        Assert.False(result.AreAllDefinitionsResolved);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GetAchievementProgressTool(new FakeGw2ApiClient { PublicError = new OperationCanceledException(cancellationSource.Token) }, TimeProvider.System).GetAchievementProgressAsync([1], cancellationSource.Token));
    }

    [Fact]
    public async Task Rejects_more_than_512_completed_bits_without_truncation()
    {
        var client = new FakeGw2ApiClient
        {
            Account = new Gw2AccountAchievementProgress([new Gw2AccountAchievementProgressEntry(1, null, null, true, null, true, Enumerable.Repeat(0L, 513).ToArray())]),
            Definitions = new Gw2PublicAchievements([new Gw2PublicAchievement(1, "One", "", "", "", "Type", [], [new Gw2AchievementBit("Bit", null, null)])], [])
        };

        await Assert.ThrowsAsync<McpException>(() => new GetAchievementProgressTool(client, TimeProvider.System).GetAchievementProgressAsync([1], CancellationToken.None));
        Assert.Equal(["account"], client.Calls);
    }

    [Fact]
    public async Task Returns_exactly_512_selected_completed_bits()
    {
        var client = new FakeGw2ApiClient
        {
            Account = new Gw2AccountAchievementProgress([new Gw2AccountAchievementProgressEntry(1, null, null, true, null, true, Enumerable.Repeat(0L, 512).ToArray())]),
            Definitions = new Gw2PublicAchievements([new Gw2PublicAchievement(1, "One", "", "", "", "Type", [], [new Gw2AchievementBit("Bit", null, null)])], [])
        };

        var result = await new GetAchievementProgressTool(client, TimeProvider.System).GetAchievementProgressAsync([1], CancellationToken.None);

        Assert.Equal(512, Assert.Single(result.Rows).CompletedBits!.Count);
        Assert.All(result.Rows[0].CompletedBits!, bit => Assert.True(bit.IsDefinitionResolved));
        Assert.Equal(["account", "definitions"], client.Calls);
    }

    [Fact]
    public async Task Caps_unresolved_bit_warnings_in_caller_and_source_order_without_changing_facts()
    {
        var client = new FakeGw2ApiClient
        {
            Account = new Gw2AccountAchievementProgress(
            [
                new Gw2AccountAchievementProgressEntry(2, null, null, true, null, true, Enumerable.Range(0, 33).Select(index => (long)index).ToArray()),
                new Gw2AccountAchievementProgressEntry(1, null, null, false, null, true, [0, 1, 2])
            ]),
            Definitions = new Gw2PublicAchievements(
            [
                new Gw2PublicAchievement(1, "One", "", "", "", "Type", [], []),
                new Gw2PublicAchievement(2, "Two", "", "", "", "Type", [], [])
            ], [])
        };

        var result = await new GetAchievementProgressTool(client, TimeProvider.System).GetAchievementProgressAsync([2, 1], CancellationToken.None);

        Assert.Equal(32, result.Warnings.Count);
        Assert.Equal(Enumerable.Range(0, 32).Select(index => $"Achievement 2 completed bit {index} has no resolvable public bit definition."), result.Warnings);
        Assert.Equal([33, 3], result.Rows.Select(row => row.CompletedBits!.Count));
        Assert.All(result.Rows, row =>
        {
            Assert.Equal("ReportedAccountProgress", row.AccountProgressStatus);
            Assert.Equal("Found", row.DefinitionStatus);
        });
        Assert.True(result.AreAllDefinitionsResolved);
        Assert.NotNull(result.DefinitionsAsOf);
    }

    private sealed class FakeGw2ApiClient : IGw2ApiClient
    {
        public List<string> Calls { get; } = [];
        public Gw2AccountAchievementProgress Account { get; set; } = new([]);
        public Gw2PublicAchievements Definitions { get; set; } = new([], []);
        public Exception? PublicError { get; set; }
        public Task<Gw2AccountAchievementProgress> GetAccountAchievementProgressAsync(CancellationToken cancellationToken) { Calls.Add("account"); return Task.FromResult(Account); }
        public Task<Gw2PublicAchievements> GetPublicAchievementsAsync(IReadOnlyList<long> achievementIds, CancellationToken cancellationToken) { Calls.Add("definitions"); return PublicError is null ? Task.FromResult(Definitions) : Task.FromException<Gw2PublicAchievements>(PublicError); }
        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Characters> GetCharactersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterBuild> GetCharacterBuildAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterEquipment> GetCharacterEquipmentAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
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
    }

    private sealed class SequenceTimeProvider : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-16T12:00:00Z").AddSeconds(calls++);
    }
}
