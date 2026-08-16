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
    private const int MaximumWarnings = 16;
    private const string SourceStatement = "Public request-time Guild Wars 2 item observation; not a source publication or currentness guarantee.";
    private const string MaterialCategoriesUnavailableWarning = "Public material categories are unavailable; item facts remain available.";

    [McpServerTool(Name = "get_items", Title = "Get items", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns bounded public Guild Wars 2 item facts observed at request time, with optional public material-category membership. It does not use credentials, account data, a local catalog cache, or Trading Post prices.")]
    public async Task<GetItemsResult> GetItemsAsync(
        [Description("Required list of 1 through 100 distinct positive canonical Guild Wars 2 item IDs.")] IReadOnlyList<long>? itemIds,
        [Description("Optional public material-category membership annotation. Omitted or null means false.")] bool? includeMaterialCategories = null,
        CancellationToken cancellationToken = default)
    {
        if (itemIds is null || itemIds.Count is < 1 or > MaximumIds || itemIds.Any(id => id <= 0) || itemIds.Distinct().Count() != itemIds.Count)
        {
            throw new McpException("Item IDs must contain 1 through 100 distinct positive canonical IDs.");
        }

        Gw2PublicItems publicItems;
        try
        {
            publicItems = await gw2ApiClient.GetPublicItemsAsync(itemIds, cancellationToken).ConfigureAwait(false);
        }
        catch (Gw2ConfigurationException exception) { throw new McpException(exception.Message, exception); }
        catch (HttpRequestException) { throw new McpException("Guild Wars 2 public items are unavailable. Try again later."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new McpException("Guild Wars 2 public items are unavailable. Try again later."); }

        var itemFactsAsOf = timeProvider.GetUtcNow();
        if (includeMaterialCategories != true)
        {
            return BuildResult(itemIds, publicItems, "NotRequested", null, itemFactsAsOf, null, publicItems.Warnings.Take(MaximumWarnings).ToArray());
        }

        try
        {
            var materialCategories = await gw2ApiClient.GetPublicMaterialCategoriesAsync(cancellationToken).ConfigureAwait(false);
            var materialCategoriesAsOf = timeProvider.GetUtcNow();
            var categoriesByItemId = new Dictionary<long, List<MaterialCategoryResult>>();
            var requestedIds = itemIds.ToHashSet();
            foreach (var category in materialCategories.Categories.OrderBy(category => category.Order).ThenBy(category => category.Id))
            {
                var categoryResult = new MaterialCategoryResult(category.Id, category.Name, category.Order);
                foreach (var itemId in category.ItemIds.Where(requestedIds.Contains))
                {
                    if (!categoriesByItemId.TryGetValue(itemId, out var categories))
                    {
                        categories = [];
                        categoriesByItemId.Add(itemId, categories);
                    }

                    categories.Add(categoryResult);
                }
            }

            return BuildResult(itemIds, publicItems, "Available", materialCategoriesAsOf, itemFactsAsOf, categoriesByItemId, publicItems.Warnings.Take(MaximumWarnings).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is Gw2ConfigurationException or HttpRequestException or IOException or OperationCanceledException)
        {
            var warnings = publicItems.Warnings.Take(MaximumWarnings - 1).Append(MaterialCategoriesUnavailableWarning).ToArray();
            return BuildResult(itemIds, publicItems, "Unavailable", null, itemFactsAsOf, null, warnings);
        }
    }

    private static GetItemsResult BuildResult(
        IReadOnlyList<long> itemIds,
        Gw2PublicItems publicItems,
        string materialCategoriesStatus,
        DateTimeOffset? materialCategoriesAsOf,
        DateTimeOffset itemFactsAsOf,
        IReadOnlyDictionary<long, List<MaterialCategoryResult>>? categoriesByItemId,
        IReadOnlyList<string> warnings)
    {
        var itemsById = publicItems.Items.ToDictionary(item => item.Id);
        var missingItemIds = itemIds.Where(id => !itemsById.ContainsKey(id)).ToArray();
        return new GetItemsResult(
            itemIds.Select(id => itemsById.TryGetValue(id, out var item)
                ? new PublicItemResult("Found", item.Id, item.Name, item.Type, item.Rarity, item.Level, item.VendorValue, item.Flags, item.GameTypes, item.Restrictions, Categories(id))
                : new PublicItemResult("NoPublicItemResource", id, null, null, null, null, null, null, null, null, Categories(id))).ToArray(),
            missingItemIds.Length == 0,
            missingItemIds,
            materialCategoriesStatus,
            materialCategoriesAsOf,
            false,
            warnings,
            itemFactsAsOf,
            SourceStatement);

        IReadOnlyList<MaterialCategoryResult>? Categories(long itemId) => materialCategoriesStatus == "Available"
            ? categoriesByItemId?.GetValueOrDefault(itemId) ?? []
            : null;
    }
}

public sealed record GetItemsResult(
    [property: JsonRequired] IReadOnlyList<PublicItemResult> Items,
    [property: JsonRequired] bool IsComplete,
    [property: JsonRequired] IReadOnlyList<long> MissingItemIds,
    [property: JsonRequired] string MaterialCategoriesStatus,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? MaterialCategoriesAsOf,
    [property: JsonRequired] bool IsAtomicSnapshot,
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
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<string>? Restrictions,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<MaterialCategoryResult>? MaterialCategories);

public sealed record MaterialCategoryResult(
    [property: JsonRequired] long Id,
    [property: JsonRequired] string Name,
    [property: JsonRequired] long Order);
