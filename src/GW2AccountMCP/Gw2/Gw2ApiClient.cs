using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GW2AccountMCP.Gw2;

public interface IGw2ApiClient
{
    Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken);
    Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken);
    Task<Gw2AccountStorage> GetAccountStorageAsync(CancellationToken cancellationToken);
    Task<Gw2CharacterBags> GetCharacterBagsAsync(CancellationToken cancellationToken);
    Task<Gw2TradingPostDelivery> GetTradingPostDeliveryAsync(CancellationToken cancellationToken);
    Task<Gw2CurrentSells> GetCurrentSellsAsync(CancellationToken cancellationToken);
    Task<Gw2Items> GetItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken);
}

public sealed class Gw2ApiClient(HttpClient httpClient, Gw2ApiOptions options, TimeProvider? timeProvider = null) : IGw2ApiClient
{
    public const string SchemaVersion = "2025-08-29T01:00:00.000Z";
    private const string Language = "en";
    private const int CurrentSellsPageSize = 200;
    private const int MaximumItemBatchSize = 200;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        }

        await ValidatePermissionsAsync(["account"], cancellationToken);

        using var response = await SendWithSingleRetryAsync("/v2/account", cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw InvalidKey();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new Gw2ConfigurationException($"GW2 account request failed with HTTP {(int)response.StatusCode}. Try again later.");
        }

        var account = await DeserializeAccountAsync(response, cancellationToken);
        return new Gw2Account(account.Name!, account.World!.Value, account.Created!.Value, account.Access!);
    }

    public async Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        }

        await ValidatePermissionsAsync(["account", "wallet"], cancellationToken);

        using var response = await SendWithSingleRetryAsync("/v2/account/wallet", cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw InvalidKey();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new Gw2ConfigurationException($"GW2 wallet request failed with HTTP {(int)response.StatusCode}. Try again later.");
        }

        var wallet = await DeserializeWalletAsync(response, cancellationToken);
        if (wallet.Count == 0)
        {
            return new Gw2Wallet([], []);
        }

        var currencyIds = wallet.Select(balance => balance.Id!.Value).Distinct().Order().ToArray();
        if (currencyIds.Length > 200)
        {
            throw new Gw2ConfigurationException("GW2 returned too many wallet currency definitions. Try again later.");
        }

        using var currenciesResponse = await SendWithSingleRetryAsync($"/v2/currencies?ids={Uri.EscapeDataString(string.Join(',', currencyIds))}", cancellationToken, authenticated: false);
        if (!currenciesResponse.IsSuccessStatusCode)
        {
            throw new Gw2ConfigurationException($"GW2 currency request failed with HTTP {(int)currenciesResponse.StatusCode}. Try again later.");
        }

        var currencies = await DeserializeCurrenciesAsync(currenciesResponse, cancellationToken);
        var namesById = currencies.ToDictionary(currency => currency.Id!.Value, currency => currency.Name!);
        var missingCurrencyIds = currencyIds.Where(id => !namesById.ContainsKey(id)).ToArray();
        return new Gw2Wallet(
            wallet.Select(balance => new Gw2WalletBalance(balance.Id!.Value, namesById.GetValueOrDefault(balance.Id.Value), balance.Value!.Value)).ToArray(),
            missingCurrencyIds.Select(id => new Gw2WalletWarning("currency_metadata_missing", id)).ToArray());
    }

    public async Task<Gw2AccountStorage> GetAccountStorageAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        }

        await ValidatePermissionsAsync(["account", "inventories"], cancellationToken);

        var stacks = new List<Gw2StorageStack>();

        using (var bankResponse = await SendWithSingleRetryAsync("/v2/account/bank", cancellationToken))
        {
            if (bankResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw InvalidKey();
            }

            if (!bankResponse.IsSuccessStatusCode)
            {
                throw new Gw2ConfigurationException($"GW2 bank request failed with HTTP {(int)bankResponse.StatusCode}. Try again later.");
            }

            var bank = await DeserializeInventorySlotsAsync(bankResponse, InvalidBankResponse, cancellationToken);
            for (var slotIndex = 0; slotIndex < bank.Count; slotIndex++)
            {
                if (bank[slotIndex] is { } stack)
                {
                    stacks.Add(new Gw2StorageStack(stack.Id!.Value, stack.Count!.Value, Gw2StorageSource.Bank, slotIndex));
                }
            }
        }

        using (var materialsResponse = await SendWithSingleRetryAsync("/v2/account/materials", cancellationToken))
        {
            if (materialsResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw InvalidKey();
            }

            if (!materialsResponse.IsSuccessStatusCode)
            {
                throw new Gw2ConfigurationException($"GW2 material-storage request failed with HTTP {(int)materialsResponse.StatusCode}. Try again later.");
            }

            var materials = await DeserializeMaterialsAsync(materialsResponse, cancellationToken);
            stacks.AddRange(materials.Select(stack => new Gw2StorageStack(stack.Id!.Value, stack.Count!.Value, Gw2StorageSource.MaterialStorage, null)));
        }

        using (var inventoryResponse = await SendWithSingleRetryAsync("/v2/account/inventory", cancellationToken))
        {
            if (inventoryResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw InvalidKey();
            }

            if (!inventoryResponse.IsSuccessStatusCode)
            {
                throw new Gw2ConfigurationException($"GW2 shared-inventory request failed with HTTP {(int)inventoryResponse.StatusCode}. Try again later.");
            }

            var inventory = await DeserializeInventorySlotsAsync(inventoryResponse, InvalidSharedInventoryResponse, cancellationToken);
            for (var slotIndex = 0; slotIndex < inventory.Count; slotIndex++)
            {
                if (inventory[slotIndex] is { } stack)
                {
                    stacks.Add(new Gw2StorageStack(stack.Id!.Value, stack.Count!.Value, Gw2StorageSource.SharedInventory, slotIndex));
                }
            }
        }

        return new Gw2AccountStorage(stacks);
    }

    public async Task<Gw2CharacterBags> GetCharacterBagsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        }

        await ValidatePermissionsAsync(["account", "characters", "inventories"], cancellationToken);

        using var charactersResponse = await SendWithSingleRetryAsync("/v2/characters", cancellationToken);
        if (charactersResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw InvalidKey();
        }

        if (charactersResponse.StatusCode != HttpStatusCode.OK)
        {
            throw new Gw2ConfigurationException($"GW2 character-list request failed with HTTP {(int)charactersResponse.StatusCode}. Try again later.");
        }

        var characterNames = await DeserializeCharacterNamesAsync(charactersResponse, cancellationToken);
        var stacks = new List<Gw2CharacterBagStack>();
        foreach (var characterName in characterNames)
        {
            var encodedCharacterName = Uri.EscapeDataString(characterName);
            using var inventoryResponse = await SendWithSingleRetryAsync($"/v2/characters/{encodedCharacterName}/inventory", cancellationToken);
            if (inventoryResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw InvalidKey();
            }

            if (inventoryResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new Gw2ConfigurationException($"GW2 character-inventory request failed with HTTP {(int)inventoryResponse.StatusCode}. Try again later.");
            }

            var inventory = await DeserializeCharacterInventoryAsync(inventoryResponse, cancellationToken);
            for (var bagIndex = 0; bagIndex < inventory.Bags!.Count; bagIndex++)
            {
                if (inventory.Bags[bagIndex] is not { } bag)
                {
                    continue;
                }

                for (var slotIndex = 0; slotIndex < bag.Inventory!.Count; slotIndex++)
                {
                    if (bag.Inventory[slotIndex] is { } stack)
                    {
                        stacks.Add(new Gw2CharacterBagStack(stack.Id!.Value, stack.Count!.Value, characterName, bagIndex, slotIndex));
                    }
                }
            }
        }

        return new Gw2CharacterBags(stacks);
    }

    public async Task<Gw2TradingPostDelivery> GetTradingPostDeliveryAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        }

        await ValidatePermissionsAsync(["account", "tradingpost"], cancellationToken);

        using var response = await SendWithSingleRetryAsync("/v2/commerce/delivery", cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw InvalidKey();
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new Gw2ConfigurationException($"GW2 delivery request failed with HTTP {(int)response.StatusCode}. Try again later.");
        }

        var delivery = await DeserializeTradingPostDeliveryAsync(response, cancellationToken);
        return new Gw2TradingPostDelivery(
            delivery.Coins!.Value,
            delivery.Items!.Select(item => new Gw2TradingPostDeliveryItem(item!.Id!.Value, item.Count!.Value)).ToArray());
    }

    public async Task<Gw2CurrentSells> GetCurrentSellsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        }

        await ValidatePermissionsAsync(["account", "tradingpost"], cancellationToken);

        var orders = new List<Gw2CurrentSellOrder>();
        CurrentSellsPagination? expectedPagination = null;
        for (var page = 0; ; page++)
        {
            using var response = await SendWithSingleRetryAsync(
                $"/v2/commerce/transactions/current/sells?page={page}&page_size={CurrentSellsPageSize}",
                cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw InvalidKey();
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Gw2ConfigurationException($"GW2 current-sells request failed with HTTP {(int)response.StatusCode}. Try again later.");
            }

            var pagination = DeserializeCurrentSellsPagination(response.Headers, page);
            if (expectedPagination is not null
                && (pagination.PageSize != expectedPagination.PageSize
                    || pagination.PageTotal != expectedPagination.PageTotal
                    || pagination.ResultTotal != expectedPagination.ResultTotal))
            {
                throw InvalidCurrentSellsPagination();
            }

            expectedPagination ??= pagination;
            var pageOrders = await DeserializeCurrentSellsAsync(response, cancellationToken);
            if (pageOrders.Count != pagination.ResultCount)
            {
                throw InvalidCurrentSellsPagination();
            }

            orders.AddRange(pageOrders.Select(order => new Gw2CurrentSellOrder(
                order.Id!.Value,
                order.ItemId!.Value,
                order.Price!.Value,
                order.Quantity!.Value,
                order.Created!.Value)));

            if (pagination.PageTotal == 0 || page == pagination.PageTotal - 1)
            {
                return new Gw2CurrentSells(orders);
            }
        }
    }

    public async Task<Gw2Items> GetItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count is 0 or > MaximumItemBatchSize
            || itemIds.Any(id => id <= 0)
            || itemIds.Distinct().Count() != itemIds.Count)
        {
            throw new ArgumentException("Item IDs must contain 1 to 200 unique positive values.", nameof(itemIds));
        }

        using var response = await SendWithSingleRetryAsync(
            $"/v2/items?ids={Uri.EscapeDataString(string.Join(',', itemIds))}",
            cancellationToken,
            authenticated: false);
        if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent)
        {
            throw new Gw2ConfigurationException($"GW2 item metadata request failed with HTTP {(int)response.StatusCode}. Try again later.");
        }

        var requestedIds = itemIds.ToHashSet();
        var itemRows = await DeserializeItemsAsync(response, requestedIds, cancellationToken);
        var itemsById = itemRows.ToDictionary(item => item.Id!.Value);
        var missingItemIds = itemIds.Where(id => !itemsById.ContainsKey(id)).ToArray();
        if ((response.StatusCode == HttpStatusCode.OK && missingItemIds.Length != 0)
            || (response.StatusCode == HttpStatusCode.PartialContent && (itemRows.Count == 0 || missingItemIds.Length == 0)))
        {
            throw InvalidItemMetadataResponse();
        }

        return new Gw2Items(
            itemIds.Where(itemsById.ContainsKey).Select(id => new Gw2Item(id, itemsById[id].Name!)).ToArray(),
            missingItemIds);
    }

    private async Task ValidatePermissionsAsync(IReadOnlyList<string> requiredPermissions, CancellationToken cancellationToken)
    {
        using var response = await SendWithSingleRetryAsync("/v2/tokeninfo", cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw InvalidKey();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new Gw2ConfigurationException($"GW2 key validation failed with HTTP {(int)response.StatusCode}. Try again later.");
        }

        var tokenInfo = await DeserializeTokenInfoAsync(response, cancellationToken);
        foreach (var requiredPermission in requiredPermissions)
        {
            if (!tokenInfo.Permissions!.Contains(requiredPermission, StringComparer.OrdinalIgnoreCase))
            {
                throw new Gw2ConfigurationException($"GW2_API_KEY is missing the required {requiredPermission} permission. Create a key with the {requiredPermission} permission.");
            }
        }
    }

    private async Task<AccountResponse> DeserializeAccountAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var account = await JsonSerializer.DeserializeAsync<AccountResponse>(stream, JsonOptions, cancellationToken);
            if (string.IsNullOrWhiteSpace(account?.Name) || account.World is not > 0 || account.Created is null || account.Created.Value == default || account.Access is null)
            {
                throw InvalidAccountResponse();
            }

            return account;
        }
        catch (JsonException)
        {
            throw InvalidAccountResponse();
        }
    }

    private async Task<TokenInfo> DeserializeTokenInfoAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var tokenInfo = await JsonSerializer.DeserializeAsync<TokenInfo>(stream, JsonOptions, cancellationToken);
            return tokenInfo?.Permissions is { } permissions && permissions.All(permission => !string.IsNullOrWhiteSpace(permission))
                ? tokenInfo
                : throw InvalidTokenPermissionResponse();
        }
        catch (JsonException)
        {
            throw InvalidTokenPermissionResponse();
        }
    }

    private async Task<List<WalletBalanceResponse>> DeserializeWalletAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var wallet = await JsonSerializer.DeserializeAsync<List<WalletBalanceResponse>>(stream, JsonOptions, cancellationToken);
            if (wallet is null || wallet.Any(balance => balance.Id is not > 0 || balance.Value is null or < 0))
            {
                throw InvalidWalletResponse();
            }

            return wallet;
        }
        catch (JsonException)
        {
            throw InvalidWalletResponse();
        }
    }

    private async Task<List<CurrencyResponse>> DeserializeCurrenciesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var currencies = await JsonSerializer.DeserializeAsync<List<CurrencyResponse>>(stream, JsonOptions, cancellationToken);
            if (currencies is null || currencies.Any(currency => currency.Id is not > 0 || string.IsNullOrWhiteSpace(currency.Name)) || currencies.Select(currency => currency.Id!.Value).Distinct().Count() != currencies.Count)
            {
                throw InvalidCurrencyResponse();
            }

            return currencies;
        }
        catch (JsonException)
        {
            throw InvalidCurrencyResponse();
        }
    }

    private async Task<List<InventoryStackResponse?>> DeserializeInventorySlotsAsync(
        HttpResponseMessage response,
        Func<Gw2ConfigurationException> invalidResponse,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var stacks = await JsonSerializer.DeserializeAsync<List<InventoryStackResponse?>>(stream, JsonOptions, cancellationToken);
            if (stacks is null || stacks.Any(stack => stack is not null && (stack.Id is not > 0 || stack.Count is not > 0)))
            {
                throw invalidResponse();
            }

            return stacks;
        }
        catch (JsonException)
        {
            throw invalidResponse();
        }
    }

    private async Task<List<MaterialStackResponse>> DeserializeMaterialsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var materials = await JsonSerializer.DeserializeAsync<List<MaterialStackResponse?>>(stream, JsonOptions, cancellationToken);
            if (materials is null
                || materials.Any(stack => stack is null || stack.Id is not > 0 || stack.Category is not > 0 || stack.Count is null or < 0)
                || materials.Select(stack => stack!.Id!.Value).Distinct().Count() != materials.Count)
            {
                throw InvalidMaterialStorageResponse();
            }

            return materials.Select(stack => stack!).ToList();
        }
        catch (JsonException)
        {
            throw InvalidMaterialStorageResponse();
        }
    }

    private async Task<List<string>> DeserializeCharacterNamesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var characterNames = await JsonSerializer.DeserializeAsync<List<string?>>(stream, JsonOptions, cancellationToken);
            if (characterNames is null
                || characterNames.Any(string.IsNullOrWhiteSpace)
                || characterNames.Distinct(StringComparer.Ordinal).Count() != characterNames.Count)
            {
                throw InvalidCharacterListResponse();
            }

            return characterNames.Select(name => name!).ToList();
        }
        catch (JsonException)
        {
            throw InvalidCharacterListResponse();
        }
    }

    private async Task<CharacterInventoryResponse> DeserializeCharacterInventoryAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var characterInventory = await JsonSerializer.DeserializeAsync<CharacterInventoryResponse>(stream, JsonOptions, cancellationToken);
            if (characterInventory?.Bags is null
                || characterInventory.Bags.Any(bag => bag is not null
                    && (bag.Id is not > 0
                        || bag.Size is not > 0
                        || bag.Inventory is null
                        || bag.Inventory.Count != bag.Size
                        || bag.Inventory.Any(stack => stack is not null && (stack.Id is not > 0 || stack.Count is not > 0)))))
            {
                throw InvalidCharacterInventoryResponse();
            }

            return characterInventory;
        }
        catch (JsonException)
        {
            throw InvalidCharacterInventoryResponse();
        }
    }

    private async Task<TradingPostDeliveryResponse> DeserializeTradingPostDeliveryAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var delivery = await JsonSerializer.DeserializeAsync<TradingPostDeliveryResponse>(stream, JsonOptions, cancellationToken);
            if (delivery?.Coins is null or < 0
                || delivery.Items is null
                || delivery.Items.Any(item => item is null || item.Id is not > 0 || item.Count is not > 0))
            {
                throw InvalidTradingPostDeliveryResponse();
            }

            return delivery;
        }
        catch (JsonException)
        {
            throw InvalidTradingPostDeliveryResponse();
        }
    }

    private static CurrentSellsPagination DeserializeCurrentSellsPagination(HttpResponseHeaders headers, int requestedPage)
    {
        var pageSize = ParsePaginationHeader(headers, "X-Page-Size");
        var pageTotal = ParsePaginationHeader(headers, "X-Page-Total");
        var resultCount = ParsePaginationHeader(headers, "X-Result-Count");
        var resultTotal = ParsePaginationHeader(headers, "X-Result-Total");
        if (pageSize != CurrentSellsPageSize
            || pageTotal > int.MaxValue
            || resultCount > pageSize
            || (resultTotal == 0 && (requestedPage != 0 || pageTotal != 0 || resultCount != 0))
            || (resultTotal > 0
                && (pageTotal == 0
                    || requestedPage >= pageTotal
                    || pageTotal != ((resultTotal - 1) / pageSize) + 1
                    || resultCount != (requestedPage == pageTotal - 1
                        ? resultTotal - ((pageTotal - 1) * pageSize)
                        : pageSize))))
        {
            throw InvalidCurrentSellsPagination();
        }

        return new CurrentSellsPagination(pageSize, (int)pageTotal, resultCount, resultTotal);
    }

    private static long ParsePaginationHeader(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values))
        {
            throw InvalidCurrentSellsPagination();
        }

        var valueArray = values.ToArray();
        if (valueArray.Length != 1
            || !long.TryParse(valueArray[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < 0)
        {
            throw InvalidCurrentSellsPagination();
        }

        return value;
    }

    private async Task<List<CurrentSellResponse>> DeserializeCurrentSellsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var orders = await JsonSerializer.DeserializeAsync<List<CurrentSellResponse?>>(stream, JsonOptions, cancellationToken);
            if (orders is null
                || orders.Any(order => order is null
                    || order.Id is not > 0
                    || order.ItemId is not > 0
                    || order.Price is not > 0
                    || order.Quantity is not > 0
                    || order.Created is null
                    || order.Created.Value == default))
            {
                throw InvalidCurrentSellsResponse();
            }

            return orders.Select(order => order!).ToList();
        }
        catch (JsonException)
        {
            throw InvalidCurrentSellsResponse();
        }
    }

    private async Task<List<ItemResponse>> DeserializeItemsAsync(
        HttpResponseMessage response,
        IReadOnlySet<long> requestedIds,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var items = await JsonSerializer.DeserializeAsync<List<ItemResponse?>>(stream, JsonOptions, cancellationToken);
            if (items is null
                || items.Any(item => item is null
                    || item.Id is not > 0
                    || !requestedIds.Contains(item.Id.Value)
                    || string.IsNullOrWhiteSpace(item.Name))
                || items.Select(item => item!.Id!.Value).Distinct().Count() != items.Count)
            {
                throw InvalidItemMetadataResponse();
            }

            return items.Select(item => item!).ToList();
        }
        catch (JsonException)
        {
            throw InvalidItemMetadataResponse();
        }
    }

    private static async Task<bool> IsInvalidKeyResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return body.Contains("invalid key", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendWithSingleRetryAsync(string path, CancellationToken cancellationToken, bool authenticated = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await SendAsync(path, cancellationToken, authenticated);
            if (attempt > 0)
            {
                return response;
            }

            TimeSpan retryDelay;
            try
            {
                var isTransient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                var isInvalidKey = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    && await IsInvalidKeyResponseAsync(response, cancellationToken);
                if (!isTransient && !isInvalidKey)
                {
                    return response;
                }

                retryDelay = isTransient ? GetRetryDelay(response) : TimeSpan.Zero;
            }
            catch
            {
                response.Dispose();
                throw;
            }

            response.Dispose();
            await Task.Delay(retryDelay, timeProvider ?? TimeProvider.System, cancellationToken);
        }
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - (timeProvider ?? TimeProvider.System).GetUtcNow();
            if (delay >= TimeSpan.Zero)
            {
                return delay;
            }
        }

        return DefaultRetryDelay;
    }

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken cancellationToken, bool authenticated)
    {
        var separator = path.Contains('?') ? '&' : '?';
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{path}{separator}lang={Language}&v={Uri.EscapeDataString(SchemaVersion)}");
        if (authenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static Gw2ConfigurationException InvalidKey() => new("GW2_API_KEY was rejected by Guild Wars 2. Check that the configured key is valid and active.");
    private static Gw2ConfigurationException InvalidAccountResponse() => new("GW2 returned an invalid account response. Try again later.");
    private static Gw2ConfigurationException InvalidWalletResponse() => new("GW2 returned an invalid wallet response. Try again later.");
    private static Gw2ConfigurationException InvalidCurrencyResponse() => new("GW2 returned an invalid currency response. Try again later.");
    private static Gw2ConfigurationException InvalidBankResponse() => new("GW2 returned an invalid bank response. Try again later.");
    private static Gw2ConfigurationException InvalidMaterialStorageResponse() => new("GW2 returned an invalid material-storage response. Try again later.");
    private static Gw2ConfigurationException InvalidSharedInventoryResponse() => new("GW2 returned an invalid shared-inventory response. Try again later.");
    private static Gw2ConfigurationException InvalidCharacterListResponse() => new("GW2 returned an invalid character-list response. Try again later.");
    private static Gw2ConfigurationException InvalidCharacterInventoryResponse() => new("GW2 returned an invalid character-inventory response. Try again later.");
    private static Gw2ConfigurationException InvalidTradingPostDeliveryResponse() => new("GW2 returned an invalid delivery response. Try again later.");
    private static Gw2ConfigurationException InvalidCurrentSellsResponse() => new("GW2 returned an invalid current-sells response. Try again later.");
    private static Gw2ConfigurationException InvalidCurrentSellsPagination() => new("GW2 returned invalid current-sells pagination metadata. Try again later.");
    private static Gw2ConfigurationException InvalidItemMetadataResponse() => new("GW2 returned an invalid item metadata response. Try again later.");
    private static Gw2ConfigurationException InvalidTokenPermissionResponse() => new("GW2 returned an invalid token-permission response. Try again later.");

    private sealed record TokenInfo(List<string?>? Permissions);
    private sealed record AccountResponse(string? Name, int? World, DateTimeOffset? Created, List<string>? Access);
    private sealed record WalletBalanceResponse(int? Id, long? Value);
    private sealed record CurrencyResponse(int? Id, string? Name);
    private sealed record InventoryStackResponse(int? Id, long? Count);
    private sealed record MaterialStackResponse(int? Id, int? Category, long? Count);
    private sealed record CharacterInventoryResponse(List<CharacterBagResponse?>? Bags);
    private sealed record CharacterBagResponse(int? Id, int? Size, List<InventoryStackResponse?>? Inventory);
    private sealed record TradingPostDeliveryResponse(long? Coins, List<TradingPostDeliveryItemResponse?>? Items);
    private sealed record TradingPostDeliveryItemResponse(long? Id, long? Count);
    private sealed record CurrentSellResponse(
        long? Id,
        [property: JsonPropertyName("item_id")] long? ItemId,
        long? Price,
        long? Quantity,
        DateTimeOffset? Created);
    private sealed record CurrentSellsPagination(long PageSize, int PageTotal, long ResultCount, long ResultTotal);
    private sealed record ItemResponse(long? Id, string? Name);
}

public sealed record Gw2ApiOptions(string ApiKey, string BaseUrl);

public sealed class Gw2ConfigurationException(string message) : Exception(message);

public sealed record Gw2Account(string Name, int World, DateTimeOffset Created, List<string> Access);
public sealed record Gw2Wallet(IReadOnlyList<Gw2WalletBalance> Balances, IReadOnlyList<Gw2WalletWarning> Warnings);
public sealed record Gw2WalletBalance(int Id, string? Name, long Value);
public sealed record Gw2WalletWarning(string Code, int CurrencyId);
public sealed record Gw2AccountStorage(IReadOnlyList<Gw2StorageStack> Stacks);
public sealed record Gw2StorageStack(int Id, long Count, Gw2StorageSource Source, int? SlotIndex);
public sealed record Gw2CharacterBags(IReadOnlyList<Gw2CharacterBagStack> Stacks);
public sealed record Gw2CharacterBagStack(int Id, long Count, string Character, int BagIndex, int SlotIndex);
public sealed record Gw2TradingPostDelivery(long Coins, IReadOnlyList<Gw2TradingPostDeliveryItem> Items);
public sealed record Gw2TradingPostDeliveryItem(long Id, long Count);
public sealed record Gw2CurrentSells(IReadOnlyList<Gw2CurrentSellOrder> Orders);
public sealed record Gw2CurrentSellOrder(long Id, long ItemId, long Price, long Quantity, DateTimeOffset Created);
public sealed record Gw2Items(IReadOnlyList<Gw2Item> Items, IReadOnlyList<long> MissingItemIds);
public sealed record Gw2Item(long Id, string Name);
public enum Gw2StorageSource
{
    Bank,
    MaterialStorage,
    SharedInventory
}
