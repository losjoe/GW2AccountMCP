using GW2AccountMCP.Gw2;
using GW2AccountMCP.Tools;
using ModelContextProtocol;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class GetCharactersTests
{
    [Fact]
    public async Task GetCharactersAsync_maps_all_core_fields_preserves_client_order_and_captures_time_after_completion()
    {
        var client = new FakeGw2ApiClient
        {
            Characters = new Gw2Characters(
            [
                new Gw2Character("Alpha Hero", "Human", "Female", "Mesmer", 80, 12, DateTimeOffset.Parse("2020-01-02T03:04:05Z"), DateTimeOffset.Parse("2026-01-02T03:04:05Z"), 4),
                new Gw2Character("Zulu Hero", "Norn", "Male", "Warrior", 2, 0, DateTimeOffset.Parse("2021-02-03T04:05:06Z"), DateTimeOffset.Parse("2026-02-03T04:05:06Z"), 0)
            ])
        };
        var tool = new GetCharactersTool(client, new RecordingTimeProvider(client));

        var result = await tool.GetCharactersAsync(CancellationToken.None);

        Assert.Equal(
        [
            ("Alpha Hero", "Human", "Female", "Mesmer", 80, 12L, DateTimeOffset.Parse("2020-01-02T03:04:05Z"), DateTimeOffset.Parse("2026-01-02T03:04:05Z"), 4L),
            ("Zulu Hero", "Norn", "Male", "Warrior", 2, 0L, DateTimeOffset.Parse("2021-02-03T04:05:06Z"), DateTimeOffset.Parse("2026-02-03T04:05:06Z"), 0L)
        ], result.Characters.Select(character => (character.Name, character.Race, character.Gender, character.Profession, character.Level, character.AgeSeconds, character.Created, character.LastModified, character.Deaths)));
        Assert.Equal(DateTimeOffset.Parse("2026-08-14T12:00:00Z"), result.AsOf);
        Assert.Equal(["characters", "time"], client.Calls);
    }

    [Theory]
    [InlineData("GW2_API_KEY is missing the required characters permission.")]
    [InlineData("GW2 returned an invalid character-core response. Try again later.")]
    public async Task GetCharactersAsync_maps_safe_configuration_errors(string message)
    {
        var client = new FakeGw2ApiClient { Error = new Gw2ConfigurationException(message) };
        var tool = new GetCharactersTool(client, TimeProvider.System);

        var error = await Assert.ThrowsAsync<McpException>(() => tool.GetCharactersAsync(CancellationToken.None));

        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetCharactersAsync_redacts_transport_and_non_caller_timeout_failures(bool timeout)
    {
        var client = new FakeGw2ApiClient
        {
            Error = timeout
                ? new OperationCanceledException("/v2/characters/Secret%20Hero/core timed out")
                : new HttpRequestException("https://example.test/v2/characters/Secret%20Hero/core failed")
        };
        var tool = new GetCharactersTool(client, TimeProvider.System);

        var error = await Assert.ThrowsAsync<McpException>(() => tool.GetCharactersAsync(CancellationToken.None));

        Assert.Equal("Guild Wars 2 character summaries are unavailable. Try again later.", error.Message);
        Assert.DoesNotContain("Secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/v2/", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCharactersAsync_propagates_caller_cancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var client = new FakeGw2ApiClient { Error = new OperationCanceledException(cancellationSource.Token) };
        var tool = new GetCharactersTool(client, TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.GetCharactersAsync(cancellationSource.Token));
    }

    private sealed class FakeGw2ApiClient : IGw2ApiClient
    {
        public List<string> Calls { get; } = [];
        public Gw2Characters Characters { get; set; } = new([]);
        public Exception? Error { get; set; }

        public Task<Gw2Characters> GetCharactersAsync(CancellationToken cancellationToken)
        {
            Calls.Add("characters");
            return Error is null ? Task.FromResult(Characters) : Task.FromException<Gw2Characters>(Error);
        }

        public Task<Gw2CharacterBuild> GetCharacterBuildAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterEquipment> GetCharacterEquipmentAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterInventory> GetCharacterInventoryAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
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
