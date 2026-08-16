using GW2AccountMCP.Gw2;
using GW2AccountMCP.Tools;
using ModelContextProtocol;
using System.Text.Json;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class GetLegendaryArmoryTests
{
    [Fact]
    public void GetLegendaryArmoryAsync_exposes_armory_contract()
    {
        IGw2ApiClient client = new FakeGw2ApiClient();

        Func<CancellationToken, Task<Gw2LegendaryArmory>> operation = client.GetLegendaryArmoryAsync;

        Assert.NotNull(operation);
    }

    [Fact]
    public async Task GetLegendaryArmoryAsync_maps_account_ownership_with_explicit_null_metadata_after_client_completion()
    {
        var client = new FakeGw2ApiClient
        {
            Armory = new Gw2LegendaryArmory(
            [
                new Gw2LegendaryArmoryEntry(1, long.MaxValue, "Synthetic Future Item", "FutureType", "FutureSubtype", "FutureWeight"),
                new Gw2LegendaryArmoryEntry(2, 0, null, null, null, null)
            ],
            false,
            [new Gw2MetadataWarning("metadata_unresolved", "items", "2")])
        };

        var result = await new GetLegendaryArmoryTool(client, new RecordingTimeProvider(client)).GetLegendaryArmoryAsync(CancellationToken.None);
        using var document = JsonSerializer.SerializeToDocument(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("AccountLegendaryArmory", result.OwnershipScope);
        Assert.Equal("AvailableForUseInSingleEquipmentTemplate", result.CountSemantics);
        Assert.Equal((1L, long.MaxValue, "Synthetic Future Item"), (result.Entries[0].Id, result.Entries[0].ArmoryCount, result.Entries[0].Name));
        var unresolved = result.Entries[1];
        Assert.Equal(0, unresolved.ArmoryCount);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("entries")[1].GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("entries")[1].GetProperty("type").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("entries")[1].GetProperty("subtype").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("entries")[1].GetProperty("weightClass").ValueKind);
        Assert.Equal(["armory", "time"], client.Calls);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("timeout")]
    public async Task GetLegendaryArmoryAsync_redacts_operational_failures(string failureKind)
    {
        const string privateDetail = "/v2/account/legendaryarmory?private-owned-id";
        var client = new FakeGw2ApiClient
        {
            Error = failureKind == "transport" ? new HttpRequestException(privateDetail) : new OperationCanceledException(privateDetail)
        };

        var error = await Assert.ThrowsAsync<McpException>(() => new GetLegendaryArmoryTool(client, TimeProvider.System).GetLegendaryArmoryAsync(CancellationToken.None));

        Assert.Equal("Guild Wars 2 Legendary Armory is unavailable. Try again later.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-owned-id", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLegendaryArmoryAsync_maps_safe_configuration_errors_and_propagates_caller_cancellation()
    {
        var configurationError = await Assert.ThrowsAsync<McpException>(() =>
            new GetLegendaryArmoryTool(new FakeGw2ApiClient { Error = new Gw2ConfigurationException("GW2_API_KEY is missing the required unlocks permission.") }, TimeProvider.System)
                .GetLegendaryArmoryAsync(CancellationToken.None));
        Assert.Contains("unlocks permission", configurationError.Message, StringComparison.OrdinalIgnoreCase);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GetLegendaryArmoryTool(new FakeGw2ApiClient { Error = new OperationCanceledException(cancellationSource.Token) }, TimeProvider.System)
                .GetLegendaryArmoryAsync(cancellationSource.Token));
    }

    private sealed class FakeGw2ApiClient : IGw2ApiClient
    {
        public List<string> Calls { get; } = [];
        public Gw2LegendaryArmory Armory { get; set; } = new([], true, []);
        public Exception? Error { get; set; }

        public Task<Gw2LegendaryArmory> GetLegendaryArmoryAsync(CancellationToken cancellationToken)
        {
            Calls.Add("armory");
            return Error is null ? Task.FromResult(Armory) : Task.FromException<Gw2LegendaryArmory>(Error);
        }

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
            return DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        }
    }
}
