using GW2AccountMCP.Items;
using GW2AccountMCP.Prices;
using GW2AccountMCP.Tools;
using ModelContextProtocol;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class GetItemPricesToolTests
{
    [Fact]
    public async Task Returns_ordered_quoted_zero_and_absent_rows_with_explicit_nulls()
    {
        var snapshot = Snapshot([new CachedPrice(1, true, 5, 10, 0, 0), new CachedPrice(long.MaxValue, false, 0, 0, 7, 11)]);
        var tool = new GetItemPricesTool(new FixedProvider(snapshot), new FixedNames(new Dictionary<long, string> { [1] = "Alpha" }), new FixedTimeProvider());

        var result = await tool.GetItemPricesAsync([long.MaxValue, 1, 2], CancellationToken.None);

        Assert.Equal([long.MaxValue, 1, 2], result.Items.Select(item => item.Id));
        Assert.Equal(("Quoted", false, 0L, (long?)null, 7L, 11L, false, true), result.Items[0] is var first ? (first.Status, first.FreeAccountTradable, first.BuyQuantity, first.HighestBuyUnitPrice, first.SellQuantity, first.LowestSellUnitPrice, first.BuyOrdersAvailable, first.SellOrdersAvailable) : default);
        Assert.Equal("Alpha", result.Items[1].Name);
        Assert.Equal(("NoPriceResourceInGeneration", (bool?)null, (long?)null, (bool?)null), result.Items[2] is var absent ? (absent.Status, absent.FreeAccountTradable, absent.BuyQuantity, absent.BuyOrdersAvailable) : default);
        Assert.True(result.IsCompletePriceGeneration);
        Assert.Equal(result.SourceCompletedAtUtc, result.AsOf);
        Assert.Contains("tp --fresh", result.FreshnessStatement, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new long[] { })]
    [InlineData(new long[] { 0 })]
    [InlineData(new long[] { 1, 1 })]
    public async Task Rejects_invalid_id_sets_before_provider_work(long[] ids)
    {
        var provider = new FixedProvider(Snapshot([]));
        var tool = new GetItemPricesTool(provider, new FixedNames(new Dictionary<long, string>()), new FixedTimeProvider());

        await Assert.ThrowsAsync<McpException>(() => tool.GetItemPricesAsync(ids, CancellationToken.None));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Item_metadata_unavailability_does_not_remove_price_facts()
    {
        var tool = new GetItemPricesTool(new FixedProvider(Snapshot([new CachedPrice(1, true, 1, 2, 3, 4)])), new FailingNames(), new FixedTimeProvider());

        var result = await tool.GetItemPricesAsync([1], CancellationToken.None);

        Assert.Null(result.Items[0].Name);
        Assert.Equal(2, result.Items[0].HighestBuyUnitPrice);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task Cache_age_is_measured_from_source_completion_not_cache_generation()
    {
        var completed = DateTimeOffset.Parse("2026-08-15T12:01:00Z").UtcDateTime;
        var snapshot = Snapshot([new CachedPrice(1, true, 1, 2, 3, 4)], completed.AddMinutes(-1), completed, completed.AddMinutes(5));
        var tool = new GetItemPricesTool(new FixedProvider(snapshot), new FixedNames(new Dictionary<long, string>()), new FixedTimeProvider(completed.AddMinutes(10)));

        var result = await tool.GetItemPricesAsync([1], CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(10), result.CacheAge);
    }

    [Fact]
    public async Task Clock_before_source_completion_clamps_cache_age_and_warns()
    {
        var completed = DateTimeOffset.Parse("2026-08-15T12:01:00Z").UtcDateTime;
        var snapshot = Snapshot([new CachedPrice(1, true, 1, 2, 3, 4)], completed.AddMinutes(-1), completed, completed.AddMinutes(2));
        var tool = new GetItemPricesTool(new FixedProvider(snapshot), new FixedNames(new Dictionary<long, string>()), new FixedTimeProvider(completed.AddMinutes(-1)));

        var result = await tool.GetItemPricesAsync([1], CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, result.CacheAge);
        Assert.Contains(result.Warnings, warning => warning.Contains("system clock precedes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Price_cache_failure_is_actionable_redacted_and_preserves_caller_cancellation()
    {
        var tool = new GetItemPricesTool(new ThrowingProvider(), new FixedNames(new Dictionary<long, string>()), new FixedTimeProvider(DateTime.UtcNow));

        var exception = await Assert.ThrowsAsync<McpException>(() => tool.GetItemPricesAsync([1], CancellationToken.None));

        Assert.Contains("price cache is unavailable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-path", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_unchanged()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var tool = new GetItemPricesTool(new FixedProvider(Snapshot([])), new FixedNames(new Dictionary<long, string>()), new FixedTimeProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.GetItemPricesAsync([1], cancellation.Token));
    }

    private static PriceCacheSnapshot Snapshot(IReadOnlyList<CachedPrice> prices, DateTime? started = null, DateTime? completed = null, DateTime? generated = null) => new(prices, new PriceCacheFingerprint("a".PadLeft(64, 'a'), "prices." + "a".PadLeft(64, 'a') + ".csv", DateTime.UnixEpoch, 1, DateTime.UnixEpoch, 1), started ?? DateTime.UnixEpoch, completed ?? DateTime.UnixEpoch.AddMinutes(1), generated ?? DateTime.UnixEpoch.AddMinutes(2));
    private sealed class FixedProvider(PriceCacheSnapshot snapshot) : IPriceSnapshotProvider { public int Calls { get; private set; } public Task<PriceSnapshotResult> GetSnapshotAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult(new PriceSnapshotResult(snapshot, null)); } }
    private sealed class FixedNames(IReadOnlyDictionary<long, string> names) : IItemNameLookup { public Task<IReadOnlyDictionary<long, string>> GetNamesAsync(CancellationToken cancellationToken) => Task.FromResult(names); }
    private sealed class FailingNames : IItemNameLookup { public Task<IReadOnlyDictionary<long, string>> GetNamesAsync(CancellationToken cancellationToken) => throw new ItemCacheException("The item cache is unavailable."); }
    private sealed class ThrowingProvider : IPriceSnapshotProvider { public Task<PriceSnapshotResult> GetSnapshotAsync(CancellationToken cancellationToken) => throw new PriceCacheException("private-path"); }
    private sealed class FixedTimeProvider(DateTime now) : TimeProvider { public FixedTimeProvider() : this(DateTimeOffset.Parse("2026-08-15T12:03:00Z").UtcDateTime) { } public override DateTimeOffset GetUtcNow() => now; }
}
