using GW2AccountMCP.Items;
using GW2AccountMCP.Prices;
using GW2AccountMCP.Tools;
using ModelContextProtocol;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class ValueItemsToolTests
{
    [Fact]
    public async Task Returns_all_factual_views_and_exact_fees_for_a_quoted_row()
    {
        var tool = new ValueItemsTool(
            new FixedProvider(Snapshot([new CachedPrice(1, true, 4, 11, 5, 13)])),
            new FixedNames(new Dictionary<long, string> { [1] = "Alpha" }),
            new FixedTimeProvider());

        var result = await tool.ValueItemsAsync([new ValueItemRequest(1, 3)], CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal((1L, "Alpha", 3L, "Quoted"), (item.ItemId, item.Name, item.Quantity, item.PriceResourceStatus));
        Assert.Equal((true, 11L, 33L, 2L, 3L, 28L), item.ImmediateSale is var immediate
            ? (immediate.IsAvailable, immediate.UnitQuote, immediate.Gross, immediate.ListingFee, immediate.ExchangeFee, immediate.Net)
            : default);
        Assert.Equal((true, 13L, 39L), item.Acquisition is var acquisition
            ? (acquisition.IsAvailable, acquisition.UnitQuote, acquisition.BuyerTotalCost)
            : default);
        Assert.Equal((true, 13L, 39L, 2L, 4L, 33L), item.HypotheticalListing is var listing
            ? (listing.IsAvailable, listing.UnitQuote, listing.Gross, listing.ListingFee, listing.ExchangeFee, listing.Net)
            : default);
        Assert.Equal((true, 33L, 2L, 3L, 28L), result.ImmediateSale is var immediateTotal
            ? (immediateTotal.IsComplete, immediateTotal.Gross, immediateTotal.ListingFee, immediateTotal.ExchangeFee, immediateTotal.Net)
            : default);
        Assert.Equal((true, 39L), result.Acquisition is var acquisitionTotal
            ? (acquisitionTotal.IsComplete, acquisitionTotal.BuyerTotalCost)
            : default);
        Assert.Equal((true, 39L, 2L, 4L, 33L), result.HypotheticalListing is var listingTotal
            ? (listingTotal.IsComplete, listingTotal.Gross, listingTotal.ListingFee, listingTotal.ExchangeFee, listingTotal.Net)
            : default);
    }

    [Fact]
    public async Task Rejects_null_empty_oversized_duplicate_and_out_of_range_input_before_dependencies()
    {
        var provider = new FixedProvider(Snapshot([]));
        var names = new FixedNames(new Dictionary<long, string>());
        var tool = new ValueItemsTool(provider, names, new FixedTimeProvider());
        var oversized = Enumerable.Range(1, 101).Select(id => new ValueItemRequest(id, 1)).ToArray();

        IReadOnlyList<ValueItemRequest>?[] invalidInputs = [null, Array.Empty<ValueItemRequest>(), oversized, [new ValueItemRequest(1, 1), new ValueItemRequest(1, 2)], [new ValueItemRequest(0, 1)], [new ValueItemRequest(-1, 1)], [new ValueItemRequest(1, 0)], [new ValueItemRequest(1, -1)], [new ValueItemRequest(1, (long)int.MaxValue + 1)]];
        foreach (var input in invalidInputs)
        {
            await Assert.ThrowsAsync<McpException>(() => tool.ValueItemsAsync(input, CancellationToken.None));
        }

        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, names.Calls);
    }

    [Fact]
    public async Task Rejects_a_null_row_before_provider_or_name_work()
    {
        var provider = new FixedProvider(Snapshot([]));
        var names = new FixedNames(new Dictionary<long, string>());
        var tool = new ValueItemsTool(provider, names, new FixedTimeProvider());
        var nullRow = (IReadOnlyList<ValueItemRequest>)(object)new ValueItemRequest?[] { null };

        var exception = await Assert.ThrowsAsync<McpException>(() => tool.ValueItemsAsync(nullRow, CancellationToken.None));

        Assert.Contains("Items must contain", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, names.Calls);
    }

    [Fact]
    public async Task Distinguishes_zero_sides_missing_resources_and_nullable_names_in_caller_order()
    {
        var tool = new ValueItemsTool(
            new FixedProvider(Snapshot([new CachedPrice(1, true, 0, 0, 7, 8), new CachedPrice(2, true, 5, 6, 0, 0)])),
            new FixedNames(new Dictionary<long, string> { [2] = "Bravo" }),
            new FixedTimeProvider());

        var result = await tool.ValueItemsAsync([new ValueItemRequest(3, 1), new ValueItemRequest(2, 1), new ValueItemRequest(1, 1)]);

        Assert.Equal([3L, 2L, 1L], result.Items.Select(item => item.ItemId));
        Assert.Equal("NoPriceResourceInGeneration", result.Items[0].PriceResourceStatus);
        Assert.Equal((false, "NoPriceResourceInGeneration", (long?)null), result.Items[0].ImmediateSale is var absent ? (absent.IsAvailable, absent.Availability, absent.Gross) : default);
        Assert.Equal("Bravo", result.Items[1].Name);
        Assert.Equal((false, "NoCurrentSellOrders", (long?)null), result.Items[1].Acquisition is var noSells ? (noSells.IsAvailable, noSells.Availability, noSells.BuyerTotalCost) : default);
        Assert.Null(result.Items[2].Name);
        Assert.Equal((false, "NoCurrentBuyOrders", (long?)null), result.Items[2].ImmediateSale is var noBuys ? (noBuys.IsAvailable, noBuys.Availability, noBuys.Gross) : default);
    }

    [Fact]
    public async Task Applies_per_row_aggregate_gross_round_half_up_and_minimum_fees()
    {
        var tool = new ValueItemsTool(
            new FixedProvider(Snapshot([new CachedPrice(1, true, 1, 5, 1, 5), new CachedPrice(2, true, 1, 9, 1, 9), new CachedPrice(3, true, 1, 10, 1, 10), new CachedPrice(4, true, 1, 15, 1, 15), new CachedPrice(5, true, 1, 30, 1, 30), new CachedPrice(6, true, 1, 1, 1, 1)])),
            new FixedNames(new Dictionary<long, string>()), new FixedTimeProvider());

        var result = await tool.ValueItemsAsync([new ValueItemRequest(1, 2), new ValueItemRequest(2, 1), new ValueItemRequest(3, 1), new ValueItemRequest(4, 1), new ValueItemRequest(5, 1), new ValueItemRequest(6, 1)]);

        Assert.Equal((10L, 1L, 1L, 8L), result.Items[0].ImmediateSale is var aggregateGross ? (aggregateGross.Gross, aggregateGross.ListingFee, aggregateGross.ExchangeFee, aggregateGross.Net) : default);
        Assert.Equal((9L, 1L, 1L, 7L), result.Items[1].ImmediateSale is var lowCopper ? (lowCopper.Gross, lowCopper.ListingFee, lowCopper.ExchangeFee, lowCopper.Net) : default);
        Assert.Equal((10L, 1L, 1L, 8L), result.Items[2].ImmediateSale is var halfUp ? (halfUp.Gross, halfUp.ListingFee, halfUp.ExchangeFee, halfUp.Net) : default);
        Assert.Equal((15L, 1L, 2L, 12L), result.Items[3].ImmediateSale is var exchangeHalfUp ? (exchangeHalfUp.Gross, exchangeHalfUp.ListingFee, exchangeHalfUp.ExchangeFee, exchangeHalfUp.Net) : default);
        Assert.Equal((30L, 2L, 3L, 25L), result.Items[4].ImmediateSale is var listingHalfUp ? (listingHalfUp.Gross, listingHalfUp.ListingFee, listingHalfUp.ExchangeFee, listingHalfUp.Net) : default);
        Assert.Equal((1L, 1L, 1L, -1L), result.Items[5].ImmediateSale is var minimumFees ? (minimumFees.Gross, minimumFees.ListingFee, minimumFees.ExchangeFee, minimumFees.Net) : default);
        Assert.Equal((75L, 7L, 9L, 59L), result.ImmediateSale is var total ? (total.Gross, total.ListingFee, total.ExchangeFee, total.Net) : default);
        Assert.Contains("round-half-up", result.FeePolicy.RoundingAndMinimumStatement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Returns_complete_sums_or_null_incomplete_totals_with_caller_order_missing_ids()
    {
        var tool = new ValueItemsTool(
            new FixedProvider(Snapshot([new CachedPrice(1, true, 1, 10, 1, 20), new CachedPrice(2, true, 0, 0, 1, 30)])),
            new FixedNames(new Dictionary<long, string>()), new FixedTimeProvider());

        var result = await tool.ValueItemsAsync([new ValueItemRequest(3, 1), new ValueItemRequest(2, 1), new ValueItemRequest(1, 1)]);

        Assert.False(result.ImmediateSale.IsComplete);
        Assert.Equal([3L, 2L], result.ImmediateSale.MissingItemIds);
        Assert.Null(result.ImmediateSale.Gross);
        Assert.False(result.Acquisition.IsComplete);
        Assert.Equal([3L], result.Acquisition.MissingItemIds);
        Assert.Null(result.Acquisition.BuyerTotalCost);
        Assert.False(result.HypotheticalListing.IsComplete);
        Assert.Equal([3L], result.HypotheticalListing.MissingItemIds);
        Assert.Null(result.HypotheticalListing.Net);
    }

    [Fact]
    public async Task Fails_the_whole_call_for_row_or_aggregate_range_overflow_even_when_another_row_is_missing()
    {
        var multiplication = new ValueItemsTool(
            new FixedProvider(Snapshot([new CachedPrice(1, true, 1, long.MaxValue, 1, long.MaxValue)])),
            new FixedNames(new Dictionary<long, string>()), new FixedTimeProvider());
        var aggregate = new ValueItemsTool(
            new FixedProvider(Snapshot([new CachedPrice(1, true, 1, long.MaxValue, 1, long.MaxValue), new CachedPrice(2, true, 1, long.MaxValue, 1, long.MaxValue)])),
            new FixedNames(new Dictionary<long, string>()), new FixedTimeProvider());

        var rowException = await Assert.ThrowsAsync<McpException>(() => multiplication.ValueItemsAsync([new ValueItemRequest(1, 2)]));
        var aggregateException = await Assert.ThrowsAsync<McpException>(() => aggregate.ValueItemsAsync([new ValueItemRequest(1, 1), new ValueItemRequest(3, 1), new ValueItemRequest(2, 1)]));

        Assert.Contains("arithmetic", rowException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(rowException.InnerException);
        Assert.Contains("Int64", aggregateException.Message, StringComparison.Ordinal);
        Assert.Null(aggregateException.InnerException);
    }

    [Fact]
    public async Task Safely_calculates_near_int64_maximum_without_overflowing_fee_rounding()
    {
        var tool = new ValueItemsTool(
            new FixedProvider(Snapshot([new CachedPrice(1, true, 1, long.MaxValue, 1, long.MaxValue)])),
            new FixedNames(new Dictionary<long, string>()), new FixedTimeProvider());

        var result = await tool.ValueItemsAsync([new ValueItemRequest(1, 1)]);

        Assert.Equal(long.MaxValue, result.ImmediateSale.Gross);
        Assert.Equal(long.MaxValue / 20, result.ImmediateSale.ListingFee);
        Assert.Equal(long.MaxValue / 10 + 1, result.ImmediateSale.ExchangeFee);
        Assert.NotNull(result.ImmediateSale.Net);
    }

    [Fact]
    public async Task Uses_one_snapshot_preserves_adoption_and_freshness_facts_and_keeps_names_best_effort()
    {
        var completed = DateTimeOffset.Parse("2026-08-15T12:01:00Z").UtcDateTime;
        var provider = new FixedProvider(Snapshot([new CachedPrice(1, true, 1, 2, 1, 3)], completed.AddMinutes(-1), completed, completed.AddMinutes(5)), "Prior generation retained.");
        var tool = new ValueItemsTool(provider, new FailingNames(), new FixedTimeProvider(completed.AddMinutes(-1)));

        var result = await tool.ValueItemsAsync([new ValueItemRequest(1, 1)]);

        Assert.Equal(1, provider.Calls);
        Assert.Equal(result.SourceCompletedAtUtc, result.AsOf);
        Assert.Equal(TimeSpan.FromMinutes(1), result.CollectionDuration);
        Assert.Equal(TimeSpan.Zero, result.CacheAge);
        Assert.Equal("ManualCachedSnapshot", result.FreshnessStatus);
        Assert.Contains(result.Warnings, warning => warning.Contains("Prior generation", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("item-name metadata", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, warning => warning.Contains("system clock", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("not execution guarantees", result.BestPriceExtrapolationStatement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no buy", result.ScopeStatement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recipe cost", result.ScopeStatement, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Warnings.Count <= 16);
    }

    [Fact]
    public async Task Cache_age_is_measured_from_source_completion_not_cache_generation()
    {
        var completed = DateTimeOffset.Parse("2026-08-15T12:01:00Z").UtcDateTime;
        var tool = new ValueItemsTool(
            new FixedProvider(Snapshot([new CachedPrice(1, true, 1, 2, 1, 3)], completed.AddMinutes(-1), completed, completed.AddMinutes(5))),
            new FixedNames(new Dictionary<long, string>()), new FixedTimeProvider(completed.AddMinutes(10)));

        var result = await tool.ValueItemsAsync([new ValueItemRequest(1, 1)]);

        Assert.Equal(TimeSpan.FromMinutes(10), result.CacheAge);
    }

    [Fact]
    public async Task Maps_initial_unavailability_to_redacted_error_and_propagates_caller_cancellation()
    {
        var unavailable = new ValueItemsTool(new ThrowingProvider(), new FixedNames(new Dictionary<long, string>()), new FixedTimeProvider());
        var exception = await Assert.ThrowsAsync<McpException>(() => unavailable.ValueItemsAsync([new ValueItemRequest(1, 1)]));
        Assert.Contains("price cache is unavailable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-path", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(exception.InnerException);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new ValueItemsTool(new FixedProvider(Snapshot([])), new FixedNames(new Dictionary<long, string>()), new FixedTimeProvider());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ValueItemsAsync([new ValueItemRequest(1, 1)], cancellation.Token));
    }

    private static PriceCacheSnapshot Snapshot(IReadOnlyList<CachedPrice> prices, DateTime? started = null, DateTime? completed = null, DateTime? generated = null) => new(
        prices,
        new PriceCacheFingerprint(new string('a', 64), "prices." + new string('a', 64) + ".csv", DateTime.UnixEpoch, 1, DateTime.UnixEpoch, 1),
        started ?? DateTime.UnixEpoch,
        completed ?? DateTime.UnixEpoch.AddMinutes(1),
        generated ?? DateTime.UnixEpoch.AddMinutes(2));

    private sealed class FixedProvider(PriceCacheSnapshot snapshot) : IPriceSnapshotProvider
    {
        private readonly string? adoptionWarning;
        public FixedProvider(PriceCacheSnapshot snapshot, string? adoptionWarning = null) : this(snapshot) => this.adoptionWarning = adoptionWarning;
        public int Calls { get; private set; }
        public Task<PriceSnapshotResult> GetSnapshotAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult(new PriceSnapshotResult(snapshot, adoptionWarning)); }
    }

    private sealed class FixedNames(IReadOnlyDictionary<long, string> names) : IItemNameLookup
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyDictionary<long, string>> GetNamesAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult(names); }
    }

    private sealed class FailingNames : IItemNameLookup
    {
        public Task<IReadOnlyDictionary<long, string>> GetNamesAsync(CancellationToken cancellationToken) => throw new ItemCacheException("private-item-path");
    }

    private sealed class ThrowingProvider : IPriceSnapshotProvider
    {
        public Task<PriceSnapshotResult> GetSnapshotAsync(CancellationToken cancellationToken) => throw new PriceCacheException("private-path");
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public FixedTimeProvider() : this(DateTimeOffset.Parse("2026-08-15T12:03:00Z").UtcDateTime) { }
        public override DateTimeOffset GetUtcNow() => now;
    }
}
