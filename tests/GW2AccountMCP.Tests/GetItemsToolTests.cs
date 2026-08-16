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

        var result = await tool.GetItemsAsync([3, 2, 1], CancellationToken.None);

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
        Assert.Contains("public request-time", result.SourceStatement, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, client.Calls);
    }

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public async Task Rejects_invalid_ids_before_client_work(IReadOnlyList<long>? itemIds)
    {
        var client = new FakeGw2ApiClient();

        await Assert.ThrowsAsync<McpException>(() => new GetItemsTool(client, TimeProvider.System).GetItemsAsync(itemIds, CancellationToken.None));

        Assert.Equal(0, client.Calls);
    }

    public static TheoryData<IReadOnlyList<long>?> InvalidIds => new()
    {
        { null }, { Array.Empty<long>() }, { new long[] { 0 } }, { new long[] { 1, 1 } }, { Enumerable.Range(1, 101).Select(id => (long)id).ToArray() }
    };

    [Fact]
    public async Task Maps_client_failure_and_preserves_caller_cancellation()
    {
        var unavailable = new FakeGw2ApiClient { Error = new Gw2ConfigurationException("GW2 public item request failed with HTTP 500. Try again later.") };
        var error = await Assert.ThrowsAsync<McpException>(() => new GetItemsTool(unavailable, TimeProvider.System).GetItemsAsync([1], CancellationToken.None));
        Assert.Contains("HTTP 500", error.Message, StringComparison.Ordinal);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var cancelled = new FakeGw2ApiClient { Error = new OperationCanceledException(cancellationSource.Token) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GetItemsTool(cancelled, TimeProvider.System).GetItemsAsync([1], cancellationSource.Token));
    }

    private sealed class FakeGw2ApiClient : IGw2ApiClient
    {
        public int Calls { get; private set; }
        public Gw2PublicItems PublicItems { get; set; } = new([], [], []);
        public Exception? Error { get; set; }

        public Task<Gw2PublicItems> GetPublicItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken)
        {
            Calls++;
            return Error is null ? Task.FromResult(PublicItems) : Task.FromException<Gw2PublicItems>(Error);
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
        public Task<Gw2LegendaryArmory> GetLegendaryArmoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-16T12:00:00Z");
    }
}
