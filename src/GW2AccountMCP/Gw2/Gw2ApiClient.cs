using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GW2AccountMCP.Gw2;

public interface IGw2ApiClient
{
    Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken);
    Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken);
    Task<Gw2AccountStorage> GetAccountStorageAsync(CancellationToken cancellationToken);
    Task<Gw2CharacterBags> GetCharacterBagsAsync(CancellationToken cancellationToken);
}

public sealed class Gw2ApiClient(HttpClient httpClient, Gw2ApiOptions options, TimeProvider? timeProvider = null) : IGw2ApiClient
{
    public const string SchemaVersion = "2025-08-29T01:00:00.000Z";
    private const string Language = "en";
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
    private static Gw2ConfigurationException InvalidTokenPermissionResponse() => new("GW2 returned an invalid token-permission response. Try again later.");

    private sealed record TokenInfo(List<string?>? Permissions);
    private sealed record AccountResponse(string? Name, int? World, DateTimeOffset? Created, List<string>? Access);
    private sealed record WalletBalanceResponse(int? Id, long? Value);
    private sealed record CurrencyResponse(int? Id, string? Name);
    private sealed record InventoryStackResponse(int? Id, long? Count);
    private sealed record MaterialStackResponse(int? Id, int? Category, long? Count);
    private sealed record CharacterInventoryResponse(List<CharacterBagResponse?>? Bags);
    private sealed record CharacterBagResponse(int? Id, int? Size, List<InventoryStackResponse?>? Inventory);
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
public enum Gw2StorageSource
{
    Bank,
    MaterialStorage,
    SharedInventory
}
