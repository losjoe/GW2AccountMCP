using GW2AccountMCP.Gw2;
using GW2AccountMCP.Tools;
using ModelContextProtocol;
using System.Text.Json;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class GetCharacterEquipmentTests
{
    [Fact]
    public async Task GetCharacterEquipmentTabsAsync_returns_all_tabs_and_ownership_disclosures()
    {
        var client = new FakeGw2ApiClient { EquipmentTabs = new Gw2CharacterEquipmentTabs("Canonical Hero", 1, [new Gw2CharacterEquipmentTab(1, "", true, [])], true, [], DateTimeOffset.UnixEpoch, null, null, null, null, DateTimeOffset.UnixEpoch) };

        var result = await new GetCharacterEquipmentTabsTool(client).GetCharacterEquipmentTabsAsync("Canonical Hero", CancellationToken.None);

        Assert.Equal("AllEquipmentTabsPveWvwCombatReferences", result.EquipmentScope);
        Assert.False(result.IsOwnershipData);
        Assert.Equal(1, result.ActiveTab);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task GetCharacterEquipmentAsync_rejects_blank_name_before_source_access(string? characterName)
    {
        var client = new FakeGw2ApiClient();
        var tool = new GetCharacterEquipmentTool(client, TimeProvider.System);

        await Assert.ThrowsAsync<McpException>(() => tool.GetCharacterEquipmentAsync(characterName!, CancellationToken.None));

        Assert.False(client.Called);
    }

    [Fact]
    public async Task GetCharacterEquipmentAsync_maps_explicit_nulls_and_captures_time_after_client_completion()
    {
        var client = new FakeGw2ApiClient
        {
            Equipment = new Gw2CharacterEquipment("Canonical Hero", 2, "", [
                new Gw2EquipmentRow("Helm", new Gw2EquipmentItem(1, null, null, null, null, null), null, [], [], null, null, null, "Equipped", "EquippedReference")
            ], false, [new Gw2MetadataWarning("metadata_unresolved", "items", "1")])
        };
        var tool = new GetCharacterEquipmentTool(client, new RecordingTimeProvider(client));

        var result = await tool.GetCharacterEquipmentAsync("Canonical Hero", CancellationToken.None);
        using var document = JsonSerializer.SerializeToDocument(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var row = Assert.Single(document.RootElement.GetProperty("equipment").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("stats").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("skin").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("binding").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("boundTo").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("item").GetProperty("name").ValueKind);
        Assert.False(result.IsOwnershipData);
        Assert.Equal(["equipment", "time"], client.Calls);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("timeout")]
    public async Task GetCharacterEquipmentAsync_redacts_operational_failures_without_private_paths(string kind)
    {
        const string privatePath = "/v2/characters/Secret%20Hero/equipmenttabs/active";
        var client = new FakeGw2ApiClient { Error = kind == "transport" ? new HttpRequestException(privatePath) : new OperationCanceledException(privatePath) };
        var tool = new GetCharacterEquipmentTool(client, TimeProvider.System);

        var error = await Assert.ThrowsAsync<McpException>(() => tool.GetCharacterEquipmentAsync("Secret Hero", CancellationToken.None));

        Assert.Equal("Guild Wars 2 character equipment is unavailable. Try again later.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("Secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCharacterEquipmentAsync_propagates_caller_cancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var client = new FakeGw2ApiClient { Error = new OperationCanceledException(cancellationSource.Token) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GetCharacterEquipmentTool(client, TimeProvider.System).GetCharacterEquipmentAsync("Synthetic Hero", cancellationSource.Token));
    }

    private sealed class FakeGw2ApiClient : IGw2ApiClient
    {
        public bool Called { get; private set; }
        public List<string> Calls { get; } = [];
        public Gw2CharacterEquipment Equipment { get; set; } = null!;
        public Gw2CharacterEquipmentTabs EquipmentTabs { get; set; } = null!;
        public Exception? Error { get; set; }
        public Task<Gw2CharacterEquipment> GetCharacterEquipmentAsync(string characterName, CancellationToken cancellationToken)
        {
            Called = true;
            Calls.Add("equipment");
            return Error is null ? Task.FromResult(Equipment) : Task.FromException<Gw2CharacterEquipment>(Error);
        }
        public Task<Gw2CharacterEquipmentTabs> GetCharacterEquipmentTabsAsync(string characterName, CancellationToken cancellationToken) => Task.FromResult(EquipmentTabs);
        public Task<Gw2CharacterInventory> GetCharacterInventoryAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Characters> GetCharactersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterBuild> GetCharacterBuildAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
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
        public Task<Gw2AccountMasterySources> GetAccountMasterySourcesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicMasteries> GetPublicMasteriesAsync(IReadOnlyList<long> masteryIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingTimeProvider(FakeGw2ApiClient client) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            client.Calls.Add("time");
            return DateTimeOffset.Parse("2026-08-14T12:00:00Z");
        }
    }
}
