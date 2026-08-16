using System.ComponentModel;
using System.Text.Json.Serialization;
using GW2AccountMCP.Items;
using GW2AccountMCP.Prices;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetItemPricesTool(IPriceSnapshotProvider priceSnapshots, IItemNameLookup itemNames, TimeProvider timeProvider)
{
    private const int MaximumIds = 100;
    private const string FreshnessStatement = "This is a manually refreshed cached price snapshot. Prices may have changed; run tp --fresh to collect a newer source snapshot.";

    [McpServerTool(
        Name = "get_item_prices",
        Title = "Get item prices",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns bounded factual Trading Post price summaries from a locally published manual cache; it does not query Guild Wars 2 at request time and does not imply order-book depth, execution, or a recommendation.")]
    public async Task<GetItemPricesResult> GetItemPricesAsync(
        [Description("Required list of 1 through 100 distinct positive canonical Guild Wars 2 item IDs.")] IReadOnlyList<long>? itemIds,
        CancellationToken cancellationToken = default)
    {
        if (itemIds is null || itemIds.Count is < 1 or > MaximumIds || itemIds.Any(id => id <= 0) || itemIds.Distinct().Count() != itemIds.Count)
        {
            throw new McpException("Item IDs must contain 1 through 100 distinct positive canonical IDs.");
        }

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

        var items = itemIds.Select(id => ToResult(id, FindPrice(loaded.Snapshot.Prices, id), names?.GetValueOrDefault(id))).ToArray();
        if (names is not null && items.Count(item => item.Name is null) is var missingNames and > 0)
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
        return new GetItemPricesResult(
            items,
            loaded.Snapshot.SourceStartedAtUtc,
            loaded.Snapshot.SourceCompletedAtUtc,
            loaded.Snapshot.CacheGeneratedAtUtc,
            loaded.Snapshot.SourceCompletedAtUtc,
            loaded.Snapshot.SourceCompletedAtUtc - loaded.Snapshot.SourceStartedAtUtc,
            cacheAge,
            "ManualCachedSnapshot",
            FreshnessStatement,
            true,
            warnings.Take(16).ToArray());
    }

    private static ItemPriceResult ToResult(long id, CachedPrice? price, string? name)
    {
        if (price is null)
        {
            return new ItemPriceResult(id, name, "NoPriceResourceInGeneration", null, null, null, null, null, null, null, false);
        }

        return new ItemPriceResult(
            id, name, "Quoted", price.Whitelisted,
            price.BuyQuantity, price.BuyUnitPrice == 0 ? null : price.BuyUnitPrice,
            price.SellQuantity, price.SellUnitPrice == 0 ? null : price.SellUnitPrice,
            price.BuyQuantity > 0, price.SellQuantity > 0, true);
    }

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
}

public sealed record GetItemPricesResult(
    IReadOnlyList<ItemPriceResult> Items,
    DateTime SourceStartedAtUtc,
    DateTime SourceCompletedAtUtc,
    DateTime CacheGeneratedAtUtc,
    DateTime AsOf,
    TimeSpan CollectionDuration,
    TimeSpan CacheAge,
    string FreshnessStatus,
    string FreshnessStatement,
    bool IsCompletePriceGeneration,
    IReadOnlyList<string> Warnings);

public sealed record ItemPriceResult(
    long Id,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? FreeAccountTradable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? BuyQuantity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? HighestBuyUnitPrice,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? SellQuantity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? LowestSellUnitPrice,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? BuyOrdersAvailable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? SellOrdersAvailable,
    bool IsPriceResourceInGeneration);
