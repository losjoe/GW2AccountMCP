using System.ComponentModel;
using System.Text.Json.Serialization;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetAccountHoldingsTool(IGw2ApiClient gw2ApiClient, TimeProvider timeProvider)
{
    private const int MaximumRequestedIds = 20;

    [McpServerTool(
        Name = "get_account_holdings",
        Title = "Get account holdings",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Gets bounded Guild Wars 2 item and currency holdings by canonical IDs, with location and completeness evidence.")]
    public async Task<AccountHoldingsResult> GetAccountHoldingsAsync(
        [Description("Optional canonical item IDs.")] long[]? itemIds = null,
        [Description("Optional canonical currency IDs.")] int[]? currencyIds = null,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(itemIds, currencyIds);
        itemIds ??= [];
        currencyIds ??= [];

        Gw2AccountStorage? storage = null;
        Gw2CharacterBags? characterBags = null;
        Gw2TradingPostDelivery? delivery = null;
        Gw2CurrentSells? currentSells = null;
        Gw2Items? itemMetadata = null;
        Gw2Wallet? wallet = null;
        var warnings = new List<HoldingsWarningResult>();
        var successfulRelevantSources = 0;

        if (itemIds.Length != 0)
        {
            storage = await TrySourceAsync(
                gw2ApiClient.GetAccountStorageAsync,
                "account_storage_unavailable",
                "Account storage is unavailable.",
                warnings,
                cancellationToken);
            successfulRelevantSources += storage is null ? 0 : 1;

            characterBags = await TrySourceAsync(
                gw2ApiClient.GetCharacterBagsAsync,
                "character_bags_unavailable",
                "Character bags are unavailable.",
                warnings,
                cancellationToken);
            successfulRelevantSources += characterBags is null ? 0 : 1;

            delivery = await TrySourceAsync(
                gw2ApiClient.GetTradingPostDeliveryAsync,
                "trading_post_delivery_unavailable",
                "Trading Post delivery is unavailable.",
                warnings,
                cancellationToken);
            successfulRelevantSources += delivery is null ? 0 : 1;

            currentSells = await TrySourceAsync(
                gw2ApiClient.GetCurrentSellsAsync,
                "trading_post_sells_unavailable",
                "Current Trading Post sells are unavailable.",
                warnings,
                cancellationToken);
            successfulRelevantSources += currentSells is null ? 0 : 1;

            try
            {
                itemMetadata = await gw2ApiClient.GetItemsAsync(itemIds, cancellationToken);
                foreach (var missingItemId in itemMetadata.MissingItemIds)
                {
                    warnings.Add(ItemMetadataWarning(missingItemId));
                }
            }
            catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
            {
                foreach (var itemId in itemIds)
                {
                    warnings.Add(ItemMetadataWarning(itemId));
                }
            }
        }

        if (currencyIds.Length != 0)
        {
            wallet = await TrySourceAsync(
                gw2ApiClient.GetWalletAsync,
                "wallet_unavailable",
                "Wallet balances are unavailable.",
                warnings,
                cancellationToken);
            successfulRelevantSources += wallet is null ? 0 : 1;
            if (wallet is not null)
            {
                foreach (var warning in wallet.Warnings.Where(warning => currencyIds.Contains(warning.CurrencyId)))
                {
                    warnings.Add(new HoldingsWarningResult(
                        warning.Code,
                        "English currency metadata is unavailable for the requested currency ID.",
                        null,
                        warning.CurrencyId));
                }
            }
        }

        if (successfulRelevantSources == 0)
        {
            throw new McpException("Guild Wars 2 holdings are unavailable. Try again later.");
        }

        try
        {
            var itemResults = BuildItemResults(itemIds, storage, characterBags, delivery, currentSells, itemMetadata);
            var currencyResults = BuildCurrencyResults(currencyIds, wallet, warnings);
            var queriedLocations = BuildQueriedLocations(itemIds.Length != 0, currencyIds.Length != 0, storage, characterBags, delivery, currentSells, wallet);
            var unavailableLocations = BuildUnavailableLocations(itemIds.Length != 0, currencyIds.Length != 0, storage, characterBags, delivery, currentSells, wallet);
            var isComplete = unavailableLocations.Count == 0 && currencyResults.All(currency => currency.OnHand is not null);
            return new AccountHoldingsResult(
                itemResults,
                currencyResults,
                isComplete,
                queriedLocations,
                unavailableLocations,
                warnings,
                timeProvider.GetUtcNow());
        }
        catch (OverflowException exception)
        {
            throw new McpException("A Guild Wars 2 holdings quantity is too large to total safely.", exception);
        }
    }

    private static void ValidateArguments(long[]? itemIds, int[]? currencyIds)
    {
        var itemCount = itemIds?.Length ?? 0;
        var currencyCount = currencyIds?.Length ?? 0;
        if (itemCount + currencyCount == 0)
        {
            throw new McpException("At least one item ID or currency ID is required.");
        }

        if (itemCount + currencyCount > MaximumRequestedIds)
        {
            throw new McpException("At most 20 combined item IDs and currency IDs may be requested.");
        }

        if (itemIds is not null && (itemIds.Any(id => id <= 0) || itemIds.Distinct().Count() != itemIds.Length))
        {
            throw new McpException("Item IDs must be unique positive values.");
        }

        if (currencyIds is not null && (currencyIds.Any(id => id <= 0) || currencyIds.Distinct().Count() != currencyIds.Length))
        {
            throw new McpException("Currency IDs must be unique positive values.");
        }
    }

    private static async Task<T?> TrySourceAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string warningCode,
        string warningMessage,
        List<HoldingsWarningResult> warnings,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await operation(cancellationToken);
        }
        catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
        {
            warnings.Add(new HoldingsWarningResult(warningCode, warningMessage, null, null));
            return null;
        }
    }

    private static bool IsUnavailableFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is Gw2ConfigurationException or HttpRequestException
        || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private static IReadOnlyList<ItemHoldingResult> BuildItemResults(
        IReadOnlyList<long> itemIds,
        Gw2AccountStorage? storage,
        Gw2CharacterBags? characterBags,
        Gw2TradingPostDelivery? delivery,
        Gw2CurrentSells? currentSells,
        Gw2Items? itemMetadata)
    {
        var namesById = itemMetadata?.Items.ToDictionary(item => item.Id, item => item.Name) ?? [];
        return itemIds.Select(itemId =>
        {
            var locations = new List<HoldingLocationResult>();
            long physicalKnownTotal = 0;
            if (storage is not null)
            {
                AddStorageLocation(itemId, Gw2StorageSource.Bank, "Bank", storage, locations, ref physicalKnownTotal);
                AddStorageLocation(itemId, Gw2StorageSource.MaterialStorage, "MaterialStorage", storage, locations, ref physicalKnownTotal);
                AddStorageLocation(itemId, Gw2StorageSource.SharedInventory, "SharedInventory", storage, locations, ref physicalKnownTotal);
            }

            if (characterBags is not null)
            {
                foreach (var characterGroup in characterBags.Stacks.Where(stack => stack.Id == itemId).GroupBy(stack => stack.Character))
                {
                    var count = CheckedSum(characterGroup.Select(stack => stack.Count));
                    physicalKnownTotal = checked(physicalKnownTotal + count);
                    if (count != 0)
                    {
                        locations.Add(new HoldingLocationResult("CharacterBag", count, characterGroup.Key));
                    }
                }
            }

            long? onHand = storage is not null && characterBags is not null ? physicalKnownTotal : null;
            long? deliveryTotal = delivery is null
                ? null
                : CheckedSum(delivery.Items.Where(item => item.Id == itemId).Select(item => item.Count));
            if (deliveryTotal is > 0)
            {
                locations.Add(new HoldingLocationResult("TradingPostDelivery", deliveryTotal.Value, null));
            }

            long? listedTotal = currentSells is null
                ? null
                : CheckedSum(currentSells.Orders.Where(order => order.ItemId == itemId).Select(order => order.Quantity));
            if (listedTotal is > 0)
            {
                locations.Add(new HoldingLocationResult("TradingPostSell", listedTotal.Value, null));
            }

            long? ownedTotal = onHand is not null && deliveryTotal is not null && listedTotal is not null
                ? checked(onHand.Value + deliveryTotal.Value + listedTotal.Value)
                : null;
            return new ItemHoldingResult(
                itemId,
                namesById.GetValueOrDefault(itemId),
                onHand,
                deliveryTotal,
                listedTotal,
                ownedTotal,
                locations);
        }).ToArray();
    }

    private static void AddStorageLocation(
        long itemId,
        Gw2StorageSource source,
        string kind,
        Gw2AccountStorage storage,
        List<HoldingLocationResult> locations,
        ref long physicalKnownTotal)
    {
        var count = CheckedSum(storage.Stacks.Where(stack => stack.Id == itemId && stack.Source == source).Select(stack => stack.Count));
        physicalKnownTotal = checked(physicalKnownTotal + count);
        if (count != 0)
        {
            locations.Add(new HoldingLocationResult(kind, count, null));
        }
    }

    private static IReadOnlyList<CurrencyHoldingResult> BuildCurrencyResults(
        IReadOnlyList<int> currencyIds,
        Gw2Wallet? wallet,
        List<HoldingsWarningResult> warnings)
    {
        return currencyIds.Select(currencyId =>
        {
            if (wallet is null)
            {
                return new CurrencyHoldingResult(currencyId, null, null, null, []);
            }

            var matchingBalances = wallet.Balances.Where(balance => balance.Id == currencyId).ToArray();
            if (matchingBalances.Length != 1)
            {
                warnings.Add(new HoldingsWarningResult(
                    matchingBalances.Length == 0 ? "currency_balance_missing" : "currency_balance_invalid",
                    matchingBalances.Length == 0
                        ? "The requested currency balance was not returned by the wallet."
                        : "The requested currency balance was not authoritative.",
                    null,
                    currencyId));
                return new CurrencyHoldingResult(currencyId, null, null, null, []);
            }

            var balance = matchingBalances[0];
            IReadOnlyList<HoldingLocationResult> locations = balance.Value == 0
                ? []
                : [new HoldingLocationResult("Wallet", balance.Value, null)];
            return new CurrencyHoldingResult(currencyId, balance.Name, balance.Value, balance.Value, locations);
        }).ToArray();
    }

    private static IReadOnlyList<string> BuildQueriedLocations(
        bool requestedItems,
        bool requestedCurrencies,
        Gw2AccountStorage? storage,
        Gw2CharacterBags? characterBags,
        Gw2TradingPostDelivery? delivery,
        Gw2CurrentSells? currentSells,
        Gw2Wallet? wallet)
    {
        var locations = new List<string>();
        if (requestedCurrencies && wallet is not null) locations.Add("Wallet");
        if (requestedItems && storage is not null) locations.AddRange(["Bank", "MaterialStorage", "SharedInventory"]);
        if (requestedItems && characterBags is not null) locations.Add("CharacterBag");
        if (requestedItems && delivery is not null) locations.Add("TradingPostDelivery");
        if (requestedItems && currentSells is not null) locations.Add("TradingPostSell");
        return locations;
    }

    private static IReadOnlyList<string> BuildUnavailableLocations(
        bool requestedItems,
        bool requestedCurrencies,
        Gw2AccountStorage? storage,
        Gw2CharacterBags? characterBags,
        Gw2TradingPostDelivery? delivery,
        Gw2CurrentSells? currentSells,
        Gw2Wallet? wallet)
    {
        var locations = new List<string>();
        if (requestedCurrencies && wallet is null) locations.Add("Wallet");
        if (requestedItems && storage is null) locations.AddRange(["Bank", "MaterialStorage", "SharedInventory"]);
        if (requestedItems && characterBags is null) locations.Add("CharacterBag");
        if (requestedItems && delivery is null) locations.Add("TradingPostDelivery");
        if (requestedItems && currentSells is null) locations.Add("TradingPostSell");
        return locations;
    }

    private static long CheckedSum(IEnumerable<long> values)
    {
        long total = 0;
        foreach (var value in values)
        {
            total = checked(total + value);
        }

        return total;
    }

    private static HoldingsWarningResult ItemMetadataWarning(long itemId) =>
        new("item_metadata_missing", "English item metadata is unavailable for the requested item ID.", itemId, null);
}

public sealed record AccountHoldingsResult(
    IReadOnlyList<ItemHoldingResult> Items,
    IReadOnlyList<CurrencyHoldingResult> Currencies,
    bool IsComplete,
    IReadOnlyList<string> QueriedLocations,
    IReadOnlyList<string> UnavailableLocations,
    IReadOnlyList<HoldingsWarningResult> Warnings,
    DateTimeOffset AsOf);

public sealed record ItemHoldingResult(
    long Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? OnHand,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? InTradingPostDelivery,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? ListedForSale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? OwnedTotal,
    IReadOnlyList<HoldingLocationResult> Locations);

public sealed record CurrencyHoldingResult(
    int Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? OnHand,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? OwnedTotal,
    IReadOnlyList<HoldingLocationResult> Locations);

public sealed record HoldingLocationResult(
    string Kind,
    long Count,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Character);

public sealed record HoldingsWarningResult(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? ItemId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? CurrencyId);
