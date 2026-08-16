using GW2AccountMCP.Gw2;
using GW2AccountMCP.Tools;
using ModelContextProtocol;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class GetItemsToolTests
{
    [Fact]
    public async Task Returns_found_and_missing_rows_in_caller_order_with_explicit_nulls_and_completion_time()
    {
        var client = new FakeGw2ApiClient
        {
            PublicItems = new Gw2PublicItems(
                [new Gw2PublicItem(2, null, "FutureType", "FutureRarity", 80, 123, ["Alpha"], ["Pve"], [])],
                [3, 1],
                Enumerable.Range(1, 20).Select(index => $"warning {index}").ToArray())
        };
        var tool = new GetItemsTool(client, new FixedTimeProvider());

        var result = await tool.GetItemsAsync([3, 2, 1], cancellationToken: CancellationToken.None);

        Assert.Equal([3L, 2L, 1L], result.Items.Select(item => item.Id));
        Assert.Equal(["NoPublicItemResource", "Found", "NoPublicItemResource"], result.Items.Select(item => item.Status));
        Assert.Equal((string?)null, result.Items[0].Name);
        Assert.Equal((string?)null, result.Items[0].Type);
        Assert.Equal((long?)null, result.Items[0].Level);
        Assert.Null(result.Items[0].Flags);
        Assert.Equal((string?)null, result.Items[1].Name);
        Assert.Equal(("FutureType", "FutureRarity", 80L, 123L), result.Items[1] is var found ? (found.Type, found.Rarity, found.Level, found.VendorValue) : default);
        Assert.Equal(["Alpha"], result.Items[1].Flags);
        Assert.Empty(result.Items[1].Restrictions!);
        Assert.False(result.IsComplete);
        Assert.Equal([3L, 1L], result.MissingItemIds);
        Assert.Equal(16, result.Warnings.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), result.AsOf);
        Assert.Equal("NotRequested", result.MaterialCategoriesStatus);
        Assert.Null(result.MaterialCategoriesAsOf);
        Assert.False(result.IsAtomicSnapshot);
        Assert.All(result.Items, item => Assert.Null(item.MaterialCategories));
        Assert.Contains("public request-time", result.SourceStatement, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, client.MaterialCategoryCalls);
    }

    [Fact]
    public async Task Returns_requested_material_categories_with_independent_completion_time()
    {
        var client = new FakeGw2ApiClient
        {
            PublicItems = new Gw2PublicItems(
                [new Gw2PublicItem(2, "Item", "Material", "Basic", 0, 0, [], [], [])],
                [3],
                []),
            MaterialCategories = new Gw2MaterialCategories(
                [
                    new Gw2MaterialCategory(20, "Later", 2, [2]),
                    new Gw2MaterialCategory(10, "Earlier", 1, [2, 3])
                ])
        };
        var tool = new GetItemsTool(client, new SequenceTimeProvider(
            DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
            DateTimeOffset.Parse("2026-08-16T12:00:01Z")));

        var result = await tool.GetItemsAsync([2, 3], includeMaterialCategories: true, cancellationToken: CancellationToken.None);

        Assert.Equal("Available", result.MaterialCategoriesStatus);
        Assert.False(result.IsAtomicSnapshot);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), result.AsOf);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:01Z"), result.MaterialCategoriesAsOf);
        Assert.Equal([10L, 20L], result.Items[0].MaterialCategories!.Select(category => category.Id));
        Assert.Equal([10L], result.Items[1].MaterialCategories!.Select(category => category.Id));
        Assert.Equal(1, client.MaterialCategoryCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task False_or_null_material_annotation_makes_no_material_call(bool? includeMaterialCategories)
    {
        var client = new FakeGw2ApiClient
        {
            PublicItems = new Gw2PublicItems([new Gw2PublicItem(1, "Item", "Material", "Basic", 0, 0, [], [], [])], [], [])
        };

        var result = await new GetItemsTool(client, new FixedTimeProvider()).GetItemsAsync(
            [1],
            includeMaterialCategories,
            CancellationToken.None);

        Assert.Equal("NotRequested", result.MaterialCategoriesStatus);
        Assert.Null(result.MaterialCategoriesAsOf);
        Assert.Null(Assert.Single(result.Items).MaterialCategories);
        Assert.Equal(0, client.MaterialCategoryCalls);
    }

    [Fact]
    public async Task Available_material_categories_use_empty_for_known_zero_membership_including_missing_items()
    {
        var client = new FakeGw2ApiClient
        {
            PublicItems = new Gw2PublicItems([], [99], []),
            MaterialCategories = new Gw2MaterialCategories([])
        };

        var result = await new GetItemsTool(client, new SequenceTimeProvider(
                DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
                DateTimeOffset.Parse("2026-08-16T12:00:01Z")))
            .GetItemsAsync([99], true, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("NoPublicItemResource", item.Status);
        Assert.NotNull(item.MaterialCategories);
        Assert.Empty(item.MaterialCategories);
        Assert.False(result.IsComplete);
        Assert.Equal([99L], result.MissingItemIds);
    }

    [Fact]
    public async Task Material_failure_preserves_item_facts_and_reports_unavailable_with_bounded_warning()
    {
        var client = new FakeGw2ApiClient
        {
            PublicItems = new Gw2PublicItems(
                [new Gw2PublicItem(1, "Item", "Material", "Basic", 0, 0, [], [], [])],
                [],
                Enumerable.Range(1, 20).Select(index => $"item warning {index}").ToArray()),
            MaterialError = new Gw2ConfigurationException("private material detail")
        };

        var result = await new GetItemsTool(client, new FixedTimeProvider()).GetItemsAsync([1], true, CancellationToken.None);

        Assert.Equal("Found", Assert.Single(result.Items).Status);
        Assert.Equal("Item", result.Items[0].Name);
        Assert.Null(result.Items[0].MaterialCategories);
        Assert.True(result.IsComplete);
        Assert.Equal("Unavailable", result.MaterialCategoriesStatus);
        Assert.Null(result.MaterialCategoriesAsOf);
        Assert.Equal(16, result.Warnings.Count);
        Assert.Contains("material categories are unavailable", result.Warnings[^1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", string.Join(' ', result.Warnings), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Material_annotation_propagates_caller_cancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var client = new FakeGw2ApiClient
        {
            PublicItems = new Gw2PublicItems([], [1], []),
            MaterialError = new OperationCanceledException(cancellationSource.Token)
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GetItemsTool(client, new FixedTimeProvider()).GetItemsAsync([1], true, cancellationSource.Token));
    }

    [Fact]
    public async Task Material_transport_and_timeout_failures_degrade_to_unavailable()
    {
        foreach (var failure in new Exception[]
        {
            new HttpRequestException("private transport detail"),
            new IOException("private response-body detail"),
            new OperationCanceledException("private timeout detail")
        })
        {
            var client = new FakeGw2ApiClient
            {
                PublicItems = new Gw2PublicItems([], [1], []),
                MaterialError = failure
            };

            var result = await new GetItemsTool(client, new FixedTimeProvider()).GetItemsAsync([1], true, CancellationToken.None);

            Assert.Equal("Unavailable", result.MaterialCategoriesStatus);
            Assert.Null(Assert.Single(result.Items).MaterialCategories);
            Assert.DoesNotContain("private", string.Join(' ', result.Warnings), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public async Task Rejects_invalid_ids_before_client_work(IReadOnlyList<long>? itemIds)
    {
        var client = new FakeGw2ApiClient();

        await Assert.ThrowsAsync<McpException>(() => new GetItemsTool(client, TimeProvider.System).GetItemsAsync(itemIds, cancellationToken: CancellationToken.None));

        Assert.Equal(0, client.Calls);
        Assert.Equal(0, client.MaterialCategoryCalls);
    }

    public static TheoryData<IReadOnlyList<long>?> InvalidIds => new()
    {
        { null }, { Array.Empty<long>() }, { new long[] { 0 } }, { new long[] { 1, 1 } }, { Enumerable.Range(1, 101).Select(id => (long)id).ToArray() }
    };

    [Fact]
    public async Task Maps_client_failure_and_preserves_caller_cancellation()
    {
        var unavailable = new FakeGw2ApiClient { Error = new Gw2ConfigurationException("GW2 public item request failed with HTTP 500. Try again later.") };
        var error = await Assert.ThrowsAsync<McpException>(() => new GetItemsTool(unavailable, TimeProvider.System).GetItemsAsync([1], cancellationToken: CancellationToken.None));
        Assert.Contains("HTTP 500", error.Message, StringComparison.Ordinal);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var cancelled = new FakeGw2ApiClient { Error = new OperationCanceledException(cancellationSource.Token) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GetItemsTool(cancelled, TimeProvider.System).GetItemsAsync([1], cancellationToken: cancellationSource.Token));
    }

    private sealed class FakeGw2ApiClient : IGw2ApiClient
    {
        public int Calls { get; private set; }
        public int MaterialCategoryCalls { get; private set; }
        public Gw2PublicItems PublicItems { get; set; } = new([], [], []);
        public Gw2MaterialCategories MaterialCategories { get; set; } = new([]);
        public Exception? Error { get; set; }
        public Exception? MaterialError { get; set; }

        public Task<Gw2PublicItems> GetPublicItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken)
        {
            Calls++;
            return Error is null ? Task.FromResult(PublicItems) : Task.FromException<Gw2PublicItems>(Error);
        }

        public Task<Gw2MaterialCategories> GetPublicMaterialCategoriesAsync(CancellationToken cancellationToken)
        {
            MaterialCategoryCalls++;
            return MaterialError is null
                ? Task.FromResult(MaterialCategories)
                : Task.FromException<Gw2MaterialCategories>(MaterialError);
        }

        public Task<Gw2PublicRecipes> GetPublicRecipesAsync(IReadOnlyList<long> recipeIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2RecipeSelector> SearchPublicRecipesByInputItemAsync(long itemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2RecipeSelector> SearchPublicRecipesByOutputItemAsync(long itemId, CancellationToken cancellationToken) => throw new NotSupportedException();

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
        public Task<Gw2LegendaryArmory> GetLegendaryArmoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-16T12:00:00Z");
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int index;

        public override DateTimeOffset GetUtcNow() => values[index++];
    }
}
