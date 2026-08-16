using GW2AccountMCP.Gw2;
using GW2AccountMCP.Tools;
using ModelContextProtocol;
using System.Text.Json;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class GetCharacterInventoryTests
{
    [Fact]
    public async Task GetCharacterInventoryAsync_maps_selected_physical_bags_and_captures_completion_time()
    {
        var client = new FakeGw2ApiClient
        {
            Inventory = new Gw2CharacterInventory("Canonical Hero", new Gw2CharacterInventoryCapacity(2, 1, 1, 0, 1),
                [new Gw2CharacterInventoryBag(0, null, []), new Gw2CharacterInventoryBag(1, new Gw2InventoryBag(10, null, 1), [new Gw2CharacterInventorySlot(0, null)])], true, [])
        };
        var tool = new GetCharacterInventoryTool(client, new FixedTimeProvider());

        var result = await tool.GetCharacterInventoryAsync("Canonical Hero", CancellationToken.None);

        Assert.Equal("Canonical Hero", result.CharacterName);
        Assert.Equal("SelectedCharacterPhysicalBags", result.InventoryScope);
        Assert.Equal(2, result.Capacity.BagPositions);
        Assert.Equal(DateTimeOffset.Parse("2026-08-15T12:00:00Z"), result.AsOf);
        Assert.Equal("Canonical Hero", client.CharacterName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task GetCharacterInventoryAsync_rejects_blank_input_before_source_access(string? characterName)
    {
        var client = new FakeGw2ApiClient();

        await Assert.ThrowsAsync<McpException>(() => new GetCharacterInventoryTool(client, TimeProvider.System).GetCharacterInventoryAsync(characterName!, CancellationToken.None));

        Assert.Null(client.CharacterName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetCharacterInventoryAsync_redacts_transport_and_non_caller_timeout_without_private_inner_exception(bool timeout)
    {
        var client = new FakeGw2ApiClient { Error = timeout ? new OperationCanceledException("/v2/characters/Secret%20Hero/inventory") : new HttpRequestException("/v2/characters/Secret%20Hero/inventory") };

        var error = await Assert.ThrowsAsync<McpException>(() => new GetCharacterInventoryTool(client, TimeProvider.System).GetCharacterInventoryAsync("Secret Hero", CancellationToken.None));

        Assert.Equal("Guild Wars 2 character inventory is unavailable. Try again later.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("Secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCharacterInventoryAsync_maps_rich_physical_stack_fields_and_serializes_unavailable_facts_as_null()
    {
        var client = new FakeGw2ApiClient
        {
            Inventory = new Gw2CharacterInventory("Canonical Hero", new Gw2CharacterInventoryCapacity(2, 1, 2, 2, 0),
            [
                new Gw2CharacterInventoryBag(0, null, []),
                new Gw2CharacterInventoryBag(1, new Gw2InventoryBag(10, "Bag", 2),
                [
                    new Gw2CharacterInventorySlot(0, new Gw2InventoryStack(new Gw2InventoryItem(1, "Sword", "Weapon", "Sword", "Rare", 80), 25, 3, new Gw2InventoryStat(2, "Selected", "Selected", [new Gw2InventoryStatAttribute("Power", 5)]), [new Gw2InventoryReference(3, "Rune"), new Gw2InventoryReference(3, "Rune")], [new Gw2InventoryReference(4, "Infusion")], new Gw2InventoryReference(5, "Skin"), "Character", "Canonical Hero")),
                    new Gw2CharacterInventorySlot(1, new Gw2InventoryStack(new Gw2InventoryItem(6, null, null, null, null, null), 1, null, new Gw2InventoryStat(7, null, "ItemDefault", null), [], [], null, null, null))
                ])
            ], false, [new Gw2MetadataWarning("metadata_unresolved", "items", "6")])
        };

        var result = await new GetCharacterInventoryTool(client, new FixedTimeProvider()).GetCharacterInventoryAsync("Canonical Hero", CancellationToken.None);
        using var document = JsonSerializer.SerializeToDocument(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var rich = document.RootElement.GetProperty("bags")[1].GetProperty("slots")[0].GetProperty("stack");
        Assert.Equal((25L, 3, "Sword", "Selected", "Character", "Canonical Hero"), (rich.GetProperty("count").GetInt64(), rich.GetProperty("charges").GetInt32(), rich.GetProperty("item").GetProperty("name").GetString(), rich.GetProperty("stats").GetProperty("source").GetString(), rich.GetProperty("binding").GetString(), rich.GetProperty("boundTo").GetString()));
        Assert.Equal(2, rich.GetProperty("upgrades").GetArrayLength());
        Assert.Equal("metadata_unresolved", Assert.Single(document.RootElement.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
        var unavailable = document.RootElement.GetProperty("bags")[1].GetProperty("slots")[1].GetProperty("stack");
        Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("charges").ValueKind);
        Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("item").GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("stats").GetProperty("attributes").ValueKind);
        Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("skin").ValueKind);
        Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("binding").ValueKind);
    }

    private sealed class FakeGw2ApiClient : IGw2ApiClient
    {
        public string? CharacterName { get; private set; }
        public Gw2CharacterInventory Inventory { get; set; } = null!;
        public Exception? Error { get; set; }
        public Task<Gw2CharacterInventory> GetCharacterInventoryAsync(string characterName, CancellationToken cancellationToken)
        {
            CharacterName = characterName;
            return Error is null ? Task.FromResult(Inventory) : Task.FromException<Gw2CharacterInventory>(Error);
        }

        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Characters> GetCharactersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterBuild> GetCharacterBuildAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterEquipment> GetCharacterEquipmentAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterEquipmentTabs> GetCharacterEquipmentTabsAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
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

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-15T12:00:00Z");
    }
}
