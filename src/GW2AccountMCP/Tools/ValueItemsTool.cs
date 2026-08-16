using System.ComponentModel;
using System.Text.Json.Serialization;
using GW2AccountMCP.Items;
using GW2AccountMCP.Prices;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class ValueItemsTool(IPriceSnapshotProvider priceSnapshots, IItemNameLookup itemNames, TimeProvider timeProvider)
{
    private const int MaximumItems = 100;
    private const string FreshnessStatement = "This is a manually refreshed cached price snapshot. Prices may have changed; run tp --fresh to collect a newer source snapshot.";
    private const string ExtrapolationStatement = "Best-price unit quotes are extrapolated across the requested quantities; price summaries do not show volume at the best level and are not execution guarantees.";
    private const string ScopeStatement = "This tool reports factual quote arithmetic only. It does not calculate recipe costs and makes no buy, sell, craft, keep, profit, flip, trend, forecast, velocity, history, or other recommendation.";

    [McpServerTool(
        Name = "value_items",
        Title = "Value items",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns bounded factual Trading Post quote arithmetic from one locally published manual price snapshot. It does not query Guild Wars 2 at request time, expose depth or execution volume, or recommend an action.")]
    public async Task<ValueItemsResult> ValueItemsAsync(
        [Description("Required list of 1 through 100 distinct positive canonical Guild Wars 2 item IDs with positive quantities no larger than Int32.MaxValue.")] IReadOnlyList<ValueItemRequest>? items,
        CancellationToken cancellationToken = default)
    {
        Validate(items);

        PriceSnapshotResult loaded;
        try { loaded = await priceSnapshots.GetSnapshotAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is PriceCacheException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new McpException("Guild Wars 2 price cache is unavailable. Run the local tp refresh command and try again.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<long, string>? names = null;
        var warnings = new List<string> { FreshnessStatement };
        if (loaded.AdoptionWarning is not null) warnings.Add(loaded.AdoptionWarning);
        try { names = await itemNames.GetNamesAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            warnings.Add("English item-name metadata is unavailable; price facts remain from the independent price cache.");
        }

        try
        {
            var rows = new List<ValueItemResult>(items!.Count);
            var immediateTotals = new SaleTotals();
            var acquisitionTotal = 0L;
            var listingTotals = new SaleTotals();
            var immediateMissing = new List<long>();
            var acquisitionMissing = new List<long>();
            var listingMissing = new List<long>();

            foreach (var request in items)
            {
                var price = FindPrice(loaded.Snapshot.Prices, request.ItemId);
                var row = ToResult(request, price, names?.GetValueOrDefault(request.ItemId));
                rows.Add(row);
                if (row.ImmediateSale.IsAvailable) immediateTotals.Add(row.ImmediateSale);
                else immediateMissing.Add(request.ItemId);
                if (row.Acquisition.IsAvailable) acquisitionTotal = checked(acquisitionTotal + row.Acquisition.BuyerTotalCost!.Value);
                else acquisitionMissing.Add(request.ItemId);
                if (row.HypotheticalListing.IsAvailable) listingTotals.Add(row.HypotheticalListing);
                else listingMissing.Add(request.ItemId);
            }

            if (names is not null && rows.Count(row => row.Name is null) is var missingNames and > 0)
            {
                warnings.Add($"English item-name metadata is unavailable for {missingNames} requested item IDs; price facts remain from the independent price cache.");
            }
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var cacheAge = now - loaded.Snapshot.SourceCompletedAtUtc;
            if (cacheAge < TimeSpan.Zero)
            {
                cacheAge = TimeSpan.Zero;
                warnings.Add("The system clock precedes the price snapshot timestamp; cache age was clamped and cannot be established accurately.");
            }

            return new ValueItemsResult(
                rows,
                immediateTotals.ToAggregate(immediateMissing),
                ToAcquisitionAggregate(acquisitionTotal, acquisitionMissing),
                listingTotals.ToAggregate(listingMissing),
                new FeePolicyResult(5, 10, "Each fee is calculated independently on each row aggregate gross with quotient/remainder round-half-up; each positive-gross fee is at least 1 copper."),
                loaded.Snapshot.SourceStartedAtUtc,
                loaded.Snapshot.SourceCompletedAtUtc,
                loaded.Snapshot.CacheGeneratedAtUtc,
                loaded.Snapshot.SourceCompletedAtUtc,
                loaded.Snapshot.SourceCompletedAtUtc - loaded.Snapshot.SourceStartedAtUtc,
                cacheAge,
                "ManualCachedSnapshot",
                FreshnessStatement,
                ExtrapolationStatement,
                ScopeStatement,
                true,
                warnings.Take(16).ToArray());
        }
        catch (OverflowException)
        {
            throw new McpException("Requested quote arithmetic exceeds the supported Int64 copper range. Reduce quantities or split the request.");
        }
    }

    private static void Validate(IReadOnlyList<ValueItemRequest>? items)
    {
        if (items is null || items.Count is < 1 or > MaximumItems
            || items.Any(item => item is null || item.ItemId <= 0 || item.Quantity is < 1 or > int.MaxValue)
            || items.Select(item => item.ItemId).Distinct().Count() != items.Count)
        {
            throw new McpException("Items must contain 1 through 100 distinct positive canonical IDs with quantities from 1 through Int32.MaxValue.");
        }
    }

    private static ValueItemResult ToResult(ValueItemRequest request, CachedPrice? price, string? name)
    {
        if (price is null)
        {
            return new ValueItemResult(
                request.ItemId, name, request.Quantity, "NoPriceResourceInGeneration",
                UnavailableSale("NoPriceResourceInGeneration"),
                UnavailableAcquisition("NoPriceResourceInGeneration"),
                UnavailableSale("NoPriceResourceInGeneration"));
        }

        var immediate = price.BuyUnitPrice > 0
            ? Sale(request.Quantity, price.BuyUnitPrice)
            : UnavailableSale("NoCurrentBuyOrders");
        var acquisition = price.SellUnitPrice > 0
            ? new AcquisitionViewResult(true, "Available", price.SellUnitPrice, checked(request.Quantity * price.SellUnitPrice))
            : UnavailableAcquisition("NoCurrentSellOrders");
        var listing = price.SellUnitPrice > 0
            ? Sale(request.Quantity, price.SellUnitPrice)
            : UnavailableSale("NoCurrentSellOrders");
        return new ValueItemResult(request.ItemId, name, request.Quantity, "Quoted", immediate, acquisition, listing);
    }

    private static SaleViewResult Sale(long quantity, long unitQuote)
    {
        var gross = checked(quantity * unitQuote);
        var listingFee = ListingFee(gross);
        var exchangeFee = ExchangeFee(gross);
        return new SaleViewResult(true, "Available", unitQuote, gross, listingFee, exchangeFee, checked(gross - listingFee - exchangeFee));
    }

    private static long ListingFee(long gross) => RoundedFee(gross, 20, 10);
    private static long ExchangeFee(long gross) => RoundedFee(gross, 10, 5);
    private static long RoundedFee(long gross, long divisor, long roundingRemainder)
    {
        var quotient = gross / divisor;
        var result = gross % divisor >= roundingRemainder ? checked(quotient + 1) : quotient;
        return Math.Max(result, 1);
    }

    private static SaleViewResult UnavailableSale(string availability) => new(false, availability, null, null, null, null, null);
    private static AcquisitionViewResult UnavailableAcquisition(string availability) => new(false, availability, null, null);
    private static AcquisitionAggregateResult ToAcquisitionAggregate(long total, IReadOnlyList<long> missing) =>
        missing.Count == 0 ? new AcquisitionAggregateResult(true, [], total) : new AcquisitionAggregateResult(false, missing, null);

    private static CachedPrice? FindPrice(IReadOnlyList<CachedPrice> prices, long id)
    {
        var low = 0;
        var high = prices.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var candidate = prices[middle];
            if (candidate.Id == id) return candidate;
            if (candidate.Id < id) low = middle + 1;
            else high = middle - 1;
        }
        return null;
    }

    private sealed class SaleTotals
    {
        private long gross;
        private long listingFee;
        private long exchangeFee;
        private long net;

        public void Add(SaleViewResult row)
        {
            gross = checked(gross + row.Gross!.Value);
            listingFee = checked(listingFee + row.ListingFee!.Value);
            exchangeFee = checked(exchangeFee + row.ExchangeFee!.Value);
            net = checked(net + row.Net!.Value);
        }

        public SaleAggregateResult ToAggregate(IReadOnlyList<long> missing) => missing.Count == 0
            ? new SaleAggregateResult(true, [], gross, listingFee, exchangeFee, net)
            : new SaleAggregateResult(false, missing, null, null, null, null);
    }
}

public sealed record ValueItemRequest(long ItemId, long Quantity);

public sealed record ValueItemsResult(
    IReadOnlyList<ValueItemResult> Items,
    SaleAggregateResult ImmediateSale,
    AcquisitionAggregateResult Acquisition,
    SaleAggregateResult HypotheticalListing,
    FeePolicyResult FeePolicy,
    DateTime SourceStartedAtUtc,
    DateTime SourceCompletedAtUtc,
    DateTime CacheGeneratedAtUtc,
    DateTime AsOf,
    TimeSpan CollectionDuration,
    TimeSpan CacheAge,
    string FreshnessStatus,
    string FreshnessStatement,
    string BestPriceExtrapolationStatement,
    string ScopeStatement,
    bool IsCompletePriceGeneration,
    IReadOnlyList<string> Warnings);

public sealed record ValueItemResult(
    long ItemId,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    long Quantity,
    string PriceResourceStatus,
    SaleViewResult ImmediateSale,
    AcquisitionViewResult Acquisition,
    SaleViewResult HypotheticalListing);

public sealed record SaleViewResult(
    bool IsAvailable,
    string Availability,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? UnitQuote,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Gross,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? ListingFee,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? ExchangeFee,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Net);

public sealed record AcquisitionViewResult(
    bool IsAvailable,
    string Availability,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? UnitQuote,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? BuyerTotalCost);

public sealed record SaleAggregateResult(
    bool IsComplete,
    IReadOnlyList<long> MissingItemIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Gross,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? ListingFee,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? ExchangeFee,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Net);

public sealed record AcquisitionAggregateResult(
    bool IsComplete,
    IReadOnlyList<long> MissingItemIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? BuyerTotalCost);

public sealed record FeePolicyResult(int ListingFeePercent, int ExchangeFeePercent, string RoundingAndMinimumStatement);
