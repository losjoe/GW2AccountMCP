using GW2AccountMCP.Tools;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class GetTradingPostActivityToolTests
{
    [Fact]
    public async Task GetTradingPostActivityTool_returns_source_order_defaults_and_disclosures_without_order_ids()
    {
        var client = new FakeGw2ApiClient
        {
            Page = new Gw2CurrentBuysPage(0, 50, 2, 51,
            [
                new Gw2CurrentBuyOrder(20, 7, 3, DateTimeOffset.Parse("2026-01-02T03:04:05Z")),
                new Gw2CurrentBuyOrder(10, 5, 4, DateTimeOffset.Parse("2026-01-03T03:04:05Z"))
            ])
        };
        var tool = new GetTradingPostActivityTool(client, new FixedTimeProvider());

        var result = await tool.GetTradingPostActivityAsync("CurrentBuys", null, null, CancellationToken.None);

        Assert.Equal(("CurrentBuys", 0, 50, 2, 51L, 2, true), (result.Mode, result.Page, result.PageSize, result.PageCount, result.TotalCount, result.ReturnedCount, result.HasMore));
        var currentBuys = Assert.IsType<CurrentBuysActivityResult>(result.CurrentBuys);
        Assert.Null(result.CurrentSells);
        Assert.Equal([20L, 10L], currentBuys.Orders.Select(order => order.ItemId));
        Assert.Equal([21L, 20L], currentBuys.Orders.Select(order => order.ReservedCoinCopper));
        Assert.Equal(41, currentBuys.ReservedCoinPageSubtotalCopper);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), result.AsOf);
        Assert.False(result.IsAtomicSnapshot);
        Assert.Contains("not owned", result.SourceStatement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("five minutes", result.FreshnessStatement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("selected page only", result.CompletenessStatement, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([(0, 50)], client.Calls);
    }

    [Fact]
    public async Task GetTradingPostActivityTool_returns_current_sells_in_source_order_with_current_buys_explicitly_null()
    {
        var client = new FakeGw2ApiClient
        {
            SellsPage = new Gw2CurrentSellsPage(1, 2, 3, 5,
            [
                new Gw2CurrentSellPageOrder(20, 7, 251, DateTimeOffset.Parse("2026-01-02T03:04:05Z")),
                new Gw2CurrentSellPageOrder(10, 5, 4, DateTimeOffset.Parse("2026-01-03T03:04:05Z"))
            ])
        };

        var result = await new GetTradingPostActivityTool(client, new FixedTimeProvider()).GetTradingPostActivityAsync("CurrentSells", 1, 2, CancellationToken.None);

        Assert.Equal(("CurrentSells", 1, 2, 3, 5L, 2, true), (result.Mode, result.Page, result.PageSize, result.PageCount, result.TotalCount, result.ReturnedCount, result.HasMore));
        Assert.Null(result.CurrentBuys);
        var currentSells = Assert.IsType<CurrentSellsActivityResult>(result.CurrentSells);
        Assert.Equal([(20L, 7L, 251L), (10L, 5L, 4L)], currentSells.Orders.Select(order => (order.ItemId, order.UnitPriceCopper, order.Quantity)));
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), result.AsOf);
        Assert.False(result.IsAtomicSnapshot);
        Assert.Contains("unfulfilled", result.SourceStatement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("listedForSale", result.SourceStatement, StringComparison.Ordinal);
        Assert.Contains("five minutes", result.FreshnessStatement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("selected page", result.CompletenessStatement, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([(1, 2)], client.SellCalls);
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("currentbuys", null, null)]
    [InlineData("CurrentBuys", -1, null)]
    [InlineData("CurrentBuys", null, 0)]
    [InlineData("CurrentBuys", null, 201)]
    public async Task GetTradingPostActivityTool_rejects_invalid_input_before_client_call(string? mode, int? page, int? pageSize)
    {
        var client = new FakeGw2ApiClient();
        var tool = new GetTradingPostActivityTool(client, TimeProvider.System);

        await Assert.ThrowsAsync<McpException>(() => tool.GetTradingPostActivityAsync(mode!, page, pageSize, CancellationToken.None));

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task GetTradingPostActivityTool_maps_source_failure_and_overflow_without_private_details_or_partial_result()
    {
        var unavailable = new FakeGw2ApiClient { Error = new Gw2ConfigurationException("private order 999999") };
        var unavailableError = await Assert.ThrowsAsync<McpException>(() => new GetTradingPostActivityTool(unavailable, TimeProvider.System).GetTradingPostActivityAsync("CurrentBuys"));
        Assert.DoesNotContain("999999", unavailableError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", unavailableError.Message, StringComparison.OrdinalIgnoreCase);

        var overflow = new FakeGw2ApiClient { Page = new Gw2CurrentBuysPage(0, 50, 1, 1, [new Gw2CurrentBuyOrder(1, long.MaxValue, 2, DateTimeOffset.Parse("2026-01-01T00:00:00Z"))]) };
        var overflowError = await Assert.ThrowsAsync<McpException>(() => new GetTradingPostActivityTool(overflow, TimeProvider.System).GetTradingPostActivityAsync("CurrentBuys"));
        Assert.Contains("too large", overflowError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTradingPostActivityTool_rejects_subtotal_overflow_when_each_order_reserve_fits()
    {
        var client = new FakeGw2ApiClient
        {
            Page = new Gw2CurrentBuysPage(0, 50, 1, 2,
            [
                new Gw2CurrentBuyOrder(1, long.MaxValue, 1, DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
                new Gw2CurrentBuyOrder(2, 1, 1, DateTimeOffset.Parse("2026-01-01T00:00:00Z"))
            ])
        };

        var error = await Assert.ThrowsAsync<McpException>(() => new GetTradingPostActivityTool(client, TimeProvider.System).GetTradingPostActivityAsync("CurrentBuys"));

        Assert.Contains("too large", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(long.MaxValue.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTradingPostActivityTool_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new FakeGw2ApiClient { Error = new OperationCanceledException(cancellation.Token) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GetTradingPostActivityTool(client, TimeProvider.System).GetTradingPostActivityAsync("CurrentBuys", cancellationToken: cancellation.Token));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-16T12:00:00Z");
    }

    private sealed class FakeGw2ApiClient : IGw2ApiClient
    {
        public List<(int Page, int PageSize)> Calls { get; } = [];
        public List<(int Page, int PageSize)> SellCalls { get; } = [];
        public Gw2CurrentBuysPage Page { get; set; } = new(0, 50, 0, 0, []);
        public Gw2CurrentSellsPage SellsPage { get; set; } = new(0, 50, 0, 0, []);
        public Exception? Error { get; set; }
        public Task<Gw2CurrentBuysPage> GetCurrentBuysPageAsync(int page, int pageSize, CancellationToken cancellationToken)
        {
            Calls.Add((page, pageSize));
            return Error is null ? Task.FromResult(Page) : Task.FromException<Gw2CurrentBuysPage>(Error);
        }
        public Task<Gw2CurrentSellsPage> GetCurrentSellsPageAsync(int page, int pageSize, CancellationToken cancellationToken)
        {
            SellCalls.Add((page, pageSize));
            return Error is null ? Task.FromResult(SellsPage) : Task.FromException<Gw2CurrentSellsPage>(Error);
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
        public Task<Gw2Items> GetItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicItems> GetPublicItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2MaterialCategories> GetPublicMaterialCategoriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicRecipes> GetPublicRecipesAsync(IReadOnlyList<long> recipeIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2RecipeSelector> SearchPublicRecipesByInputItemAsync(long itemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2RecipeSelector> SearchPublicRecipesByOutputItemAsync(long itemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountRecipeUnlocks> GetAccountRecipeUnlocksAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2LegendaryArmory> GetLegendaryArmoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
