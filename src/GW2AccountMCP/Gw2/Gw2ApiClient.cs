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
    Task<Gw2Characters> GetCharactersAsync(CancellationToken cancellationToken);
    Task<Gw2CharacterBuild> GetCharacterBuildAsync(string characterName, CancellationToken cancellationToken);
    Task<Gw2CharacterEquipment> GetCharacterEquipmentAsync(string characterName, CancellationToken cancellationToken);
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
    private const int MaximumEquipmentRows = 32;
    private const int MaximumEquipmentReferences = 200;
    private const int MaximumEquipmentStatAttributes = 32;
    private const int MaximumFallbackEquipmentRows = 256;
    private static readonly string[] EquipmentSlotOrder = ["Helm", "Shoulders", "Coat", "Gloves", "Leggings", "Boots", "Backpack", "Accessory1", "Accessory2", "Amulet", "Ring1", "Ring2", "WeaponA1", "WeaponA2", "WeaponB1", "WeaponB2", "HelmAquatic", "WeaponAquaticA", "WeaponAquaticB", "Relic"];
    private static readonly HashSet<string> KnownSpecialEquipmentSlots = ["Sickle", "Axe", "Pick", "FishingRod", "FishingBait", "FishingLure", "PowerCore", "SensoryArray", "ServiceChip"];
    private static readonly HashSet<string> KnownInactiveEquipmentLocations = ["Armory", "LegendaryArmory"];
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

    public async Task<Gw2Characters> GetCharactersAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        }

        await ValidatePermissionsAsync(["account", "characters"], cancellationToken);

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
        var characters = new List<Gw2Character>();
        foreach (var characterName in characterNames)
        {
            var encodedCharacterName = Uri.EscapeDataString(characterName);
            using var coreResponse = await SendWithSingleRetryAsync($"/v2/characters/{encodedCharacterName}/core", cancellationToken);
            if (coreResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw InvalidKey();
            }

            if (coreResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new Gw2ConfigurationException($"GW2 character-core request failed with HTTP {(int)coreResponse.StatusCode}. Try again later.");
            }

            var core = await DeserializeCharacterCoreAsync(coreResponse, characterName, cancellationToken);
            characters.Add(new Gw2Character(
                core.Name!,
                core.Race!,
                core.Gender!,
                core.Profession!,
                core.Level!.Value,
                core.Age!.Value,
                core.Created!.Value,
                core.LastModified!.Value,
                core.Deaths!.Value));
        }

        return new Gw2Characters(characters.OrderBy(character => character.Name, StringComparer.Ordinal).ToArray());
    }

    public async Task<Gw2CharacterBuild> GetCharacterBuildAsync(string characterName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        }

        await ValidatePermissionsAsync(["account", "characters", "builds"], cancellationToken);
        using var charactersResponse = await SendWithSingleRetryAsync("/v2/characters", cancellationToken);
        if (charactersResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw InvalidKey();
        }

        if (charactersResponse.StatusCode != HttpStatusCode.OK)
        {
            throw new Gw2ConfigurationException($"GW2 character-list request failed with HTTP {(int)charactersResponse.StatusCode}. Try again later.");
        }

        var canonicalCharacterName = (await DeserializeCharacterNamesAsync(charactersResponse, cancellationToken))
            .SingleOrDefault(name => string.Equals(name, characterName, StringComparison.Ordinal));
        if (canonicalCharacterName is null)
        {
            throw new Gw2ConfigurationException("The requested character was not found in the authenticated roster.");
        }

        using var activeResponse = await SendWithSingleRetryAsync(
            $"/v2/characters/{Uri.EscapeDataString(canonicalCharacterName)}/buildtabs/active",
            cancellationToken);
        if (activeResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw InvalidKey();
        }

        if (activeResponse.StatusCode != HttpStatusCode.OK)
        {
            throw new Gw2ConfigurationException($"GW2 character-build request failed with HTTP {(int)activeResponse.StatusCode}. Try again later.");
        }

        var build = await DeserializeActiveCharacterBuildAsync(activeResponse, cancellationToken);
        var specializationIds = build.Specializations.Where(slot => slot.Id is not null).Select(slot => slot.Id!.Value).Distinct().Order().ToArray();
        var traitIds = build.Specializations.SelectMany(slot => slot.Traits).Where(id => id is not null).Select(id => id!.Value).Distinct().Order().ToArray();
        var petIds = build.Pets is null ? [] : build.Pets.Terrestrial.Concat(build.Pets.Aquatic).Where(id => id is not null).Select(id => id!.Value).Distinct().Order().ToArray();
        var legendIds = build.Legends is null ? [] : build.Legends.Terrestrial.Concat(build.Legends.Aquatic).Where(id => id is not null).Select(id => id!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var selectedSkillIds = build.TerrestrialSkills.AllIds().Concat(build.AquaticSkills.AllIds()).Distinct().Order().ToArray();

        var specializations = await ResolveNumericMetadataAsync("specializations", specializationIds, cancellationToken);
        var traits = await ResolveNumericMetadataAsync("traits", traitIds, cancellationToken);
        var pets = await ResolveNumericMetadataAsync("pets", petIds, cancellationToken);
        var legends = await ResolveLegendMetadataAsync(legendIds, cancellationToken);
        var swapSkillIds = legends.Rows.Values.Select(legend => legend.Swap).Distinct().Order().ToArray();
        var skills = await ResolveNumericMetadataAsync("skills", selectedSkillIds.Concat(swapSkillIds).Distinct().Order().ToArray(), cancellationToken);

        var warnings = BuildMetadataWarnings(specializationIds, specializations, traitIds, traits, petIds, pets, legendIds, legends, selectedSkillIds.Concat(swapSkillIds).Distinct(), skills);
        return new Gw2CharacterBuild(
            canonicalCharacterName,
            build.Tab,
            build.Name,
            build.Profession,
            build.Specializations.Select(slot => new Gw2BuildSpecialization(
                ToReference(slot.Id, specializations.Rows),
                slot.Traits.Select(id => ToReference(id, traits.Rows)).ToArray())).ToArray(),
            new Gw2BuildSkills(ToReference(build.TerrestrialSkills.Heal, skills.Rows), build.TerrestrialSkills.Utilities.Select(id => ToReference(id, skills.Rows)).ToArray(), ToReference(build.TerrestrialSkills.Elite, skills.Rows)),
            new Gw2BuildSkills(ToReference(build.AquaticSkills.Heal, skills.Rows), build.AquaticSkills.Utilities.Select(id => ToReference(id, skills.Rows)).ToArray(), ToReference(build.AquaticSkills.Elite, skills.Rows)),
            build.Pets is { } petsSlots
                ? new Gw2BuildPets(petsSlots.Terrestrial.Select(id => ToReference(id, pets.Rows)).ToArray(), petsSlots.Aquatic.Select(id => ToReference(id, pets.Rows)).ToArray())
                : null,
            build.Legends is { } legendSlots
                ? new Gw2BuildLegends(legendSlots.Terrestrial.Select(id => ToLegendReference(id, legends.Rows, skills.Rows)).ToArray(), legendSlots.Aquatic.Select(id => ToLegendReference(id, legends.Rows, skills.Rows)).ToArray())
                : null,
            warnings.Count == 0,
            warnings);
    }

    public async Task<Gw2CharacterEquipment> GetCharacterEquipmentAsync(string characterName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        }

        if (string.IsNullOrWhiteSpace(characterName)) throw new Gw2ConfigurationException("characterName is required and must not be blank.");
        await ValidatePermissionsAsync(["account", "characters", "builds", "inventories"], cancellationToken);
        using var charactersResponse = await SendWithSingleRetryAsync("/v2/characters", cancellationToken);
        EnsureAuthenticatedOk(charactersResponse, "character-list");
        var canonicalCharacterName = (await DeserializeCharacterNamesAsync(charactersResponse, cancellationToken))
            .SingleOrDefault(name => string.Equals(name, characterName, StringComparison.Ordinal));
        if (canonicalCharacterName is null) throw new Gw2ConfigurationException("The requested character was not found in the authenticated roster.");

        var encodedName = Uri.EscapeDataString(canonicalCharacterName);
        using var activeResponse = await SendWithSingleRetryAsync($"/v2/characters/{encodedName}/equipmenttabs/active", cancellationToken);
        EnsureAuthenticatedOk(activeResponse, "character-equipment");
        var payload = await DeserializeActiveEquipmentAsync(activeResponse, cancellationToken);
        var rows = payload.Rows.ToList();
        if (!rows.Any(row => row.Slot == "Relic"))
        {
            using var fallbackResponse = await SendWithSingleRetryAsync($"/v2/characters/{encodedName}/equipment", cancellationToken);
            EnsureAuthenticatedOk(fallbackResponse, "character-equipment");
            var relic = await DeserializeFallbackRelicAsync(fallbackResponse, cancellationToken);
            if (relic is not null) rows.Add(relic);
        }

        if (rows.Count > MaximumEquipmentRows) throw InvalidCharacterEquipmentResponse();
        var referenceOccurrences = rows.Sum(row => 1 + row.Upgrades.Count + row.Infusions.Count);
        if (referenceOccurrences > MaximumEquipmentReferences) throw InvalidCharacterEquipmentResponse();
        rows = rows.OrderBy(row => Array.IndexOf(EquipmentSlotOrder, row.Slot) is var index && index >= 0 ? index : int.MaxValue)
            .ThenBy(row => Array.IndexOf(EquipmentSlotOrder, row.Slot) >= 0 ? string.Empty : row.Slot, StringComparer.Ordinal).ToList();

        var itemIds = rows.Select(row => row.ItemId).Concat(rows.SelectMany(row => row.Upgrades)).Concat(rows.SelectMany(row => row.Infusions)).Distinct().Order().ToArray();
        var itemMetadata = await ResolveEquipmentItemsAsync(itemIds, rows.Select(row => row.ItemId).ToHashSet(), cancellationToken);
        var itemStatIds = rows.Where(row => row.SelectedStatId is not null).Select(row => row.SelectedStatId!.Value)
            .Concat(rows.Where(row => row.SelectedStatId is null).Select(row => itemMetadata.Rows.GetValueOrDefault(row.ItemId)?.DefaultStatId).Where(id => id is not null).Select(id => id!.Value))
            .Distinct().Order().ToArray();
        var itemStats = await ResolveEquipmentNamesAsync("itemstats", itemStatIds, cancellationToken);
        var skinIds = rows.Where(row => row.SkinId is not null).Select(row => row.SkinId!.Value).Distinct().Order().ToArray();
        var skins = await ResolveEquipmentNamesAsync("skins", skinIds, cancellationToken);
        var warnings = new List<Gw2MetadataWarning>();
        AddEquipmentWarnings(warnings, "items", itemIds, itemMetadata.Rows.Keys);
        AddEquipmentWarnings(warnings, "itemstats", itemStatIds, itemStats.Rows.Keys);
        AddEquipmentWarnings(warnings, "skins", skinIds, skins.Rows.Keys);
        return new Gw2CharacterEquipment(canonicalCharacterName, payload.Tab, payload.Name,
            rows.Select(row => ToEquipmentRow(row, itemMetadata.Rows, itemStats.Rows, skins.Rows)).ToArray(),
            warnings.Count == 0, warnings);
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

    private async Task<ActiveBuildPayload> DeserializeActiveCharacterBuildAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var outer = RequiredObject(document.RootElement);
            var tab = RequiredInt(outer, "tab");
            if (tab <= 0 || !RequiredBoolean(outer, "is_active")) throw InvalidCharacterBuildResponse();
            var build = RequiredObject(RequiredProperty(outer, "build"));
            var name = RequiredString(build, "name", allowEmpty: true);
            var profession = RequiredString(build, "profession", allowEmpty: false);
            var specializations = RequiredArray(build, "specializations");
            if (specializations.GetArrayLength() != 3) throw InvalidCharacterBuildResponse();
            var specializationSlots = specializations.EnumerateArray().Select(ParseSpecialization).ToArray();
            if (specializationSlots.Any(slot => slot.Id is null && slot.Traits.Any(id => id is not null))) throw InvalidCharacterBuildResponse();
            var terrestrialSkills = ParseSkills(RequiredObject(RequiredProperty(build, "skills")));
            var aquaticSkills = ParseSkills(RequiredObject(RequiredProperty(build, "aquatic_skills")));

            PetSlots? pets = null;
            LegendSlots? legends = null;
            if (profession == "Ranger")
            {
                if (HasProperty(build, "legends") || HasProperty(build, "aquatic_legends")) throw InvalidCharacterBuildResponse();
                var petObject = RequiredObject(RequiredProperty(build, "pets"));
                pets = new PetSlots(ParseNullablePositiveIntegers(RequiredArray(petObject, "terrestrial"), 2), ParseNullablePositiveIntegers(RequiredArray(petObject, "aquatic"), 2));
            }
            else if (profession == "Revenant")
            {
                if (HasProperty(build, "pets")) throw InvalidCharacterBuildResponse();
                legends = new LegendSlots(ParseNullableNonblankStrings(RequiredArray(build, "legends"), 2), ParseNullableNonblankStrings(RequiredArray(build, "aquatic_legends"), 2));
            }
            else if (HasProperty(build, "pets") || HasProperty(build, "legends") || HasProperty(build, "aquatic_legends"))
            {
                throw InvalidCharacterBuildResponse();
            }

            return new ActiveBuildPayload(tab, name, profession, specializationSlots, terrestrialSkills, aquaticSkills, pets, legends);
        }
        catch (JsonException)
        {
            throw InvalidCharacterBuildResponse();
        }
    }

    private async Task<NumericMetadataBatch> ResolveNumericMetadataAsync(string resolver, IReadOnlyList<int> requestedIds, CancellationToken cancellationToken)
    {
        if (requestedIds.Count == 0) return new(new Dictionary<int, string>());
        try
        {
            using var response = await SendWithSingleRetryAsync($"/v2/{resolver}?ids={Uri.EscapeDataString(string.Join(',', requestedIds))}", cancellationToken, authenticated: false);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(new Dictionary<int, string>());
            if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent) return new(new Dictionary<int, string>());
            var rows = await DeserializeNumericMetadataAsync(response, requestedIds.ToHashSet(), cancellationToken);
            var ids = rows.Keys.ToHashSet();
            if ((response.StatusCode == HttpStatusCode.OK && !ids.SetEquals(requestedIds))
                || (response.StatusCode == HttpStatusCode.PartialContent && (ids.Count == 0 || ids.Count == requestedIds.Count)))
            {
                return new(new Dictionary<int, string>());
            }

            return new(rows);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(new Dictionary<int, string>());
        }
    }

    private async Task<LegendMetadataBatch> ResolveLegendMetadataAsync(IReadOnlyList<string> requestedIds, CancellationToken cancellationToken)
    {
        if (requestedIds.Count == 0) return new(new Dictionary<string, LegendMetadata>(StringComparer.Ordinal));
        try
        {
            using var response = await SendWithSingleRetryAsync($"/v2/legends?ids={Uri.EscapeDataString(string.Join(',', requestedIds))}", cancellationToken, authenticated: false);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(new Dictionary<string, LegendMetadata>(StringComparer.Ordinal));
            if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent) return new(new Dictionary<string, LegendMetadata>(StringComparer.Ordinal));
            var rows = await DeserializeLegendMetadataAsync(response, requestedIds.ToHashSet(StringComparer.Ordinal), cancellationToken);
            var ids = rows.Keys.ToHashSet(StringComparer.Ordinal);
            if ((response.StatusCode == HttpStatusCode.OK && !ids.SetEquals(requestedIds))
                || (response.StatusCode == HttpStatusCode.PartialContent && (ids.Count == 0 || ids.Count == requestedIds.Count)))
            {
                return new(new Dictionary<string, LegendMetadata>(StringComparer.Ordinal));
            }

            return new(rows);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(new Dictionary<string, LegendMetadata>(StringComparer.Ordinal));
        }
    }

    private async Task<Dictionary<int, string>> DeserializeNumericMetadataAsync(HttpResponseMessage response, IReadOnlySet<int> requestedIds, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw InvalidCharacterBuildResponse();
        var rows = new Dictionary<int, string>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var objectRow = RequiredObject(row);
            var id = RequiredInt(objectRow, "id");
            var name = RequiredString(objectRow, "name", allowEmpty: false);
            if (id <= 0 || !requestedIds.Contains(id) || !rows.TryAdd(id, name)) throw InvalidCharacterBuildResponse();
        }

        return rows;
    }

    private async Task<Dictionary<string, LegendMetadata>> DeserializeLegendMetadataAsync(HttpResponseMessage response, IReadOnlySet<string> requestedIds, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw InvalidCharacterBuildResponse();
        var rows = new Dictionary<string, LegendMetadata>(StringComparer.Ordinal);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var objectRow = RequiredObject(row);
            var id = RequiredString(objectRow, "id", allowEmpty: false);
            var code = RequiredInt(objectRow, "code");
            var swap = RequiredInt(objectRow, "swap");
            if (swap <= 0 || !requestedIds.Contains(id) || !rows.TryAdd(id, new LegendMetadata(code, swap))) throw InvalidCharacterBuildResponse();
        }

        return rows;
    }

    private static IReadOnlyList<Gw2MetadataWarning> BuildMetadataWarnings(
        IEnumerable<int> specializationIds, NumericMetadataBatch specializations,
        IEnumerable<int> traitIds, NumericMetadataBatch traits,
        IEnumerable<int> petIds, NumericMetadataBatch pets,
        IEnumerable<string> legendIds, LegendMetadataBatch legends,
        IEnumerable<int> skillIds, NumericMetadataBatch skills)
    {
        var warnings = new List<Gw2MetadataWarning>();
        AddWarnings(warnings, "specializations", specializationIds.Select(id => id.ToString(CultureInfo.InvariantCulture)), specializations.Rows.Keys.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        AddWarnings(warnings, "traits", traitIds.Select(id => id.ToString(CultureInfo.InvariantCulture)), traits.Rows.Keys.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        AddWarnings(warnings, "pets", petIds.Select(id => id.ToString(CultureInfo.InvariantCulture)), pets.Rows.Keys.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        AddWarnings(warnings, "legends", legendIds, legends.Rows.Keys);
        AddWarnings(warnings, "skills", skillIds.Select(id => id.ToString(CultureInfo.InvariantCulture)), skills.Rows.Keys.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        return warnings;
    }

    private static void AddWarnings(List<Gw2MetadataWarning> warnings, string resolver, IEnumerable<string> requested, IEnumerable<string> resolved)
    {
        var resolvedSet = resolved.ToHashSet(StringComparer.Ordinal);
        foreach (var id in requested.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Where(id => !resolvedSet.Contains(id)))
        {
            warnings.Add(new Gw2MetadataWarning("metadata_unresolved", resolver, id));
        }
    }

    private static Gw2NumericReference? ToReference(int? id, IReadOnlyDictionary<int, string> names) => id is { } value ? new Gw2NumericReference(value, names.GetValueOrDefault(value)) : null;

    private static Gw2LegendReference? ToLegendReference(string? id, IReadOnlyDictionary<string, LegendMetadata> legends, IReadOnlyDictionary<int, string> skills)
    {
        if (id is null) return null;
        return legends.TryGetValue(id, out var legend)
            ? new Gw2LegendReference(id, legend.Code, new Gw2NumericReference(legend.Swap, skills.GetValueOrDefault(legend.Swap)))
            : new Gw2LegendReference(id, null, null);
    }

    private static SpecializationSlot ParseSpecialization(JsonElement element)
    {
        var specialization = RequiredObject(element);
        var id = NullablePositiveInt(specialization, "id");
        return new SpecializationSlot(id, ParseNullablePositiveIntegers(RequiredArray(specialization, "traits"), 3));
    }

    private static SkillSlots ParseSkills(JsonElement skills)
    {
        return new SkillSlots(NullablePositiveInt(skills, "heal"), ParseNullablePositiveIntegers(RequiredArray(skills, "utilities"), 3), NullablePositiveInt(skills, "elite"));
    }

    private static int?[] ParseNullablePositiveIntegers(JsonElement array, int count)
    {
        if (array.GetArrayLength() != count) throw InvalidCharacterBuildResponse();
        return array.EnumerateArray().Select(value =>
        {
            if (value.ValueKind == JsonValueKind.Null) return (int?)null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id) || id <= 0) throw InvalidCharacterBuildResponse();
            return id;
        }).ToArray();
    }

    private static string?[] ParseNullableNonblankStrings(JsonElement array, int count)
    {
        if (array.GetArrayLength() != count) throw InvalidCharacterBuildResponse();
        return array.EnumerateArray().Select(value =>
        {
            if (value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw InvalidCharacterBuildResponse();
            return value.GetString();
        }).ToArray();
    }

    private static JsonElement RequiredProperty(JsonElement element, string name)
    {
        var properties = element.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (properties.Length != 1) throw InvalidCharacterBuildResponse();
        return properties[0].Value;
    }

    private static bool HasProperty(JsonElement element, string name) => element.EnumerateObject().Any(property => property.NameEquals(name));
    private static JsonElement RequiredObject(JsonElement element) => element.ValueKind == JsonValueKind.Object ? element : throw InvalidCharacterBuildResponse();
    private static JsonElement RequiredArray(JsonElement element, string name)
    {
        var value = RequiredProperty(element, name);
        return value.ValueKind == JsonValueKind.Array ? value : throw InvalidCharacterBuildResponse();
    }

    private static string RequiredString(JsonElement element, string name, bool allowEmpty)
    {
        var value = RequiredProperty(element, name);
        if (value.ValueKind != JsonValueKind.String || (!allowEmpty && string.IsNullOrWhiteSpace(value.GetString()))) throw InvalidCharacterBuildResponse();
        return value.GetString()!;
    }

    private static int RequiredInt(JsonElement element, string name)
    {
        var value = RequiredProperty(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw InvalidCharacterBuildResponse();
        return number;
    }

    private static bool RequiredBoolean(JsonElement element, string name)
    {
        var value = RequiredProperty(element, name);
        return value.ValueKind is JsonValueKind.True ? true : value.ValueKind is JsonValueKind.False ? false : throw InvalidCharacterBuildResponse();
    }

    private static int? NullablePositiveInt(JsonElement element, string name)
    {
        var value = RequiredProperty(element, name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number <= 0) throw InvalidCharacterBuildResponse();
        return number;
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
                || materials.Any(stack => stack is null || stack.Id is not > 0 || stack.Category is not > 0 || stack.Count is null or < 0))
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

    private async Task<CharacterCoreResponse> DeserializeCharacterCoreAsync(
        HttpResponseMessage response,
        string requestedName,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var core = await JsonSerializer.DeserializeAsync<CharacterCoreResponse>(stream, JsonOptions, cancellationToken);
            if (core is null
                || core.Name != requestedName
                || string.IsNullOrWhiteSpace(core.Race)
                || string.IsNullOrWhiteSpace(core.Gender)
                || string.IsNullOrWhiteSpace(core.Profession)
                || core.Level is not > 0
                || core.Age is null or < 0
                || core.Created is null || core.Created.Value == default
                || core.LastModified is null || core.LastModified.Value == default
                || core.Deaths is null or < 0)
            {
                throw InvalidCharacterCoreResponse();
            }

            return core;
        }
        catch (JsonException)
        {
            throw InvalidCharacterCoreResponse();
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

    private static void EnsureAuthenticatedOk(HttpResponseMessage response, string operation)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw InvalidKey();
        if (response.StatusCode != HttpStatusCode.OK) throw new Gw2ConfigurationException($"GW2 {operation} request failed with HTTP {(int)response.StatusCode}. Try again later.");
    }

    private async Task<ActiveEquipmentPayload> DeserializeActiveEquipmentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = EquipmentObject(document.RootElement);
            var tab = EquipmentInt(root, "tab");
            if (tab <= 0 || !EquipmentBoolean(root, "is_active")) throw InvalidCharacterEquipmentResponse();
            var equipment = EquipmentArray(root, "equipment");
            if (equipment.GetArrayLength() > MaximumEquipmentRows) throw InvalidCharacterEquipmentResponse();
            var rows = new List<AuthenticatedEquipmentRow>();
            foreach (var element in equipment.EnumerateArray())
            {
                var row = EquipmentObject(element);
                var slot = EquipmentString(row, "slot", false);
                if (KnownSpecialEquipmentSlots.Contains(slot)) continue;
                var parsed = ParseEquipmentRow(row, slot, primary: true);
                if (rows.Any(existing => existing.Slot == parsed.Slot)) throw InvalidCharacterEquipmentResponse();
                rows.Add(parsed);
            }
            return new ActiveEquipmentPayload(tab, EquipmentString(root, "name", true), rows);
        }
        catch (JsonException) { throw InvalidCharacterEquipmentResponse(); }
    }

    private async Task<AuthenticatedEquipmentRow?> DeserializeFallbackRelicAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var equipment = EquipmentArray(EquipmentObject(document.RootElement), "equipment");
            if (equipment.GetArrayLength() > MaximumFallbackEquipmentRows) throw InvalidCharacterEquipmentResponse();
            AuthenticatedEquipmentRow? candidate = null;
            foreach (var element in equipment.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object || !TryEquipmentString(element, "slot", out var slot) || slot != "Relic") continue;
                var location = EquipmentString(element, "location", false);
                if (KnownInactiveEquipmentLocations.Contains(location)) continue;
                if (location is not "Equipped" and not "EquippedFromLegendaryArmory") throw InvalidCharacterEquipmentResponse();
                if (candidate is not null) throw InvalidCharacterEquipmentResponse();
                candidate = ParseEquipmentRow(element, slot, primary: false);
            }
            return candidate;
        }
        catch (JsonException) { throw InvalidCharacterEquipmentResponse(); }
    }

    private static AuthenticatedEquipmentRow ParseEquipmentRow(JsonElement row, string slot, bool primary)
    {
        var itemId = EquipmentLong(row, "id");
        if (itemId <= 0) throw InvalidCharacterEquipmentResponse();
        var location = EquipmentString(row, "location", false);
        if (primary && KnownInactiveEquipmentLocations.Contains(location)) throw InvalidCharacterEquipmentResponse();
        var skinId = OptionalPositiveLong(row, "skin");
        var upgrades = OptionalPositiveLongArray(row, "upgrades");
        var infusions = OptionalPositiveLongArray(row, "infusions");
        var binding = OptionalNullableString(row, "binding");
        var boundTo = OptionalNullableString(row, "bound_to");
        if ((binding == "Character" && boundTo is null) || ((binding is null or "Account") && boundTo is not null)) throw InvalidCharacterEquipmentResponse();
        long? selectedStatId = null;
        IReadOnlyList<Gw2EquipmentStatAttribute>? selectedAttributes = null;
        if (TryEquipmentProperty(row, "stats", out var stats))
        {
            var statsObject = EquipmentObject(stats);
            selectedStatId = EquipmentLong(statsObject, "id");
            if (selectedStatId <= 0) throw InvalidCharacterEquipmentResponse();
            var attributes = EquipmentObject(EquipmentProperty(statsObject, "attributes"));
            var pairs = new List<Gw2EquipmentStatAttribute>();
            foreach (var attribute in attributes.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(attribute.Name) || attribute.Value.ValueKind != JsonValueKind.Number || !attribute.Value.TryGetInt32(out var value)) throw InvalidCharacterEquipmentResponse();
                if (pairs.Any(pair => pair.Name == attribute.Name)) throw InvalidCharacterEquipmentResponse();
                pairs.Add(new Gw2EquipmentStatAttribute(attribute.Name, value));
            }
            if (pairs.Count > MaximumEquipmentStatAttributes) throw InvalidCharacterEquipmentResponse();
            selectedAttributes = pairs.Count == 0 ? null : pairs.OrderBy(pair => pair.Name, StringComparer.Ordinal).ToArray();
        }
        var referenceKind = location == "Equipped" ? "EquippedReference" : location == "EquippedFromLegendaryArmory" ? "LegendaryArmoryReference" : "UnknownEquipmentReference";
        return new AuthenticatedEquipmentRow(slot, itemId, skinId, upgrades, infusions, binding, boundTo, location, referenceKind, selectedStatId, selectedAttributes);
    }

    private async Task<EquipmentItemBatch> ResolveEquipmentItemsAsync(IReadOnlyList<long> requestedIds, IReadOnlySet<long> primaryIds, CancellationToken cancellationToken)
    {
        if (requestedIds.Count == 0) return new(new Dictionary<long, EquipmentItemMetadata>());
        try
        {
            using var response = await SendWithSingleRetryAsync($"/v2/items?ids={Uri.EscapeDataString(string.Join(',', requestedIds))}", cancellationToken, authenticated: false);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(new Dictionary<long, EquipmentItemMetadata>());
            if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent) return new(new Dictionary<long, EquipmentItemMetadata>());
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return new(new Dictionary<long, EquipmentItemMetadata>());
            var rows = new Dictionary<long, EquipmentItemMetadata>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var item = EquipmentObject(element);
                var id = EquipmentLong(item, "id");
                var name = EquipmentString(item, "name", true);
                if (id <= 0 || !requestedIds.Contains(id) || !rows.TryAdd(id, ParseEquipmentItemMetadata(item, id, name, primaryIds.Contains(id)))) return new(new Dictionary<long, EquipmentItemMetadata>());
            }
            if ((response.StatusCode == HttpStatusCode.OK && rows.Count != requestedIds.Count)
                || (response.StatusCode == HttpStatusCode.PartialContent && (rows.Count == 0 || rows.Count == requestedIds.Count))) return new(new Dictionary<long, EquipmentItemMetadata>());
            return new(rows);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(new Dictionary<long, EquipmentItemMetadata>()); }
    }

    private static EquipmentItemMetadata ParseEquipmentItemMetadata(JsonElement item, long id, string name, bool primary)
    {
        if (!primary) return new(id, name, null, null, null, null, null);
        var type = EquipmentString(item, "type", false);
        var rarity = EquipmentString(item, "rarity", false);
        var level = EquipmentInt(item, "level");
        if (level < 0) throw InvalidCharacterEquipmentResponse();
        string? subtype = null;
        long? defaultStatId = null;
        if (TryEquipmentProperty(item, "details", out var detailsValue))
        {
            var details = EquipmentObject(detailsValue);
            if (TryEquipmentProperty(details, "type", out var typeValue))
            {
                if (typeValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(typeValue.GetString())) throw InvalidCharacterEquipmentResponse();
                subtype = typeValue.GetString();
            }
            if (TryEquipmentProperty(details, "infix_upgrade", out var infixValue))
            {
                var infix = EquipmentObject(infixValue);
                if (TryEquipmentProperty(infix, "id", out var idValue))
                {
                    if (idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetInt64(out var parsedDefaultStatId) || parsedDefaultStatId <= 0) throw InvalidCharacterEquipmentResponse();
                    defaultStatId = parsedDefaultStatId;
                }
            }
        }
        return new(id, name, type, subtype, rarity, level, defaultStatId);
    }

    private async Task<EquipmentNameBatch> ResolveEquipmentNamesAsync(string resolver, IReadOnlyList<long> requestedIds, CancellationToken cancellationToken)
    {
        if (requestedIds.Count == 0) return new(new Dictionary<long, string>());
        try
        {
            using var response = await SendWithSingleRetryAsync($"/v2/{resolver}?ids={Uri.EscapeDataString(string.Join(',', requestedIds))}", cancellationToken, authenticated: false);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(new Dictionary<long, string>());
            if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent) return new(new Dictionary<long, string>());
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return new(new Dictionary<long, string>());
            var rows = new Dictionary<long, string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var row = EquipmentObject(element);
                var id = EquipmentLong(row, "id");
                var name = EquipmentString(row, "name", false);
                if (id <= 0 || !requestedIds.Contains(id) || !rows.TryAdd(id, name)) return new(new Dictionary<long, string>());
            }
            if ((response.StatusCode == HttpStatusCode.OK && rows.Count != requestedIds.Count)
                || (response.StatusCode == HttpStatusCode.PartialContent && (rows.Count == 0 || rows.Count == requestedIds.Count))) return new(new Dictionary<long, string>());
            return new(rows);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(new Dictionary<long, string>()); }
    }

    private static Gw2EquipmentRow ToEquipmentRow(AuthenticatedEquipmentRow row, IReadOnlyDictionary<long, EquipmentItemMetadata> items, IReadOnlyDictionary<long, string> stats, IReadOnlyDictionary<long, string> skins)
    {
        items.TryGetValue(row.ItemId, out var item);
        var statId = row.SelectedStatId ?? item?.DefaultStatId;
        var stat = statId is null ? null : new Gw2EquipmentStat(statId.Value, stats.GetValueOrDefault(statId.Value), row.SelectedStatId is null ? "ItemDefault" : "Selected", row.SelectedStatId is null ? null : row.SelectedAttributes);
        return new Gw2EquipmentRow(row.Slot, new Gw2EquipmentItem(row.ItemId, item?.Name, item?.Type, item?.Subtype, item?.Rarity, item?.Level), stat,
            row.Upgrades.Select(id => new Gw2EquipmentReference(id, items.GetValueOrDefault(id)?.Name)).ToArray(), row.Infusions.Select(id => new Gw2EquipmentReference(id, items.GetValueOrDefault(id)?.Name)).ToArray(),
            row.SkinId is { } skinId ? new Gw2EquipmentReference(skinId, skins.GetValueOrDefault(skinId)) : null, row.Binding, row.BoundTo, row.Location, row.ReferenceKind);
    }

    private static void AddEquipmentWarnings(List<Gw2MetadataWarning> warnings, string resolver, IEnumerable<long> requested, IEnumerable<long> resolved)
    {
        var resolvedSet = resolved.ToHashSet();
        warnings.AddRange(requested.Distinct().Where(id => !resolvedSet.Contains(id)).Order().Select(id => new Gw2MetadataWarning("metadata_unresolved", resolver, id.ToString(CultureInfo.InvariantCulture))));
    }

    private static JsonElement EquipmentProperty(JsonElement element, string name)
    {
        var matches = element.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (matches.Length != 1) throw InvalidCharacterEquipmentResponse();
        return matches[0].Value;
    }
    private static bool TryEquipmentProperty(JsonElement element, string name, out JsonElement value)
    {
        var matches = element.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (matches.Length > 1) throw InvalidCharacterEquipmentResponse();
        value = matches.Length == 1 ? matches[0].Value : default;
        return matches.Length == 1;
    }
    private static JsonElement EquipmentObject(JsonElement element) => element.ValueKind == JsonValueKind.Object ? element : throw InvalidCharacterEquipmentResponse();
    private static JsonElement EquipmentArray(JsonElement element, string name) => EquipmentProperty(element, name).ValueKind == JsonValueKind.Array ? EquipmentProperty(element, name) : throw InvalidCharacterEquipmentResponse();
    private static string EquipmentString(JsonElement element, string name, bool allowEmpty)
    {
        var value = EquipmentProperty(element, name);
        if (value.ValueKind != JsonValueKind.String || (!allowEmpty && string.IsNullOrWhiteSpace(value.GetString()))) throw InvalidCharacterEquipmentResponse();
        return value.GetString()!;
    }
    private static bool TryEquipmentString(JsonElement element, string name, out string? value)
    {
        value = null;
        return TryEquipmentProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String && (value = property.GetString()) is not null;
    }
    private static long EquipmentLong(JsonElement element, string name)
    {
        var value = EquipmentProperty(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) throw InvalidCharacterEquipmentResponse();
        return number;
    }
    private static int EquipmentInt(JsonElement element, string name)
    {
        var value = EquipmentProperty(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw InvalidCharacterEquipmentResponse();
        return number;
    }
    private static bool EquipmentBoolean(JsonElement element, string name) => EquipmentProperty(element, name).ValueKind == JsonValueKind.True ? true : EquipmentProperty(element, name).ValueKind == JsonValueKind.False ? false : throw InvalidCharacterEquipmentResponse();
    private static long? OptionalPositiveLong(JsonElement element, string name)
    {
        if (!TryEquipmentProperty(element, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number <= 0) throw InvalidCharacterEquipmentResponse();
        return number;
    }
    private static IReadOnlyList<long> OptionalPositiveLongArray(JsonElement element, string name)
    {
        if (!TryEquipmentProperty(element, name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw InvalidCharacterEquipmentResponse();
        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var id) && id > 0 ? id : throw InvalidCharacterEquipmentResponse()).ToArray();
    }
    private static string? OptionalNullableString(JsonElement element, string name)
    {
        if (!TryEquipmentProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw InvalidCharacterEquipmentResponse();
        return value.GetString();
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
    private static Gw2ConfigurationException InvalidCharacterCoreResponse() => new("GW2 returned an invalid character-core response. Try again later.");
    private static Gw2ConfigurationException InvalidTradingPostDeliveryResponse() => new("GW2 returned an invalid delivery response. Try again later.");
    private static Gw2ConfigurationException InvalidCurrentSellsResponse() => new("GW2 returned an invalid current-sells response. Try again later.");
    private static Gw2ConfigurationException InvalidCurrentSellsPagination() => new("GW2 returned invalid current-sells pagination metadata. Try again later.");
    private static Gw2ConfigurationException InvalidItemMetadataResponse() => new("GW2 returned an invalid item metadata response. Try again later.");
    private static Gw2ConfigurationException InvalidTokenPermissionResponse() => new("GW2 returned an invalid token-permission response. Try again later.");
    private static Gw2ConfigurationException InvalidCharacterBuildResponse() => new("GW2 returned an invalid character-build response. Try again later.");
    private static Gw2ConfigurationException InvalidCharacterEquipmentResponse() => new("GW2 returned an invalid character-equipment response. Try again later.");

    private sealed record ActiveBuildPayload(int Tab, string Name, string Profession, IReadOnlyList<SpecializationSlot> Specializations, SkillSlots TerrestrialSkills, SkillSlots AquaticSkills, PetSlots? Pets, LegendSlots? Legends);
    private sealed record SpecializationSlot(int? Id, IReadOnlyList<int?> Traits);
    private sealed record SkillSlots(int? Heal, IReadOnlyList<int?> Utilities, int? Elite)
    {
        public IEnumerable<int> AllIds() => new[] { Heal }.Concat(Utilities).Append(Elite).Where(id => id is not null).Select(id => id!.Value);
    }
    private sealed record PetSlots(IReadOnlyList<int?> Terrestrial, IReadOnlyList<int?> Aquatic);
    private sealed record LegendSlots(IReadOnlyList<string?> Terrestrial, IReadOnlyList<string?> Aquatic);
    private sealed record NumericMetadataBatch(IReadOnlyDictionary<int, string> Rows);
    private sealed record LegendMetadataBatch(IReadOnlyDictionary<string, LegendMetadata> Rows);
    private sealed record LegendMetadata(int Code, int Swap);
    private sealed record TokenInfo(List<string?>? Permissions);
    private sealed record AccountResponse(string? Name, int? World, DateTimeOffset? Created, List<string>? Access);
    private sealed record WalletBalanceResponse(int? Id, long? Value);
    private sealed record CurrencyResponse(int? Id, string? Name);
    private sealed record InventoryStackResponse(int? Id, long? Count);
    private sealed record MaterialStackResponse(int? Id, int? Category, long? Count);
    private sealed record CharacterInventoryResponse(List<CharacterBagResponse?>? Bags);
    private sealed record CharacterBagResponse(int? Id, int? Size, List<InventoryStackResponse?>? Inventory);
    private sealed record CharacterCoreResponse(
        string? Name,
        string? Race,
        string? Gender,
        string? Profession,
        int? Level,
        long? Age,
        DateTimeOffset? Created,
        [property: JsonPropertyName("last_modified")] DateTimeOffset? LastModified,
        long? Deaths);
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
    private sealed record ActiveEquipmentPayload(int Tab, string Name, IReadOnlyList<AuthenticatedEquipmentRow> Rows);
    private sealed record AuthenticatedEquipmentRow(string Slot, long ItemId, long? SkinId, IReadOnlyList<long> Upgrades, IReadOnlyList<long> Infusions, string? Binding, string? BoundTo, string Location, string ReferenceKind, long? SelectedStatId, IReadOnlyList<Gw2EquipmentStatAttribute>? SelectedAttributes);
    private sealed record EquipmentItemBatch(IReadOnlyDictionary<long, EquipmentItemMetadata> Rows);
    private sealed record EquipmentNameBatch(IReadOnlyDictionary<long, string> Rows);
    private sealed record EquipmentItemMetadata(long Id, string Name, string? Type, string? Subtype, string? Rarity, int? Level, long? DefaultStatId);
}

public sealed record Gw2ApiOptions(string ApiKey, string BaseUrl);

public sealed class Gw2ConfigurationException(string message) : Exception(message);

public sealed record Gw2Account(string Name, int World, DateTimeOffset Created, List<string> Access);
public sealed record Gw2Wallet(IReadOnlyList<Gw2WalletBalance> Balances, IReadOnlyList<Gw2WalletWarning> Warnings);
public sealed record Gw2WalletBalance(int Id, string? Name, long Value);
public sealed record Gw2WalletWarning(string Code, int CurrencyId);
public sealed record Gw2Characters(IReadOnlyList<Gw2Character> Characters);
public sealed record Gw2Character(
    string Name,
    string Race,
    string Gender,
    string Profession,
    int Level,
    long AgeSeconds,
    DateTimeOffset Created,
    DateTimeOffset LastModified,
    long Deaths);
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
public sealed record Gw2CharacterBuild(
    string CharacterName,
    int Tab,
    string BuildName,
    string Profession,
    IReadOnlyList<Gw2BuildSpecialization> Specializations,
    Gw2BuildSkills TerrestrialSkills,
    Gw2BuildSkills AquaticSkills,
    Gw2BuildPets? Pets,
    Gw2BuildLegends? Legends,
    bool IsMetadataComplete,
    IReadOnlyList<Gw2MetadataWarning> Warnings);
public sealed record Gw2BuildSpecialization(Gw2NumericReference? Specialization, IReadOnlyList<Gw2NumericReference?> SelectedTraits);
public sealed record Gw2BuildSkills(Gw2NumericReference? Heal, IReadOnlyList<Gw2NumericReference?> Utilities, Gw2NumericReference? Elite);
public sealed record Gw2BuildPets(IReadOnlyList<Gw2NumericReference?> Terrestrial, IReadOnlyList<Gw2NumericReference?> Aquatic);
public sealed record Gw2BuildLegends(IReadOnlyList<Gw2LegendReference?> Terrestrial, IReadOnlyList<Gw2LegendReference?> Aquatic);
public sealed record Gw2NumericReference(int Id, string? Name);
public sealed record Gw2LegendReference(string Id, int? Code, Gw2NumericReference? SwapSkill);
public sealed record Gw2MetadataWarning(string Code, string Resolver, string ReferenceId);
public sealed record Gw2CharacterEquipment(string CharacterName, int Tab, string EquipmentTabName, IReadOnlyList<Gw2EquipmentRow> Equipment, bool IsMetadataComplete, IReadOnlyList<Gw2MetadataWarning> Warnings);
public sealed record Gw2EquipmentRow(string Slot, Gw2EquipmentItem Item, Gw2EquipmentStat? Stats, IReadOnlyList<Gw2EquipmentReference> Upgrades, IReadOnlyList<Gw2EquipmentReference> Infusions, Gw2EquipmentReference? Skin, string? Binding, string? BoundTo, string Location, string ReferenceKind);
public sealed record Gw2EquipmentItem(long Id, string? Name, string? Type, string? Subtype, string? Rarity, int? Level);
public sealed record Gw2EquipmentStat(long Id, string? Name, string Source, IReadOnlyList<Gw2EquipmentStatAttribute>? Attributes);
public sealed record Gw2EquipmentStatAttribute(string Name, int Value);
public sealed record Gw2EquipmentReference(long Id, string? Name);
public enum Gw2StorageSource
{
    Bank,
    MaterialStorage,
    SharedInventory
}
