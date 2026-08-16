using System.ComponentModel;
using System.Text.Json.Serialization;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetItemsTool(IGw2ApiClient gw2ApiClient, TimeProvider timeProvider)
{
    private const int MaximumIds = 100;
    private const string SourceStatement = "Public request-time Guild Wars 2 item observation; not a source publication or currentness guarantee.";

    [McpServerTool(Name = "get_items", Title = "Get items", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns bounded public Guild Wars 2 item facts observed at request time. It does not use credentials, account data, a local catalog cache, or Trading Post prices.")]
    public async Task<GetItemsResult> GetItemsAsync(
        [Description("Required list of 1 through 100 distinct positive canonical Guild Wars 2 item IDs.")] IReadOnlyList<long>? itemIds,
        CancellationToken cancellationToken = default)
    {
        if (itemIds is null || itemIds.Count is < 1 or > MaximumIds || itemIds.Any(id => id <= 0) || itemIds.Distinct().Count() != itemIds.Count)
        {
            throw new McpException("Item IDs must contain 1 through 100 distinct positive canonical IDs.");
        }

        try
        {
            var publicItems = await gw2ApiClient.GetPublicItemsAsync(itemIds, cancellationToken).ConfigureAwait(false);
            var itemsById = publicItems.Items.ToDictionary(item => item.Id);
            var missingItemIds = itemIds.Where(id => !itemsById.ContainsKey(id)).ToArray();
            return new GetItemsResult(
                itemIds.Select(id => itemsById.TryGetValue(id, out var item)
                    ? new PublicItemResult("Found", item.Id, item.Name, item.Type, item.Rarity, item.Level, item.VendorValue, item.Flags, item.GameTypes, item.Restrictions)
                    : new PublicItemResult("NoPublicItemResource", id, null, null, null, null, null, null, null, null)).ToArray(),
                missingItemIds.Length == 0,
                missingItemIds,
                publicItems.Warnings.Take(16).ToArray(),
                timeProvider.GetUtcNow(),
                SourceStatement);
        }
        catch (Gw2ConfigurationException exception) { throw new McpException(exception.Message, exception); }
        catch (HttpRequestException) { throw new McpException("Guild Wars 2 public items are unavailable. Try again later."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new McpException("Guild Wars 2 public items are unavailable. Try again later."); }
    }
}

public sealed record GetItemsResult(
    [property: JsonRequired] IReadOnlyList<PublicItemResult> Items,
    [property: JsonRequired] bool IsComplete,
    [property: JsonRequired] IReadOnlyList<long> MissingItemIds,
    [property: JsonRequired] IReadOnlyList<string> Warnings,
    [property: JsonRequired] DateTimeOffset AsOf,
    [property: JsonRequired] string SourceStatement);

public sealed record PublicItemResult(
    [property: JsonRequired] string Status,
    [property: JsonRequired] long Id,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Type,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Rarity,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Level,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? VendorValue,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<string>? Flags,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<string>? GameTypes,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<string>? Restrictions);
