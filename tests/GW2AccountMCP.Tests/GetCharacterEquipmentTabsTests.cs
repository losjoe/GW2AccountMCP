using System.Text.Json;
using GW2AccountMCP.Gw2;
using GW2AccountMCP.Tools;
using ModelContextProtocol;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class GetCharacterEquipmentTabsTests
{
    [Fact]
    public async Task Invoke_returns_all_tabs_and_ownership_disclosures()
    {
        var result = await new GetCharacterEquipmentTabsTool(new Fake()).GetCharacterEquipmentTabsAsync("Canonical Hero");
        Assert.Equal([1, 2], result.Tabs.Select(tab => tab.Tab));
        Assert.Equal("AllEquipmentTabsPveWvwCombatReferences", result.EquipmentScope);
        Assert.False(result.IsOwnershipData);
        Assert.False(result.IsAtomicSnapshot);
    }

    [Fact]
    public async Task Invoke_serializes_explicit_nullable_metadata_fields()
    {
        var result = await new GetCharacterEquipmentTabsTool(new Fake()).GetCharacterEquipmentTabsAsync("Canonical Hero");
        using var document = JsonSerializer.SerializeToDocument(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var name in new[] { "activeTab", "equipmentAsOf", "itemsAsOf", "itemStatsAsOf", "skinsAsOf" }) Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty(name).ValueKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task Invoke_rejects_nonblank_contract_violations_without_normalizing_character_name(string? name)
    {
        var fake = new Fake();
        await Assert.ThrowsAsync<McpException>(() => new GetCharacterEquipmentTabsTool(fake).GetCharacterEquipmentTabsAsync(name!));
        Assert.Null(fake.Name);
    }

    [Fact]
    public async Task Invoke_returns_empty_tabs_contract()
    {
        var fake = new Fake { Value = new Gw2CharacterEquipmentTabs("Canonical Hero", null, [], true, [], DateTimeOffset.UnixEpoch, null, null, null, null, DateTimeOffset.UnixEpoch) };
        var result = await new GetCharacterEquipmentTabsTool(fake).GetCharacterEquipmentTabsAsync("Canonical Hero");
        Assert.Null(result.ActiveTab);
        Assert.Empty(result.Tabs);
    }

    [Fact]
    public async Task Invoke_does_not_report_equipment_references_as_ownership()
    {
        var result = await new GetCharacterEquipmentTabsTool(new Fake()).GetCharacterEquipmentTabsAsync("Canonical Hero");
        Assert.Contains("isOwnershipData is false", result.OwnershipStatement, StringComparison.Ordinal);
        Assert.Contains("references", result.ScopeStatement, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fake : IGw2ApiClient
    {
        public string? Name { get; private set; }
        public Gw2CharacterEquipmentTabs Value { get; set; } = new("Canonical Hero", null, [new Gw2CharacterEquipmentTab(1, "", true, []), new Gw2CharacterEquipmentTab(2, "", false, [])], true, [], DateTimeOffset.UnixEpoch, null, null, null, null, DateTimeOffset.UnixEpoch);
        public Task<Gw2CharacterEquipmentTabs> GetCharacterEquipmentTabsAsync(string characterName, CancellationToken cancellationToken) { Name = characterName; return Task.FromResult(Value); }
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
        public Task<Gw2AccountAchievementProgress> GetAccountAchievementProgressAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicAchievements> GetPublicAchievementsAsync(IReadOnlyList<long> achievementIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountMasterySources> GetAccountMasterySourcesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicMasteries> GetPublicMasteriesAsync(IReadOnlyList<long> masteryIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
