using System.ComponentModel;
using System.Text.Json.Serialization;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetTradingPostActivityTool(IGw2ApiClient gw2ApiClient, TimeProvider timeProvider)
{
    private const int DefaultPage = 0;
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 200;

    [McpServerTool(
        Name = "get_trading_post_activity",
        Title = "Get Trading Post activity",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Gets one complete, bounded page of current pending Trading Post buy or sell orders.")]
    public async Task<TradingPostActivityResult> GetTradingPostActivityAsync(
        [Description("Required activity mode: CurrentBuys or CurrentSells.")] string mode,
        [Description("Optional zero-based page number.")] int? page = null,
        [Description("Optional page size from 1 through 200.")] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        if (mode is not "CurrentBuys" and not "CurrentSells") throw new McpException("mode must be CurrentBuys or CurrentSells.");
        var requestedPage = page ?? DefaultPage;
        var requestedPageSize = pageSize ?? DefaultPageSize;
        if (requestedPage < 0) throw new McpException("page must be a nonnegative Int32.");
        if (requestedPageSize is < 1 or > MaximumPageSize) throw new McpException("pageSize must be from 1 through 200.");

        try
        {
            if (mode == "CurrentBuys")
            {
                var currentBuys = await gw2ApiClient.GetCurrentBuysPageAsync(requestedPage, requestedPageSize, cancellationToken);
                var subtotal = 0L;
                var orders = new List<CurrentBuyOrderResult>();
                foreach (var order in currentBuys.Orders)
                {
                    var reservedCoin = checked(order.Price * order.Quantity);
                    subtotal = checked(subtotal + reservedCoin);
                    orders.Add(new CurrentBuyOrderResult(order.ItemId, order.Price, order.Quantity, order.Created, reservedCoin));
                }

                return new TradingPostActivityResult(
                    "CurrentBuys",
                    new CurrentBuysActivityResult(orders, subtotal),
                    null,
                    currentBuys.Page,
                    currentBuys.PageSize,
                    currentBuys.PageCount,
                    currentBuys.TotalCount,
                    currentBuys.Orders.Count,
                    checked(currentBuys.Page + 1) < currentBuys.PageCount,
                    timeProvider.GetUtcNow(),
                    false,
                    "Current buy quantities are pending orders, not owned items or account holdings. Source order is preserved for this page, but official documentation does not guarantee chronological or global order.",
                    "Guild Wars 2 documents transaction data as cached for about five minutes, so it may differ from real-time; asOf is observation completion, not source publication freshness.",
                    "This selected page is complete per one accepted response; reserved coin and subtotal cover this selected page only, not the account total, and cross-call pagination stability is not guaranteed.");
            }

            var currentSells = await gw2ApiClient.GetCurrentSellsPageAsync(requestedPage, requestedPageSize, cancellationToken);
            return new TradingPostActivityResult(
                "CurrentSells",
                null,
                new CurrentSellsActivityResult(currentSells.Orders.Select(order => new CurrentSellOrderResult(order.ItemId, order.Price, order.Quantity, order.Created)).ToArray()),
                currentSells.Page,
                currentSells.PageSize,
                currentSells.PageCount,
                currentSells.TotalCount,
                currentSells.Orders.Count,
                checked(currentSells.Page + 1) < currentSells.PageCount,
                timeProvider.GetUtcNow(),
                false,
                "Current sell quantity is the API-reported currently unfulfilled listed quantity. These listed assets are already represented by holdings listedForSale, not on-hand, and must not be added as a second ownership source. Source order is preserved for this selected page, but official documentation does not guarantee chronological or global order.",
                "Guild Wars 2 documents transaction data as cached for about five minutes, so it may differ from real-time; asOf is parse completion, not source publication freshness.",
                "This selected page is complete per one accepted response. It has no account-wide, cross-call stable pagination, valuation, execution, profit, or recommendation implication.");
        }
        catch (OverflowException)
        {
            throw new McpException("Trading Post reserved coin is too large to total safely.");
        }
        catch (Gw2ConfigurationException)
        {
            throw new McpException("Trading Post activity is unavailable. Try again later.");
        }
        catch (HttpRequestException)
        {
            throw new McpException("Trading Post activity is unavailable. Try again later.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException("Trading Post activity is unavailable. Try again later.");
        }
    }
}

public sealed record TradingPostActivityResult(
    [property: JsonRequired] string Mode,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] CurrentBuysActivityResult? CurrentBuys,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] CurrentSellsActivityResult? CurrentSells,
    [property: JsonRequired] int Page,
    [property: JsonRequired] int PageSize,
    [property: JsonRequired] int PageCount,
    [property: JsonRequired] long TotalCount,
    [property: JsonRequired] int ReturnedCount,
    [property: JsonRequired] bool HasMore,
    [property: JsonRequired] DateTimeOffset AsOf,
    [property: JsonRequired] bool IsAtomicSnapshot,
    [property: JsonRequired] string SourceStatement,
    [property: JsonRequired] string FreshnessStatement,
    [property: JsonRequired] string CompletenessStatement);

public sealed record CurrentBuysActivityResult(
    [property: JsonRequired] IReadOnlyList<CurrentBuyOrderResult> Orders,
    [property: JsonRequired] long ReservedCoinPageSubtotalCopper);

public sealed record CurrentBuyOrderResult(
    [property: JsonRequired] long ItemId,
    [property: JsonRequired] long UnitPriceCopper,
    [property: JsonRequired] long Quantity,
    [property: JsonRequired] DateTimeOffset CreatedAt,
    [property: JsonRequired] long ReservedCoinCopper);

public sealed record CurrentSellsActivityResult(
    [property: JsonRequired] IReadOnlyList<CurrentSellOrderResult> Orders);

public sealed record CurrentSellOrderResult(
    [property: JsonRequired] long ItemId,
    [property: JsonRequired] long UnitPriceCopper,
    [property: JsonRequired] long Quantity,
    [property: JsonRequired] DateTimeOffset CreatedAt);
