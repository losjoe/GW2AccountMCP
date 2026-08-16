using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text;
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
    Task<Gw2CharacterEquipmentTabs> GetCharacterEquipmentTabsAsync(string characterName, CancellationToken cancellationToken);
    Task<Gw2CharacterInventory> GetCharacterInventoryAsync(string characterName, CancellationToken cancellationToken);
    Task<Gw2AccountStorage> GetAccountStorageAsync(CancellationToken cancellationToken);
    Task<Gw2CharacterBags> GetCharacterBagsAsync(CancellationToken cancellationToken);
    Task<Gw2TradingPostDelivery> GetTradingPostDeliveryAsync(CancellationToken cancellationToken);
    Task<Gw2CurrentSells> GetCurrentSellsAsync(CancellationToken cancellationToken);
    Task<Gw2CurrentBuysPage> GetCurrentBuysPageAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<Gw2CurrentSellsPage> GetCurrentSellsPageAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<Gw2Items> GetItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken);
    Task<Gw2PublicItems> GetPublicItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken);
    Task<Gw2MaterialCategories> GetPublicMaterialCategoriesAsync(CancellationToken cancellationToken);
    Task<Gw2PublicRecipes> GetPublicRecipesAsync(IReadOnlyList<long> recipeIds, CancellationToken cancellationToken);
    Task<Gw2RecipeSelector> SearchPublicRecipesByInputItemAsync(long itemId, CancellationToken cancellationToken);
    Task<Gw2RecipeSelector> SearchPublicRecipesByOutputItemAsync(long itemId, CancellationToken cancellationToken);
    Task<Gw2AccountRecipeUnlocks> GetAccountRecipeUnlocksAsync(CancellationToken cancellationToken);
    Task<Gw2LegendaryArmory> GetLegendaryArmoryAsync(CancellationToken cancellationToken);
    Task<Gw2AccountAchievementProgress> GetAccountAchievementProgressAsync(CancellationToken cancellationToken);
    Task<Gw2PublicAchievements> GetPublicAchievementsAsync(IReadOnlyList<long> achievementIds, CancellationToken cancellationToken);
    Task<Gw2AccountMasterySources> GetAccountMasterySourcesAsync(CancellationToken cancellationToken);
    Task<Gw2PublicMasteries> GetPublicMasteriesAsync(IReadOnlyList<long> masteryIds, CancellationToken cancellationToken);
}

public sealed class Gw2ApiClient(HttpClient httpClient, Gw2ApiOptions options, TimeProvider? timeProvider = null) : IGw2ApiClient
{
    public const string SchemaVersion = "2025-08-29T01:00:00.000Z";
    private const string Language = "en";
    private const int CurrentSellsPageSize = 200;
    private const int MaximumCurrentBuysPageSize = 200;
    private const int MaximumCurrentBuysPayloadBytes = 256 * 1024;
    private const int MaximumCurrentSellsPageSize = 200;
    private const int MaximumCurrentSellsPayloadBytes = 256 * 1024;
    private const int MaximumItemBatchSize = 200;
    private const int MaximumPublicItemBatchSize = 100;
    private const int MaximumMaterialCategoryPayloadBytes = 128 * 1024;
    private const int MaximumMaterialCategories = 64;
    private const int MaximumMaterialMemberships = 10_000;
    private const int MaximumPublicRecipeBatchSize = 100;
    private const int MaximumRecipeSelectorPayloadBytes = 64 * 1024;
    private const int MaximumRecipeSelectorIds = 5_000;
    private const int MaximumAccountRecipePayloadBytes = 256 * 1024;
    private const int MaximumAccountRecipeIds = 25_000;
    private const int MaximumLegendaryArmoryRows = 256;
    private const int MaximumAccountAchievementPayloadBytes = 4 * 1024 * 1024;
    private const int MaximumAccountAchievementRows = 20_000;
    private const int MaximumPublicAchievementPayloadBytes = 1024 * 1024;
    private const int MaximumAccountMasteryPayloadBytes = 256 * 1024;
    private const int MaximumAccountMasteryRows = 200;
    private const int MaximumMasteryPointsPayloadBytes = 1024 * 1024;
    private const int MaximumMasteryPointTotals = 32;
    private const int MaximumMasteryUnlockedEntries = 10_000;
    private const int MaximumPublicMasteryPayloadBytes = 2 * 1024 * 1024;
    private const int MaximumPublicMasteryBatchSize = 200;
    private const int MaximumPublicMasteryLevels = 2_048;
    private const int MaximumEquipmentRows = 32;
    private const int MaximumEquipmentReferences = 200;
    private const int MaximumEquipmentStatAttributes = 32;
    private const int MaximumFallbackEquipmentRows = 256;
    private const int MaximumEquipmentTabsPayloadBytes = 2 * 1024 * 1024;
    private const int MaximumEquipmentTabs = 16;
    private const int MaximumEquipmentTabRows = 64;
    private const int MaximumEquipmentTabTotalRows = 512;
    private const int MaximumEquipmentTabReferences = 4_096;
    private const int MaximumEquipmentTabStatAttributes = 4_096;
    private const int MaximumEquipmentTabMetadataIdentities = 4_096;
    private const int MaximumEquipmentTabWarnings = 256;
    private const int MaximumEquipmentTabStringLength = 256;
    private static readonly string[] EquipmentSlotOrder = ["Helm", "Shoulders", "Coat", "Gloves", "Leggings", "Boots", "Backpack", "Accessory1", "Accessory2", "Amulet", "Ring1", "Ring2", "WeaponA1", "WeaponA2", "WeaponB1", "WeaponB2", "HelmAquatic", "WeaponAquaticA", "WeaponAquaticB", "Relic"];
    private static readonly HashSet<string> KnownSpecialEquipmentSlots = ["Sickle", "Axe", "Pick", "FishingRod", "FishingBait", "FishingLure", "PowerCore", "SensoryArray", "ServiceChip"];
    private static readonly HashSet<string> KnownInactiveEquipmentLocations = ["Armory", "LegendaryArmory"];
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RecipeAttemptTimeout = TimeSpan.FromSeconds(15);
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

    public async Task<Gw2CharacterEquipmentTabs> GetCharacterEquipmentTabsAsync(string characterName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        if (string.IsNullOrWhiteSpace(characterName)) throw new Gw2ConfigurationException("characterName is required and must not be blank.");
        try
        {
            await ValidateEquipmentTabsPermissionsAsync(["account", "characters", "builds", "inventories"], cancellationToken);
            using var roster = await SendEquipmentTabsWithSingleRetryAsync("/v2/characters", cancellationToken, true);
            EnsureAuthenticatedOk(roster.Response, "character-list");
            var canonicalName = (await DeserializeCharacterNamesAsync(roster.Response, roster.CancellationToken)).SingleOrDefault(name => string.Equals(name, characterName, StringComparison.Ordinal));
            if (canonicalName is null) throw new Gw2ConfigurationException("The requested character was not found in the authenticated roster.");
            using var response = await SendEquipmentTabsWithSingleRetryAsync($"/v2/characters/{Uri.EscapeDataString(canonicalName)}/equipmenttabs", cancellationToken, true);
            EnsureAuthenticatedOk(response.Response, "character-equipment-tabs");
            var payload = await DeserializeEquipmentTabsAsync(response.Response, response.CancellationToken);
            var equipmentTabsAsOf = (timeProvider ?? TimeProvider.System).GetUtcNow();
            if (payload.Count == 0) return new Gw2CharacterEquipmentTabs(canonicalName, null, [], true, [], equipmentTabsAsOf, null, null, null, null, equipmentTabsAsOf);
            var missingRelics = payload.Where(tab => !tab.Rows.Any(row => row.Slot == "Relic")).ToArray();
            DateTimeOffset? equipmentAsOf = null;
            if (missingRelics.Length != 0)
            {
                using var fallback = await SendEquipmentTabsWithSingleRetryAsync($"/v2/characters/{Uri.EscapeDataString(canonicalName)}/equipment", cancellationToken, true);
                EnsureAuthenticatedOk(fallback.Response, "character-equipment");
                AddFallbackRelics(payload, await DeserializeFallbackEquipmentAsync(fallback.Response, fallback.CancellationToken));
                equipmentAsOf = (timeProvider ?? TimeProvider.System).GetUtcNow();
            }
            var rows = payload.SelectMany(tab => tab.Rows).ToArray();
            if (rows.Aggregate(0, (count, row) => checked(count + 1 + row.Upgrades.Count + row.Infusions.Count)) > MaximumEquipmentTabReferences
                || rows.Aggregate(0, (count, row) => checked(count + (row.SelectedAttributes?.Count ?? 0))) > MaximumEquipmentTabStatAttributes)
                throw InvalidCharacterEquipmentTabsResponse();
            var itemIds = rows.Select(row => row.ItemId).Concat(rows.SelectMany(row => row.Upgrades)).Concat(rows.SelectMany(row => row.Infusions)).Distinct().Order().ToArray();
            var selectedStatIds = rows.Where(row => row.SelectedStatId is not null).Select(row => row.SelectedStatId!.Value).Distinct().Order().ToArray();
            var skinIds = rows.Where(row => row.SkinId is not null).Select(row => row.SkinId!.Value).Distinct().Order().ToArray();
            EnsureEquipmentTabMetadataIdentityCount(itemIds, selectedStatIds, skinIds);
            var itemMetadata = await ResolveEquipmentTabItemsAsync(itemIds, rows.Select(row => row.ItemId).ToHashSet(), cancellationToken);
            var itemsAsOf = itemIds.Length == 0 ? null : (DateTimeOffset?)(timeProvider ?? TimeProvider.System).GetUtcNow();
            var statIds = selectedStatIds.Concat(rows.Where(row => row.SelectedStatId is null).Select(row => itemMetadata.GetValueOrDefault(row.ItemId)?.DefaultStatId).OfType<long>()).Distinct().Order().ToArray();
            EnsureEquipmentTabMetadataIdentityCount(itemIds, statIds, skinIds);
            var stats = await ResolveEquipmentTabNamesAsync("itemstats", statIds, cancellationToken);
            var statsAsOf = statIds.Length == 0 ? null : (DateTimeOffset?)(timeProvider ?? TimeProvider.System).GetUtcNow();
            var skins = await ResolveEquipmentTabNamesAsync("skins", skinIds, cancellationToken);
            var skinsAsOf = skinIds.Length == 0 ? null : (DateTimeOffset?)(timeProvider ?? TimeProvider.System).GetUtcNow();
            var warnings = new List<Gw2MetadataWarning>(); AddEquipmentWarnings(warnings, "items", itemIds, itemMetadata.Keys); AddEquipmentWarnings(warnings, "itemstats", statIds, stats.Keys); AddEquipmentWarnings(warnings, "skins", skinIds, skins.Keys);
            if (warnings.Count > MaximumEquipmentTabWarnings)
            {
                var overflow = warnings.Count - (MaximumEquipmentTabWarnings - 1);
                var resolver = warnings.Select(warning => warning.Resolver).Distinct(StringComparer.Ordinal).Take(2).Count() == 1 ? warnings[0].Resolver : "multiple";
                warnings = warnings.Take(MaximumEquipmentTabWarnings - 1).Append(new Gw2MetadataWarning("metadata_warning_limit_reached", resolver, overflow.ToString(CultureInfo.InvariantCulture))).ToList();
            }
            return new Gw2CharacterEquipmentTabs(canonicalName, payload.Single(tab => tab.IsActive).Tab, payload.OrderBy(tab => tab.Tab).Select(tab => new Gw2CharacterEquipmentTab(tab.Tab, tab.Name, tab.IsActive, tab.Rows.OrderBy(row => Array.IndexOf(EquipmentSlotOrder, row.Slot) is var index && index >= 0 ? index : int.MaxValue).ThenBy(row => row.Slot, StringComparer.Ordinal).Select(row => ToEquipmentRow(row, itemMetadata, stats, skins)).ToArray())).ToArray(), warnings.Count == 0, warnings, equipmentTabsAsOf, equipmentAsOf, itemsAsOf, statsAsOf, skinsAsOf, (timeProvider ?? TimeProvider.System).GetUtcNow());
        }
        catch (IOException) { throw InvalidCharacterEquipmentTabsResponse(); }
        catch (JsonException) { throw InvalidCharacterEquipmentTabsResponse(); }
        catch (HttpRequestException) { throw InvalidCharacterEquipmentTabsResponse(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new Gw2ConfigurationException("GW2 character equipment-tabs request timed out. Try again later."); }
    }

    public async Task<Gw2CharacterInventory> GetCharacterInventoryAsync(string characterName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        if (string.IsNullOrWhiteSpace(characterName)) throw new Gw2ConfigurationException("characterName is required and must not be blank.");
        await ValidatePermissionsAsync(["account", "characters", "inventories"], cancellationToken);
        using var rosterResponse = await SendWithSingleRetryAsync("/v2/characters", cancellationToken);
        EnsureAuthenticatedOk(rosterResponse, "character-list");
        var canonicalName = (await DeserializeCharacterNamesAsync(rosterResponse, cancellationToken)).SingleOrDefault(name => string.Equals(name, characterName, StringComparison.Ordinal));
        if (canonicalName is null) throw new Gw2ConfigurationException("The requested character was not found in the authenticated roster.");
        using var response = await SendWithSingleRetryAsync($"/v2/characters/{Uri.EscapeDataString(canonicalName)}/inventory", cancellationToken);
        EnsureAuthenticatedOk(response, "character-inventory");
        var payload = await DeserializeSelectedCharacterInventoryAsync(response, cancellationToken);
        var stacks = payload.Bags.Where(bag => bag is not null).SelectMany(bag => bag!.Slots).Where(stack => stack is not null).Select(stack => stack!).ToArray();
        var itemIds = payload.Bags.Where(bag => bag is not null).Select(bag => bag!.Id).Concat(stacks.SelectMany(stack => new[] { stack.ItemId }.Concat(stack.Upgrades).Concat(stack.Infusions))).Distinct().Order().ToArray();
        var items = await ResolveInventoryItemsAsync(itemIds, stacks.Select(stack => stack.ItemId).ToHashSet(), cancellationToken);
        var statIds = stacks.Select(stack => stack.SelectedStatId ?? items.GetValueOrDefault(stack.ItemId)?.DefaultStatId).Where(id => id is not null).Select(id => id!.Value).Distinct().Order().ToArray();
        var stats = await ResolveInventoryNamesAsync("itemstats", statIds, cancellationToken);
        var skinIds = stacks.Where(stack => stack.SkinId is not null).Select(stack => stack.SkinId!.Value).Distinct().Order().ToArray();
        var skins = await ResolveInventoryNamesAsync("skins", skinIds, cancellationToken);
        var warnings = new List<Gw2MetadataWarning>();
        AddEquipmentWarnings(warnings, "items", itemIds, items.Keys);
        AddEquipmentWarnings(warnings, "itemstats", statIds, stats.Keys);
        AddEquipmentWarnings(warnings, "skins", skinIds, skins.Keys);
        var bags = new List<Gw2CharacterInventoryBag>();
        var equipped = 0;
        var slotsTotal = 0;
        var occupied = 0;
        for (var bagPosition = 0; bagPosition < payload.Bags.Count; bagPosition++)
        {
            var bag = payload.Bags[bagPosition];
            if (bag is null) { bags.Add(new Gw2CharacterInventoryBag(bagPosition, null, [])); continue; }
            equipped++;
            slotsTotal += bag.Size;
            var slots = new List<Gw2CharacterInventorySlot>();
            for (var slotPosition = 0; slotPosition < bag.Slots.Count; slotPosition++)
            {
                var stack = bag.Slots[slotPosition];
                if (stack is null) { slots.Add(new Gw2CharacterInventorySlot(slotPosition, null)); continue; }
                occupied++;
                var metadata = items.GetValueOrDefault(stack.ItemId);
                var statId = stack.SelectedStatId ?? metadata?.DefaultStatId;
                var stat = statId is null ? null : new Gw2InventoryStat(statId.Value, stats.GetValueOrDefault(statId.Value), stack.SelectedStatId is null ? "ItemDefault" : "Selected", stack.SelectedStatId is null ? null : stack.SelectedAttributes);
                slots.Add(new Gw2CharacterInventorySlot(slotPosition, new Gw2InventoryStack(new Gw2InventoryItem(stack.ItemId, metadata?.Name, metadata?.Type, metadata?.Subtype, metadata?.Rarity, metadata?.Level), stack.Count, stack.Charges, stat, stack.Upgrades.Select(id => new Gw2InventoryReference(id, items.GetValueOrDefault(id)?.Name)).ToArray(), stack.Infusions.Select(id => new Gw2InventoryReference(id, items.GetValueOrDefault(id)?.Name)).ToArray(), stack.SkinId is { } skinId ? new Gw2InventoryReference(skinId, skins.GetValueOrDefault(skinId)) : null, stack.Binding, stack.BoundTo)));
            }
            bags.Add(new Gw2CharacterInventoryBag(bagPosition, new Gw2InventoryBag(bag.Id, items.GetValueOrDefault(bag.Id)?.Name, bag.Size), slots));
        }
        return new Gw2CharacterInventory(canonicalName, new Gw2CharacterInventoryCapacity(payload.Bags.Count, equipped, slotsTotal, occupied, slotsTotal - occupied), bags, warnings.Count == 0, warnings);
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

    public async Task<Gw2CurrentBuysPage> GetCurrentBuysPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page), "Page must be nonnegative.");
        if (pageSize is < 1 or > MaximumCurrentBuysPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be from 1 through 200.");
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");

        try
        {
            await ValidatePermissionsAsync(["account", "tradingpost"], cancellationToken);
            using var request = await SendCurrentBuysWithSingleRetryAsync(
                $"/v2/commerce/transactions/current/buys?page={page}&page_size={pageSize}", cancellationToken);
            var response = request.Response;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw InvalidKey();
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Gw2ConfigurationException($"GW2 current-buys request failed with HTTP {(int)response.StatusCode}. Try again later.");
            }

            var pagination = DeserializeCurrentBuysPagination(response.Headers, page, pageSize);
            var orders = await DeserializeCurrentBuysAsync(response, request.CancellationToken);
            if (orders.Count != pagination.ResultCount) throw InvalidCurrentBuysPagination();
            return new Gw2CurrentBuysPage(page, pageSize, pagination.PageCount, pagination.TotalCount, orders);
        }
        catch (IOException)
        {
            throw InvalidCurrentBuysResponse();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new Gw2ConfigurationException("GW2 current-buys request timed out. Try again later.");
        }
    }

    public async Task<Gw2CurrentSellsPage> GetCurrentSellsPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page), "Page must be nonnegative.");
        if (pageSize is < 1 or > MaximumCurrentSellsPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be from 1 through 200.");
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");

        try
        {
            await ValidatePermissionsAsync(["account", "tradingpost"], cancellationToken);
            using var request = await SendCurrentSellsWithSingleRetryAsync(
                $"/v2/commerce/transactions/current/sells?page={page}&page_size={pageSize}", cancellationToken);
            var response = request.Response;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw InvalidKey();
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Gw2ConfigurationException($"GW2 current-sells request failed with HTTP {(int)response.StatusCode}. Try again later.");
            }

            var pagination = DeserializeCurrentSellsPagePagination(response.Headers, page, pageSize);
            var orders = await DeserializeCurrentSellsPageAsync(response, request.CancellationToken);
            if (orders.Count != pagination.ResultCount) throw InvalidCurrentSellsPagination();
            return new Gw2CurrentSellsPage(page, pageSize, pagination.PageCount, pagination.TotalCount, orders);
        }
        catch (IOException)
        {
            throw InvalidCurrentSellsResponse();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new Gw2ConfigurationException("GW2 current-sells request timed out. Try again later.");
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

    public async Task<Gw2PublicItems> GetPublicItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count is 0 or > MaximumPublicItemBatchSize
            || itemIds.Any(id => id <= 0)
            || itemIds.Distinct().Count() != itemIds.Count)
        {
            throw new ArgumentException("Item IDs must contain 1 to 100 unique positive values.", nameof(itemIds));
        }

        using var response = await SendWithSingleRetryAsync(
            $"/v2/items?ids={Uri.EscapeDataString(string.Join(',', itemIds))}",
            cancellationToken,
            authenticated: false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new Gw2PublicItems([], itemIds.ToArray(), []);
        }

        if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent)
        {
            throw new Gw2ConfigurationException($"GW2 public item request failed with HTTP {(int)response.StatusCode}. Try again later.");
        }

        var itemRows = await DeserializePublicItemsAsync(response, itemIds.ToHashSet(), cancellationToken);
        var itemsById = itemRows.ToDictionary(item => item.Id);
        var missingItemIds = itemIds.Where(id => !itemsById.ContainsKey(id)).ToArray();
        if ((response.StatusCode == HttpStatusCode.OK && missingItemIds.Length != 0)
            || (response.StatusCode == HttpStatusCode.PartialContent && (itemRows.Count == 0 || missingItemIds.Length == 0)))
        {
            throw InvalidPublicItemResponse();
        }

        return new Gw2PublicItems(
            itemIds.Where(itemsById.ContainsKey).Select(id => itemsById[id]).ToArray(),
            missingItemIds,
            itemRows.Any(item => item.Name is null)
                ? ["One or more returned public item names were blank and are represented as null."]
                : []);
    }

    public async Task<Gw2MaterialCategories> GetPublicMaterialCategoriesAsync(CancellationToken cancellationToken)
    {
        using var response = await SendWithSingleRetryAsync(
            "/v2/materials?ids=all",
            cancellationToken,
            authenticated: false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new Gw2ConfigurationException($"GW2 public material-category request failed with HTTP {(int)response.StatusCode}. Try again later.");
        }

        return new Gw2MaterialCategories(await DeserializeMaterialCategoriesAsync(response, cancellationToken));
    }

    public async Task<Gw2PublicRecipes> GetPublicRecipesAsync(IReadOnlyList<long> recipeIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeIds);
        if (recipeIds.Count is 0 or > MaximumPublicRecipeBatchSize
            || recipeIds.Any(id => id <= 0)
            || recipeIds.Distinct().Count() != recipeIds.Count)
        {
            throw new ArgumentException("Recipe IDs must contain 1 to 100 unique positive values.", nameof(recipeIds));
        }

        using var request = await SendRecipeWithSingleRetryAsync(
            $"/v2/recipes?ids={Uri.EscapeDataString(string.Join(',', recipeIds))}",
            cancellationToken,
            authenticated: false);
        var response = request.Response;
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new Gw2PublicRecipes([], recipeIds.ToArray());
        }

        if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent)
        {
            throw new Gw2ConfigurationException($"GW2 public recipe request failed with HTTP {(int)response.StatusCode}. Try again later.");
        }

        var recipes = await DeserializePublicRecipesAsync(response, recipeIds.ToHashSet(), request.CancellationToken);
        var recipesById = recipes.ToDictionary(recipe => recipe.Id);
        var missingRecipeIds = recipeIds.Where(id => !recipesById.ContainsKey(id)).ToArray();
        if ((response.StatusCode == HttpStatusCode.OK && missingRecipeIds.Length != 0)
            || (response.StatusCode == HttpStatusCode.PartialContent && (recipes.Count == 0 || missingRecipeIds.Length == 0)))
        {
            throw InvalidPublicRecipeResponse();
        }

        return new Gw2PublicRecipes(
            recipeIds.Where(recipesById.ContainsKey).Select(id => recipesById[id]).ToArray(),
            missingRecipeIds);
    }

    public Task<Gw2RecipeSelector> SearchPublicRecipesByInputItemAsync(long itemId, CancellationToken cancellationToken) =>
        SearchPublicRecipesAsync("input", itemId, cancellationToken);

    public Task<Gw2RecipeSelector> SearchPublicRecipesByOutputItemAsync(long itemId, CancellationToken cancellationToken) =>
        SearchPublicRecipesAsync("output", itemId, cancellationToken);

    private async Task<Gw2RecipeSelector> SearchPublicRecipesAsync(string selector, long itemId, CancellationToken cancellationToken)
    {
        if (itemId <= 0) throw new ArgumentOutOfRangeException(nameof(itemId), "Item ID must be positive.");
        using var request = await SendRecipeWithSingleRetryAsync(
            $"/v2/recipes/search?{selector}={itemId.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken,
            authenticated: false);
        var response = request.Response;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new Gw2ConfigurationException($"GW2 public recipe selector request failed with HTTP {(int)response.StatusCode}. Try again later.");
        }

        return new Gw2RecipeSelector(await DeserializeRecipeSelectorAsync(response, request.CancellationToken));
    }

    public async Task<Gw2AccountRecipeUnlocks> GetAccountRecipeUnlocksAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        }

        await ValidatePermissionsAsync(["account", "unlocks"], cancellationToken);
        using var request = await SendRecipeWithSingleRetryAsync(
            "/v2/account/recipes",
            cancellationToken,
            authenticated: true);
        var response = request.Response;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw InvalidKey();
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new Gw2ConfigurationException($"GW2 account recipe-unlock request failed with HTTP {(int)response.StatusCode}. Try again later.");
        }

        return new Gw2AccountRecipeUnlocks(await DeserializeAccountRecipeUnlocksAsync(response, request.CancellationToken));
    }

    public async Task<Gw2LegendaryArmory> GetLegendaryArmoryAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        }

        await ValidatePermissionsAsync(["account", "inventories", "unlocks"], cancellationToken);
        using var response = await SendWithSingleRetryAsync("/v2/account/legendaryarmory", cancellationToken);
        EnsureAuthenticatedOk(response, "Legendary Armory");
        var ownership = await DeserializeLegendaryArmoryAsync(response, cancellationToken);
        var metadata = await ResolveLegendaryArmoryItemsAsync(ownership.Select(entry => entry.Id).ToArray(), cancellationToken);
        var warnings = ownership.Where(entry => !metadata.ContainsKey(entry.Id))
            .Select(entry => new Gw2MetadataWarning("metadata_unresolved", "items", entry.Id.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        return new Gw2LegendaryArmory(
            ownership.Select(entry => metadata.TryGetValue(entry.Id, out var item)
                ? new Gw2LegendaryArmoryEntry(entry.Id, entry.Count, item.Name, item.Type, item.Subtype, item.WeightClass)
                : new Gw2LegendaryArmoryEntry(entry.Id, entry.Count, null, null, null, null)).ToArray(),
            warnings.Length == 0,
            warnings);
    }

    public async Task<Gw2AccountAchievementProgress> GetAccountAchievementProgressAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        try
        {
            await ValidatePermissionsAsync(["account", "progression"], cancellationToken);
            using var request = await SendAchievementWithSingleRetryAsync("/v2/account/achievements", cancellationToken, authenticated: true);
            if (request.Response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw InvalidKey();
            if (request.Response.StatusCode != HttpStatusCode.OK) throw new Gw2ConfigurationException($"GW2 account achievement-progress request failed with HTTP {(int)request.Response.StatusCode}. Try again later.");
            return new Gw2AccountAchievementProgress(await DeserializeAccountAchievementProgressAsync(request.Response, request.CancellationToken));
        }
        catch (IOException) { throw InvalidAccountAchievementProgressResponse(); }
        catch (JsonException) { throw InvalidAccountAchievementProgressResponse(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new Gw2ConfigurationException("GW2 account achievement-progress request timed out. Try again later."); }
    }

    public async Task<Gw2PublicAchievements> GetPublicAchievementsAsync(IReadOnlyList<long> achievementIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(achievementIds);
        if (achievementIds.Count is 0 or > 20 || achievementIds.Any(id => id <= 0) || achievementIds.Distinct().Count() != achievementIds.Count)
        {
            throw new ArgumentException("Achievement IDs must contain 1 to 20 unique positive values.", nameof(achievementIds));
        }

        try
        {
            using var request = await SendAchievementWithSingleRetryAsync($"/v2/achievements?ids={Uri.EscapeDataString(string.Join(',', achievementIds))}", cancellationToken, authenticated: false);
            if (request.Response.StatusCode == HttpStatusCode.NotFound)
            {
                await ReadAchievementBodyAsync(request.Response.Content, MaximumPublicAchievementPayloadBytes, InvalidPublicAchievementResponse, request.CancellationToken);
                return new Gw2PublicAchievements([], achievementIds.ToArray());
            }
            if (request.Response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent)
            {
                throw new Gw2ConfigurationException($"GW2 public achievement-definition request failed with HTTP {(int)request.Response.StatusCode}. Try again later.");
            }

            var rows = await DeserializePublicAchievementsAsync(request.Response, achievementIds.ToHashSet(), request.CancellationToken);
            var byId = rows.ToDictionary(row => row.Id);
            var missing = achievementIds.Where(id => !byId.ContainsKey(id)).ToArray();
            if ((request.Response.StatusCode == HttpStatusCode.OK && missing.Length != 0)
                || (request.Response.StatusCode == HttpStatusCode.PartialContent && (rows.Count == 0 || missing.Length == 0)))
            {
                throw InvalidPublicAchievementResponse();
            }

            return new Gw2PublicAchievements(achievementIds.Where(byId.ContainsKey).Select(id => byId[id]).ToArray(), missing);
        }
        catch (IOException) { throw InvalidPublicAchievementResponse(); }
        catch (JsonException) { throw InvalidPublicAchievementResponse(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new Gw2ConfigurationException("GW2 public achievement-definition request timed out. Try again later."); }
    }

    public async Task<Gw2AccountMasterySources> GetAccountMasterySourcesAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");
        try
        {
            using var permissionsRequest = await SendAchievementWithSingleRetryAsync("/v2/tokeninfo", cancellationToken, authenticated: true);
            if (permissionsRequest.Response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw InvalidKey();
            if (!permissionsRequest.Response.IsSuccessStatusCode)
            {
                throw new Gw2ConfigurationException($"GW2 key validation failed with HTTP {(int)permissionsRequest.Response.StatusCode}. Try again later.");
            }

            var tokenInfo = await DeserializeTokenInfoAsync(permissionsRequest.Response, permissionsRequest.CancellationToken);
            foreach (var requiredPermission in new[] { "account", "progression" })
            {
                if (!tokenInfo.Permissions!.Contains(requiredPermission, StringComparer.OrdinalIgnoreCase))
                {
                    throw new Gw2ConfigurationException($"GW2_API_KEY is missing the required {requiredPermission} permission. Create a key with the {requiredPermission} permission.");
                }
            }

            using var masteriesRequest = await SendAchievementWithSingleRetryAsync("/v2/account/masteries", cancellationToken, authenticated: true);
            EnsureMasteryAuthenticatedOk(masteriesRequest.Response, "account masteries");
            var tracks = await DeserializeAccountMasteriesAsync(masteriesRequest.Response, masteriesRequest.CancellationToken);
            var masteriesAsOf = (timeProvider ?? TimeProvider.System).GetUtcNow();
            using var pointsRequest = await SendAchievementWithSingleRetryAsync("/v2/account/mastery/points", cancellationToken, authenticated: true);
            EnsureMasteryAuthenticatedOk(pointsRequest.Response, "mastery points");
            var pointTotals = await DeserializeMasteryPointsAsync(pointsRequest.Response, pointsRequest.CancellationToken);
            return new Gw2AccountMasterySources(tracks, pointTotals, masteriesAsOf, (timeProvider ?? TimeProvider.System).GetUtcNow());
        }
        catch (IOException) { throw InvalidAccountMasteryResponse(); }
        catch (JsonException) { throw InvalidAccountMasteryResponse(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new Gw2ConfigurationException("GW2 account mastery request timed out. Try again later."); }
    }

    public async Task<Gw2PublicMasteries> GetPublicMasteriesAsync(IReadOnlyList<long> masteryIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(masteryIds);
        if (masteryIds.Count is 0 or > MaximumPublicMasteryBatchSize || masteryIds.Any(id => id <= 0) || masteryIds.Distinct().Count() != masteryIds.Count)
        {
            throw new ArgumentException("Mastery IDs must contain 1 to 200 unique positive values.", nameof(masteryIds));
        }

        try
        {
            using var request = await SendAchievementWithSingleRetryAsync($"/v2/masteries?ids={Uri.EscapeDataString(string.Join(',', masteryIds))}", cancellationToken, authenticated: false);
            if (request.Response.StatusCode == HttpStatusCode.NotFound)
            {
                await ReadAchievementBodyAsync(request.Response.Content, MaximumPublicMasteryPayloadBytes, InvalidPublicMasteryResponse, request.CancellationToken);
                return new Gw2PublicMasteries([], masteryIds.ToArray());
            }
            if (request.Response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent)
            {
                throw new Gw2ConfigurationException($"GW2 public mastery request failed with HTTP {(int)request.Response.StatusCode}. Try again later.");
            }

            var rows = await DeserializePublicMasteriesAsync(request.Response, masteryIds.ToHashSet(), request.CancellationToken);
            var byId = rows.ToDictionary(row => row.Id);
            var missing = masteryIds.Where(id => !byId.ContainsKey(id)).ToArray();
            if ((request.Response.StatusCode == HttpStatusCode.OK && missing.Length != 0)
                || (request.Response.StatusCode == HttpStatusCode.PartialContent && (rows.Count == 0 || missing.Length == 0)))
            {
                throw InvalidPublicMasteryResponse();
            }
            return new Gw2PublicMasteries(masteryIds.Where(byId.ContainsKey).Select(id => byId[id]).ToArray(), missing);
        }
        catch (IOException) { throw InvalidPublicMasteryResponse(); }
        catch (JsonException) { throw InvalidPublicMasteryResponse(); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new Gw2ConfigurationException("GW2 public mastery request timed out. Try again later."); }
    }

    private async Task<IReadOnlyList<Gw2AccountMasteryTrack>> DeserializeAccountMasteriesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await ReadAchievementBodyAsync(response.Content, MaximumAccountMasteryPayloadBytes, InvalidAccountMasteryResponse, cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > MaximumAccountMasteryRows) throw InvalidAccountMasteryResponse();
        var ids = new HashSet<long>();
        var tracks = new List<Gw2AccountMasteryTrack>();
        foreach (var value in document.RootElement.EnumerateArray())
        {
            var row = MasteryObject(value, InvalidAccountMasteryResponse);
            var id = MasteryPositiveLong(row, "id", InvalidAccountMasteryResponse);
            if (!ids.Add(id)) throw InvalidAccountMasteryResponse();
            tracks.Add(new Gw2AccountMasteryTrack(id, MasteryOptionalNonnegativeLong(row, "level", InvalidAccountMasteryResponse)));
        }
        return tracks;
    }

    private async Task<IReadOnlyList<Gw2MasteryPointTotal>> DeserializeMasteryPointsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await ReadAchievementBodyAsync(response.Content, MaximumMasteryPointsPayloadBytes, InvalidMasteryPointsResponse, cancellationToken));
        var root = MasteryObject(document.RootElement, InvalidMasteryPointsResponse);
        var totals = MasteryArray(root, "totals", InvalidMasteryPointsResponse);
        var unlocked = MasteryArray(root, "unlocked", InvalidMasteryPointsResponse);
        if (totals.GetArrayLength() > MaximumMasteryPointTotals || unlocked.GetArrayLength() > MaximumMasteryUnlockedEntries) throw InvalidMasteryPointsResponse();
        foreach (var entry in unlocked.EnumerateArray()) if (entry.ValueKind != JsonValueKind.Number || !entry.TryGetInt64(out var id) || id <= 0) throw InvalidMasteryPointsResponse();
        var regions = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Gw2MasteryPointTotal>();
        foreach (var value in totals.EnumerateArray())
        {
            var row = MasteryObject(value, InvalidMasteryPointsResponse);
            var region = MasteryRequiredString(row, "region", false, 64, InvalidMasteryPointsResponse);
            if (!regions.Add(region)) throw InvalidMasteryPointsResponse();
            result.Add(new Gw2MasteryPointTotal(region, MasteryNonnegativeLong(row, "spent", InvalidMasteryPointsResponse), MasteryNonnegativeLong(row, "earned", InvalidMasteryPointsResponse)));
        }
        return result;
    }

    private async Task<IReadOnlyList<Gw2PublicMastery>> DeserializePublicMasteriesAsync(HttpResponseMessage response, IReadOnlySet<long> requestedIds, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await ReadAchievementBodyAsync(response.Content, MaximumPublicMasteryPayloadBytes, InvalidPublicMasteryResponse, cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw InvalidPublicMasteryResponse();
        var ids = new HashSet<long>();
        var levelsTotal = 0;
        var rows = new List<Gw2PublicMastery>();
        foreach (var value in document.RootElement.EnumerateArray())
        {
            var row = MasteryObject(value, InvalidPublicMasteryResponse);
            var id = MasteryPositiveLong(row, "id", InvalidPublicMasteryResponse);
            if (!requestedIds.Contains(id) || !ids.Add(id)) throw InvalidPublicMasteryResponse();
            var levels = MasteryArray(row, "levels", InvalidPublicMasteryResponse);
            levelsTotal = checked(levelsTotal + levels.GetArrayLength());
            if (levelsTotal > MaximumPublicMasteryLevels) throw InvalidPublicMasteryResponse();
            rows.Add(new Gw2PublicMastery(id,
                MasteryRequiredString(row, "name", false, 256, InvalidPublicMasteryResponse),
                MasteryRequiredString(row, "requirement", true, 2048, InvalidPublicMasteryResponse),
                MasteryRequiredString(row, "region", false, 64, InvalidPublicMasteryResponse),
                MasteryLong(row, "order", InvalidPublicMasteryResponse),
                levels.EnumerateArray().Select(level => ParsePublicMasteryLevel(level)).ToArray()));
        }
        return rows;
    }

    private static Gw2PublicMasteryLevel ParsePublicMasteryLevel(JsonElement value)
    {
        var row = MasteryObject(value, InvalidPublicMasteryResponse);
        return new Gw2PublicMasteryLevel(
            MasteryRequiredString(row, "name", false, 256, InvalidPublicMasteryResponse),
            MasteryRequiredString(row, "description", true, 4096, InvalidPublicMasteryResponse),
            MasteryRequiredString(row, "instruction", true, 4096, InvalidPublicMasteryResponse),
            MasteryNonnegativeLong(row, "point_cost", InvalidPublicMasteryResponse),
            MasteryNonnegativeLong(row, "exp_cost", InvalidPublicMasteryResponse));
    }

    private static void EnsureMasteryAuthenticatedOk(HttpResponseMessage response, string source)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw InvalidKey();
        if (response.StatusCode != HttpStatusCode.OK) throw new Gw2ConfigurationException($"GW2 {source} request failed with HTTP {(int)response.StatusCode}. Try again later.");
    }

    private static JsonElement MasteryObject(JsonElement value, Func<Gw2ConfigurationException> invalid) => value.ValueKind == JsonValueKind.Object ? value : throw invalid();
    private static bool TryMasteryProperty(JsonElement value, string name, Func<Gw2ConfigurationException> invalid, out JsonElement property)
    {
        var properties = value.EnumerateObject().Where(candidate => candidate.NameEquals(name)).ToArray();
        if (properties.Length > 1) throw invalid();
        property = properties.Length == 1 ? properties[0].Value : default;
        return properties.Length == 1;
    }
    private static JsonElement MasteryProperty(JsonElement value, string name, Func<Gw2ConfigurationException> invalid) => TryMasteryProperty(value, name, invalid, out var property) ? property : throw invalid();
    private static JsonElement MasteryArray(JsonElement value, string name, Func<Gw2ConfigurationException> invalid) => MasteryProperty(value, name, invalid).ValueKind == JsonValueKind.Array ? MasteryProperty(value, name, invalid) : throw invalid();
    private static long MasteryLong(JsonElement value, string name, Func<Gw2ConfigurationException> invalid) => MasteryProperty(value, name, invalid).ValueKind == JsonValueKind.Number && MasteryProperty(value, name, invalid).TryGetInt64(out var number) ? number : throw invalid();
    private static long MasteryPositiveLong(JsonElement value, string name, Func<Gw2ConfigurationException> invalid) => MasteryLong(value, name, invalid) is var number && number > 0 ? number : throw invalid();
    private static long MasteryNonnegativeLong(JsonElement value, string name, Func<Gw2ConfigurationException> invalid) => MasteryLong(value, name, invalid) is var number && number >= 0 ? number : throw invalid();
    private static long? MasteryOptionalNonnegativeLong(JsonElement value, string name, Func<Gw2ConfigurationException> invalid)
    {
        if (!TryMasteryProperty(value, name, invalid, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) && number >= 0 ? number : throw invalid();
    }
    private static string MasteryRequiredString(JsonElement value, string name, bool allowBlank, int maximumLength, Func<Gw2ConfigurationException> invalid)
    {
        var property = MasteryProperty(value, name, invalid);
        return property.ValueKind == JsonValueKind.String && property.GetString() is { } text && text.Length <= maximumLength && (allowBlank || !string.IsNullOrWhiteSpace(text)) ? text : throw invalid();
    }

    private async Task<IReadOnlyList<Gw2AccountAchievementProgressEntry>> DeserializeAccountAchievementProgressAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await ReadAchievementBodyAsync(response.Content, MaximumAccountAchievementPayloadBytes, InvalidAccountAchievementProgressResponse, cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > MaximumAccountAchievementRows) throw InvalidAccountAchievementProgressResponse();
        var ids = new HashSet<long>();
        var rows = new List<Gw2AccountAchievementProgressEntry>();
        foreach (var value in document.RootElement.EnumerateArray())
        {
            var row = AchievementObject(value, InvalidAccountAchievementProgressResponse);
            var id = AchievementPositiveLong(row, "id", InvalidAccountAchievementProgressResponse);
            if (!ids.Add(id)) throw InvalidAccountAchievementProgressResponse();
            var done = AchievementBoolean(row, "done", InvalidAccountAchievementProgressResponse);
            var current = AchievementOptionalNonnegativeLong(row, "current", InvalidAccountAchievementProgressResponse);
            var max = AchievementOptionalNonnegativeLong(row, "max", InvalidAccountAchievementProgressResponse);
            var repeated = AchievementOptionalNonnegativeLong(row, "repeated", InvalidAccountAchievementProgressResponse);
            var unlocked = AchievementOptionalBoolean(row, "unlocked", true, InvalidAccountAchievementProgressResponse);
            IReadOnlyList<long>? bits = null;
            if (TryAchievementProperty(row, "bits", InvalidAccountAchievementProgressResponse, out var bitsValue))
            {
                if (bitsValue.ValueKind != JsonValueKind.Array) throw InvalidAccountAchievementProgressResponse();
                bits = bitsValue.EnumerateArray().Select(bit => bit.ValueKind == JsonValueKind.Number && bit.TryGetInt64(out var index) && index >= 0 ? index : throw InvalidAccountAchievementProgressResponse()).ToArray();
            }
            rows.Add(new Gw2AccountAchievementProgressEntry(id, current, max, done, repeated, unlocked, bits));
        }
        return rows;
    }

    private async Task<IReadOnlyList<Gw2PublicAchievement>> DeserializePublicAchievementsAsync(HttpResponseMessage response, IReadOnlySet<long> requestedIds, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await ReadAchievementBodyAsync(response.Content, MaximumPublicAchievementPayloadBytes, InvalidPublicAchievementResponse, cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw InvalidPublicAchievementResponse();
        var rows = new List<Gw2PublicAchievement>();
        var ids = new HashSet<long>();
        foreach (var value in document.RootElement.EnumerateArray())
        {
            var row = AchievementObject(value, InvalidPublicAchievementResponse);
            var id = AchievementPositiveLong(row, "id", InvalidPublicAchievementResponse);
            if (!requestedIds.Contains(id) || !ids.Add(id)) throw InvalidPublicAchievementResponse();
            var name = AchievementRequiredString(row, "name", false, InvalidPublicAchievementResponse);
            var type = AchievementRequiredString(row, "type", false, InvalidPublicAchievementResponse);
            var description = AchievementOptionalString(row, "description", true, InvalidPublicAchievementResponse);
            var requirement = AchievementOptionalString(row, "requirement", true, InvalidPublicAchievementResponse);
            var lockedText = AchievementOptionalString(row, "locked_text", true, InvalidPublicAchievementResponse);
            var flags = AchievementOptionalStringArray(row, "flags", InvalidPublicAchievementResponse);
            IReadOnlyList<Gw2AchievementBit>? bits = null;
            if (TryAchievementProperty(row, "bits", InvalidPublicAchievementResponse, out var bitsValue))
            {
                if (bitsValue.ValueKind != JsonValueKind.Array) throw InvalidPublicAchievementResponse();
                bits = bitsValue.EnumerateArray().Select(bit => ParseAchievementBit(bit)).ToArray();
            }
            rows.Add(new Gw2PublicAchievement(id, name, description, requirement, lockedText, type, flags, bits));
        }
        return rows;
    }

    private static Gw2AchievementBit ParseAchievementBit(JsonElement value)
    {
        var bit = AchievementObject(value, InvalidPublicAchievementResponse);
        var type = AchievementOptionalString(bit, "type", true, InvalidPublicAchievementResponse);
        var id = AchievementOptionalNonnegativeLong(bit, "id", InvalidPublicAchievementResponse);
        var text = AchievementOptionalString(bit, "text", true, InvalidPublicAchievementResponse);
        return new Gw2AchievementBit(type, id, text);
    }

    private static async Task<byte[]> ReadAchievementBodyAsync(HttpContent content, int maximumBytes, Func<Gw2ConfigurationException> invalid, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes) throw invalid();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var total = 0;
        for (var read = await stream.ReadAsync(chunk, cancellationToken); read != 0; read = await stream.ReadAsync(chunk, cancellationToken))
        {
            total = checked(total + read);
            if (total > maximumBytes) throw invalid();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }

    private static JsonElement AchievementObject(JsonElement value, Func<Gw2ConfigurationException> invalid) => value.ValueKind == JsonValueKind.Object ? value : throw invalid();
    private static bool TryAchievementProperty(JsonElement value, string name, Func<Gw2ConfigurationException> invalid, out JsonElement property)
    {
        var matches = value.EnumerateObject().Where(candidate => candidate.NameEquals(name)).ToArray();
        if (matches.Length > 1) throw invalid();
        property = matches.Length == 1 ? matches[0].Value : default;
        return matches.Length == 1;
    }
    private static JsonElement AchievementProperty(JsonElement value, string name, Func<Gw2ConfigurationException> invalid) => TryAchievementProperty(value, name, invalid, out var property) ? property : throw invalid();
    private static long AchievementPositiveLong(JsonElement value, string name, Func<Gw2ConfigurationException> invalid) => AchievementProperty(value, name, invalid).ValueKind == JsonValueKind.Number && AchievementProperty(value, name, invalid).TryGetInt64(out var number) && number > 0 ? number : throw invalid();
    private static long? AchievementOptionalNonnegativeLong(JsonElement value, string name, Func<Gw2ConfigurationException> invalid)
    {
        if (!TryAchievementProperty(value, name, invalid, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) && number >= 0 ? number : throw invalid();
    }
    private static bool AchievementBoolean(JsonElement value, string name, Func<Gw2ConfigurationException> invalid) => AchievementProperty(value, name, invalid).ValueKind is JsonValueKind.True ? true : AchievementProperty(value, name, invalid).ValueKind is JsonValueKind.False ? false : throw invalid();
    private static bool AchievementOptionalBoolean(JsonElement value, string name, bool absentValue, Func<Gw2ConfigurationException> invalid)
    {
        if (!TryAchievementProperty(value, name, invalid, out var property)) return absentValue;
        return property.ValueKind is JsonValueKind.True ? true : property.ValueKind is JsonValueKind.False ? false : throw invalid();
    }
    private static string AchievementRequiredString(JsonElement value, string name, bool allowBlank, Func<Gw2ConfigurationException> invalid)
    {
        var property = AchievementProperty(value, name, invalid);
        return property.ValueKind == JsonValueKind.String && (allowBlank || !string.IsNullOrWhiteSpace(property.GetString())) ? property.GetString()! : throw invalid();
    }
    private static string? AchievementOptionalString(JsonElement value, string name, bool allowBlank, Func<Gw2ConfigurationException> invalid)
    {
        if (!TryAchievementProperty(value, name, invalid, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        return property.ValueKind == JsonValueKind.String && (allowBlank || !string.IsNullOrWhiteSpace(property.GetString())) ? property.GetString() : throw invalid();
    }
    private static IReadOnlyList<string> AchievementOptionalStringArray(JsonElement value, string name, Func<Gw2ConfigurationException> invalid)
    {
        if (!TryAchievementProperty(value, name, invalid, out var property)) return [];
        if (property.ValueKind != JsonValueKind.Array) throw invalid();
        return property.EnumerateArray().Select(entry => entry.ValueKind == JsonValueKind.String ? entry.GetString()! : throw invalid()).ToArray();
    }

    private async Task<IReadOnlyList<LegendaryArmoryOwnership>> DeserializeLegendaryArmoryAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > MaximumLegendaryArmoryRows)
            {
                throw InvalidLegendaryArmoryResponse();
            }

            var ownership = new List<LegendaryArmoryOwnership>();
            var ids = new HashSet<long>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) throw InvalidLegendaryArmoryResponse();
                if (element.EnumerateObject().GroupBy(property => property.Name).Any(group => group.Count() > 1)) throw InvalidLegendaryArmoryResponse();
                var id = LegendaryArmoryLong(element, "id");
                var count = LegendaryArmoryLong(element, "count");
                if (id <= 0 || count < 0 || !ids.Add(id)) throw InvalidLegendaryArmoryResponse();
                ownership.Add(new LegendaryArmoryOwnership(id, count));
            }

            return ownership.OrderBy(entry => entry.Id).ToArray();
        }
        catch (JsonException)
        {
            throw InvalidLegendaryArmoryResponse();
        }
    }

    private async Task<Dictionary<long, LegendaryArmoryItemMetadata>> ResolveLegendaryArmoryItemsAsync(IReadOnlyList<long> requestedIds, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<long, LegendaryArmoryItemMetadata>();
        foreach (var chunk in requestedIds.Chunk(MaximumItemBatchSize))
        {
            try
            {
                using var response = await SendWithSingleRetryAsync(
                    $"/v2/items?ids={Uri.EscapeDataString(string.Join(',', chunk))}",
                    cancellationToken,
                    authenticated: false);
                if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent) continue;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.ValueKind != JsonValueKind.Array) continue;

                var rows = new Dictionary<long, LegendaryArmoryItemMetadata>();
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) throw InvalidLegendaryArmoryMetadataResponse();
                    var id = LegendaryArmoryLong(element, "id");
                    var name = LegendaryArmoryString(element, "name");
                    var type = LegendaryArmoryString(element, "type");
                    if (id <= 0 || !chunk.Contains(id) || !rows.TryAdd(id, ParseLegendaryArmoryItemMetadata(element, id, name, type)))
                    {
                        throw InvalidLegendaryArmoryMetadataResponse();
                    }
                }

                if ((response.StatusCode == HttpStatusCode.OK && rows.Count != chunk.Length)
                    || (response.StatusCode == HttpStatusCode.PartialContent && (rows.Count == 0 || rows.Count == chunk.Length)))
                {
                    continue;
                }

                foreach (var row in rows) resolved.Add(row.Key, row.Value);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        return resolved;
    }

    private static LegendaryArmoryItemMetadata ParseLegendaryArmoryItemMetadata(JsonElement item, long id, string name, string type)
    {
        string? subtype = null;
        string? weightClass = null;
        if (TryLegendaryArmoryProperty(item, "details", out var detailsValue))
        {
            if (detailsValue.ValueKind != JsonValueKind.Object) throw InvalidLegendaryArmoryMetadataResponse();
            if (TryLegendaryArmoryProperty(detailsValue, "type", out var subtypeValue))
            {
                subtype = LegendaryArmoryStringValue(subtypeValue);
            }

            if (TryLegendaryArmoryProperty(detailsValue, "weight_class", out var weightClassValue))
            {
                var suppliedWeightClass = LegendaryArmoryStringValue(weightClassValue);
                if (type == "Armor") weightClass = suppliedWeightClass;
            }
        }

        return new LegendaryArmoryItemMetadata(id, name, type, subtype, weightClass);
    }

    private static long LegendaryArmoryLong(JsonElement objectElement, string name)
    {
        var value = LegendaryArmoryRequiredProperty(objectElement, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) throw InvalidLegendaryArmoryResponse();
        return number;
    }

    private static string LegendaryArmoryString(JsonElement objectElement, string name) =>
        LegendaryArmoryStringValue(LegendaryArmoryRequiredProperty(objectElement, name));

    private static string LegendaryArmoryStringValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw InvalidLegendaryArmoryMetadataResponse();
        return value.GetString()!;
    }

    private static JsonElement LegendaryArmoryRequiredProperty(JsonElement objectElement, string name)
    {
        var properties = objectElement.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        return properties.Length == 1 ? properties[0].Value : throw InvalidLegendaryArmoryResponse();
    }

    private static bool TryLegendaryArmoryProperty(JsonElement objectElement, string name, out JsonElement value)
    {
        var properties = objectElement.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (properties.Length > 1) throw InvalidLegendaryArmoryMetadataResponse();
        if (properties.Length == 0)
        {
            value = default;
            return false;
        }

        value = properties[0].Value;
        return true;
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

    private async Task ValidateEquipmentTabsPermissionsAsync(IReadOnlyList<string> requiredPermissions, CancellationToken cancellationToken)
    {
        using var request = await SendEquipmentTabsWithSingleRetryAsync("/v2/tokeninfo", cancellationToken, authenticated: true);
        if (request.Response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw InvalidKey();
        if (request.Response.StatusCode != HttpStatusCode.OK)
            throw new Gw2ConfigurationException($"GW2 key validation failed with HTTP {(int)request.Response.StatusCode}. Try again later.");

        var tokenInfo = await DeserializeTokenInfoAsync(request.Response, request.CancellationToken);
        foreach (var permission in requiredPermissions)
        {
            if (!tokenInfo.Permissions!.Contains(permission, StringComparer.OrdinalIgnoreCase))
                throw new Gw2ConfigurationException($"GW2_API_KEY is missing the required {permission} permission. Create a key with the {permission} permission.");
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

    private async Task<SelectedCharacterInventoryPayload> DeserializeSelectedCharacterInventoryAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = InventoryObject(document.RootElement);
            var bagsArray = InventoryArray(root, "bags");
            if (bagsArray.GetArrayLength() > options.Limits.MaxBagPositions) throw InvalidCharacterInventoryResponse();
            var bags = new List<AuthenticatedInventoryBag?>();
            long totalSlots = 0;
            long references = 0;
            long attributes = 0;
            foreach (var bagElement in bagsArray.EnumerateArray())
            {
                if (bagElement.ValueKind == JsonValueKind.Null) { bags.Add(null); continue; }
                var bag = InventoryObject(bagElement);
                var id = InventoryPositiveLong(bag, "id");
                var size = InventoryPositiveInt(bag, "size");
                var slotsArray = InventoryArray(bag, "inventory");
                if (slotsArray.GetArrayLength() != size || slotsArray.GetArrayLength() > options.Limits.MaxSlotsPerBag) throw InvalidCharacterInventoryResponse();
                totalSlots = checked(totalSlots + slotsArray.GetArrayLength());
                if (totalSlots > options.Limits.MaxTotalSlots) throw InvalidCharacterInventoryResponse();
                references = checked(references + 1);
                var slots = new List<AuthenticatedInventoryStack?>();
                foreach (var stackElement in slotsArray.EnumerateArray())
                {
                    if (stackElement.ValueKind == JsonValueKind.Null) { slots.Add(null); continue; }
                    var stack = ParseInventoryStack(InventoryObject(stackElement));
                    references = checked(references + 1 + stack.Upgrades.Count + stack.Infusions.Count);
                    attributes = checked(attributes + (stack.SelectedAttributes?.Count ?? 0));
                    slots.Add(stack);
                }
                bags.Add(new AuthenticatedInventoryBag(id, size, slots));
            }
            if (references > options.Limits.MaxItemReferences || attributes > options.Limits.MaxStatAttributes) throw CharacterInventoryContextLimitExceeded();
            return new SelectedCharacterInventoryPayload(bags);
        }
        catch (JsonException) { throw InvalidCharacterInventoryResponse(); }
        catch (OverflowException) { throw InvalidCharacterInventoryResponse(); }
    }

    private static AuthenticatedInventoryStack ParseInventoryStack(JsonElement stack)
    {
        var itemId = InventoryPositiveLong(stack, "id");
        var count = InventoryLong(stack, "count");
        if (count is < 1 or > 250) throw InvalidCharacterInventoryResponse();
        var charges = InventoryOptionalNonnegativeInt(stack, "charges");
        var upgrades = InventoryOptionalPositiveLongArray(stack, "upgrades");
        var infusions = InventoryOptionalPositiveLongArray(stack, "infusions");
        if (upgrades.Count > 16 || infusions.Count > 16) throw InvalidCharacterInventoryResponse();
        var skinId = InventoryOptionalPositiveLong(stack, "skin");
        var binding = InventoryOptionalNullableString(stack, "binding");
        var boundTo = InventoryOptionalNullableString(stack, "bound_to");
        if ((binding == "Character" && boundTo is null) || ((binding is null or "Account") && boundTo is not null)) throw InvalidCharacterInventoryResponse();
        long? selectedStatId = null;
        IReadOnlyList<Gw2InventoryStatAttribute>? selectedAttributes = null;
        if (TryInventoryProperty(stack, "stats", out var stats))
        {
            var statObject = InventoryObject(stats);
            selectedStatId = InventoryPositiveLong(statObject, "id");
            var attributes = InventoryObject(InventoryProperty(statObject, "attributes"));
            var rows = new List<Gw2InventoryStatAttribute>();
            foreach (var attribute in attributes.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(attribute.Name) || attribute.Value.ValueKind != JsonValueKind.Number || !attribute.Value.TryGetInt32(out var value) || rows.Any(row => row.Name == attribute.Name)) throw InvalidCharacterInventoryResponse();
                rows.Add(new Gw2InventoryStatAttribute(attribute.Name, value));
            }
            if (rows.Count > 32) throw InvalidCharacterInventoryResponse();
            selectedAttributes = rows.Count == 0 ? null : rows.OrderBy(row => row.Name, StringComparer.Ordinal).ToArray();
        }
        return new AuthenticatedInventoryStack(itemId, count, charges, upgrades, infusions, skinId, binding, boundTo, selectedStatId, selectedAttributes);
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

    private static CurrentBuysPagination DeserializeCurrentBuysPagination(HttpResponseHeaders headers, int requestedPage, int requestedPageSize)
    {
        var pageSize = ParseCurrentBuysPaginationHeader(headers, "X-Page-Size");
        var pageCount = ParseCurrentBuysPaginationHeader(headers, "X-Page-Total");
        var resultCount = ParseCurrentBuysPaginationHeader(headers, "X-Result-Count");
        var totalCount = ParseCurrentBuysPaginationHeader(headers, "X-Result-Total");
        if (pageSize != requestedPageSize
            || pageCount > int.MaxValue
            || resultCount > MaximumCurrentBuysPageSize
            || (totalCount == 0 && (requestedPage != 0 || pageCount != 0 || resultCount != 0))
            || (totalCount > 0 && (pageCount == 0 || requestedPage >= pageCount
                || pageCount != checked(((totalCount - 1) / pageSize) + 1)
                || resultCount != (requestedPage == pageCount - 1
                    ? totalCount - ((pageCount - 1) * pageSize)
                    : pageSize))))
        {
            throw InvalidCurrentBuysPagination();
        }

        return new CurrentBuysPagination((int)pageCount, (int)resultCount, totalCount);
    }

    private static long ParseCurrentBuysPaginationHeader(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values)) throw InvalidCurrentBuysPagination();
        var valueArray = values.ToArray();
        if (valueArray.Length != 1
            || !long.TryParse(valueArray[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < 0)
        {
            throw InvalidCurrentBuysPagination();
        }

        return value;
    }

    private static CurrentSellsPagePagination DeserializeCurrentSellsPagePagination(HttpResponseHeaders headers, int requestedPage, int requestedPageSize)
    {
        var pageSize = ParseCurrentSellsPagePaginationHeader(headers, "X-Page-Size");
        var pageCount = ParseCurrentSellsPagePaginationHeader(headers, "X-Page-Total");
        var resultCount = ParseCurrentSellsPagePaginationHeader(headers, "X-Result-Count");
        var totalCount = ParseCurrentSellsPagePaginationHeader(headers, "X-Result-Total");
        if (pageSize != requestedPageSize
            || pageCount > int.MaxValue
            || resultCount > MaximumCurrentSellsPageSize
            || (totalCount == 0 && (requestedPage != 0 || pageCount != 0 || resultCount != 0))
            || (totalCount > 0 && (pageCount == 0 || requestedPage >= pageCount
                || pageCount != checked(((totalCount - 1) / pageSize) + 1)
                || resultCount != (requestedPage == pageCount - 1
                    ? totalCount - ((pageCount - 1) * pageSize)
                    : pageSize))))
        {
            throw InvalidCurrentSellsPagination();
        }

        return new CurrentSellsPagePagination((int)pageCount, (int)resultCount, totalCount);
    }

    private static long ParseCurrentSellsPagePaginationHeader(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values)) throw InvalidCurrentSellsPagination();
        var valueArray = values.ToArray();
        if (valueArray.Length != 1
            || !long.TryParse(valueArray[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < 0)
        {
            throw InvalidCurrentSellsPagination();
        }

        return value;
    }

    private async Task<IReadOnlyList<Gw2CurrentBuyOrder>> DeserializeCurrentBuysAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await ReadCurrentBuysBodyAsync(response.Content, cancellationToken);
            await using var stream = new MemoryStream(body, writable: false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > MaximumCurrentBuysPageSize) throw InvalidCurrentBuysResponse();

            var ids = new HashSet<long>();
            var orders = new List<Gw2CurrentBuyOrder>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) throw InvalidCurrentBuysResponse();
                var id = CurrentBuyLong(element, "id");
                var itemId = CurrentBuyLong(element, "item_id");
                var price = CurrentBuyLong(element, "price");
                var quantity = CurrentBuyLong(element, "quantity");
                var created = CurrentBuyDateTimeOffset(element, "created");
                if (id <= 0 || itemId <= 0 || price <= 0 || quantity <= 0 || created == default || !ids.Add(id)) throw InvalidCurrentBuysResponse();
                orders.Add(new Gw2CurrentBuyOrder(itemId, price, quantity, created));
            }

            return orders;
        }
        catch (JsonException)
        {
            throw InvalidCurrentBuysResponse();
        }
        catch (IOException)
        {
            throw InvalidCurrentBuysResponse();
        }
    }

    private static long CurrentBuyLong(JsonElement element, string name)
    {
        var values = element.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (values.Length != 1 || values[0].Value.ValueKind != JsonValueKind.Number || !values[0].Value.TryGetInt64(out var value)) throw InvalidCurrentBuysResponse();
        return value;
    }

    private static DateTimeOffset CurrentBuyDateTimeOffset(JsonElement element, string name)
    {
        var values = element.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (values.Length != 1 || values[0].Value.ValueKind != JsonValueKind.String || !values[0].Value.TryGetDateTimeOffset(out var value)) throw InvalidCurrentBuysResponse();
        return value;
    }

    private static async Task<byte[]> ReadCurrentBuysBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumCurrentBuysPayloadBytes) throw InvalidCurrentBuysResponse();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length > MaximumCurrentBuysPayloadBytes - read) throw InvalidCurrentBuysResponse();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private async Task<IReadOnlyList<Gw2CurrentSellPageOrder>> DeserializeCurrentSellsPageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await ReadCurrentSellsPageBodyAsync(response.Content, cancellationToken);
            await using var stream = new MemoryStream(body, writable: false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > MaximumCurrentSellsPageSize) throw InvalidCurrentSellsResponse();

            var ids = new HashSet<long>();
            var orders = new List<Gw2CurrentSellPageOrder>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) throw InvalidCurrentSellsResponse();
                var id = CurrentSellPageLong(element, "id");
                var itemId = CurrentSellPageLong(element, "item_id");
                var price = CurrentSellPageLong(element, "price");
                var quantity = CurrentSellPageLong(element, "quantity");
                var created = CurrentSellPageDateTimeOffset(element, "created");
                if (id <= 0 || itemId <= 0 || price <= 0 || quantity <= 0 || created == default || !ids.Add(id)) throw InvalidCurrentSellsResponse();
                orders.Add(new Gw2CurrentSellPageOrder(itemId, price, quantity, created));
            }

            return orders;
        }
        catch (JsonException)
        {
            throw InvalidCurrentSellsResponse();
        }
        catch (IOException)
        {
            throw InvalidCurrentSellsResponse();
        }
    }

    private static long CurrentSellPageLong(JsonElement element, string name)
    {
        var values = element.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (values.Length != 1 || values[0].Value.ValueKind != JsonValueKind.Number || !values[0].Value.TryGetInt64(out var value)) throw InvalidCurrentSellsResponse();
        return value;
    }

    private static DateTimeOffset CurrentSellPageDateTimeOffset(JsonElement element, string name)
    {
        var values = element.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (values.Length != 1 || values[0].Value.ValueKind != JsonValueKind.String || !values[0].Value.TryGetDateTimeOffset(out var value)) throw InvalidCurrentSellsResponse();
        return value;
    }

    private static async Task<byte[]> ReadCurrentSellsPageBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumCurrentSellsPayloadBytes) throw InvalidCurrentSellsResponse();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length > MaximumCurrentSellsPayloadBytes - read) throw InvalidCurrentSellsResponse();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
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

    private async Task<List<Gw2PublicItem>> DeserializePublicItemsAsync(
        HttpResponseMessage response,
        IReadOnlySet<long> requestedIds,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw InvalidPublicItemResponse();

            var items = new List<Gw2PublicItem>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) throw InvalidPublicItemResponse();
                var id = PublicItemLong(element, "id");
                var name = PublicItemString(element, "name", allowBlank: true);
                var type = PublicItemString(element, "type", allowBlank: false);
                var rarity = PublicItemString(element, "rarity", allowBlank: false);
                var level = PublicItemLong(element, "level");
                var vendorValue = PublicItemLong(element, "vendor_value");
                if (id <= 0 || level < 0 || vendorValue < 0 || !requestedIds.Contains(id)) throw InvalidPublicItemResponse();
                if (items.Any(item => item.Id == id)) throw InvalidPublicItemResponse();
                items.Add(new Gw2PublicItem(
                    id,
                    string.IsNullOrWhiteSpace(name) ? null : name,
                    type,
                    rarity,
                    level,
                    vendorValue,
                    PublicItemStrings(element, "flags"),
                    PublicItemStrings(element, "game_types"),
                    PublicItemStrings(element, "restrictions")));
            }

            return items;
        }
        catch (JsonException)
        {
            throw InvalidPublicItemResponse();
        }
    }

    private async Task<IReadOnlyList<Gw2MaterialCategory>> DeserializeMaterialCategoriesAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            if (response.Content.Headers.ContentLength is > MaximumMaterialCategoryPayloadBytes)
            {
                throw InvalidPublicMaterialCategoryResponse();
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var bytesRead = await responseStream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0) break;
                if (payload.Length + bytesRead > MaximumMaterialCategoryPayloadBytes)
                {
                    throw InvalidPublicMaterialCategoryResponse();
                }

                await payload.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }

            payload.Position = 0;
            using var document = await JsonDocument.ParseAsync(payload, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() > MaximumMaterialCategories)
            {
                throw InvalidPublicMaterialCategoryResponse();
            }

            var categories = new List<Gw2MaterialCategory>();
            var categoryIds = new HashSet<long>();
            var membershipCount = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) throw InvalidPublicMaterialCategoryResponse();
                var id = MaterialCategoryLong(element, "id");
                var name = MaterialCategoryString(element, "name");
                var order = MaterialCategoryLong(element, "order");
                var items = MaterialCategoryProperty(element, "items");
                if (id <= 0 || !categoryIds.Add(id) || items.ValueKind != JsonValueKind.Array)
                {
                    throw InvalidPublicMaterialCategoryResponse();
                }

                var itemIds = new List<long>();
                var uniqueItemIds = new HashSet<long>();
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number
                        || !item.TryGetInt64(out var itemId)
                        || itemId <= 0
                        || !uniqueItemIds.Add(itemId))
                    {
                        throw InvalidPublicMaterialCategoryResponse();
                    }

                    membershipCount = checked(membershipCount + 1);
                    if (membershipCount > MaximumMaterialMemberships) throw InvalidPublicMaterialCategoryResponse();
                    itemIds.Add(itemId);
                }

                categories.Add(new Gw2MaterialCategory(id, name, order, itemIds));
            }

            return categories;
        }
        catch (JsonException)
        {
            throw InvalidPublicMaterialCategoryResponse();
        }
        catch (OverflowException)
        {
            throw InvalidPublicMaterialCategoryResponse();
        }
    }

    private static JsonElement MaterialCategoryProperty(JsonElement category, string name)
    {
        var properties = category.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        return properties.Length == 1 ? properties[0].Value : throw InvalidPublicMaterialCategoryResponse();
    }

    private static long MaterialCategoryLong(JsonElement category, string name)
    {
        var value = MaterialCategoryProperty(category, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) throw InvalidPublicMaterialCategoryResponse();
        return number;
    }

    private static string MaterialCategoryString(JsonElement category, string name)
    {
        var value = MaterialCategoryProperty(category, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw InvalidPublicMaterialCategoryResponse();
        return value.GetString()!;
    }

    private async Task<IReadOnlyList<Gw2PublicRecipe>> DeserializePublicRecipesAsync(
        HttpResponseMessage response,
        IReadOnlySet<long> requestedIds,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw InvalidPublicRecipeResponse();

            var recipes = new List<Gw2PublicRecipe>();
            var recipeIds = new HashSet<long>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) throw InvalidPublicRecipeResponse();
                var id = RecipeLong(element, "id");
                var type = RecipeString(element, "type");
                var outputItemId = RecipeLong(element, "output_item_id");
                var outputItemCount = RecipeLong(element, "output_item_count");
                var timeToCraftMs = RecipeLong(element, "time_to_craft_ms");
                var minRating = RecipeLong(element, "min_rating");
                if (id <= 0
                    || !requestedIds.Contains(id)
                    || !recipeIds.Add(id)
                    || outputItemId <= 0
                    || outputItemCount <= 0
                    || timeToCraftMs < 0
                    || minRating < 0)
                {
                    throw InvalidPublicRecipeResponse();
                }

                var ingredientsElement = RecipeProperty(element, "ingredients");
                if (ingredientsElement.ValueKind != JsonValueKind.Array) throw InvalidPublicRecipeResponse();
                var ingredients = new List<Gw2RecipeIngredient>();
                foreach (var ingredientElement in ingredientsElement.EnumerateArray())
                {
                    if (ingredientElement.ValueKind != JsonValueKind.Object) throw InvalidPublicRecipeResponse();
                    var kind = RecipeString(ingredientElement, "type");
                    var ingredientId = RecipeLong(ingredientElement, "id");
                    var count = RecipeLong(ingredientElement, "count");
                    if (ingredientId <= 0 || count <= 0) throw InvalidPublicRecipeResponse();
                    ingredients.Add(new Gw2RecipeIngredient(kind, ingredientId, count));
                }

                recipes.Add(new Gw2PublicRecipe(
                    id,
                    type,
                    outputItemId,
                    outputItemCount,
                    OptionalRecipePositiveLong(element, "output_upgrade_id"),
                    minRating,
                    timeToCraftMs,
                    RecipeStrings(element, "disciplines"),
                    RecipeStrings(element, "flags"),
                    ingredients));
            }

            return recipes;
        }
        catch (JsonException)
        {
            throw InvalidPublicRecipeResponse();
        }
    }

    private async Task<IReadOnlyList<long>> DeserializeRecipeSelectorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            if (response.Content.Headers.ContentLength is > MaximumRecipeSelectorPayloadBytes)
            {
                throw InvalidPublicRecipeSelectorResponse();
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var bytesRead = await responseStream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0) break;
                if (payload.Length + bytesRead > MaximumRecipeSelectorPayloadBytes)
                {
                    throw InvalidPublicRecipeSelectorResponse();
                }

                await payload.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }

            payload.Position = 0;
            using var document = await JsonDocument.ParseAsync(payload, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() > MaximumRecipeSelectorIds)
            {
                throw InvalidPublicRecipeSelectorResponse();
            }

            var recipeIds = new HashSet<long>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Number
                    || !element.TryGetInt64(out var recipeId)
                    || recipeId <= 0
                    || !recipeIds.Add(recipeId))
                {
                    throw InvalidPublicRecipeSelectorResponse();
                }
            }

            return recipeIds.Order().ToArray();
        }
        catch (JsonException)
        {
            throw InvalidPublicRecipeSelectorResponse();
        }
    }

    private async Task<IReadOnlyList<long>> DeserializeAccountRecipeUnlocksAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            if (response.Content.Headers.ContentLength is > MaximumAccountRecipePayloadBytes)
            {
                throw InvalidAccountRecipeUnlockResponse();
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var bytesRead = await responseStream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0) break;
                if (payload.Length + bytesRead > MaximumAccountRecipePayloadBytes)
                {
                    throw InvalidAccountRecipeUnlockResponse();
                }

                await payload.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }

            payload.Position = 0;
            using var document = await JsonDocument.ParseAsync(payload, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() > MaximumAccountRecipeIds)
            {
                throw InvalidAccountRecipeUnlockResponse();
            }

            var recipeIds = new HashSet<long>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Number
                    || !element.TryGetInt64(out var recipeId)
                    || recipeId <= 0
                    || !recipeIds.Add(recipeId))
                {
                    throw InvalidAccountRecipeUnlockResponse();
                }
            }

            return recipeIds.Order().ToArray();
        }
        catch (JsonException)
        {
            throw InvalidAccountRecipeUnlockResponse();
        }
    }

    private static JsonElement RecipeProperty(JsonElement recipe, string name)
    {
        var properties = recipe.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        return properties.Length == 1 ? properties[0].Value : throw InvalidPublicRecipeResponse();
    }

    private static long RecipeLong(JsonElement recipe, string name)
    {
        var value = RecipeProperty(recipe, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) throw InvalidPublicRecipeResponse();
        return number;
    }

    private static long? OptionalRecipePositiveLong(JsonElement recipe, string name)
    {
        var properties = recipe.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (properties.Length == 0) return null;
        if (properties.Length != 1
            || properties[0].Value.ValueKind != JsonValueKind.Number
            || !properties[0].Value.TryGetInt64(out var number)
            || number <= 0)
        {
            throw InvalidPublicRecipeResponse();
        }

        return number;
    }

    private static string RecipeString(JsonElement recipe, string name)
    {
        var value = RecipeProperty(recipe, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw InvalidPublicRecipeResponse();
        return value.GetString()!;
    }

    private static IReadOnlyList<string> RecipeStrings(JsonElement recipe, string name)
    {
        var value = RecipeProperty(recipe, name);
        if (value.ValueKind != JsonValueKind.Array) throw InvalidPublicRecipeResponse();
        var values = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString())) throw InvalidPublicRecipeResponse();
            values.Add(element.GetString()!);
        }

        return values.Order(StringComparer.Ordinal).ToArray();
    }

    private static JsonElement PublicItemProperty(JsonElement item, string name)
    {
        var properties = item.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        return properties.Length == 1 ? properties[0].Value : throw InvalidPublicItemResponse();
    }

    private static long PublicItemLong(JsonElement item, string name)
    {
        var value = PublicItemProperty(item, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) throw InvalidPublicItemResponse();
        return number;
    }

    private static string PublicItemString(JsonElement item, string name, bool allowBlank)
    {
        var value = PublicItemProperty(item, name);
        if (value.ValueKind != JsonValueKind.String || (!allowBlank && string.IsNullOrWhiteSpace(value.GetString()))) throw InvalidPublicItemResponse();
        return value.GetString()!;
    }

    private static IReadOnlyList<string> PublicItemStrings(JsonElement item, string name)
    {
        var value = PublicItemProperty(item, name);
        if (value.ValueKind != JsonValueKind.Array) throw InvalidPublicItemResponse();
        var strings = new List<string>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString())) throw InvalidPublicItemResponse();
            strings.Add(entry.GetString()!);
        }

        return strings.Order(StringComparer.Ordinal).ToArray();
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

    private async Task<List<EquipmentTabsPayload>> DeserializeEquipmentTabsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > MaximumEquipmentTabsPayloadBytes) throw InvalidCharacterEquipmentTabsResponse();
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > MaximumEquipmentTabs) throw InvalidCharacterEquipmentTabsResponse();
            var tabs = new List<EquipmentTabsPayload>();
            var total = 0;
            var references = 0;
            var statAttributes = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var tab = EquipmentObject(element); var id = EquipmentInt(tab, "tab"); var name = EquipmentString(tab, "name", true);
                if (id <= 0 || name.Length > MaximumEquipmentTabStringLength || tabs.Any(existing => existing.Tab == id)) throw InvalidCharacterEquipmentTabsResponse();
                var equipment = EquipmentArray(tab, "equipment"); if (equipment.GetArrayLength() > MaximumEquipmentTabRows || checked(total += equipment.GetArrayLength()) > MaximumEquipmentTabTotalRows) throw InvalidCharacterEquipmentTabsResponse();
                var rows = new List<AuthenticatedEquipmentRow>();
                foreach (var elementRow in equipment.EnumerateArray())
                {
                    var row = EquipmentObject(elementRow); var slot = EquipmentString(row, "slot", false);
                    if (slot.Length > MaximumEquipmentTabStringLength) throw InvalidCharacterEquipmentTabsResponse();
                    if (KnownSpecialEquipmentSlots.Contains(slot)) continue;
                    var parsed = ParseEquipmentRow(row, slot, primary: false);
                    if (parsed.Location.Length > MaximumEquipmentTabStringLength || parsed.Binding?.Length > MaximumEquipmentTabStringLength || parsed.BoundTo?.Length > MaximumEquipmentTabStringLength || rows.Any(existing => existing.Slot == slot)) throw InvalidCharacterEquipmentTabsResponse();
                    if (parsed.Upgrades.Count > 16 || parsed.Infusions.Count > 16 || parsed.SelectedAttributes?.Count > 32) throw InvalidCharacterEquipmentTabsResponse();
                    if (parsed.SelectedAttributes?.Any(attribute => attribute.Name.Length > MaximumEquipmentTabStringLength) == true) throw InvalidCharacterEquipmentTabsResponse();
                    if (checked(references += 1 + parsed.Upgrades.Count + parsed.Infusions.Count) > MaximumEquipmentTabReferences
                        || checked(statAttributes += parsed.SelectedAttributes?.Count ?? 0) > MaximumEquipmentTabStatAttributes) throw InvalidCharacterEquipmentTabsResponse();
                    rows.Add(parsed);
                }
                tabs.Add(new EquipmentTabsPayload(id, name, EquipmentBoolean(tab, "is_active"), rows));
            }
            if (tabs.Count != 0 && tabs.Count(tab => tab.IsActive) != 1) throw InvalidCharacterEquipmentTabsResponse();
            return tabs;
        }
        catch (Exception exception) when (exception is JsonException or Gw2ConfigurationException or OverflowException) { throw InvalidCharacterEquipmentTabsResponse(); }
    }

    private async Task<List<FallbackEquipmentRow>> DeserializeFallbackEquipmentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken); if (bytes.Length > MaximumEquipmentTabsPayloadBytes) throw InvalidCharacterEquipmentTabsResponse();
        try
        {
            using var document = JsonDocument.Parse(bytes); var equipment = EquipmentArray(EquipmentObject(document.RootElement), "equipment");
            if (equipment.GetArrayLength() > MaximumEquipmentTabTotalRows) throw InvalidCharacterEquipmentTabsResponse();
            var rows = new List<FallbackEquipmentRow>();
            var references = 0;
            var statAttributes = 0;
            foreach (var element in equipment.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object || !TryEquipmentString(element, "slot", out var slot) || slot != "Relic") continue;
                var tabs = EquipmentArray(element, "tabs").EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id) && id > 0 ? id : throw InvalidCharacterEquipmentTabsResponse()).ToArray();
                if (tabs.Length == 0 || tabs.Distinct().Count() != tabs.Length) throw InvalidCharacterEquipmentTabsResponse();
                var parsed = ParseEquipmentRow(element, slot, false);
                if (parsed.Location.Length > MaximumEquipmentTabStringLength || parsed.Binding?.Length > MaximumEquipmentTabStringLength || parsed.BoundTo?.Length > MaximumEquipmentTabStringLength || parsed.Upgrades.Count > 16 || parsed.Infusions.Count > 16 || parsed.SelectedAttributes?.Count > 32 || parsed.SelectedAttributes?.Any(attribute => attribute.Name.Length > MaximumEquipmentTabStringLength) == true) throw InvalidCharacterEquipmentTabsResponse();
                if (checked(references += 1 + parsed.Upgrades.Count + parsed.Infusions.Count) > MaximumEquipmentTabReferences || checked(statAttributes += parsed.SelectedAttributes?.Count ?? 0) > MaximumEquipmentTabStatAttributes) throw InvalidCharacterEquipmentTabsResponse();
                rows.Add(new FallbackEquipmentRow(parsed, tabs));
            }
            return rows;
        }
        catch (Exception exception) when (exception is JsonException or Gw2ConfigurationException or OverflowException) { throw InvalidCharacterEquipmentTabsResponse(); }
    }

    private static void AddFallbackRelics(IReadOnlyList<EquipmentTabsPayload> tabs, IReadOnlyList<FallbackEquipmentRow> fallback)
    {
        var returnedTabIds = tabs.Select(tab => tab.Tab).ToHashSet();
        if (fallback.SelectMany(row => row.Tabs).Any(tabId => !returnedTabIds.Contains(tabId))) throw InvalidCharacterEquipmentTabsResponse();
        foreach (var tab in tabs.Where(tab => !tab.Rows.Any(row => row.Slot == "Relic")))
        {
            var matches = fallback.Where(row => row.Tabs.Contains(tab.Tab)).ToArray();
            if (matches.Length != 1) throw InvalidCharacterEquipmentTabsResponse();
            tab.Rows.Add(matches[0].Row);
        }
    }

    private async Task<Dictionary<long, EquipmentItemMetadata>> ResolveEquipmentTabItemsAsync(IReadOnlyList<long> ids, IReadOnlySet<long> primaryIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, EquipmentItemMetadata>();
        foreach (var chunk in ids.Chunk(MaximumItemBatchSize))
        {
            var batch = await ResolveEquipmentTabItemsBatchAsync(chunk, primaryIds, cancellationToken);
            foreach (var row in batch) result[row.Key] = row.Value;
        }
        return result;
    }
    private async Task<Dictionary<long, string>> ResolveEquipmentTabNamesAsync(string resolver, IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, string>();
        foreach (var chunk in ids.Chunk(MaximumItemBatchSize))
        {
            var batch = await ResolveEquipmentTabNamesBatchAsync(resolver, chunk, cancellationToken);
            foreach (var row in batch) result[row.Key] = row.Value;
        }
        return result;
    }

    private static void EnsureEquipmentTabMetadataIdentityCount(IReadOnlyCollection<long> itemIds, IReadOnlyCollection<long> statIds, IReadOnlyCollection<long> skinIds)
    {
        if (checked(itemIds.Count + statIds.Count + skinIds.Count) > MaximumEquipmentTabMetadataIdentities) throw InvalidCharacterEquipmentTabsResponse();
    }

    private async Task<Dictionary<long, EquipmentItemMetadata>> ResolveEquipmentTabItemsBatchAsync(IReadOnlyList<long> requestedIds, IReadOnlySet<long> primaryIds, CancellationToken cancellationToken)
    {
        try
        {
            using var request = await SendEquipmentTabsWithSingleRetryAsync($"/v2/items?ids={Uri.EscapeDataString(string.Join(',', requestedIds))}", cancellationToken, authenticated: false);
            if (request.Response.StatusCode == HttpStatusCode.NotFound) return [];
            if (request.Response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent) return [];
            using var document = await JsonDocument.ParseAsync(await request.Response.Content.ReadAsStreamAsync(request.CancellationToken), cancellationToken: request.CancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var rows = new Dictionary<long, EquipmentItemMetadata>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var item = EquipmentObject(element);
                var id = EquipmentLong(item, "id");
                var name = EquipmentString(item, "name", true);
                if (id <= 0 || !requestedIds.Contains(id) || !rows.TryAdd(id, ParseEquipmentItemMetadata(item, id, name, primaryIds.Contains(id)))) return [];
            }
            return IsValidEquipmentTabBatch(request.Response.StatusCode, requestedIds.Count, rows.Count) ? rows : [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return []; }
    }

    private async Task<Dictionary<long, string>> ResolveEquipmentTabNamesBatchAsync(string resolver, IReadOnlyList<long> requestedIds, CancellationToken cancellationToken)
    {
        try
        {
            using var request = await SendEquipmentTabsWithSingleRetryAsync($"/v2/{resolver}?ids={Uri.EscapeDataString(string.Join(',', requestedIds))}", cancellationToken, authenticated: false);
            if (request.Response.StatusCode == HttpStatusCode.NotFound) return [];
            if (request.Response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent) return [];
            using var document = await JsonDocument.ParseAsync(await request.Response.Content.ReadAsStreamAsync(request.CancellationToken), cancellationToken: request.CancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var rows = new Dictionary<long, string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var row = EquipmentObject(element);
                var id = EquipmentLong(row, "id");
                var name = EquipmentString(row, "name", false);
                if (id <= 0 || !requestedIds.Contains(id) || !rows.TryAdd(id, name)) return [];
            }
            return IsValidEquipmentTabBatch(request.Response.StatusCode, requestedIds.Count, rows.Count) ? rows : [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return []; }
    }

    private static bool IsValidEquipmentTabBatch(HttpStatusCode statusCode, int requestedCount, int returnedCount) =>
        statusCode == HttpStatusCode.OK ? returnedCount == requestedCount : returnedCount > 0 && returnedCount < requestedCount;

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
        var referenceKind = location == "Equipped" ? "EquippedReference" : location == "Armory" ? "EquipmentTemplateReference" : location is "EquippedFromLegendaryArmory" or "LegendaryArmory" ? "LegendaryArmoryReference" : "UnknownEquipmentReference";
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

    private async Task<Dictionary<long, InventoryItemMetadata>> ResolveInventoryItemsAsync(IReadOnlyList<long> requestedIds, IReadOnlySet<long> primaryIds, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<long, InventoryItemMetadata>();
        foreach (var chunk in requestedIds.Chunk(MaximumItemBatchSize))
        {
            try
            {
                using var response = await SendWithSingleRetryAsync($"/v2/items?ids={Uri.EscapeDataString(string.Join(',', chunk))}", cancellationToken, authenticated: false);
                if (response.StatusCode == HttpStatusCode.NotFound) continue;
                if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent) continue;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.ValueKind != JsonValueKind.Array) continue;
                var rows = new Dictionary<long, InventoryItemMetadata>();
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    var item = InventoryObject(element);
                    var id = InventoryPositiveLong(item, "id");
                    if (!chunk.Contains(id) || !rows.TryAdd(id, ParseInventoryItemMetadata(item, id, primaryIds.Contains(id)))) throw InvalidCharacterInventoryResponse();
                }
                if ((response.StatusCode == HttpStatusCode.OK && rows.Count != chunk.Length)
                    || (response.StatusCode == HttpStatusCode.PartialContent && (rows.Count == 0 || rows.Count == chunk.Length))) continue;
                foreach (var row in rows) resolved.Add(row.Key, row.Value);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { }
        }
        return resolved;
    }

    private async Task<Dictionary<long, string>> ResolveInventoryNamesAsync(string resolver, IReadOnlyList<long> requestedIds, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<long, string>();
        foreach (var chunk in requestedIds.Chunk(MaximumItemBatchSize))
        {
            try
            {
                using var response = await SendWithSingleRetryAsync($"/v2/{resolver}?ids={Uri.EscapeDataString(string.Join(',', chunk))}", cancellationToken, authenticated: false);
                if (response.StatusCode == HttpStatusCode.NotFound) continue;
                if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent) continue;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.ValueKind != JsonValueKind.Array) continue;
                var rows = new Dictionary<long, string>();
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    var row = InventoryObject(element);
                    var id = InventoryPositiveLong(row, "id");
                    var name = InventoryString(row, "name", false);
                    if (!chunk.Contains(id) || !rows.TryAdd(id, name)) throw InvalidCharacterInventoryResponse();
                }
                if ((response.StatusCode == HttpStatusCode.OK && rows.Count != chunk.Length)
                    || (response.StatusCode == HttpStatusCode.PartialContent && (rows.Count == 0 || rows.Count == chunk.Length))) continue;
                foreach (var row in rows) resolved.Add(row.Key, row.Value);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { }
        }
        return resolved;
    }

    private static InventoryItemMetadata ParseInventoryItemMetadata(JsonElement item, long id, bool primary)
    {
        var name = InventoryString(item, "name", false);
        if (!primary) return new InventoryItemMetadata(id, name, null, null, null, null, null);
        var type = InventoryString(item, "type", false);
        var rarity = InventoryString(item, "rarity", false);
        var level = InventoryInt(item, "level");
        if (level < 0) throw InvalidCharacterInventoryResponse();
        string? subtype = null;
        long? defaultStatId = null;
        if (TryInventoryProperty(item, "details", out var detailsValue))
        {
            var details = InventoryObject(detailsValue);
            if (TryInventoryProperty(details, "type", out var typeValue))
            {
                if (typeValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(typeValue.GetString())) throw InvalidCharacterInventoryResponse();
                subtype = typeValue.GetString();
            }
            if (TryInventoryProperty(details, "infix_upgrade", out var infixValue))
            {
                var infix = InventoryObject(infixValue);
                if (TryInventoryProperty(infix, "id", out var idValue))
                {
                    if (idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetInt64(out var statId) || statId <= 0) throw InvalidCharacterInventoryResponse();
                    defaultStatId = statId;
                }
            }
        }
        return new InventoryItemMetadata(id, name, type, subtype, rarity, level, defaultStatId);
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

    private static JsonElement InventoryProperty(JsonElement element, string name)
    {
        var matches = element.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (matches.Length != 1) throw InvalidCharacterInventoryResponse();
        return matches[0].Value;
    }
    private static bool TryInventoryProperty(JsonElement element, string name, out JsonElement value)
    {
        var matches = element.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        if (matches.Length > 1) throw InvalidCharacterInventoryResponse();
        value = matches.Length == 1 ? matches[0].Value : default;
        return matches.Length == 1;
    }
    private static JsonElement InventoryObject(JsonElement element) => element.ValueKind == JsonValueKind.Object ? element : throw InvalidCharacterInventoryResponse();
    private static JsonElement InventoryArray(JsonElement element, string name)
    {
        var value = InventoryProperty(element, name);
        return value.ValueKind == JsonValueKind.Array ? value : throw InvalidCharacterInventoryResponse();
    }
    private static string InventoryString(JsonElement element, string name, bool allowEmpty)
    {
        var value = InventoryProperty(element, name);
        if (value.ValueKind != JsonValueKind.String || (!allowEmpty && string.IsNullOrWhiteSpace(value.GetString()))) throw InvalidCharacterInventoryResponse();
        return value.GetString()!;
    }
    private static long InventoryLong(JsonElement element, string name)
    {
        var value = InventoryProperty(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) throw InvalidCharacterInventoryResponse();
        return number;
    }
    private static long InventoryPositiveLong(JsonElement element, string name)
    {
        var value = InventoryLong(element, name);
        return value > 0 ? value : throw InvalidCharacterInventoryResponse();
    }
    private static int InventoryPositiveInt(JsonElement element, string name)
    {
        var value = InventoryProperty(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number <= 0) throw InvalidCharacterInventoryResponse();
        return number;
    }
    private static int InventoryInt(JsonElement element, string name)
    {
        var value = InventoryProperty(element, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw InvalidCharacterInventoryResponse();
        return number;
    }
    private static int? InventoryOptionalNonnegativeInt(JsonElement element, string name)
    {
        if (!TryInventoryProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0) throw InvalidCharacterInventoryResponse();
        return number;
    }
    private static long? InventoryOptionalPositiveLong(JsonElement element, string name)
    {
        if (!TryInventoryProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number <= 0) throw InvalidCharacterInventoryResponse();
        return number;
    }
    private static IReadOnlyList<long> InventoryOptionalPositiveLongArray(JsonElement element, string name)
    {
        if (!TryInventoryProperty(element, name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw InvalidCharacterInventoryResponse();
        return value.EnumerateArray().Select(row => row.ValueKind == JsonValueKind.Number && row.TryGetInt64(out var id) && id > 0 ? id : throw InvalidCharacterInventoryResponse()).ToArray();
    }
    private static string? InventoryOptionalNullableString(JsonElement element, string name)
    {
        if (!TryInventoryProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw InvalidCharacterInventoryResponse();
        return value.GetString();
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

    private async Task<bool> IsCurrentBuysInvalidKeyResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await ReadCurrentBuysBodyAsync(response.Content, cancellationToken);
        return Encoding.UTF8.GetString(body).Contains("invalid key", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsCurrentSellsInvalidKeyResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await ReadCurrentSellsPageBodyAsync(response.Content, cancellationToken);
        return Encoding.UTF8.GetString(body).Contains("invalid key", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<TimedHttpResponse> SendAchievementWithSingleRetryAsync(string path, CancellationToken cancellationToken, bool authenticated)
    {
        for (var attempt = 0; ; attempt++)
        {
            var timeoutSource = new CancellationTokenSource(RecipeAttemptTimeout, timeProvider ?? TimeProvider.System);
            var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            HttpResponseMessage response;
            try { response = await SendAsync(path, linkedSource.Token, authenticated); }
            catch { linkedSource.Dispose(); timeoutSource.Dispose(); throw; }

            bool retry;
            try
            {
                var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                var invalidKey = authenticated && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    && await IsInvalidKeyResponseAsync(response, linkedSource.Token);
                retry = transient || invalidKey;
            }
            catch { response.Dispose(); linkedSource.Dispose(); timeoutSource.Dispose(); throw; }

            if (attempt == 0 && retry)
            {
                var delay = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ? GetRetryDelay(response) : TimeSpan.Zero;
                response.Dispose();
                linkedSource.Dispose();
                timeoutSource.Dispose();
                await Task.Delay(delay, timeProvider ?? TimeProvider.System, cancellationToken);
                continue;
            }
            return new TimedHttpResponse(response, timeoutSource, linkedSource);
        }
    }

    private Task<TimedHttpResponse> SendEquipmentTabsWithSingleRetryAsync(string path, CancellationToken cancellationToken, bool authenticated) =>
        SendAchievementWithSingleRetryAsync(path, cancellationToken, authenticated);

    private async Task<TimedHttpResponse> SendRecipeWithSingleRetryAsync(string path, CancellationToken cancellationToken, bool authenticated)
    {
        for (var attempt = 0; ; attempt++)
        {
            var timeoutSource = new CancellationTokenSource(RecipeAttemptTimeout, timeProvider ?? TimeProvider.System);
            var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            HttpResponseMessage response;
            try
            {
                response = await SendAsync(path, linkedSource.Token, authenticated);
            }
            catch
            {
                linkedSource.Dispose();
                timeoutSource.Dispose();
                throw;
            }

            bool isTransient;
            bool isInvalidKey;
            try
            {
                isTransient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                isInvalidKey = authenticated
                    && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    && await IsInvalidKeyResponseAsync(response, linkedSource.Token);
            }
            catch
            {
                response.Dispose();
                linkedSource.Dispose();
                timeoutSource.Dispose();
                throw;
            }

            if (attempt == 0 && (isTransient || isInvalidKey))
            {
                var retryDelay = isTransient ? GetRetryDelay(response) : TimeSpan.Zero;
                response.Dispose();
                linkedSource.Dispose();
                timeoutSource.Dispose();
                await Task.Delay(retryDelay, timeProvider ?? TimeProvider.System, cancellationToken);
                continue;
            }

            return new TimedHttpResponse(response, timeoutSource, linkedSource);
        }
    }

    private async Task<TimedHttpResponse> SendCurrentBuysWithSingleRetryAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var timeoutSource = new CancellationTokenSource(RecipeAttemptTimeout, timeProvider ?? TimeProvider.System);
            var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            HttpResponseMessage response;
            try
            {
                response = await SendAsync(path, linkedSource.Token, authenticated: true);
            }
            catch
            {
                linkedSource.Dispose();
                timeoutSource.Dispose();
                throw;
            }

            bool isTransient;
            bool isInvalidKey;
            try
            {
                isTransient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                isInvalidKey = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    && await IsCurrentBuysInvalidKeyResponseAsync(response, linkedSource.Token);
            }
            catch
            {
                response.Dispose();
                linkedSource.Dispose();
                timeoutSource.Dispose();
                throw;
            }

            if (attempt == 0 && (isTransient || isInvalidKey))
            {
                var retryDelay = isTransient ? GetRetryDelay(response) : TimeSpan.Zero;
                response.Dispose();
                linkedSource.Dispose();
                timeoutSource.Dispose();
                await Task.Delay(retryDelay, timeProvider ?? TimeProvider.System, cancellationToken);
                continue;
            }

            return new TimedHttpResponse(response, timeoutSource, linkedSource);
        }
    }

    private async Task<TimedHttpResponse> SendCurrentSellsWithSingleRetryAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var timeoutSource = new CancellationTokenSource(RecipeAttemptTimeout, timeProvider ?? TimeProvider.System);
            var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            HttpResponseMessage response;
            try
            {
                response = await SendAsync(path, linkedSource.Token, authenticated: true);
            }
            catch
            {
                linkedSource.Dispose();
                timeoutSource.Dispose();
                throw;
            }

            bool isTransient;
            bool isInvalidKey;
            try
            {
                isTransient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                isInvalidKey = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    && await IsCurrentSellsInvalidKeyResponseAsync(response, linkedSource.Token);
            }
            catch
            {
                response.Dispose();
                linkedSource.Dispose();
                timeoutSource.Dispose();
                throw;
            }

            if (attempt == 0 && (isTransient || isInvalidKey))
            {
                var retryDelay = isTransient ? GetRetryDelay(response) : TimeSpan.Zero;
                response.Dispose();
                linkedSource.Dispose();
                timeoutSource.Dispose();
                await Task.Delay(retryDelay, timeProvider ?? TimeProvider.System, cancellationToken);
                continue;
            }

            return new TimedHttpResponse(response, timeoutSource, linkedSource);
        }
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
    private static Gw2ConfigurationException InvalidCurrentBuysResponse() => new("GW2 returned an invalid current-buys response. Try again later.");
    private static Gw2ConfigurationException InvalidCurrentBuysPagination() => new("GW2 returned invalid current-buys pagination metadata. Try again later.");
    private static Gw2ConfigurationException InvalidItemMetadataResponse() => new("GW2 returned an invalid item metadata response. Try again later.");
    private static Gw2ConfigurationException InvalidPublicItemResponse() => new("GW2 returned an invalid public item response. Try again later.");
    private static Gw2ConfigurationException InvalidPublicMaterialCategoryResponse() => new("GW2 returned an invalid public material-category response. Try again later.");
    private static Gw2ConfigurationException InvalidPublicRecipeResponse() => new("GW2 returned an invalid public recipe response. Try again later.");
    private static Gw2ConfigurationException InvalidPublicRecipeSelectorResponse() => new("GW2 returned an invalid public recipe selector response. Try again later.");
    private static Gw2ConfigurationException InvalidAccountRecipeUnlockResponse() => new("GW2 returned an invalid account recipe-unlock response. Try again later.");
    private static Gw2ConfigurationException InvalidLegendaryArmoryResponse() => new("GW2 returned an invalid Legendary Armory response. Try again later.");
    private static Gw2ConfigurationException InvalidLegendaryArmoryMetadataResponse() => new("GW2 returned invalid Legendary Armory item metadata. Try again later.");
    private static Gw2ConfigurationException InvalidAccountAchievementProgressResponse() => new("GW2 returned an invalid account achievement-progress response. Try again later.");
    private static Gw2ConfigurationException InvalidPublicAchievementResponse() => new("GW2 returned an invalid public achievement-definition response. Try again later.");
    private static Gw2ConfigurationException InvalidAccountMasteryResponse() => new("GW2 returned an invalid account masteries response. Try again later.");
    private static Gw2ConfigurationException InvalidMasteryPointsResponse() => new("GW2 returned an invalid mastery-points response. Try again later.");
    private static Gw2ConfigurationException InvalidPublicMasteryResponse() => new("GW2 returned invalid public mastery metadata. Try again later.");
    private static Gw2ConfigurationException InvalidTokenPermissionResponse() => new("GW2 returned an invalid token-permission response. Try again later.");
    private static Gw2ConfigurationException InvalidCharacterBuildResponse() => new("GW2 returned an invalid character-build response. Try again later.");
    private static Gw2ConfigurationException InvalidCharacterEquipmentResponse() => new("GW2 returned an invalid character-equipment response. Try again later.");
    private static Gw2ConfigurationException InvalidCharacterEquipmentTabsResponse() => new("GW2 returned an invalid character-equipment-tabs response. Try again later.");
    private static Gw2ConfigurationException CharacterInventoryContextLimitExceeded() => new("GW2 character inventory exceeds configured response limits. Try again later.");

    private sealed class TimedHttpResponse(
        HttpResponseMessage response,
        CancellationTokenSource timeoutSource,
        CancellationTokenSource linkedSource) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;
        public CancellationToken CancellationToken => linkedSource.Token;

        public void Dispose()
        {
            Response.Dispose();
            linkedSource.Dispose();
            timeoutSource.Dispose();
        }
    }

    private sealed record ActiveBuildPayload(int Tab, string Name, string Profession, IReadOnlyList<SpecializationSlot> Specializations, SkillSlots TerrestrialSkills, SkillSlots AquaticSkills, PetSlots? Pets, LegendSlots? Legends);
    private sealed record EquipmentTabsPayload(int Tab, string Name, bool IsActive, List<AuthenticatedEquipmentRow> Rows);
    private sealed record FallbackEquipmentRow(AuthenticatedEquipmentRow Row, IReadOnlyList<int> Tabs);
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
    private sealed record CurrentBuysPagination(int PageCount, int ResultCount, long TotalCount);
    private sealed record CurrentSellsPagePagination(int PageCount, int ResultCount, long TotalCount);
    private sealed record ItemResponse(long? Id, string? Name);
    private sealed record LegendaryArmoryOwnership(long Id, long Count);
    private sealed record LegendaryArmoryItemMetadata(long Id, string Name, string Type, string? Subtype, string? WeightClass);
    private sealed record ActiveEquipmentPayload(int Tab, string Name, IReadOnlyList<AuthenticatedEquipmentRow> Rows);
    private sealed record AuthenticatedEquipmentRow(string Slot, long ItemId, long? SkinId, IReadOnlyList<long> Upgrades, IReadOnlyList<long> Infusions, string? Binding, string? BoundTo, string Location, string ReferenceKind, long? SelectedStatId, IReadOnlyList<Gw2EquipmentStatAttribute>? SelectedAttributes);
    private sealed record EquipmentItemBatch(IReadOnlyDictionary<long, EquipmentItemMetadata> Rows);
    private sealed record EquipmentNameBatch(IReadOnlyDictionary<long, string> Rows);
    private sealed record EquipmentItemMetadata(long Id, string Name, string? Type, string? Subtype, string? Rarity, int? Level, long? DefaultStatId);
    private sealed record SelectedCharacterInventoryPayload(IReadOnlyList<AuthenticatedInventoryBag?> Bags);
    private sealed record AuthenticatedInventoryBag(long Id, int Size, IReadOnlyList<AuthenticatedInventoryStack?> Slots);
    private sealed record AuthenticatedInventoryStack(long ItemId, long Count, int? Charges, IReadOnlyList<long> Upgrades, IReadOnlyList<long> Infusions, long? SkinId, string? Binding, string? BoundTo, long? SelectedStatId, IReadOnlyList<Gw2InventoryStatAttribute>? SelectedAttributes);
    private sealed record InventoryItemMetadata(long Id, string Name, string? Type, string? Subtype, string? Rarity, int? Level, long? DefaultStatId);
}

public sealed record CharacterInventoryLimits(
    int MaxBagPositions,
    int MaxSlotsPerBag,
    int MaxTotalSlots,
    int MaxItemReferences,
    int MaxStatAttributes)
{
    public static CharacterInventoryLimits Default { get; } = new(20, 40, 640, 1024, 2048);

    public static CharacterInventoryLimits FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var invalidSettings = new List<string>();
        var bagPositions = ReadSetting(configuration, "GW2_CHARACTER_INVENTORY_MAX_BAG_POSITIONS", Default.MaxBagPositions, invalidSettings);
        var slotsPerBag = ReadSetting(configuration, "GW2_CHARACTER_INVENTORY_MAX_SLOTS_PER_BAG", Default.MaxSlotsPerBag, invalidSettings);
        var totalSlots = ReadSetting(configuration, "GW2_CHARACTER_INVENTORY_MAX_TOTAL_SLOTS", Default.MaxTotalSlots, invalidSettings);
        var itemReferences = ReadSetting(configuration, "GW2_CHARACTER_INVENTORY_MAX_ITEM_REFERENCES", Default.MaxItemReferences, invalidSettings);
        var statAttributes = ReadSetting(configuration, "GW2_CHARACTER_INVENTORY_MAX_STAT_ATTRIBUTES", Default.MaxStatAttributes, invalidSettings);

        if (invalidSettings.Count == 0)
        {
            var maximumPossibleSlots = checked((long)bagPositions * slotsPerBag);
            if (totalSlots < slotsPerBag || totalSlots > maximumPossibleSlots)
            {
                invalidSettings.Add("GW2_CHARACTER_INVENTORY_MAX_TOTAL_SLOTS");
            }

            if (itemReferences < checked((long)bagPositions + totalSlots))
            {
                invalidSettings.Add("GW2_CHARACTER_INVENTORY_MAX_ITEM_REFERENCES");
            }
        }

        if (invalidSettings.Count != 0)
        {
            throw new Gw2ConfigurationException($"Invalid character inventory limit configuration: {string.Join(", ", invalidSettings.Distinct(StringComparer.Ordinal))}.");
        }

        return new CharacterInventoryLimits(bagPositions, slotsPerBag, totalSlots, itemReferences, statAttributes);
    }

    private static int ReadSetting(IConfiguration configuration, string settingName, int fallback, List<string> invalidSettings)
    {
        var raw = configuration[settingName];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            invalidSettings.Add(settingName);
            return fallback;
        }

        return value;
    }
}

public sealed record Gw2ApiOptions
{
    public Gw2ApiOptions(string apiKey, string baseUrl)
        : this(apiKey, baseUrl, CharacterInventoryLimits.Default)
    {
    }

    public Gw2ApiOptions(string apiKey, string baseUrl, CharacterInventoryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ApiKey = apiKey;
        BaseUrl = baseUrl;
        Limits = limits;
    }

    public string ApiKey { get; }
    public string BaseUrl { get; }
    public CharacterInventoryLimits Limits { get; }
}

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
public sealed record Gw2CurrentBuysPage(int Page, int PageSize, int PageCount, long TotalCount, IReadOnlyList<Gw2CurrentBuyOrder> Orders);
public sealed record Gw2CurrentBuyOrder(long ItemId, long Price, long Quantity, DateTimeOffset Created);
public sealed record Gw2CurrentSellsPage(int Page, int PageSize, int PageCount, long TotalCount, IReadOnlyList<Gw2CurrentSellPageOrder> Orders);
public sealed record Gw2CurrentSellPageOrder(long ItemId, long Price, long Quantity, DateTimeOffset Created);
public sealed record Gw2Items(IReadOnlyList<Gw2Item> Items, IReadOnlyList<long> MissingItemIds);
public sealed record Gw2Item(long Id, string Name);
public sealed record Gw2PublicItems(IReadOnlyList<Gw2PublicItem> Items, IReadOnlyList<long> MissingItemIds, IReadOnlyList<string> Warnings);
public sealed record Gw2PublicItem(long Id, string? Name, string Type, string Rarity, long Level, long VendorValue, IReadOnlyList<string> Flags, IReadOnlyList<string> GameTypes, IReadOnlyList<string> Restrictions);
public sealed record Gw2MaterialCategories(IReadOnlyList<Gw2MaterialCategory> Categories);
public sealed record Gw2MaterialCategory(long Id, string Name, long Order, IReadOnlyList<long> ItemIds);
public sealed record Gw2PublicRecipes(IReadOnlyList<Gw2PublicRecipe> Recipes, IReadOnlyList<long> MissingRecipeIds);
public sealed record Gw2PublicRecipe(
    long Id,
    string Type,
    long OutputItemId,
    long OutputItemCount,
    long? OutputUpgradeId,
    long MinRating,
    long TimeToCraftMs,
    IReadOnlyList<string> Disciplines,
    IReadOnlyList<string> Flags,
    IReadOnlyList<Gw2RecipeIngredient> Ingredients);
public sealed record Gw2RecipeIngredient(string Kind, long Id, long Count);
public sealed record Gw2RecipeSelector(IReadOnlyList<long> RecipeIds);
public sealed record Gw2AccountRecipeUnlocks(IReadOnlyList<long> RecipeIds);
public sealed record Gw2LegendaryArmory(IReadOnlyList<Gw2LegendaryArmoryEntry> Entries, bool IsMetadataComplete, IReadOnlyList<Gw2MetadataWarning> Warnings);
public sealed record Gw2LegendaryArmoryEntry(long Id, long ArmoryCount, string? Name, string? Type, string? Subtype, string? WeightClass);
public sealed record Gw2AccountAchievementProgress(IReadOnlyList<Gw2AccountAchievementProgressEntry> Entries);
public sealed record Gw2AccountAchievementProgressEntry(long Id, long? Current, long? Max, bool Done, long? Repeated, bool IsUnlocked, IReadOnlyList<long>? CompletedBits);
public sealed record Gw2PublicAchievements(IReadOnlyList<Gw2PublicAchievement> Achievements, IReadOnlyList<long> MissingAchievementIds);
public sealed record Gw2PublicAchievement(long Id, string Name, string? Description, string? Requirement, string? LockedText, string Type, IReadOnlyList<string> Flags, IReadOnlyList<Gw2AchievementBit>? Bits);
public sealed record Gw2AchievementBit(string? Type, long? Id, string? Text);
public sealed record Gw2AccountMasterySources(
    IReadOnlyList<Gw2AccountMasteryTrack> Tracks,
    IReadOnlyList<Gw2MasteryPointTotal> PointTotals,
    DateTimeOffset AccountMasteriesAsOf,
    DateTimeOffset MasteryPointsAsOf);
public sealed record Gw2AccountMasteryTrack(long Id, long? SourceLevel);
public sealed record Gw2MasteryPointTotal(string Region, long Spent, long Earned);
public sealed record Gw2PublicMasteries(IReadOnlyList<Gw2PublicMastery> Masteries, IReadOnlyList<long> MissingMasteryIds);
public sealed record Gw2PublicMastery(long Id, string Name, string Requirement, string Region, long Order, IReadOnlyList<Gw2PublicMasteryLevel> Levels);
public sealed record Gw2PublicMasteryLevel(string Name, string Description, string Instruction, long PointCost, long ExperienceCost);
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
public sealed record Gw2CharacterEquipmentTabs(string CharacterName, int? ActiveTab, IReadOnlyList<Gw2CharacterEquipmentTab> Tabs, bool IsMetadataComplete, IReadOnlyList<Gw2MetadataWarning> Warnings, DateTimeOffset EquipmentTabsAsOf, DateTimeOffset? EquipmentAsOf, DateTimeOffset? ItemsAsOf, DateTimeOffset? ItemStatsAsOf, DateTimeOffset? SkinsAsOf, DateTimeOffset AsOf);
public sealed record Gw2CharacterEquipmentTab(int Tab, string EquipmentTabName, bool IsActive, IReadOnlyList<Gw2EquipmentRow> Equipment);
public sealed record Gw2EquipmentRow(string Slot, Gw2EquipmentItem Item, Gw2EquipmentStat? Stats, IReadOnlyList<Gw2EquipmentReference> Upgrades, IReadOnlyList<Gw2EquipmentReference> Infusions, Gw2EquipmentReference? Skin, string? Binding, string? BoundTo, string Location, string ReferenceKind);
public sealed record Gw2EquipmentItem(long Id, string? Name, string? Type, string? Subtype, string? Rarity, int? Level);
public sealed record Gw2EquipmentStat(long Id, string? Name, string Source, IReadOnlyList<Gw2EquipmentStatAttribute>? Attributes);
public sealed record Gw2EquipmentStatAttribute(string Name, int Value);
public sealed record Gw2EquipmentReference(long Id, string? Name);
public sealed record Gw2CharacterInventory(string CharacterName, Gw2CharacterInventoryCapacity Capacity, IReadOnlyList<Gw2CharacterInventoryBag> Bags, bool IsMetadataComplete, IReadOnlyList<Gw2MetadataWarning> Warnings);
public sealed record Gw2CharacterInventoryCapacity(int BagPositions, int EquippedBags, int TotalSlots, int OccupiedSlots, int EmptySlots);
public sealed record Gw2CharacterInventoryBag(int BagPosition, Gw2InventoryBag? Bag, IReadOnlyList<Gw2CharacterInventorySlot> Slots);
public sealed record Gw2InventoryBag(long Id, string? Name, int Size);
public sealed record Gw2CharacterInventorySlot(int SlotPosition, Gw2InventoryStack? Stack);
public sealed record Gw2InventoryStack(Gw2InventoryItem Item, long Count, int? Charges, Gw2InventoryStat? Stats, IReadOnlyList<Gw2InventoryReference> Upgrades, IReadOnlyList<Gw2InventoryReference> Infusions, Gw2InventoryReference? Skin, string? Binding, string? BoundTo);
public sealed record Gw2InventoryItem(long Id, string? Name, string? Type, string? Subtype, string? Rarity, int? Level);
public sealed record Gw2InventoryStat(long Id, string? Name, string Source, IReadOnlyList<Gw2InventoryStatAttribute>? Attributes);
public sealed record Gw2InventoryStatAttribute(string Name, int Value);
public sealed record Gw2InventoryReference(long Id, string? Name);
public enum Gw2StorageSource
{
    Bank,
    MaterialStorage,
    SharedInventory
}
