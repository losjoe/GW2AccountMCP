using System.Net;
using System.Text;
using GW2AccountMCP.Gw2;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class Gw2ApiClientTests
{
    [Fact]
    public async Task GetCharacterEquipmentAsync_selects_exact_roster_name_and_uses_encoded_active_route()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","builds","inventories"]}""",
            """["Other Hero","Path/Query?# Hero"]""",
            """{"tab":1,"name":"","is_active":true,"equipment":[],"equipment_pvp":[{}]}""",
            """{"equipment":[]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var equipment = await client.GetCharacterEquipmentAsync("Path/Query?# Hero", CancellationToken.None);

        Assert.Equal("Path/Query?# Hero", equipment.CharacterName);
        Assert.Equal(
            ["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/characters?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/characters/Path%2FQuery%3F%23%20Hero/equipmenttabs/active?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/characters/Path%2FQuery%3F%23%20Hero/equipment?lang=en&v=2025-08-29T01%3A00%3A00.000Z"],
            handler.RequestUris);
    }

    [Fact]
    public async Task GetCharacterEquipmentAsync_filters_special_slots_recovers_relic_and_preserves_repeated_references()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","builds","inventories"]}""",
            """["Synthetic Hero"]""",
            """{"tab":2,"name":"","is_active":true,"equipment":[{"slot":"Sickle"},{"slot":"WeaponB1","id":1,"location":"Equipped","upgrades":[2,2],"infusions":[3],"skin":4,"stats":{"id":5,"attributes":{"Zeta":2,"Alpha":1}}},{"slot":"Helm","id":8,"location":"Equipped"}],"equipment_pvp":"ignored"}""",
            """{"equipment":[{"slot":"Relic","id":6,"location":"EquippedFromLegendaryArmory"}]}""",
            """[{"id":1,"name":"","type":"Weapon","rarity":"Rare","level":80,"details":{"type":"Sword","infix_upgrade":{"id":7}}},{"id":2,"name":"Upgrade"},{"id":3,"name":"Infusion"},{"id":6,"name":"Relic","type":"UpgradeComponent","rarity":"Exotic","level":0},{"id":8,"name":"Helm","type":"Armor","rarity":"Fine","level":1,"details":{"infix_upgrade":{"id":9}}}]""",
            """[{"id":5,"name":"Selected"},{"id":9,"name":"Default"}]""",
            """[{"id":4,"name":"Skin"}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var equipment = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"))
            .GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None);

        Assert.Equal(["Helm", "WeaponB1", "Relic"], equipment.Equipment.Select(row => row.Slot));
        var helm = equipment.Equipment[0];
        Assert.Equal((9L, "Default", "ItemDefault"), (helm.Stats!.Id, helm.Stats.Name, helm.Stats.Source));
        Assert.Null(helm.Stats.Attributes);
        var weapon = equipment.Equipment[1];
        Assert.Equal([2L, 2L], weapon.Upgrades.Select(upgrade => upgrade.Id));
        Assert.Equal(["Alpha", "Zeta"], weapon.Stats!.Attributes!.Select(attribute => attribute.Name));
        Assert.Equal("EquippedReference", weapon.ReferenceKind);
        Assert.Equal("LegendaryArmoryReference", equipment.Equipment[2].ReferenceKind);
        Assert.True(equipment.IsMetadataComplete);
        Assert.Equal(
            ["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/characters?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/characters/Synthetic%20Hero/equipmenttabs/active?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/characters/Synthetic%20Hero/equipment?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/items?ids=1%2C2%2C3%2C6%2C8&lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/itemstats?ids=5%2C9&lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/skins?ids=4&lang=en&v=2025-08-29T01%3A00%3A00.000Z"],
            handler.RequestUris);
    }

    [Fact]
    public async Task GetCharacterEquipmentAsync_discards_malformed_public_metadata_and_returns_ordered_warnings()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","builds","inventories"]}""", """["Synthetic Hero"]""",
            """{"tab":1,"name":"tab","is_active":true,"equipment":[{"slot":"Helm","id":2,"location":"FutureLocation","stats":{"id":3,"attributes":{}}},{"slot":"Relic","id":1,"location":"Equipped","skin":4}],"equipment_pvp":[]}""",
            """[{"id":1,"name":"Relic","type":"UpgradeComponent","rarity":"Rare","level":0},{"id":2,"name":"Helm","type":"Armor","rarity":"Rare","level":0}]""",
            new ResponseSpec("[]", HttpStatusCode.PartialContent),
            new ResponseSpec("[]", HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var equipment = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"))
            .GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None);

        Assert.Equal("UnknownEquipmentReference", equipment.Equipment[0].ReferenceKind);
        Assert.False(equipment.IsMetadataComplete);
        Assert.Equal([("itemstats", "3"), ("skins", "4")], equipment.Warnings.Select(warning => (warning.Resolver, warning.ReferenceId)));
        Assert.Null(equipment.Equipment[0].Stats!.Name);
    }

    [Theory]
    [MemberData(nameof(OverBoundPrimaryEquipment))]
    public async Task GetCharacterEquipmentAsync_rejects_bounds_without_metadata_or_truncation(string equipment, int expectedAuthenticatedCalls)
    {
        var responses = new List<object> { EquipmentPermissions, EquipmentRoster, ActiveEquipment(equipment) };
        if (expectedAuthenticatedCalls == 4) responses.Add("""{"equipment":[]}""");
        var handler = new RecordingHandler(responses.ToArray());
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None));

        Assert.Equal(expectedAuthenticatedCalls, handler.RequestUris.Count);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("/v2/items?", StringComparison.Ordinal));
    }

    public static TheoryData<string, int> OverBoundPrimaryEquipment => new()
    {
        { "[" + string.Join(',', Enumerable.Range(1, 33).Select(_ => "{\"slot\":\"Sickle\"}")) + "]", 3 },
        { "[" + EquipmentRow("Helm", 1, ",\"upgrades\":[" + string.Join(',', Enumerable.Range(2, 200).Append(1).Select(id => id.ToString())) + "]") + "]", 4 },
        { "[" + EquipmentRow("Helm", 1, ",\"stats\":{\"id\":2,\"attributes\":{" + string.Join(',', Enumerable.Range(1, 33).Select(id => "\"A" + id + "\":" + id)) + "}}") + "]", 3 }
    };

    [Fact]
    public async Task GetCharacterEquipmentAsync_rejects_final_retained_bound_after_recovered_relic()
    {
        var primaryRows = "[" + string.Join(',', Enumerable.Range(1, 32).Select(id => EquipmentRow("Future" + id, id))) + "]";
        var handler = new RecordingHandler(EquipmentPermissions, EquipmentRoster, ActiveEquipment(primaryRows), """{"equipment":[{"slot":"Relic","id":33,"location":"Equipped"}]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None));

        Assert.Equal(4, handler.RequestUris.Count);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("/v2/items?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCharacterEquipmentAsync_rejects_over_bound_fallback_before_metadata()
    {
        var handler = new RecordingHandler(EquipmentPermissions, EquipmentRoster, ActiveEquipment("[]"), "{" + "\"equipment\":[" + string.Join(',', Enumerable.Repeat("null", 257)) + "]}");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None));

        Assert.Equal(4, handler.RequestUris.Count);
    }

    [Theory]
    [MemberData(nameof(InvalidPrimaryEquipment))]
    public async Task GetCharacterEquipmentAsync_rejects_retained_authenticated_contradictions(string equipment)
    {
        var handler = new RecordingHandler(EquipmentPermissions, EquipmentRoster, ActiveEquipment(equipment));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None));

        Assert.Equal(3, handler.RequestUris.Count);
    }

    public static TheoryData<string> InvalidPrimaryEquipment => new()
    {
        { "[" + EquipmentRow("Helm", 1) + "," + EquipmentRow("Helm", 2) + "]" },
        { "[{\"slot\":\"Helm\",\"id\":1,\"location\":\"Armory\"}]" },
        { "[{\"slot\":\"Helm\",\"id\":1,\"location\":\"LegendaryArmory\"}]" },
        { "[" + EquipmentRow("Helm", 1, ",\"binding\":\"Character\"") + "]" },
        { "[" + EquipmentRow("Helm", 1, ",\"binding\":\"Account\",\"bound_to\":\"Synthetic Hero\"") + "]" }
    };

    [Theory]
    [MemberData(nameof(InvalidFallbackRelics))]
    public async Task GetCharacterEquipmentAsync_rejects_ambiguous_or_unknown_fallback_relics(string fallback)
    {
        var handler = new RecordingHandler(EquipmentPermissions, EquipmentRoster, ActiveEquipment("[]"), fallback);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None));

        Assert.Equal(4, handler.RequestUris.Count);
    }

    public static TheoryData<string> InvalidFallbackRelics => new()
    {
        { """{"equipment":[{"slot":"Relic","location":"FutureLocation"}]}""" },
        { """{"equipment":[{"slot":"Relic","id":1,"location":"Equipped"},{"slot":"Relic","id":2,"location":"EquippedFromLegendaryArmory"}]}""" }
    };

    [Fact]
    public async Task GetCharacterEquipmentAsync_ignores_unrelated_and_inactive_fallback_rows_when_no_active_relic_exists()
    {
        var handler = new RecordingHandler(EquipmentPermissions, EquipmentRoster, ActiveEquipment("[]"), """{"equipment":[null,{"slot":3},{"slot":"Helm"},{"slot":"Relic","location":"Armory"},{"slot":"Relic","location":"LegendaryArmory"}]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var equipment = await EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None);

        Assert.Empty(equipment.Equipment);
        Assert.True(equipment.IsMetadataComplete);
        Assert.Equal(4, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetCharacterEquipmentAsync_accepts_primary_relic_without_requesting_fallback()
    {
        var handler = new RecordingHandler(EquipmentPermissions, EquipmentRoster, ActiveEquipment("[" + EquipmentRow("Relic", 1) + "]"), """[{"id":1,"name":"Relic","type":"UpgradeComponent","rarity":"Rare","level":0}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var equipment = await EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None);

        Assert.Single(equipment.Equipment);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("/equipment?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCharacterEquipmentAsync_retains_unknown_primary_slot_and_location()
    {
        var handler = new RecordingHandler(EquipmentPermissions, EquipmentRoster, ActiveEquipment("""[{"slot":"FutureCombatSlot","id":1,"location":"FutureLocation"}]"""), """{"equipment":[]}""", """[{"id":1,"name":"Future item","type":"Armor","rarity":"Rare","level":0}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var row = Assert.Single((await EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None)).Equipment);

        Assert.Equal(("FutureCombatSlot", "FutureLocation", "UnknownEquipmentReference"), (row.Slot, row.Location, row.ReferenceKind));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("characters")]
    [InlineData("builds")]
    [InlineData("inventories")]
    public async Task GetCharacterEquipmentAsync_requires_each_permission_before_roster_access(string missingPermission)
    {
        var permissions = new[] { "account", "characters", "builds", "inventories" }.Where(permission => permission != missingPermission);
        var handler = new RecordingHandler("{\"permissions\":[" + string.Join(',', permissions.Select(permission => "\"" + permission + "\"")) + "]}");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None));

        Assert.Contains(missingPermission + " permission", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task GetCharacterEquipmentAsync_retains_valid_partial_item_metadata_and_warns_only_for_omitted_ids()
    {
        var handler = new RecordingHandler(EquipmentPermissions, EquipmentRoster, ActiveEquipment("[" + EquipmentRow("Helm", 1) + "," + EquipmentRow("Gloves", 2) + "]"), """{"equipment":[]}""", new ResponseSpec("""[{"id":1,"name":"Helm","type":"Armor","rarity":"Rare","level":0}]""", HttpStatusCode.PartialContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var equipment = await EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None);

        Assert.Equal(("Helm", (string?)null), (equipment.Equipment[0].Item.Name, equipment.Equipment[1].Item.Name));
        Assert.Equal(("items", "2"), Assert.Single(equipment.Warnings) is var warning ? (warning.Resolver, warning.ReferenceId) : default);
        Assert.False(equipment.IsMetadataComplete);
    }

    [Theory]
    [MemberData(nameof(InvalidItemBatches))]
    public async Task GetCharacterEquipmentAsync_discards_entire_invalid_item_batch(string itemBatch, HttpStatusCode status)
    {
        var handler = new RecordingHandler(EquipmentPermissions, EquipmentRoster, ActiveEquipment("[" + EquipmentRow("Helm", 1) + "]"), """{"equipment":[]}""", new ResponseSpec(itemBatch, status));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var equipment = await EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None);

        Assert.Null(Assert.Single(equipment.Equipment).Item.Name);
        Assert.Equal(("items", "1"), Assert.Single(equipment.Warnings) is var warning ? (warning.Resolver, warning.ReferenceId) : default);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("/v2/itemstats?", StringComparison.Ordinal));
    }

    public static TheoryData<string, HttpStatusCode> InvalidItemBatches => new()
    {
        { "not-json", HttpStatusCode.OK },
        { """[{"id":1,"name":"One","type":"Armor","rarity":"Rare","level":0},{"id":1,"name":"Duplicate","type":"Armor","rarity":"Rare","level":0}]""", HttpStatusCode.OK },
        { """[{"id":2,"name":"Unrequested","type":"Armor","rarity":"Rare","level":0}]""", HttpStatusCode.OK },
        { "[]", HttpStatusCode.OK },
        { """[{"id":1,"name":"One","type":"Armor","rarity":"Rare","level":0}]""", HttpStatusCode.PartialContent }
    };

    [Fact]
    public async Task GetCharacterEquipmentAsync_consumes_upgrade_only_metadata_but_selected_stats_survive_invalid_primary_metadata()
    {
        var handler = new RecordingHandler(EquipmentPermissions, EquipmentRoster,
            ActiveEquipment("[" + EquipmentRow("Helm", 1, ",\"upgrades\":[2],\"stats\":{\"id\":3,\"attributes\":{}}") + "]"),
            """{"equipment":[]}""",
            """[{"id":1,"name":"Primary missing required fields"},{"id":2,"name":"Upgrade only"}]""",
            """[{"id":3,"name":"Selected Stat"}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var equipment = await EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None);

        var row = Assert.Single(equipment.Equipment);
        Assert.Null(row.Item.Name);
        Assert.Null(Assert.Single(row.Upgrades).Name);
        Assert.Equal((3L, "Selected Stat", "Selected"), (row.Stats!.Id, row.Stats.Name, row.Stats.Source));
        Assert.Equal(["/v2/itemstats?ids=3&lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris.Where(uri => uri.StartsWith("/v2/itemstats?", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GetCharacterEquipmentAsync_accepts_upgrade_only_item_metadata_without_primary_fields()
    {
        var handler = new RecordingHandler(EquipmentPermissions, EquipmentRoster, ActiveEquipment("[" + EquipmentRow("Helm", 1, ",\"upgrades\":[2],\"infusions\":[2]") + "]"), """{"equipment":[]}""", """[{"id":1,"name":"Helm","type":"Armor","rarity":"Rare","level":0},{"id":2,"name":"Socket metadata only"}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var row = Assert.Single((await EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None)).Equipment);

        Assert.Equal("Helm", row.Item.Name);
        Assert.Equal(["Socket metadata only"], row.Upgrades.Select(reference => reference.Name));
        Assert.Equal(["Socket metadata only"], row.Infusions.Select(reference => reference.Name));
    }

    [Fact]
    public async Task GetCharacterEquipmentAsync_does_not_request_default_itemstat_after_invalid_primary_item_metadata()
    {
        var handler = new RecordingHandler(EquipmentPermissions, EquipmentRoster, ActiveEquipment("[" + EquipmentRow("Helm", 1) + "]"), """{"equipment":[]}""", """[{"id":1,"name":"Missing primary fields"}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        await EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", CancellationToken.None);

        Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("/v2/itemstats?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCharacterEquipmentAsync_propagates_caller_cancellation_and_stops_before_roster()
    {
        using var cancellationSource = new CancellationTokenSource();
        var handler = new RecordingHandler(EquipmentPermissions) { OnRequest = cancellationSource.Cancel };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EquipmentClient(httpClient).GetCharacterEquipmentAsync("Synthetic Hero", cancellationSource.Token));

        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task GetAccountAsync_missing_key_is_actionable_and_makes_no_request()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task GetAccountAsync_validates_account_permission_before_mapping_account_response()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","wallet"]}""",
            """{"name":"Example.1234","world":2206,"created":"2020-01-02T03:04:05Z","access":["GuildWars2","PathOfFire"]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var account = await client.GetAccountAsync(CancellationToken.None);

        Assert.Equal("Example.1234", account.Name);
        Assert.Equal(2206, account.World);
        Assert.Equal(DateTimeOffset.Parse("2020-01-02T03:04:05Z"), account.Created);
        Assert.Equal(["GuildWars2", "PathOfFire"], account.Access);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/account?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetAccountAsync_missing_account_permission_is_actionable_and_redacted()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("""{"permissions":["wallet"]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Contains("account permission", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task GetAccountAsync_invalid_key_error_is_actionable_and_redacted()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("Invalid key", HttpStatusCode.Unauthorized, "Invalid key", HttpStatusCode.Unauthorized);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetAccountAsync_does_not_retry_non_invalid_key_unauthorized_response()
    {
        var handler = new RecordingHandler("Permission denied", HttpStatusCode.Forbidden);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task GetAccountAsync_retries_a_transient_token_info_response()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("", HttpStatusCode.ServiceUnavailable),
            new ResponseSpec("""{"permissions":["account"]}"""),
            new ResponseSpec("""{"name":"Example.1234","world":2206,"created":"2020-01-02T03:04:05Z","access":[]}"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var account = await client.GetAccountAsync(CancellationToken.None);

        Assert.Equal("Example.1234", account.Name);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/account?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetAccountAsync_retries_a_transient_account_response()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account"]}"""),
            new ResponseSpec("", HttpStatusCode.BadGateway),
            new ResponseSpec("""{"name":"Example.1234","world":2206,"created":"2020-01-02T03:04:05Z","access":[]}"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var account = await client.GetAccountAsync(CancellationToken.None);

        Assert.Equal("Example.1234", account.Name);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/account?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/account?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetAccountAsync_stops_after_one_transient_retry()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account"]}"""),
            new ResponseSpec("", HttpStatusCode.GatewayTimeout),
            new ResponseSpec("", HttpStatusCode.GatewayTimeout));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Contains("HTTP 504", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetAccountAsync_honors_retry_after_without_waiting_in_the_test()
    {
        var timeProvider = new ImmediateTimeProvider();
        var handler = new RecordingHandler(
            new ResponseSpec("", HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(2)),
            new ResponseSpec("""{"permissions":["account"]}"""),
            new ResponseSpec("""{"name":"Example.1234","world":2206,"created":"2020-01-02T03:04:05Z","access":[]}"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"), timeProvider);

        await client.GetAccountAsync(CancellationToken.None);

        Assert.Equal(3, handler.RequestUris.Count);
        Assert.Equal([TimeSpan.FromSeconds(2)], timeProvider.RequestedDelays);
    }

    [Fact]
    public async Task GetAccountAsync_retries_an_invalid_key_marker_from_account()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account"]}"""),
            new ResponseSpec("Invalid key", HttpStatusCode.Unauthorized),
            new ResponseSpec("""{"name":"Example.1234","world":2206,"created":"2020-01-02T03:04:05Z","access":[]}"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var account = await client.GetAccountAsync(CancellationToken.None);

        Assert.Equal("Example.1234", account.Name);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetAccountAsync_does_not_retry_an_ordinary_account_auth_failure()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account"]}"""),
            new ResponseSpec("Permission denied", HttpStatusCode.Forbidden));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":null,\"world\":null,\"created\":null,\"access\":null}")]
    [InlineData("{malformed")]
    public async Task GetAccountAsync_rejects_incomplete_or_malformed_account_responses(string accountResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account"]}"""),
            new ResponseSpec(accountResponse));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Contains("account response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(accountResponse, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"permissions\":null}")]
    [InlineData("{malformed")]
    public async Task GetAccountAsync_rejects_incomplete_or_malformed_token_permission_responses(string tokenResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(new ResponseSpec(tokenResponse));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Contains("token-permission response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(tokenResponse, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAccountAsync_propagates_cancellation_during_a_retry()
    {
        using var cancellationSource = new CancellationTokenSource();
        var handler = new RecordingHandler(new ResponseSpec("", HttpStatusCode.ServiceUnavailable))
        {
            OnRequest = cancellationSource.Cancel
        };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAccountAsync(cancellationSource.Token));
    }

    [Fact]
    public async Task GetWalletAsync_joins_currency_names_in_wallet_order_and_uses_canonical_public_request()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","wallet"]}"""),
            new ResponseSpec("""[{"id":2,"value":0},{"id":1,"value":42},{"id":2,"value":7}]"""),
            new ResponseSpec("""[{"id":1,"name":"Coin"},{"id":2,"name":"Karma"}]"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var wallet = await client.GetWalletAsync(CancellationToken.None);

        Assert.Equal([(2, "Karma", 0L), (1, "Coin", 42L), (2, "Karma", 7L)], wallet.Balances.Select(balance => (balance.Id, balance.Name, balance.Value)));
        Assert.Empty(wallet.Warnings);
        Assert.Equal(
            ["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/account/wallet?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/currencies?ids=1%2C2&lang=en&v=2025-08-29T01%3A00%3A00.000Z"],
            handler.RequestUris);
        Assert.Equal(["Bearer " + apiKey, "Bearer " + apiKey, null], handler.AuthorizationHeaders);
    }

    [Fact]
    public async Task GetWalletAsync_missing_or_invalid_key_stops_before_wallet_and_currency_requests()
    {
        var missingKeyHandler = new RecordingHandler();
        using var missingKeyHttpClient = new HttpClient(missingKeyHandler) { BaseAddress = new Uri("https://example.test") };
        var missingKeyClient = new Gw2ApiClient(missingKeyHttpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var missingKeyError = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => missingKeyClient.GetWalletAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", missingKeyError.Message, StringComparison.Ordinal);
        Assert.Empty(missingKeyHandler.RequestUris);

        var apiKey = new string('k', 16);
        var invalidKeyHandler = new RecordingHandler("Invalid key", HttpStatusCode.Unauthorized, "Invalid key", HttpStatusCode.Unauthorized);
        using var invalidKeyHttpClient = new HttpClient(invalidKeyHandler) { BaseAddress = new Uri("https://example.test") };
        var invalidKeyClient = new Gw2ApiClient(invalidKeyHttpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var invalidKeyError = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => invalidKeyClient.GetWalletAsync(CancellationToken.None));

        Assert.DoesNotContain(apiKey, invalidKeyError.Message, StringComparison.Ordinal);
        Assert.Equal(2, invalidKeyHandler.RequestUris.Count);
    }

    [Fact]
    public async Task GetWalletAsync_rejects_malformed_token_permissions_before_downstream_calls()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("{malformed");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetWalletAsync(CancellationToken.None));

        Assert.Contains("token-permission response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Single(handler.RequestUris);
    }

    [Theory]
    [InlineData("{\"permissions\":[\"account\"]}", "wallet permission")]
    [InlineData("{\"permissions\":[\"wallet\"]}", "account permission")]
    public async Task GetWalletAsync_requires_each_operation_specific_permission_before_downstream_calls(string tokenResponse, string requiredPermission)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(new ResponseSpec(tokenResponse));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetWalletAsync(CancellationToken.None));

        Assert.Contains(requiredPermission, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetWalletAsync_empty_wallet_skips_currency_metadata()
    {
        var handler = new RecordingHandler("""{"permissions":["account","wallet"]}""", "[]");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var wallet = await client.GetWalletAsync(CancellationToken.None);

        Assert.Empty(wallet.Balances);
        Assert.Empty(wallet.Warnings);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetWalletAsync_retains_balance_and_returns_bounded_warning_when_currency_metadata_is_missing()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","wallet"]}""",
            """[{"id":1,"value":99},{"id":2,"value":0}]""",
            new ResponseSpec("""[{"id":1,"name":"Coin"}]""", HttpStatusCode.PartialContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var wallet = await client.GetWalletAsync(CancellationToken.None);

        Assert.Equal([(1, "Coin", 99L), (2, null, 0L)], wallet.Balances.Select(balance => (balance.Id, balance.Name, balance.Value)));
        var warning = Assert.Single(wallet.Warnings);
        Assert.Equal("currency_metadata_missing", warning.Code);
        Assert.Equal(2, warning.CurrencyId);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":0,\"value\":1}]")]
    [InlineData("[{\"id\":1,\"value\":-1}]")]
    public async Task GetWalletAsync_rejects_malformed_or_invalid_wallet_responses(string walletResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("""{"permissions":["account","wallet"]}""", walletResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetWalletAsync(CancellationToken.None));

        Assert.Contains("wallet response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":0,\"name\":\"Coin\"}]")]
    public async Task GetWalletAsync_rejects_malformed_or_invalid_currency_responses(string currencyResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("""{"permissions":["account","wallet"]}""", """[{"id":1,"value":0}]""", currencyResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetWalletAsync(CancellationToken.None));

        Assert.Contains("currency response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAccountStorageAsync_normalizes_all_sources_without_aggregating_stacks()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","inventories"]}""",
            """[null,{"id":11,"count":2},{"id":11,"count":3}]""",
            """[{"id":11,"category":1,"count":0},{"id":12,"category":2,"count":4}]""",
            """[{"id":11,"count":1},null,{"id":13,"count":5}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var storage = await client.GetAccountStorageAsync(CancellationToken.None);

        Assert.Equal(
            [
                (11, 2L, Gw2StorageSource.Bank, (int?)1),
                (11, 3L, Gw2StorageSource.Bank, (int?)2),
                (11, 0L, Gw2StorageSource.MaterialStorage, null),
                (12, 4L, Gw2StorageSource.MaterialStorage, null),
                (11, 1L, Gw2StorageSource.SharedInventory, (int?)0),
                (13, 5L, Gw2StorageSource.SharedInventory, (int?)2)
            ],
            storage.Stacks.Select(stack => (stack.Id, stack.Count, stack.Source, stack.SlotIndex)));
        Assert.Equal(
            [
                "/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/account/bank?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/account/materials?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/account/inventory?lang=en&v=2025-08-29T01%3A00%3A00.000Z"
            ],
            handler.RequestUris);
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetAccountStorageAsync_missing_key_is_actionable_and_makes_no_request()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountStorageAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestUris);
    }

    [Theory]
    [InlineData("{\"permissions\":[\"account\"]}", "inventories permission")]
    [InlineData("{\"permissions\":[\"inventories\"]}", "account permission")]
    public async Task GetAccountStorageAsync_requires_each_permission_before_storage_requests(string tokenResponse, string requiredPermission)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(tokenResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountStorageAsync(CancellationToken.None));

        Assert.Contains(requiredPermission, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetAccountStorageAsync_accepts_empty_sources()
    {
        var handler = new RecordingHandler("""{"permissions":["account","inventories"]}""", "[]", "[]", "[]");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var storage = await client.GetAccountStorageAsync(CancellationToken.None);

        Assert.Empty(storage.Stacks);
        Assert.Equal(4, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetAccountStorageAsync_retains_duplicate_valid_material_rows()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","inventories"]}""",
            "[]",
            """[{"id":11,"category":1,"count":0},{"id":11,"category":2,"count":4}]""",
            "[]");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var storage = await client.GetAccountStorageAsync(CancellationToken.None);

        Assert.Equal(
            [(11, 0L, Gw2StorageSource.MaterialStorage, (int?)null), (11, 4L, Gw2StorageSource.MaterialStorage, null)],
            storage.Stacks.Select(stack => (stack.Id, stack.Count, stack.Source, stack.SlotIndex)));
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("null")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":0,\"count\":1}]")]
    [InlineData("[{\"id\":1,\"count\":0}]")]
    [InlineData("[{\"id\":1,\"count\":-1}]")]
    public async Task GetAccountStorageAsync_rejects_invalid_bank_responses(string bankResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("""{"permissions":["account","inventories"]}""", bankResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountStorageAsync(CancellationToken.None));

        Assert.Contains("bank response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(bankResponse, error.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":1,\"count\":0}]")]
    [InlineData("[{\"id\":0,\"category\":1,\"count\":0}]")]
    [InlineData("[{\"id\":1,\"category\":1,\"count\":-1}]")]
    public async Task GetAccountStorageAsync_rejects_invalid_material_responses(string materialResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("""{"permissions":["account","inventories"]}""", "[]", materialResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountStorageAsync(CancellationToken.None));

        Assert.Contains("material-storage response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(materialResponse, error.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("null")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":0,\"count\":1}]")]
    [InlineData("[{\"id\":1,\"count\":0}]")]
    [InlineData("[{\"id\":1,\"count\":-1}]")]
    public async Task GetAccountStorageAsync_rejects_invalid_shared_inventory_responses(string inventoryResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("""{"permissions":["account","inventories"]}""", "[]", "[]", inventoryResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountStorageAsync(CancellationToken.None));

        Assert.Contains("shared-inventory response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(inventoryResponse, error.Message, StringComparison.Ordinal);
        Assert.Equal(4, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetAccountStorageAsync_source_failure_is_total_and_does_not_continue()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","inventories"]}"""),
            new ResponseSpec("[]"),
            new ResponseSpec("account data must not appear in the error", HttpStatusCode.InternalServerError));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountStorageAsync(CancellationToken.None));

        Assert.Contains("material-storage request failed with HTTP 500", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("account data", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetAccountStorageAsync_reuses_authenticated_single_retry_for_storage_sources()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","inventories"]}"""),
            new ResponseSpec("", HttpStatusCode.ServiceUnavailable),
            new ResponseSpec("[]"),
            new ResponseSpec("[]"),
            new ResponseSpec("[]"));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var storage = await client.GetAccountStorageAsync(CancellationToken.None);

        Assert.Empty(storage.Stacks);
        Assert.Equal(5, handler.RequestUris.Count);
        Assert.Equal(2, handler.RequestUris.Count(uri => uri.StartsWith("/v2/account/bank?", StringComparison.Ordinal)));
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetCharacterBagsAsync_missing_key_is_actionable_and_makes_no_request()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBagsAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestUris);
    }

    [Theory]
    [InlineData("{\"permissions\":[\"characters\",\"inventories\"]}", "account permission")]
    [InlineData("{\"permissions\":[\"account\",\"inventories\"]}", "characters permission")]
    [InlineData("{\"permissions\":[\"account\",\"characters\"]}", "inventories permission")]
    public async Task GetCharacterBagsAsync_requires_each_permission_before_character_requests(string tokenResponse, string requiredPermission)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(tokenResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBagsAsync(CancellationToken.None));

        Assert.Contains(requiredPermission, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetCharacterBagsAsync_traverses_every_character_and_preserves_stack_locations()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","inventories"]}""",
            """["First Hero","Path/Query?# Hero","Last Hero"]""",
            """{"bags":[null,{"id":901,"size":3,"inventory":[null,{"id":11,"count":2},{"id":11,"count":3}]}]}""",
            """{"bags":[{"id":902,"size":1,"inventory":[{"id":11,"count":4}]}]}""",
            """{"bags":[{"id":903,"size":2,"inventory":[null,{"id":12,"count":5}]}]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var bags = await client.GetCharacterBagsAsync(CancellationToken.None);

        Assert.Equal(
            [
                (11, 2L, "First Hero", 1, 1),
                (11, 3L, "First Hero", 1, 2),
                (11, 4L, "Path/Query?# Hero", 0, 0),
                (12, 5L, "Last Hero", 0, 1)
            ],
            bags.Stacks.Select(stack => (stack.Id, stack.Count, stack.Character, stack.BagIndex, stack.SlotIndex)));
        Assert.Equal(
            [
                "/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/characters?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/characters/First%20Hero/inventory?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/characters/Path%2FQuery%3F%23%20Hero/inventory?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/characters/Last%20Hero/inventory?lang=en&v=2025-08-29T01%3A00%3A00.000Z"
            ],
            handler.RequestUris);
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetCharacterBagsAsync_accepts_empty_characters_bags_and_slots()
    {
        var emptyCharactersHandler = new RecordingHandler(
            """{"permissions":["account","characters","inventories"]}""",
            "[]");
        using var emptyCharactersHttpClient = new HttpClient(emptyCharactersHandler) { BaseAddress = new Uri("https://example.test") };
        var emptyCharactersClient = new Gw2ApiClient(emptyCharactersHttpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var emptyCharacters = await emptyCharactersClient.GetCharacterBagsAsync(CancellationToken.None);

        Assert.Empty(emptyCharacters.Stacks);
        Assert.Equal(2, emptyCharactersHandler.RequestUris.Count);

        var emptyBagsHandler = new RecordingHandler(
            """{"permissions":["account","characters","inventories"]}""",
            """["Empty Bags","Empty Slots"]""",
            """{"bags":[]}""",
            """{"bags":[{"id":904,"size":2,"inventory":[null,null]}]}""");
        using var emptyBagsHttpClient = new HttpClient(emptyBagsHandler) { BaseAddress = new Uri("https://example.test") };
        var emptyBagsClient = new Gw2ApiClient(emptyBagsHttpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var emptyBags = await emptyBagsClient.GetCharacterBagsAsync(CancellationToken.None);

        Assert.Empty(emptyBags.Stacks);
        Assert.Equal(4, emptyBagsHandler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"Duplicate Hero\",\"Duplicate Hero\"]")]
    public async Task GetCharacterBagsAsync_rejects_invalid_character_lists(string characterResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","inventories"]}""",
            characterResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBagsAsync(CancellationToken.None));

        Assert.Contains("character-list response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(characterResponse, error.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"bags\":null}")]
    [InlineData("{\"bags\":[{}]}")]
    [InlineData("{\"bags\":[{\"id\":0,\"size\":1,\"inventory\":[null]}]}")]
    [InlineData("{\"bags\":[{\"id\":1,\"size\":0,\"inventory\":[]}]}")]
    [InlineData("{\"bags\":[{\"id\":1,\"size\":1,\"inventory\":null}]}")]
    [InlineData("{\"bags\":[{\"id\":1,\"size\":2,\"inventory\":[null]}]}")]
    [InlineData("{\"bags\":[{\"id\":1,\"size\":1,\"inventory\":[{}]}]}")]
    [InlineData("{\"bags\":[{\"id\":1,\"size\":1,\"inventory\":[{\"id\":0,\"count\":1}]}]}")]
    [InlineData("{\"bags\":[{\"id\":1,\"size\":1,\"inventory\":[{\"id\":2,\"count\":0}]}]}")]
    public async Task GetCharacterBagsAsync_rejects_invalid_character_inventory(string inventoryResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","inventories"]}""",
            """["Synthetic Hero"]""",
            inventoryResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBagsAsync(CancellationToken.None));

        Assert.Contains("character-inventory response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(inventoryResponse, error.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetCharacterBagsAsync_character_failure_is_total_and_stops_sequential_traversal()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","characters","inventories"]}"""),
            new ResponseSpec("""["First Hero","Failed Hero","Unrequested Hero"]"""),
            new ResponseSpec("""{"bags":[{"id":901,"size":1,"inventory":[{"id":11,"count":2}]}]}"""),
            new ResponseSpec("account data must not appear in the error", HttpStatusCode.InternalServerError));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBagsAsync(CancellationToken.None));

        Assert.Contains("character-inventory request failed with HTTP 500", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("account data", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, handler.RequestUris.Count);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("Unrequested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCharacterBagsAsync_rejects_partial_character_list()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","characters","inventories"]}"""),
            new ResponseSpec("""["Partial Hero"]""", HttpStatusCode.PartialContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBagsAsync(CancellationToken.None));

        Assert.Contains("character-list request failed with HTTP 206", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetCharacterBagsAsync_rejects_partial_character_inventory_and_stops_traversal()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","characters","inventories"]}"""),
            new ResponseSpec("""["Partial Hero","Unrequested Hero"]"""),
            new ResponseSpec("""{"bags":[]}""", HttpStatusCode.PartialContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBagsAsync(CancellationToken.None));

        Assert.Contains("character-inventory request failed with HTTP 206", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, handler.RequestUris.Count);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("Unrequested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCharacterBagsAsync_reuses_authenticated_single_retry_for_character_inventory()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","characters","inventories"]}"""),
            new ResponseSpec("""["Retry Hero"]"""),
            new ResponseSpec("", HttpStatusCode.ServiceUnavailable),
            new ResponseSpec("""{"bags":[]}"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var bags = await client.GetCharacterBagsAsync(CancellationToken.None);

        Assert.Empty(bags.Stacks);
        Assert.Equal(4, handler.RequestUris.Count);
        Assert.Equal(2, handler.RequestUris.Count(uri => uri.StartsWith("/v2/characters/Retry%20Hero/inventory?", StringComparison.Ordinal)));
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Theory]
    [InlineData("{\"permissions\":[\"characters\"]}", "account permission")]
    [InlineData("{\"permissions\":[\"account\"]}", "characters permission")]
    public async Task GetCharactersAsync_requires_only_each_core_permission_before_character_requests(string tokenResponse, string requiredPermission)
    {
        var handler = new RecordingHandler(tokenResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharactersAsync(CancellationToken.None));

        Assert.Contains(requiredPermission, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetCharactersAsync_requires_a_configured_key_before_any_request()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharactersAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task GetCharactersAsync_traverses_encoded_names_sequentially_maps_all_fields_and_sorts_ordinally()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","characters"]}""",
            """["Zulu Hero","Path/Query?# Hero","Alpha Hero"]""",
            CoreCharacter("Zulu Hero", "Human", "Male", "Guardian", 80, 12, "2020-01-02T03:04:05Z", "2026-01-02T03:04:05Z", 4, "\"future\":true"),
            CoreCharacter("Path/Query?# Hero", "Asura", "Female", "Engineer", 31, 0, "2021-02-03T04:05:06Z", "2026-02-03T04:05:06Z", 0),
            CoreCharacter("Alpha Hero", "Norn", "Female", "Ranger", 2, 99, "2022-03-04T05:06:07Z", "2026-03-04T05:06:07Z", 8));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var characters = await client.GetCharactersAsync(CancellationToken.None);

        Assert.Equal(
        [
            ("Alpha Hero", "Norn", "Female", "Ranger", 2, 99L, DateTimeOffset.Parse("2022-03-04T05:06:07Z"), DateTimeOffset.Parse("2026-03-04T05:06:07Z"), 8L),
            ("Path/Query?# Hero", "Asura", "Female", "Engineer", 31, 0L, DateTimeOffset.Parse("2021-02-03T04:05:06Z"), DateTimeOffset.Parse("2026-02-03T04:05:06Z"), 0L),
            ("Zulu Hero", "Human", "Male", "Guardian", 80, 12L, DateTimeOffset.Parse("2020-01-02T03:04:05Z"), DateTimeOffset.Parse("2026-01-02T03:04:05Z"), 4L)
        ], characters.Characters.Select(character => (character.Name, character.Race, character.Gender, character.Profession, character.Level, character.AgeSeconds, character.Created, character.LastModified, character.Deaths)));
        Assert.Equal(
        [
            "/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
            "/v2/characters?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
            "/v2/characters/Zulu%20Hero/core?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
            "/v2/characters/Path%2FQuery%3F%23%20Hero/core?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
            "/v2/characters/Alpha%20Hero/core?lang=en&v=2025-08-29T01%3A00%3A00.000Z"
        ], handler.RequestUris);
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetCharactersAsync_accepts_an_empty_roster()
    {
        var handler = new RecordingHandler("""{"permissions":["account","characters"]}""", "[]");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var characters = await client.GetCharactersAsync(CancellationToken.None);

        Assert.Empty(characters.Characters);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("null")]
    [InlineData("[\"\"]")]
    [InlineData("[\"Duplicate Hero\",\"Duplicate Hero\"]")]
    public async Task GetCharactersAsync_rejects_malformed_missing_or_duplicate_character_lists(string characterResponse)
    {
        var handler = new RecordingHandler("""{"permissions":["account","characters"]}""", characterResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharactersAsync(CancellationToken.None));

        Assert.Contains("character-list response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("{}")]
    [InlineData("{\"name\":\"Synthetic Hero\",\"race\":\"Human\",\"gender\":\"Male\",\"profession\":\"Guardian\",\"level\":0,\"age\":0,\"created\":\"2020-01-02T03:04:05Z\",\"last_modified\":\"2026-01-02T03:04:05Z\",\"deaths\":0}")]
    [InlineData("{\"name\":\"Synthetic Hero\",\"race\":\"Human\",\"gender\":\"Male\",\"profession\":\"Guardian\",\"level\":1,\"age\":-1,\"created\":\"2020-01-02T03:04:05Z\",\"last_modified\":\"2026-01-02T03:04:05Z\",\"deaths\":0}")]
    [InlineData("{\"name\":\"Synthetic Hero\",\"race\":\"Human\",\"gender\":\"Male\",\"profession\":\"Guardian\",\"level\":1,\"age\":0,\"created\":\"0001-01-01T00:00:00Z\",\"last_modified\":\"2026-01-02T03:04:05Z\",\"deaths\":0}")]
    public async Task GetCharactersAsync_rejects_malformed_missing_or_invalid_core_rows(string coreResponse)
    {
        var handler = new RecordingHandler("""{"permissions":["account","characters"]}""", """["Synthetic Hero"]""", coreResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharactersAsync(CancellationToken.None));

        Assert.Contains("character-core response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetCharactersAsync_rejects_a_core_name_mismatch()
    {
        var handler = new RecordingHandler("""{"permissions":["account","characters"]}""", """["Requested Hero"]""", CoreCharacter("Returned Hero"));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharactersAsync(CancellationToken.None));

        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetCharactersAsync_rejects_list_or_core_authentication_failures_without_continuing(bool coreFailure)
    {
        var responses = coreFailure
            ? new object[] { """{"permissions":["account","characters"]}""", """["First Hero","Unrequested Hero"]""", new ResponseSpec("Permission denied", HttpStatusCode.Forbidden) }
            : ["""{"permissions":["account","characters"]}""", new ResponseSpec("Permission denied", HttpStatusCode.Unauthorized)];
        var handler = new RecordingHandler(responses);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharactersAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Equal(coreFailure ? 3 : 2, handler.RequestUris.Count);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("Unrequested", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(206, false)]
    [InlineData(206, true)]
    [InlineData(500, false)]
    [InlineData(500, true)]
    public async Task GetCharactersAsync_rejects_non_ok_responses_as_total_and_stops_traversal(int statusCode, bool coreFailure)
    {
        var responses = coreFailure
            ? new object[] { """{"permissions":["account","characters"]}""", """["First Hero","Unrequested Hero"]""", new ResponseSpec("private response", (HttpStatusCode)statusCode) }
            : ["""{"permissions":["account","characters"]}""", new ResponseSpec("[]", (HttpStatusCode)statusCode)];
        var handler = new RecordingHandler(responses);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharactersAsync(CancellationToken.None));

        Assert.Equal(coreFailure ? 3 : 2, handler.RequestUris.Count);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("Unrequested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCharactersAsync_propagates_caller_cancellation_and_stops_traversal()
    {
        using var cancellationSource = new CancellationTokenSource();
        var requests = 0;
        var handler = new RecordingHandler(
            """{"permissions":["account","characters"]}""",
            """["First Hero","Unrequested Hero"]""",
            CoreCharacter("First Hero"))
        {
            OnRequest = () =>
            {
                if (++requests == 3) cancellationSource.Cancel();
            }
        };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCharactersAsync(cancellationSource.Token));

        Assert.Equal(3, handler.RequestUris.Count);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("Unrequested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetTradingPostDeliveryAsync_missing_key_is_actionable_and_makes_no_request()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetTradingPostDeliveryAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestUris);
    }

    [Theory]
    [InlineData("{\"permissions\":[\"tradingpost\"]}", "account permission")]
    [InlineData("{\"permissions\":[\"account\"]}", "tradingpost permission")]
    public async Task GetTradingPostDeliveryAsync_requires_each_permission_before_delivery_request(string tokenResponse, string requiredPermission)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(tokenResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetTradingPostDeliveryAsync(CancellationToken.None));

        Assert.Contains(requiredPermission, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetTradingPostDeliveryAsync_normalizes_complete_delivery_without_aggregating_items()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","tradingpost"]}""",
            """{"coins":4294967295,"items":[{"id":101,"count":2},{"id":101,"count":4294967296},{"id":202,"count":3}]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var delivery = await client.GetTradingPostDeliveryAsync(CancellationToken.None);

        Assert.Equal(4294967295L, delivery.Coins);
        Assert.Equal([(101L, 2L), (101L, 4294967296L), (202L, 3L)], delivery.Items.Select(item => (item.Id, item.Count)));
        Assert.Equal(
            [
                "/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/commerce/delivery?lang=en&v=2025-08-29T01%3A00%3A00.000Z"
            ],
            handler.RequestUris);
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetTradingPostDeliveryAsync_accepts_zero_coins_and_empty_items()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","tradingpost"]}""",
            """{"coins":0,"items":[]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var delivery = await client.GetTradingPostDeliveryAsync(CancellationToken.None);

        Assert.Equal(0L, delivery.Coins);
        Assert.Empty(delivery.Items);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"coins\":null,\"items\":[]}")]
    [InlineData("{\"coins\":-1,\"items\":[]}")]
    [InlineData("{\"coins\":0}")]
    [InlineData("{\"coins\":0,\"items\":null}")]
    [InlineData("{\"coins\":0,\"items\":[null]}")]
    [InlineData("{\"coins\":0,\"items\":[{}]}")]
    [InlineData("{\"coins\":0,\"items\":[{\"id\":0,\"count\":1}]}")]
    [InlineData("{\"coins\":0,\"items\":[{\"id\":-1,\"count\":1}]}")]
    [InlineData("{\"coins\":0,\"items\":[{\"id\":1,\"count\":0}]}")]
    [InlineData("{\"coins\":0,\"items\":[{\"id\":1,\"count\":-1}]}")]
    [InlineData("{\"coins\":0,\"items\":[{\"id\":\"1\",\"count\":1}]}")]
    [InlineData("{\"coins\":0,\"items\":[{\"id\":1,\"count\":\"1\"}]}")]
    public async Task GetTradingPostDeliveryAsync_rejects_malformed_or_invalid_delivery_responses(string deliveryResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","tradingpost"]}""",
            deliveryResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetTradingPostDeliveryAsync(CancellationToken.None));

        Assert.Contains("delivery response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(deliveryResponse, error.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetTradingPostDeliveryAsync_rejects_partial_content_even_when_payload_is_valid()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec("""{"coins":0,"items":[]}""", HttpStatusCode.PartialContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetTradingPostDeliveryAsync(CancellationToken.None));

        Assert.Contains("delivery request failed with HTTP 206", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetTradingPostDeliveryAsync_reuses_authenticated_single_retry_for_delivery()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec("", HttpStatusCode.ServiceUnavailable),
            new ResponseSpec("""{"coins":0,"items":[]}"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var delivery = await client.GetTradingPostDeliveryAsync(CancellationToken.None);

        Assert.Empty(delivery.Items);
        Assert.Equal(3, handler.RequestUris.Count);
        Assert.Equal(2, handler.RequestUris.Count(uri => uri.StartsWith("/v2/commerce/delivery?", StringComparison.Ordinal)));
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetTradingPostDeliveryAsync_maps_auth_and_http_failures_without_exposing_response_content()
    {
        var apiKey = new string('k', 16);
        var authHandler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec("private delivery content", HttpStatusCode.Forbidden));
        using var authHttpClient = new HttpClient(authHandler) { BaseAddress = new Uri("https://example.test") };
        var authClient = new Gw2ApiClient(authHttpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var authError = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => authClient.GetTradingPostDeliveryAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", authError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, authError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private delivery content", authError.Message, StringComparison.Ordinal);
        Assert.Equal(2, authHandler.RequestUris.Count);

        var httpHandler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec("private delivery content", HttpStatusCode.InternalServerError));
        using var failureHttpClient = new HttpClient(httpHandler) { BaseAddress = new Uri("https://example.test") };
        var failureClient = new Gw2ApiClient(failureHttpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var httpError = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => failureClient.GetTradingPostDeliveryAsync(CancellationToken.None));

        Assert.Contains("delivery request failed with HTTP 500", httpError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, httpError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private delivery content", httpError.Message, StringComparison.Ordinal);
        Assert.Equal(2, httpHandler.RequestUris.Count);
    }

    [Fact]
    public async Task GetCurrentSellsAsync_missing_key_is_actionable_and_makes_no_request()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCurrentSellsAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestUris);
    }

    [Theory]
    [InlineData("{\"permissions\":[\"tradingpost\"]}", "account permission")]
    [InlineData("{\"permissions\":[\"account\"]}", "tradingpost permission")]
    public async Task GetCurrentSellsAsync_requires_each_permission_before_transaction_requests(string tokenResponse, string requiredPermission)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(tokenResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCurrentSellsAsync(CancellationToken.None));

        Assert.Contains(requiredPermission, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetCurrentSellsAsync_normalizes_one_complete_page_without_aggregating_or_reordering()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec(
                """[{"id":7001,"item_id":301,"price":4294967295,"quantity":2,"created":"2026-01-02T03:04:05Z"},{"id":7002,"item_id":301,"price":4,"quantity":4294967296,"created":"2026-02-03T04:05:06+00:00"}]""",
                Headers: PaginationHeaders(resultCount: "2", resultTotal: "2")));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var sells = await client.GetCurrentSellsAsync(CancellationToken.None);

        Assert.Equal(
            [
                (7001L, 301L, 4294967295L, 2L, DateTimeOffset.Parse("2026-01-02T03:04:05Z")),
                (7002L, 301L, 4L, 4294967296L, DateTimeOffset.Parse("2026-02-03T04:05:06Z"))
            ],
            sells.Orders.Select(order => (order.Id, order.ItemId, order.Price, order.Quantity, order.Created)));
        Assert.Equal(
            [
                "/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/commerce/transactions/current/sells?page=0&page_size=200&lang=en&v=2025-08-29T01%3A00%3A00.000Z"
            ],
            handler.RequestUris);
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetCurrentSellsAsync_exhausts_every_advertised_page_once_in_order()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec(CurrentSellPage(1, 200), Headers: PaginationHeaders(pageTotal: "3", resultCount: "200", resultTotal: "401")),
            new ResponseSpec(CurrentSellPage(201, 200), Headers: PaginationHeaders(pageTotal: "3", resultCount: "200", resultTotal: "401")),
            new ResponseSpec(CurrentSellPage(401, 1), Headers: PaginationHeaders(pageTotal: "3", resultCount: "1", resultTotal: "401")));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var sells = await client.GetCurrentSellsAsync(CancellationToken.None);

        Assert.Equal(401, sells.Orders.Count);
        Assert.Equal(7401L, sells.Orders[^1].Id);
        Assert.Equal(
            [
                "/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/commerce/transactions/current/sells?page=0&page_size=200&lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/commerce/transactions/current/sells?page=1&page_size=200&lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/commerce/transactions/current/sells?page=2&page_size=200&lang=en&v=2025-08-29T01%3A00%3A00.000Z"
            ],
            handler.RequestUris);
    }

    [Fact]
    public async Task GetCurrentSellsAsync_accepts_only_a_consistent_empty_result()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec("[]", Headers: PaginationHeaders(pageTotal: "0", resultCount: "0", resultTotal: "0")));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var sells = await client.GetCurrentSellsAsync(CancellationToken.None);

        Assert.Empty(sells.Orders);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    public static TheoryData<string> InvalidCurrentSellResponses => new()
    {
        "{malformed",
        "null",
        "{}",
        "[null]",
        "[{}]",
        "[{\"id\":0,\"item_id\":1,\"price\":1,\"quantity\":1,\"created\":\"2026-01-02T03:04:05Z\"}]",
        "[{\"id\":1,\"item_id\":0,\"price\":1,\"quantity\":1,\"created\":\"2026-01-02T03:04:05Z\"}]",
        "[{\"id\":1,\"item_id\":1,\"price\":0,\"quantity\":1,\"created\":\"2026-01-02T03:04:05Z\"}]",
        "[{\"id\":1,\"item_id\":1,\"price\":1,\"quantity\":0,\"created\":\"2026-01-02T03:04:05Z\"}]",
        "[{\"id\":1,\"item_id\":1,\"price\":1,\"quantity\":1}]",
        "[{\"id\":1,\"item_id\":1,\"price\":1,\"quantity\":1,\"created\":\"invalid\"}]",
        "[{\"id\":\"1\",\"item_id\":1,\"price\":1,\"quantity\":1,\"created\":\"2026-01-02T03:04:05Z\"}]"
    };

    [Theory]
    [MemberData(nameof(InvalidCurrentSellResponses))]
    public async Task GetCurrentSellsAsync_rejects_malformed_or_invalid_rows_without_exposing_content(string sellsResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec(sellsResponse, Headers: PaginationHeaders(resultCount: "1", resultTotal: "1")));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCurrentSellsAsync(CancellationToken.None));

        Assert.Contains("current-sells response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sellsResponse, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("X-Page-Size")]
    [InlineData("X-Page-Total")]
    [InlineData("X-Result-Count")]
    [InlineData("X-Result-Total")]
    public async Task GetCurrentSellsAsync_rejects_missing_pagination_headers(string missingHeader)
    {
        var headers = PaginationHeaders(resultCount: "1", resultTotal: "1");
        headers.Remove(missingHeader);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec(CurrentSellPage(1, 1), Headers: headers));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCurrentSellsAsync(CancellationToken.None));

        Assert.Contains("pagination", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("X-Page-Size", "not-a-number")]
    [InlineData("X-Page-Size", "0")]
    [InlineData("X-Page-Size", "199")]
    [InlineData("X-Page-Total", "-1")]
    [InlineData("X-Result-Count", "-1")]
    [InlineData("X-Result-Total", "-1")]
    public async Task GetCurrentSellsAsync_rejects_malformed_or_invalid_pagination_values(string headerName, string headerValue)
    {
        var headers = PaginationHeaders(resultCount: "1", resultTotal: "1");
        headers[headerName] = headerValue;
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec(CurrentSellPage(1, 1), Headers: headers));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCurrentSellsAsync(CancellationToken.None));

        Assert.Contains("pagination", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCurrentSellsAsync_rejects_contradictory_page_metadata()
    {
        var handlers = new[]
        {
            new RecordingHandler(
                new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
                new ResponseSpec(CurrentSellPage(1, 1), Headers: PaginationHeaders(resultCount: "2", resultTotal: "2"))),
            new RecordingHandler(
                new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
                new ResponseSpec(CurrentSellPage(1, 1), Headers: PaginationHeaders(pageTotal: "2", resultCount: "1", resultTotal: "1"))),
            new RecordingHandler(
                new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
                new ResponseSpec("[]", Headers: PaginationHeaders(pageTotal: "1", resultCount: "0", resultTotal: "0")))
        };

        foreach (var handler in handlers)
        {
            using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
            var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

            var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCurrentSellsAsync(CancellationToken.None));

            Assert.Contains("pagination", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task GetCurrentSellsAsync_rejects_changing_pagination_metadata_and_stops()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec(CurrentSellPage(1, 200), Headers: PaginationHeaders(pageTotal: "2", resultCount: "200", resultTotal: "201")),
            new ResponseSpec(CurrentSellPage(201, 1), Headers: PaginationHeaders(pageTotal: "2", resultCount: "1", resultTotal: "202")));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCurrentSellsAsync(CancellationToken.None));

        Assert.Contains("pagination", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetCurrentSellsAsync_rejects_partial_or_failed_later_page_without_requesting_more()
    {
        foreach (var failureStatus in new[] { HttpStatusCode.PartialContent, HttpStatusCode.InternalServerError })
        {
            var handler = new RecordingHandler(
                new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
                new ResponseSpec(CurrentSellPage(1, 200), Headers: PaginationHeaders(pageTotal: "3", resultCount: "200", resultTotal: "401")),
                new ResponseSpec("private transaction content", failureStatus, Headers: PaginationHeaders(pageTotal: "3", resultCount: "200", resultTotal: "401")));
            using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
            var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

            var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCurrentSellsAsync(CancellationToken.None));

            Assert.Contains($"HTTP {(int)failureStatus}", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private transaction content", error.Message, StringComparison.Ordinal);
            Assert.Equal(3, handler.RequestUris.Count);
            Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("page=2", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task GetCurrentSellsAsync_retries_only_the_failed_page_without_duplicating_rows()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec(CurrentSellPage(1, 200), Headers: PaginationHeaders(pageTotal: "2", resultCount: "200", resultTotal: "201")),
            new ResponseSpec("", HttpStatusCode.ServiceUnavailable),
            new ResponseSpec(CurrentSellPage(201, 1), Headers: PaginationHeaders(pageTotal: "2", resultCount: "1", resultTotal: "201")));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var sells = await client.GetCurrentSellsAsync(CancellationToken.None);

        Assert.Equal(201, sells.Orders.Count);
        Assert.Equal(1, handler.RequestUris.Count(uri => uri.Contains("page=0", StringComparison.Ordinal)));
        Assert.Equal(2, handler.RequestUris.Count(uri => uri.Contains("page=1", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GetCurrentSellsAsync_maps_auth_failure_without_exposing_response_content()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec("private transaction content", HttpStatusCode.Forbidden));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCurrentSellsAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private transaction content", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLegendaryArmoryAsync_requires_a_configured_key_before_any_request()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => LegendaryArmoryClient(httpClient, string.Empty).GetLegendaryArmoryAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestUris);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("inventories")]
    [InlineData("unlocks")]
    public async Task GetLegendaryArmoryAsync_requires_each_operation_permission_before_ownership_access(string missingPermission)
    {
        var permissions = new[] { "account", "inventories", "unlocks" }.Where(permission => permission != missingPermission);
        var handler = new RecordingHandler("{\"permissions\":[" + string.Join(',', permissions.Select(permission => "\"" + permission + "\"")) + "]}");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => LegendaryArmoryClient(httpClient).GetLegendaryArmoryAsync(CancellationToken.None));

        Assert.Contains(missingPermission + " permission", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task GetLegendaryArmoryAsync_returns_sorted_ownership_preserves_signed_64_bit_counts_and_resolves_compact_metadata()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","inventories","unlocks"]}""",
            """[{"id":2,"count":0,"future":true},{"id":1,"count":9223372036854775807}]""",
            """[{"id":1,"name":"Synthetic One","type":"FutureType","details":{"type":"FutureSubtype","weight_class":"FutureWeight"}},{"id":2,"name":"Synthetic Two","type":"Armor","details":{"weight_class":"FutureArmorWeight"}}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var armory = await LegendaryArmoryClient(httpClient, apiKey).GetLegendaryArmoryAsync(CancellationToken.None);

        Assert.Equal(
            [(1L, long.MaxValue, "Synthetic One", "FutureType", "FutureSubtype", (string?)null), (2L, 0L, "Synthetic Two", "Armor", (string?)null, "FutureArmorWeight")],
            armory.Entries.Select(entry => (entry.Id, entry.ArmoryCount, entry.Name, entry.Type, entry.Subtype, entry.WeightClass)));
        Assert.True(armory.IsMetadataComplete);
        Assert.Empty(armory.Warnings);
        Assert.Equal(
            ["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/account/legendaryarmory?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/items?ids=1%2C2&lang=en&v=2025-08-29T01%3A00%3A00.000Z"],
            handler.RequestUris);
        Assert.Equal(["Bearer " + apiKey, "Bearer " + apiKey, null], handler.AuthorizationHeaders);
    }

    [Fact]
    public async Task GetLegendaryArmoryAsync_accepts_empty_ownership_without_a_public_request()
    {
        var handler = new RecordingHandler("""{"permissions":["account","inventories","unlocks"]}""", "[]");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var armory = await LegendaryArmoryClient(httpClient).GetLegendaryArmoryAsync(CancellationToken.None);

        Assert.Empty(armory.Entries);
        Assert.True(armory.IsMetadataComplete);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetLegendaryArmoryAsync_maps_authenticated_authorization_failure_to_invalid_key(HttpStatusCode statusCode)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("""{"permissions":["account","inventories","unlocks"]}""", new ResponseSpec("private ownership", statusCode));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => LegendaryArmoryClient(httpClient, apiKey).GetLegendaryArmoryAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.PartialContent)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task GetLegendaryArmoryAsync_rejects_non_200_ownership_without_metadata(HttpStatusCode statusCode)
    {
        var handler = new RecordingHandler("""{"permissions":["account","inventories","unlocks"]}""", new ResponseSpec("private ownership", statusCode));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => LegendaryArmoryClient(httpClient).GetLegendaryArmoryAsync(CancellationToken.None));

        Assert.Contains($"HTTP {(int)statusCode}", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private ownership", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Theory]
    [MemberData(nameof(InvalidLegendaryArmoryOwnershipResponses))]
    public async Task GetLegendaryArmoryAsync_rejects_invalid_or_duplicate_ownership_before_metadata(string ownershipResponse)
    {
        var handler = new RecordingHandler("""{"permissions":["account","inventories","unlocks"]}""", ownershipResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => LegendaryArmoryClient(httpClient).GetLegendaryArmoryAsync(CancellationToken.None));

        Assert.Contains("Legendary Armory response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ownershipResponse, error.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    public static TheoryData<string> InvalidLegendaryArmoryOwnershipResponses => new()
    {
        "{malformed", "{}", "null", "[null]", "[{}]", "[{\"id\":1}]", "[{\"count\":1}]",
        "[{\"id\":1,\"id\":2,\"count\":1}]", "[{\"id\":1,\"count\":1,\"count\":2}]",
        "[{\"id\":1,\"count\":1,\"future\":true,\"future\":false}]",
        "[{\"id\":0,\"count\":1}]", "[{\"id\":-1,\"count\":1}]", "[{\"id\":1.5,\"count\":1}]", "[{\"id\":9223372036854775808,\"count\":1}]",
        "[{\"id\":1,\"count\":-1}]", "[{\"id\":1,\"count\":1.5}]", "[{\"id\":1,\"count\":9223372036854775808}]",
        "[{\"id\":1,\"count\":1},{\"id\":1,\"count\":0}]", "[{\"id\":\"1\",\"count\":1}]"
    };

    [Fact]
    public async Task GetLegendaryArmoryAsync_accepts_256_rows_in_two_sequential_batches_and_rejects_257_before_metadata()
    {
        var acceptedHandler = new RecordingHandler(
            """{"permissions":["account","inventories","unlocks"]}""",
            LegendaryOwnership(Enumerable.Range(1, 256).Select(id => (long)id)),
            LegendaryMetadata(Enumerable.Range(1, 200).Select(id => (long)id)),
            LegendaryMetadata(Enumerable.Range(201, 56).Select(id => (long)id)));
        using var acceptedHttpClient = new HttpClient(acceptedHandler) { BaseAddress = new Uri("https://example.test") };

        var accepted = await LegendaryArmoryClient(acceptedHttpClient).GetLegendaryArmoryAsync(CancellationToken.None);

        Assert.Equal(256, accepted.Entries.Count);
        Assert.True(accepted.IsMetadataComplete);
        Assert.Equal(4, acceptedHandler.RequestUris.Count);
        Assert.Contains("ids=1%2C2", acceptedHandler.RequestUris[2], StringComparison.Ordinal);
        Assert.Contains("ids=201%2C202", acceptedHandler.RequestUris[3], StringComparison.Ordinal);

        var rejectedHandler = new RecordingHandler("""{"permissions":["account","inventories","unlocks"]}""", LegendaryOwnership(Enumerable.Range(1, 257).Select(id => (long)id)));
        using var rejectedHttpClient = new HttpClient(rejectedHandler) { BaseAddress = new Uri("https://example.test") };

        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => LegendaryArmoryClient(rejectedHttpClient).GetLegendaryArmoryAsync(CancellationToken.None));

        Assert.Equal(2, rejectedHandler.RequestUris.Count);
    }

    [Fact]
    public async Task GetLegendaryArmoryAsync_retains_a_valid_206_metadata_subset_with_ordered_warning()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","inventories","unlocks"]}""",
            LegendaryOwnership([1, 2]),
            new ResponseSpec("""[{"id":1,"name":"Synthetic One","type":"Weapon"}]""", HttpStatusCode.PartialContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var armory = await LegendaryArmoryClient(httpClient).GetLegendaryArmoryAsync(CancellationToken.None);

        Assert.Equal("Synthetic One", armory.Entries[0].Name);
        Assert.Null(armory.Entries[1].Name);
        Assert.False(armory.IsMetadataComplete);
        Assert.Equal([("metadata_unresolved", "items", "2")], armory.Warnings.Select(warning => (warning.Code, warning.Resolver, warning.ReferenceId)));
    }

    [Fact]
    public async Task GetLegendaryArmoryAsync_degrades_public_status_transport_and_noncaller_timeout_metadata_failures()
    {
        foreach (var publicFailure in new object[]
        {
            new ResponseSpec("private metadata", HttpStatusCode.NotFound),
            new ResponseSpec("private metadata", HttpStatusCode.InternalServerError),
            new HttpRequestException("private transport"),
            new OperationCanceledException("private timeout")
        })
        {
            var handler = new RecordingHandler("""{"permissions":["account","inventories","unlocks"]}""", LegendaryOwnership([1]), publicFailure);
            using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

            var armory = await LegendaryArmoryClient(httpClient).GetLegendaryArmoryAsync(CancellationToken.None);

            Assert.Null(Assert.Single(armory.Entries).Name);
            Assert.False(armory.IsMetadataComplete);
            Assert.Equal("1", Assert.Single(armory.Warnings).ReferenceId);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidLegendaryArmoryMetadataBatches))]
    public async Task GetLegendaryArmoryAsync_discards_only_invalid_metadata_batches(string metadataResponse, HttpStatusCode statusCode)
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","inventories","unlocks"]}""",
            LegendaryOwnership([1, 2]),
            new ResponseSpec(metadataResponse, statusCode));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var armory = await LegendaryArmoryClient(httpClient).GetLegendaryArmoryAsync(CancellationToken.None);

        Assert.All(armory.Entries, entry => Assert.Equal((string?)null, entry.Name));
        Assert.Equal(["1", "2"], armory.Warnings.Select(warning => warning.ReferenceId));
    }

    public static TheoryData<string, HttpStatusCode> InvalidLegendaryArmoryMetadataBatches => new()
    {
        { "{malformed", HttpStatusCode.OK }, { "{}", HttpStatusCode.OK }, { "[{}]", HttpStatusCode.OK },
        { "[{\"id\":1,\"name\":\"One\",\"type\":\"Weapon\"},{\"id\":1,\"name\":\"Duplicate\",\"type\":\"Weapon\"}]", HttpStatusCode.OK },
        { "[{\"id\":3,\"name\":\"Unrequested\",\"type\":\"Weapon\"}]", HttpStatusCode.OK },
        { "[{\"id\":1,\"name\":\"\",\"type\":\"Weapon\"},{\"id\":2,\"name\":\"Two\",\"type\":\"Weapon\"}]", HttpStatusCode.OK },
        { "[{\"id\":1,\"name\":\"One\",\"type\":\"\"},{\"id\":2,\"name\":\"Two\",\"type\":\"Weapon\"}]", HttpStatusCode.OK },
        { "[{\"id\":1,\"name\":\"One\",\"type\":\"Weapon\",\"details\":[]},{\"id\":2,\"name\":\"Two\",\"type\":\"Weapon\"}]", HttpStatusCode.OK },
        { "[{\"id\":1,\"name\":\"One\",\"type\":\"Weapon\",\"details\":{\"type\":\" \"}},{\"id\":2,\"name\":\"Two\",\"type\":\"Weapon\"}]", HttpStatusCode.OK },
        { "[{\"id\":1,\"name\":\"One\",\"type\":\"Weapon\",\"details\":{\"weight_class\":1}},{\"id\":2,\"name\":\"Two\",\"type\":\"Weapon\"}]", HttpStatusCode.OK },
        { "[{\"id\":1,\"name\":\"One\",\"type\":\"Weapon\"}]", HttpStatusCode.OK },
        { "[{\"id\":1,\"name\":\"One\",\"type\":\"Weapon\"},{\"id\":2,\"name\":\"Two\",\"type\":\"Weapon\"}]", HttpStatusCode.PartialContent },
        { "[]", HttpStatusCode.PartialContent }
    };

    [Fact]
    public async Task GetLegendaryArmoryAsync_continues_after_a_failed_first_metadata_batch_and_propagates_caller_cancellation_before_later_batches()
    {
        var ids = Enumerable.Range(1, 256).Select(id => (long)id).ToArray();
        var handler = new RecordingHandler(
            """{"permissions":["account","inventories","unlocks"]}""",
            LegendaryOwnership(ids),
            new ResponseSpec("private first batch", HttpStatusCode.NotFound),
            LegendaryMetadata(ids.Skip(200)));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var armory = await LegendaryArmoryClient(httpClient).GetLegendaryArmoryAsync(CancellationToken.None);

        Assert.Null(armory.Entries[0].Name);
        Assert.Equal("Synthetic 201", armory.Entries[200].Name);
        Assert.Equal(200, armory.Warnings.Count);

        using var cancellationSource = new CancellationTokenSource();
        RecordingHandler? cancellationHandler = null;
        cancellationHandler = new RecordingHandler(
            """{"permissions":["account","inventories","unlocks"]}""",
            LegendaryOwnership(ids),
            LegendaryMetadata(ids.Take(200)),
            LegendaryMetadata(ids.Skip(200)))
        {
            OnRequest = () =>
            {
                if (cancellationHandler!.RequestUris.Count == 3) cancellationSource.Cancel();
            }
        };
        using var cancellationHttpClient = new HttpClient(cancellationHandler!) { BaseAddress = new Uri("https://example.test") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LegendaryArmoryClient(cancellationHttpClient).GetLegendaryArmoryAsync(cancellationSource.Token));

        Assert.Equal(3, cancellationHandler!.RequestUris.Count);
    }

    [Fact]
    public async Task GetPublicItemsAsync_returns_rich_caller_ordered_rows_without_authentication()
    {
        var handler = new RecordingHandler(new ResponseSpec("""[{"id":2,"name":"Second Item","type":"FutureType","rarity":"FutureRarity","level":80,"vendor_value":123,"flags":["Zeta","Alpha"],"game_types":["Wvw","Pve"],"restrictions":[],"future_field":true},{"id":1,"name":"First Item","type":"Weapon","rarity":"Rare","level":0,"vendor_value":0,"flags":[],"game_types":[],"restrictions":["Beta","Alpha"]}]"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var items = await client.GetPublicItemsAsync([2, 1], CancellationToken.None);

        Assert.Equal([2L, 1L], items.Items.Select(item => item.Id));
        Assert.Equal(("Second Item", "FutureType", "FutureRarity", 80L, 123L), items.Items[0] is var first ? (first.Name, first.Type, first.Rarity, first.Level, first.VendorValue) : default);
        Assert.Equal(["Alpha", "Zeta"], items.Items[0].Flags);
        Assert.Equal(["Pve", "Wvw"], items.Items[0].GameTypes);
        Assert.Equal(["Alpha", "Beta"], items.Items[1].Restrictions);
        Assert.Empty(items.MissingItemIds);
        Assert.Empty(items.Warnings);
        Assert.Equal(["/v2/items?ids=2%2C1&lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
        Assert.Equal([null], handler.AuthorizationHeaders);
    }

    [Fact]
    public async Task GetPublicItemsAsync_accepts_a_proper_partial_subset_and_normalizes_blank_names()
    {
        var handler = new RecordingHandler(new ResponseSpec("""[{"id":2,"name":" \t","type":"Armor","rarity":"Rare","level":1,"vendor_value":2,"flags":[],"game_types":[],"restrictions":[]}]""", HttpStatusCode.PartialContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var items = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test")).GetPublicItemsAsync([3, 2, 1], CancellationToken.None);

        Assert.Equal([2L], items.Items.Select(item => item.Id));
        Assert.Null(items.Items[0].Name);
        Assert.Equal([3L, 1L], items.MissingItemIds);
        Assert.Equal(["One or more returned public item names were blank and are represented as null."], items.Warnings);
    }

    [Fact]
    public async Task GetPublicItemsAsync_treats_explicit_id_not_found_as_all_missing()
    {
        var handler = new RecordingHandler(new ResponseSpec("private item payload", HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var items = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test")).GetPublicItemsAsync([2, 1], CancellationToken.None);

        Assert.Empty(items.Items);
        Assert.Equal([2L, 1L], items.MissingItemIds);
        Assert.Empty(items.Warnings);
        Assert.Equal([null], handler.AuthorizationHeaders);
    }

    [Theory]
    [MemberData(nameof(InvalidPublicItemIdBatches))]
    public async Task GetPublicItemsAsync_rejects_invalid_batches_before_request(long[] itemIds)
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test")).GetPublicItemsAsync(itemIds, CancellationToken.None));

        Assert.Empty(handler.RequestUris);
    }

    public static TheoryData<long[]> InvalidPublicItemIdBatches => new()
    {
        { [] }, { [0] }, { [-1] }, { [1, 1] }, { Enumerable.Range(1, 101).Select(id => (long)id).ToArray() }
    };

    [Theory]
    [MemberData(nameof(InvalidPublicItemResponses))]
    public async Task GetPublicItemsAsync_rejects_malformed_records_and_status_body_contradictions(ResponseSpec response)
    {
        var handler = new RecordingHandler(response);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => new Gw2ApiClient(httpClient, new Gw2ApiOptions("secret", "https://example.test")).GetPublicItemsAsync([1, 2], CancellationToken.None));

        Assert.Contains("public item", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<ResponseSpec> InvalidPublicItemResponses => new()
    {
        { new ResponseSpec("private body", HttpStatusCode.InternalServerError) },
        { new ResponseSpec("""[{"id":1,"name":"One","type":"Weapon","rarity":"Rare","level":0,"vendor_value":0,"flags":[],"game_types":[],"restrictions":[]}]""") },
        { new ResponseSpec("[]", HttpStatusCode.PartialContent) },
        { new ResponseSpec("""[{"id":1,"name":"One","type":"Weapon","rarity":"Rare","level":0,"vendor_value":0,"flags":[],"game_types":[],"restrictions":[]},{"id":2,"name":"Two","type":"Armor","rarity":"Fine","level":0,"vendor_value":0,"flags":[],"game_types":[],"restrictions":[]}]""", HttpStatusCode.PartialContent) },
        { new ResponseSpec("""[{"id":1,"name":"One","type":"Weapon","rarity":"Rare","level":0,"vendor_value":0,"flags":[null],"game_types":[],"restrictions":[]}]""", HttpStatusCode.PartialContent) },
        { new ResponseSpec("""[{"id":1,"name":"One","type":"Weapon","rarity":"Rare","level":0,"vendor_value":0,"flags":[" "],"game_types":[],"restrictions":[]}]""", HttpStatusCode.PartialContent) },
        { new ResponseSpec("""[{"id":1,"name":"One","type":"Weapon","rarity":"Rare","level":-1,"vendor_value":0,"flags":[],"game_types":[],"restrictions":[]}]""", HttpStatusCode.PartialContent) }
    };

    [Fact]
    public async Task GetPublicItemsAsync_retries_transient_responses_and_propagates_caller_cancellation()
    {
        var handler = new RecordingHandler(new ResponseSpec("", HttpStatusCode.ServiceUnavailable), new ResponseSpec("""[{"id":1,"name":"One","type":"Weapon","rarity":"Rare","level":0,"vendor_value":0,"flags":[],"game_types":[],"restrictions":[]}]"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        await new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test")).GetPublicItemsAsync([1], CancellationToken.None);

        Assert.Equal(2, handler.RequestUris.Count);
        Assert.All(handler.AuthorizationHeaders, value => Assert.Null(value));

        using var cancellationSource = new CancellationTokenSource();
        var cancellationHandler = new RecordingHandler("""[{"id":1,"name":"One","type":"Weapon","rarity":"Rare","level":0,"vendor_value":0,"flags":[],"game_types":[],"restrictions":[]}]""") { OnRequest = cancellationSource.Cancel };
        using var cancellationHttpClient = new HttpClient(cancellationHandler) { BaseAddress = new Uri("https://example.test") };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Gw2ApiClient(cancellationHttpClient, new Gw2ApiOptions(string.Empty, "https://example.test")).GetPublicItemsAsync([1], cancellationSource.Token));
    }

    [Fact]
    public async Task GetItemsAsync_requests_only_caller_ids_in_order_without_authentication()
    {
        var handler = new RecordingHandler(new ResponseSpec("""[{"id":2,"name":"Second Item"},{"id":1,"name":"First Item"}]"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var items = await client.GetItemsAsync([2, 1], CancellationToken.None);

        Assert.Equal([(2L, "Second Item"), (1L, "First Item")], items.Items.Select(item => (item.Id, item.Name)));
        Assert.Empty(items.MissingItemIds);
        Assert.Equal(["/v2/items?ids=2%2C1&lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
        Assert.Equal([null], handler.AuthorizationHeaders);
    }

    [Theory]
    [MemberData(nameof(InvalidItemIdBatches))]
    public async Task GetItemsAsync_rejects_invalid_batches_before_request(long[] itemIds)
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.GetItemsAsync(itemIds, CancellationToken.None));

        Assert.Empty(handler.RequestUris);
    }

    public static TheoryData<long[]> InvalidItemIdBatches => new()
    {
        { [] },
        { [0] },
        { [-1] },
        { [1, 1] },
        { Enumerable.Range(1, 201).Select(value => (long)value).ToArray() }
    };

    [Fact]
    public async Task GetItemsAsync_accepts_partial_content_and_reports_missing_ids_in_caller_order()
    {
        var handler = new RecordingHandler(new ResponseSpec("""[{"id":2,"name":"Second Item"}]""", HttpStatusCode.PartialContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var items = await client.GetItemsAsync([3, 2, 1], CancellationToken.None);

        Assert.Equal([(2L, "Second Item")], items.Items.Select(item => (item.Id, item.Name)));
        Assert.Equal([3L, 1L], items.MissingItemIds);
    }

    public static TheoryData<string> InvalidItemMetadataResponses => new()
    {
        "{malformed",
        "null",
        "{}",
        "[null]",
        "[{}]",
        "[{\"id\":0,\"name\":\"Item\"}]",
        "[{\"id\":1,\"name\":\"\"}]",
        "[{\"id\":1,\"name\":\"Item\"},{\"id\":1,\"name\":\"Duplicate\"}]",
        "[{\"id\":2,\"name\":\"Unrequested\"}]",
        "[{\"id\":\"1\",\"name\":\"Wrong Type\"}]",
        "[{\"id\":1,\"name\":1}]"
    };

    [Theory]
    [MemberData(nameof(InvalidItemMetadataResponses))]
    public async Task GetItemsAsync_rejects_malformed_invalid_duplicate_or_unrequested_rows(string responseContent)
    {
        var handler = new RecordingHandler(new ResponseSpec(responseContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetItemsAsync([1], CancellationToken.None));

        Assert.Contains("item metadata", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(responseContent, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetItemsAsync_rejects_incomplete_http_200_or_nonpartial_http_206()
    {
        foreach (var response in new[]
        {
            new ResponseSpec("""[{"id":1,"name":"First Item"}]"""),
            new ResponseSpec("""[{"id":1,"name":"First Item"},{"id":2,"name":"Second Item"}]""", HttpStatusCode.PartialContent)
        })
        {
            var handler = new RecordingHandler(response);
            using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
            var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

            await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetItemsAsync([1, 2], CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetItemsAsync_maps_not_found_and_http_failure_to_redacted_metadata_error(HttpStatusCode statusCode)
    {
        var handler = new RecordingHandler(new ResponseSpec("private metadata payload", statusCode));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetItemsAsync([1], CancellationToken.None));

        Assert.Contains($"HTTP {(int)statusCode}", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private metadata payload", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetItemsAsync_reuses_unauthenticated_retry_path()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("", HttpStatusCode.ServiceUnavailable),
            new ResponseSpec("""[{"id":1,"name":"First Item"}]"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var items = await client.GetItemsAsync([1], CancellationToken.None);

        Assert.Single(items.Items);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.All(handler.AuthorizationHeaders, value => Assert.Null(value));
    }

    [Fact]
    public async Task GetCharacterBuildAsync_selects_exact_canonical_roster_name_requests_only_active_tab_and_enriches_valid_legends_before_skills()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","builds"]}""", """["Other Hero","Path/Query?# Hero"]""",
            """{"tab":2,"is_active":true,"build":{"name":"","profession":"Revenant","specializations":[{"id":10,"traits":[11,null,null]},{"id":null,"traits":[null,null,null]},{"id":null,"traits":[null,null,null]}],"skills":{"heal":20,"utilities":[21,null,null],"elite":null},"aquatic_skills":{"heal":null,"utilities":[null,null,null],"elite":null},"legends":["LegendB",null],"aquatic_legends":[null,null]}}""",
            """[{"id":10,"name":"Invocation"}]""", """[{"id":11,"name":"Trait"}]""", """[{"id":"LegendB","code":7,"swap":30}]""", """[{"id":20,"name":"Heal"},{"id":21,"name":"Utility"},{"id":30,"name":"Swap"}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var build = await client.GetCharacterBuildAsync("Path/Query?# Hero", CancellationToken.None);

        Assert.Equal("Path/Query?# Hero", build.CharacterName);
        Assert.Equal("Invocation", build.Specializations[0].Specialization!.Name);
        Assert.Equal("Utility", build.TerrestrialSkills.Utilities[0]!.Name);
        Assert.Equal(7, build.Legends!.Terrestrial[0]!.Code);
        Assert.Equal("Swap", build.Legends.Terrestrial[0]!.SwapSkill!.Name);
        Assert.True(build.IsMetadataComplete);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/characters?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/characters/Path%2FQuery%3F%23%20Hero/buildtabs/active?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/specializations?ids=10&lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/traits?ids=11&lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/legends?ids=LegendB&lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/skills?ids=20%2C21%2C30&lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetCharacterBuildAsync_degrades_invalid_metadata_batches_with_deterministic_deduplicated_warnings()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","builds"]}""", """["Synthetic Hero"]""",
            """{"tab":1,"is_active":true,"build":{"name":"Build","profession":"Guardian","specializations":[{"id":10,"traits":[11,11,null]},{"id":null,"traits":[null,null,null]},{"id":null,"traits":[null,null,null]}],"skills":{"heal":20,"utilities":[20,null,null],"elite":null},"aquatic_skills":{"heal":null,"utilities":[null,null,null],"elite":null}}}""",
            new ResponseSpec("""[{"id":10,"name":"Invocation"}]""", HttpStatusCode.PartialContent),
            new ResponseSpec("""[{"id":11,"name":"Trait"},{"id":11,"name":"Duplicate"}]"""), new ResponseSpec("private skill body", HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var build = await client.GetCharacterBuildAsync("Synthetic Hero", CancellationToken.None);

        Assert.False(build.IsMetadataComplete);
        Assert.Equal([("specializations", "10"), ("traits", "11"), ("skills", "20")], build.Warnings.Select(warning => (warning.Resolver, warning.ReferenceId)));
        Assert.Null(build.Specializations[0].Specialization!.Name);
        Assert.Null(build.Specializations[0].SelectedTraits[0]!.Name);
        Assert.Null(build.TerrestrialSkills.Heal!.Name);
    }

    [Fact]
    public async Task GetCharacterBuildAsync_accepts_a_nonempty_206_strict_subset_and_only_valid_legends_feed_swap_skills()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","builds"]}""", """["Synthetic Hero"]""",
            """{"tab":1,"is_active":true,"build":{"name":"Build","profession":"Revenant","specializations":[{"id":null,"traits":[null,null,null]},{"id":null,"traits":[null,null,null]},{"id":null,"traits":[null,null,null]}],"skills":{"heal":null,"utilities":[null,null,null],"elite":null},"aquatic_skills":{"heal":null,"utilities":[null,null,null],"elite":null},"legends":["LegendA","LegendB"],"aquatic_legends":[null,null]}}""",
            new ResponseSpec("""[{"id":"LegendB","code":7,"swap":30}]""", HttpStatusCode.PartialContent),
            """[{"id":30,"name":"Swap"}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var build = await client.GetCharacterBuildAsync("Synthetic Hero", CancellationToken.None);

        Assert.False(build.IsMetadataComplete);
        Assert.Equal([("legends", "LegendA")], build.Warnings.Select(warning => (warning.Resolver, warning.ReferenceId)));
        Assert.Null(build.Legends!.Terrestrial[0]!.Code);
        Assert.Null(build.Legends.Terrestrial[0]!.SwapSkill);
        Assert.Equal((7, 30, "Swap"), (build.Legends.Terrestrial[1]!.Code, build.Legends.Terrestrial[1]!.SwapSkill!.Id, build.Legends.Terrestrial[1]!.SwapSkill!.Name));
        Assert.Equal(5, handler.RequestUris.Count);
        Assert.Equal("/v2/skills?ids=30&lang=en&v=2025-08-29T01%3A00%3A00.000Z", handler.RequestUris[^1]);
    }

    [Fact]
    public async Task GetCharacterBuildAsync_rejects_inactive_or_malformed_authenticated_build_without_resolvers()
    {
        foreach (var payload in new[]
        {
            """{"tab":1,"is_active":false,"build":{}}""",
            """{"tab":1,"is_active":true,"build":{"name":"Build","profession":"Guardian","specializations":[]}}""",
            """{"tab":1,"tab":2,"is_active":true,"build":{}}"""
        })
        {
            var handler = new RecordingHandler("""{"permissions":["account","characters","builds"]}""", """["Synthetic Hero"]""", payload);
            using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
            var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

            var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBuildAsync("Synthetic Hero", CancellationToken.None));

            Assert.Contains("character-build response", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3, handler.RequestUris.Count);
        }
    }

    [Fact]
    public async Task GetCharacterBuildAsync_propagates_caller_cancellation_before_traversal_continues()
    {
        using var cancellationSource = new CancellationTokenSource();
        var handler = new RecordingHandler("""{"permissions":["account","characters","builds"]}""")
        {
            OnRequest = cancellationSource.Cancel
        };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCharacterBuildAsync("Synthetic Hero", cancellationSource.Token));

        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task GetCharacterBuildAsync_enriches_valid_numeric_206_rows_and_warns_only_omitted_ids()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","builds"]}""", """["Synthetic Hero"]""",
            ActiveBuild("Guardian", "[{\"id\":10,\"traits\":[null,null,null]},{\"id\":20,\"traits\":[null,null,null]},{\"id\":null,\"traits\":[null,null,null]}]"),
            new ResponseSpec("""[{"id":10,"name":"First"}]""", HttpStatusCode.PartialContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var build = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterBuildAsync("Synthetic Hero", CancellationToken.None);

        Assert.Equal("First", build.Specializations[0].Specialization!.Name);
        Assert.Null(build.Specializations[1].Specialization!.Name);
        Assert.Equal([("specializations", "20")], build.Warnings.Select(warning => (warning.Resolver, warning.ReferenceId)));
    }

    [Theory]
    [MemberData(nameof(InvalidLegendBatches))]
    public async Task GetCharacterBuildAsync_invalid_or_missing_legends_retain_ids_warn_and_do_not_feed_skills(ResponseSpec response)
    {
        var handler = new RecordingHandler("""{"permissions":["account","characters","builds"]}""", """["Synthetic Hero"]""",
            ActiveBuild("Revenant", conditional: ",\"legends\":[\"LegendA\",null],\"aquatic_legends\":[null,null]"), response);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var build = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterBuildAsync("Synthetic Hero", CancellationToken.None);

        Assert.Equal(("LegendA", (int?)null, (Gw2NumericReference?)null), (build.Legends!.Terrestrial[0]!.Id, build.Legends.Terrestrial[0]!.Code, build.Legends.Terrestrial[0]!.SwapSkill));
        Assert.Equal([("legends", "LegendA")], build.Warnings.Select(warning => (warning.Resolver, warning.ReferenceId)));
        Assert.DoesNotContain(handler.RequestUris, uri => uri.StartsWith("/v2/skills?", StringComparison.Ordinal));
    }

    public static TheoryData<ResponseSpec> InvalidLegendBatches => new()
    {
        { new ResponseSpec("private legend response", HttpStatusCode.NotFound) },
        { new ResponseSpec("""[{"id":"LegendA","code":7,"swap":30},{"id":"LegendA","code":8,"swap":31}]""") }
    };

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    public async Task GetCharacterBuildAsync_failed_skills_preserve_legend_identity_and_metadata_failures_continue(string failureKind)
    {
        Exception skillFailure = failureKind == "http" ? new HttpRequestException("public skills unavailable") : new OperationCanceledException("public skills timed out");
        var handler = new RecordingHandler("""{"permissions":["account","characters","builds"]}""", """["Synthetic Hero"]""",
            ActiveBuild("Revenant", conditional: ",\"legends\":[\"LegendA\",null],\"aquatic_legends\":[null,null]"),
            """[{"id":"LegendA","code":7,"swap":30}]""", skillFailure);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var build = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterBuildAsync("Synthetic Hero", CancellationToken.None);

        var legend = build.Legends!.Terrestrial[0]!;
        Assert.Equal((7, 30, (string?)null), (legend.Code, legend.SwapSkill!.Id, legend.SwapSkill.Name));
        Assert.Equal([("skills", "30")], build.Warnings.Select(warning => (warning.Resolver, warning.ReferenceId)));
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("timeout")]
    public async Task GetCharacterBuildAsync_specialization_metadata_failure_degrades_only_its_batch_and_continues(string failureKind)
    {
        Exception failure = failureKind == "transport" ? new HttpRequestException("public specializations unavailable") : new OperationCanceledException("public specializations timed out");
        var handler = new RecordingHandler("""{"permissions":["account","characters","builds"]}""", """["Synthetic Hero"]""",
            ActiveBuild("Guardian", "[{\"id\":10,\"traits\":[11,null,null]},{\"id\":null,\"traits\":[null,null,null]},{\"id\":null,\"traits\":[null,null,null]}]", "{\"heal\":20,\"utilities\":[null,null,null],\"elite\":null}"),
            failure, """[{"id":11,"name":"Trait"}]""", """[{"id":20,"name":"Heal"}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var build = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterBuildAsync("Synthetic Hero", CancellationToken.None);

        Assert.Null(build.Specializations[0].Specialization!.Name);
        Assert.Equal("Trait", build.Specializations[0].SelectedTraits[0]!.Name);
        Assert.Equal("Heal", build.TerrestrialSkills.Heal!.Name);
        Assert.Equal([("specializations", "10")], build.Warnings.Select(warning => (warning.Resolver, warning.ReferenceId)));
    }

    [Fact]
    public async Task GetCharacterBuildAsync_parses_ranger_pets_and_tolerates_unknown_authenticated_fields()
    {
        var handler = new RecordingHandler("""{"permissions":["account","characters","builds"]}""", """["Synthetic Hero"]""",
            ActiveBuild("Ranger", conditional: ",\"pets\":{\"terrestrial\":[1,null],\"aquatic\":[null,2]}", outer: ",\"future_outer\":true", buildExtra: ",\"future_build\":true"),
            """[{"id":1,"name":"First Pet"},{"id":2,"name":"Second Pet"}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var build = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterBuildAsync("Synthetic Hero", CancellationToken.None);

        Assert.Null(build.Legends);
        Assert.Equal(("First Pet", (string?)null, (string?)null, "Second Pet"), (build.Pets!.Terrestrial[0]!.Name, build.Pets.Terrestrial[1]?.Name, build.Pets.Aquatic[0]?.Name, build.Pets.Aquatic[1]!.Name));
        Assert.True(build.IsMetadataComplete);
    }

    [Theory]
    [MemberData(nameof(ContradictoryConditionalBuilds))]
    public async Task GetCharacterBuildAsync_rejects_conditional_contradictions_and_non_ok_active_responses_without_resolvers(ResponseSpec activeResponse)
    {
        var handler = new RecordingHandler("""{"permissions":["account","characters","builds"]}""", """["Synthetic Hero"]""", activeResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterBuildAsync("Synthetic Hero", CancellationToken.None));

        Assert.Equal(3, handler.RequestUris.Count);
    }

    public static TheoryData<ResponseSpec> ContradictoryConditionalBuilds => new()
    {
        { new ResponseSpec(ActiveBuild("Ranger", conditional: ",\"pets\":{\"terrestrial\":[null,null],\"aquatic\":[null,null]},\"legends\":[null,null],\"aquatic_legends\":[null,null]")) },
        { new ResponseSpec(ActiveBuild("Revenant", conditional: ",\"pets\":{\"terrestrial\":[null,null],\"aquatic\":[null,null]},\"legends\":[null,null],\"aquatic_legends\":[null,null]")) },
        { new ResponseSpec("private active", HttpStatusCode.PartialContent) },
        { new ResponseSpec("private active", HttpStatusCode.InternalServerError) }
    };

    [Fact]
    public async Task GetCharacterInventoryAsync_selects_exact_roster_name_preserves_positions_and_maps_stack_metadata()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","inventories"]}""", """["Other Hero","Path/Query?# Hero"]""",
            """{"bags":[null,{"id":10,"size":2,"inventory":[null,{"id":1,"count":2,"charges":0,"upgrades":[2,2],"infusions":[3],"skin":4,"binding":"Character","bound_to":"Path/Query?# Hero","stats":{"id":5,"attributes":{"Zeta":2,"Alpha":1}}}]}]}""",
            """[{"id":1,"name":"Sword","type":"Weapon","rarity":"Rare","level":80,"details":{"type":"Sword","infix_upgrade":{"id":6}}},{"id":2,"name":"Upgrade"},{"id":3,"name":"Infusion"},{"id":10,"name":"Bag"}]""",
            """[{"id":5,"name":"Selected"}]""", """[{"id":4,"name":"Skin"}]"""
        );
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var inventory = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Path/Query?# Hero", CancellationToken.None);

        Assert.Equal((2, 1, 2, 1, 1), (inventory.Capacity.BagPositions, inventory.Capacity.EquippedBags, inventory.Capacity.TotalSlots, inventory.Capacity.OccupiedSlots, inventory.Capacity.EmptySlots));
        Assert.Null(inventory.Bags[0].Bag);
        var stack = inventory.Bags[1].Slots[1].Stack!;
        Assert.Equal(("Sword", 2L, 0, "Character", "Path/Query?# Hero"), (stack.Item.Name, stack.Count, stack.Charges, stack.Binding, stack.BoundTo));
        Assert.Equal(["Alpha", "Zeta"], stack.Stats!.Attributes!.Select(attribute => attribute.Name));
        Assert.Equal([2L, 2L], stack.Upgrades.Select(upgrade => upgrade.Id));
        Assert.Equal("Selected", stack.Stats.Name);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/characters?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/characters/Path%2FQuery%3F%23%20Hero/inventory?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/items?ids=1%2C2%2C3%2C10&lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/itemstats?ids=5&lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/skins?ids=4&lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetCharacterInventoryAsync_degrades_only_invalid_metadata_batches_and_orders_canonical_warnings()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","inventories"]}""", """["Synthetic Hero"]""",
            """{"bags":[{"id":10,"size":2,"inventory":[{"id":2,"count":1,"stats":{"id":4,"attributes":{}}},{"id":1,"count":1,"skin":3}]}]}""",
            new ResponseSpec("[]", HttpStatusCode.NotFound), new ResponseSpec("[]", HttpStatusCode.PartialContent), new ResponseSpec("[]", HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var inventory = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None);

        Assert.False(inventory.IsMetadataComplete);
        Assert.Equal([("items", "1"), ("items", "2"), ("items", "10"), ("itemstats", "4"), ("skins", "3")], inventory.Warnings.Select(warning => (warning.Resolver, warning.ReferenceId)));
        Assert.Null(inventory.Bags[0].Slots[0].Stack!.Stats!.Name);
        Assert.Null(inventory.Bags[0].Slots[1].Stack!.Skin!.Name);
    }

    [Theory]
    [MemberData(nameof(SelectedInventoryBounds))]
    public async Task GetCharacterInventoryAsync_enforces_each_configured_bound_at_and_over_the_limit(CharacterInventoryLimits limits, string atLimit, string overLimit)
    {
        var atHandler = InventoryHandler(atLimit, "[]");
        using var atClient = new HttpClient(atHandler) { BaseAddress = new Uri("https://example.test") };
        await new Gw2ApiClient(atClient, new Gw2ApiOptions(new string('k', 16), "https://example.test", limits)).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None);

        var overHandler = InventoryHandler(overLimit, "[]");
        using var overClient = new HttpClient(overHandler) { BaseAddress = new Uri("https://example.test") };
        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => new Gw2ApiClient(overClient, new Gw2ApiOptions(new string('k', 16), "https://example.test", limits)).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None));

        Assert.Contains("inventory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, overHandler.RequestUris.Count);
    }

    public static TheoryData<CharacterInventoryLimits, string, string> SelectedInventoryBounds => new()
    {
        { new CharacterInventoryLimits(1, 1, 1, 2, 1), "{\"bags\":[null]}", "{\"bags\":[null,null]}" },
        { new CharacterInventoryLimits(1, 1, 2, 3, 1), Inventory("{\"id\":10,\"size\":1,\"inventory\":[null]}"), Inventory("{\"id\":10,\"size\":2,\"inventory\":[null,null]}") },
        { new CharacterInventoryLimits(2, 2, 2, 4, 1), Inventory("{\"id\":10,\"size\":1,\"inventory\":[null]}", "{\"id\":11,\"size\":1,\"inventory\":[null]}"), Inventory("{\"id\":10,\"size\":2,\"inventory\":[null,null]}", "{\"id\":11,\"size\":1,\"inventory\":[null]}") },
        { new CharacterInventoryLimits(1, 1, 1, 2, 1), Inventory("{\"id\":10,\"size\":1,\"inventory\":[{\"id\":1,\"count\":1}]}"), Inventory("{\"id\":10,\"size\":1,\"inventory\":[{\"id\":1,\"count\":1,\"upgrades\":[2]}]}") },
        { new CharacterInventoryLimits(1, 1, 1, 2, 2), Inventory("{\"id\":10,\"size\":1,\"inventory\":[{\"id\":1,\"count\":1,\"stats\":{\"id\":2,\"attributes\":{\"A\":1,\"B\":2}}}]}"), Inventory("{\"id\":10,\"size\":1,\"inventory\":[{\"id\":1,\"count\":1,\"stats\":{\"id\":2,\"attributes\":{\"A\":1,\"B\":2,\"C\":3}}}]}") }
    };

    [Theory]
    [MemberData(nameof(InvalidSelectedInventoryPayloads))]
    public async Task GetCharacterInventoryAsync_rejects_structural_and_per_stack_contradictions(string payload)
    {
        var handler = InventoryHandler(payload, "[]");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None));

        Assert.Equal(3, handler.RequestUris.Count);
    }

    public static TheoryData<string> InvalidSelectedInventoryPayloads => new()
    {
        { "{\"bags\":[],\"bags\":[]}" },
        { Inventory("{\"id\":1,\"size\":2,\"inventory\":[null]}") },
        { StackPayload("{\"id\":1,\"count\":0}") }, { StackPayload("{\"id\":1,\"count\":251}") },
        { StackPayload("{\"id\":1,\"count\":1,\"charges\":-1}") }, { StackPayload("{\"id\":1,\"count\":1,\"charges\":1.5}") },
        { StackPayload("{\"id\":1,\"count\":1,\"stats\":{\"id\":2,\"attributes\":[]}}") },
        { StackPayload("{\"id\":1,\"count\":1,\"stats\":{\"id\":2,\"attributes\":{\"A\":1,\"A\":2}}}") },
        { StackPayload("{\"id\":1,\"count\":1,\"stats\":{\"id\":2,\"attributes\":{" + string.Join(',', Enumerable.Range(1, 33).Select(id => "\"A" + id + "\":1")) + "}}}") },
        { StackPayload("{\"id\":1,\"count\":1,\"upgrades\":[" + string.Join(',', Enumerable.Repeat("2", 17)) + "]}") },
        { StackPayload("{\"id\":1,\"count\":1,\"infusions\":[" + string.Join(',', Enumerable.Repeat("2", 17)) + "]}") },
        { StackPayload("{\"id\":1,\"count\":1,\"binding\":\"Character\"}") },
        { StackPayload("{\"id\":1,\"count\":1,\"binding\":\"Account\",\"bound_to\":\"Synthetic Hero\"}") }
    };

    [Fact]
    public async Task GetCharacterInventoryAsync_accepts_future_binding_additive_fields_and_per_stack_component_maxima()
    {
        var payload = StackPayload("{\"id\":1,\"count\":1,\"binding\":\"FutureBinding\",\"bound_to\":\"Synthetic Hero\",\"upgrades\":[" + string.Join(',', Enumerable.Repeat("2", 16)) + "],\"infusions\":[" + string.Join(',', Enumerable.Repeat("3", 16)) + "],\"future\":true}", ",\"future_outer\":true");
        var handler = InventoryHandler(payload, "[]");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var inventory = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None);

        Assert.Equal("FutureBinding", inventory.Bags[0].Slots[0].Stack!.Binding);
        Assert.Equal((16, 16), (inventory.Bags[0].Slots[0].Stack!.Upgrades.Count, inventory.Bags[0].Slots[0].Stack!.Infusions.Count));
    }

    [Fact]
    public async Task GetCharacterInventoryAsync_skips_public_calls_without_metadata_references_and_batches_ascending_items_sequentially()
    {
        var emptyHandler = InventoryHandler("""{"bags":[]}""");
        using var emptyClient = new HttpClient(emptyHandler) { BaseAddress = new Uri("https://example.test") };
        await new Gw2ApiClient(emptyClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None);
        Assert.Equal(3, emptyHandler.RequestUris.Count);

        var firstChunk = Enumerable.Range(1, 200).Select(id => (long)id).ToArray();
        var secondChunk = new[] { 201L }.Concat(Enumerable.Range(1001, 6).Select(id => (long)id)).ToArray();
        var handler = InventoryHandler(LargeInventoryPayload(), ItemMetadata(firstChunk), ItemMetadata(secondChunk));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var inventory = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None);

        Assert.True(inventory.IsMetadataComplete);
        Assert.Equal("/v2/items?ids=" + Uri.EscapeDataString(string.Join(',', firstChunk)) + "&lang=en&v=2025-08-29T01%3A00%3A00.000Z", handler.RequestUris[3]);
        Assert.Equal("/v2/items?ids=" + Uri.EscapeDataString(string.Join(',', secondChunk)) + "&lang=en&v=2025-08-29T01%3A00%3A00.000Z", handler.RequestUris[4]);
    }

    [Theory]
    [MemberData(nameof(DegradedItemBatches))]
    public async Task GetCharacterInventoryAsync_continues_after_item_batch_degradation_and_keeps_later_metadata(object firstResponse)
    {
        var firstChunk = Enumerable.Range(1, 200).Select(id => (long)id).ToArray();
        var secondChunk = new[] { 201L }.Concat(Enumerable.Range(1001, 6).Select(id => (long)id)).ToArray();
        var handler = InventoryHandler(LargeInventoryPayload(), firstResponse, ItemMetadata(secondChunk));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var inventory = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None);

        Assert.False(inventory.IsMetadataComplete);
        Assert.Equal(200, inventory.Warnings.Count(warning => warning.Resolver == "items"));
        Assert.Equal("Item 201", inventory.Bags.SelectMany(bag => bag.Slots).Select(slot => slot.Stack).First(stack => stack?.Item.Id == 201)!.Item.Name);
        Assert.Equal(5, handler.RequestUris.Count);
    }

    public static TheoryData<object> DegradedItemBatches => new()
    {
        { new ResponseSpec("[]", HttpStatusCode.NotFound) },
        { new ResponseSpec("[]", HttpStatusCode.BadRequest) },
        { new ResponseSpec("not-json") },
        { new HttpRequestException("public item failure") },
        { new OperationCanceledException("public item timeout") }
    };

    [Fact]
    public async Task GetCharacterInventoryAsync_retains_a_valid_nonempty_strict_206_item_subset()
    {
        var handler = InventoryHandler(MetadataInventoryPayload(), new ResponseSpec("""[{"id":1,"name":"One","type":"Weapon","rarity":"Rare","level":80}]""", HttpStatusCode.PartialContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var inventory = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None);

        Assert.Equal("One", inventory.Bags[0].Slots[0].Stack!.Item.Name);
        Assert.Equal([("items", "2"), ("items", "10")], inventory.Warnings.Select(warning => (warning.Resolver, warning.ReferenceId)));
    }

    [Fact]
    public async Task GetCharacterInventoryAsync_uses_item_default_stats_when_authenticated_selected_stats_are_absent()
    {
        var handler = InventoryHandler(StackPayload("{\"id\":1,\"count\":1}"),
            """[{"id":1,"name":"Sword","type":"Weapon","rarity":"Rare","level":80,"details":{"infix_upgrade":{"id":7}}},{"id":10,"name":"Bag"}]""",
            """[{"id":7,"name":"Default prefix"}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var inventory = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None);

        var stat = inventory.Bags[0].Slots[0].Stack!.Stats!;
        Assert.Equal((7L, "Default prefix", "ItemDefault"), (stat.Id, stat.Name, stat.Source));
        Assert.Null(stat.Attributes);
        Assert.Equal(
            ["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/characters?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/characters/Synthetic%20Hero/inventory?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/items?ids=1%2C10&lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/itemstats?ids=7&lang=en&v=2025-08-29T01%3A00%3A00.000Z"],
            handler.RequestUris);
    }

    [Fact]
    public async Task GetCharacterInventoryAsync_continues_skin_name_batches_after_an_invalid_first_chunk()
    {
        var skinIds = Enumerable.Range(1, 201).Select(id => (long)id).ToArray();
        var firstChunk = skinIds[..200];
        var limits = new CharacterInventoryLimits(6, 40, 240, 300, 1);
        var handler = InventoryHandler(LargeSkinInventoryPayload(), ItemMetadata(new[] { 1L }.Concat(Enumerable.Range(1001, 6).Select(id => (long)id))),
            """[{"id":1,"name":"First"},{"id":1,"name":"Duplicate"}]""",
            """[{"id":201,"name":"Last skin"}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var inventory = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test", limits)).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None);

        var stacks = inventory.Bags.SelectMany(bag => bag.Slots).Select(slot => slot.Stack).Where(stack => stack is not null).Select(stack => stack!).ToArray();
        Assert.Null(stacks[0].Skin!.Name);
        Assert.Equal("Last skin", stacks.Single(stack => stack.Skin!.Id == 201).Skin!.Name);
        var warnings = inventory.Warnings.Where(warning => warning.Resolver == "skins").ToArray();
        Assert.Equal(200, warnings.Length);
        Assert.Equal(Enumerable.Range(1, 200).Select(id => id.ToString()), warnings.Select(warning => warning.ReferenceId));
        Assert.Equal("/v2/skins?ids=" + Uri.EscapeDataString(string.Join(',', firstChunk)) + "&lang=en&v=2025-08-29T01%3A00%3A00.000Z", handler.RequestUris[4]);
        Assert.Equal("/v2/skins?ids=201&lang=en&v=2025-08-29T01%3A00%3A00.000Z", handler.RequestUris[5]);
    }

    [Theory]
    [MemberData(nameof(InvalidItemMetadataBatches))]
    public async Task GetCharacterInventoryAsync_discards_only_invalid_item_metadata_batches(object response)
    {
        var handler = InventoryHandler(MetadataInventoryPayload(), response);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var inventory = await new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None);

        Assert.Equal(["1", "2", "10"], inventory.Warnings.Where(warning => warning.Resolver == "items").Select(warning => warning.ReferenceId));
    }

    public static TheoryData<object> InvalidItemMetadataBatches => new()
    {
        { new ResponseSpec("""[{"id":1,"name":"One","type":"Weapon","rarity":"Rare","level":80},{"id":1,"name":"Again","type":"Weapon","rarity":"Rare","level":80}]""") },
        { new ResponseSpec("""[{"id":99,"name":"Other","type":"Weapon","rarity":"Rare","level":80}]""") },
        { new ResponseSpec("not-json") },
        { new ResponseSpec("""[{"id":1,"name":"One","type":"Weapon","rarity":"Rare","level":80},{"id":2,"name":"Two","type":"Weapon","rarity":"Rare","level":80},{"id":10,"name":"Bag"}]""", HttpStatusCode.PartialContent) }
    };

    [Fact]
    public async Task GetCharacterInventoryAsync_propagates_caller_cancellation_during_public_metadata_without_later_requests()
    {
        using var cancellationSource = new CancellationTokenSource();
        RecordingHandler? handler = null;
        handler = InventoryHandler(MetadataInventoryPayload(), () => { if (handler!.RequestUris.Count == 4) cancellationSource.Cancel(); }, """[{"id":1,"name":"One","type":"Weapon","rarity":"Rare","level":80},{"id":2,"name":"Two","type":"Weapon","rarity":"Rare","level":80},{"id":10,"name":"Bag"}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Synthetic Hero", cancellationSource.Token));

        Assert.Equal(4, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("characters")]
    [InlineData("inventories")]
    public async Task GetCharacterInventoryAsync_requires_each_permission_before_roster(string missingPermission)
    {
        var permissions = new[] { "account", "characters", "inventories" }.Where(permission => permission != missingPermission);
        var handler = new RecordingHandler("{\"permissions\":[" + string.Join(',', permissions.Select(permission => "\"" + permission + "\"")) + "]}");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None));

        Assert.Contains(missingPermission + " permission", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task GetCharacterInventoryAsync_propagates_caller_cancellation_without_later_requests()
    {
        using var cancellationSource = new CancellationTokenSource();
        var handler = new RecordingHandler("""{"permissions":["account","characters","inventories"]}""") { OnRequest = cancellationSource.Cancel };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Synthetic Hero", cancellationSource.Token));

        Assert.Single(handler.RequestUris);
    }

    [Theory]
    [InlineData(206)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task GetCharacterInventoryAsync_redacts_roster_not_found_and_private_failures(int status)
    {
        var rosterHandler = new RecordingHandler("""{"permissions":["account","characters","inventories"]}""", """["Other Hero"]""");
        using var rosterClient = new HttpClient(rosterHandler) { BaseAddress = new Uri("https://example.test") };
        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => new Gw2ApiClient(rosterClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None));

        var privateHandler = new RecordingHandler("""{"permissions":["account","characters","inventories"]}""", """["Synthetic Hero"]""", new ResponseSpec("private detail", (HttpStatusCode)status));
        using var privateClient = new HttpClient(privateHandler) { BaseAddress = new Uri("https://example.test") };
        var privateError = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => new Gw2ApiClient(privateClient, new Gw2ApiOptions(new string('k', 16), "https://example.test")).GetCharacterInventoryAsync("Synthetic Hero", CancellationToken.None));
        Assert.DoesNotContain("Synthetic Hero", privateError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private detail", privateError.Message, StringComparison.Ordinal);
    }

    private const string EquipmentPermissions = """{"permissions":["account","characters","builds","inventories"]}""";
    private const string EquipmentRoster = """["Synthetic Hero"]""";
    private static RecordingHandler InventoryHandler(string inventory, params object[] publicResponses) => new([
        """{"permissions":["account","characters","inventories"]}""", """["Synthetic Hero"]""", inventory,
        .. publicResponses,
        new ResponseSpec("[]", HttpStatusCode.NotFound), new ResponseSpec("[]", HttpStatusCode.NotFound), new ResponseSpec("[]", HttpStatusCode.NotFound)
    ]);
    private static RecordingHandler InventoryHandler(string inventory, Action onRequest, params object[] publicResponses) => new([
        """{"permissions":["account","characters","inventories"]}""", """["Synthetic Hero"]""", inventory,
        .. publicResponses,
        new ResponseSpec("[]", HttpStatusCode.NotFound), new ResponseSpec("[]", HttpStatusCode.NotFound), new ResponseSpec("[]", HttpStatusCode.NotFound)
    ]) { OnRequest = onRequest };
    private static string Inventory(params string[] bags) => "{\"bags\":[" + string.Join(',', bags) + "]}";
    private static string StackPayload(string stack, string rootExtra = "") => "{\"bags\":[{\"id\":10,\"size\":1,\"inventory\":[" + stack + "]}]" + rootExtra + "}";
    private static string LargeInventoryPayload()
    {
        var itemId = 1;
        var bags = Enumerable.Range(0, 6).Select(bag =>
        {
            var slots = Enumerable.Range(0, 40).Select(_ => itemId <= 201 ? "{\"id\":" + itemId++ + ",\"count\":1}" : "null");
            return "{\"id\":" + (1001 + bag) + ",\"size\":40,\"inventory\":[" + string.Join(',', slots) + "]}";
        });
        return Inventory(bags.ToArray());
    }
    private static string LargeSkinInventoryPayload()
    {
        var skinId = 1;
        var bags = Enumerable.Range(0, 6).Select(bag =>
        {
            var slots = Enumerable.Range(0, 40).Select(_ => skinId <= 201 ? "{\"id\":1,\"count\":1,\"skin\":" + skinId++ + "}" : "null");
            return "{\"id\":" + (1001 + bag) + ",\"size\":40,\"inventory\":[" + string.Join(',', slots) + "]}";
        });
        return Inventory(bags.ToArray());
    }
    private static string MetadataInventoryPayload() => Inventory("{\"id\":10,\"size\":2,\"inventory\":[{\"id\":1,\"count\":1},{\"id\":2,\"count\":1}]}");
    private static string ItemMetadata(IEnumerable<long> ids) => "[" + string.Join(',', ids.Select(id => id <= 201
        ? "{\"id\":" + id + ",\"name\":\"Item " + id + "\",\"type\":\"Weapon\",\"rarity\":\"Rare\",\"level\":80}"
        : "{\"id\":" + id + ",\"name\":\"Bag " + id + "\"}")) + "]";
    private static Gw2ApiClient EquipmentClient(HttpClient client) => new(client, new Gw2ApiOptions(new string('k', 16), "https://example.test"));
    private static string ActiveEquipment(string equipment) => "{\"tab\":1,\"name\":\"\",\"is_active\":true,\"equipment\":" + equipment + "}";
    private static Gw2ApiClient LegendaryArmoryClient(HttpClient httpClient, string? apiKey = null) =>
        new(httpClient, new Gw2ApiOptions(apiKey ?? new string('k', 16), "https://example.test"));

    private static string LegendaryOwnership(IEnumerable<long> ids) =>
        "[" + string.Join(',', ids.Select(id => "{\"id\":" + id + ",\"count\":1}")) + "]";

    private static string LegendaryMetadata(IEnumerable<long> ids) =>
        "[" + string.Join(',', ids.Select(id => "{\"id\":" + id + ",\"name\":\"Synthetic " + id + "\",\"type\":\"Weapon\"}")) + "]";

    private static string EquipmentRow(string slot, int id, string extra = "") => "{\"slot\":\"" + slot + "\",\"id\":" + id + ",\"location\":\"Equipped\"" + extra + "}";

    private static string ActiveBuild(string profession, string? specializations = null, string? skills = null, string conditional = "", string outer = "", string buildExtra = "") =>
        "{\"tab\":1,\"is_active\":true" + outer
        + ",\"build\":{\"name\":\"Build\",\"profession\":\"" + profession + "\"" + buildExtra
        + ",\"specializations\":" + (specializations ?? "[{\"id\":null,\"traits\":[null,null,null]},{\"id\":null,\"traits\":[null,null,null]},{\"id\":null,\"traits\":[null,null,null]}]")
        + ",\"skills\":" + (skills ?? "{\"heal\":null,\"utilities\":[null,null,null],\"elite\":null}")
        + ",\"aquatic_skills\":{\"heal\":null,\"utilities\":[null,null,null],\"elite\":null}" + conditional + "}}";

    private static Dictionary<string, string> PaginationHeaders(
        string pageSize = "200",
        string pageTotal = "1",
        string resultCount = "1",
        string resultTotal = "1") => new(StringComparer.OrdinalIgnoreCase)
    {
        ["X-Page-Size"] = pageSize,
        ["X-Page-Total"] = pageTotal,
        ["X-Result-Count"] = resultCount,
        ["X-Result-Total"] = resultTotal
    };

    private static string CurrentSellPage(int firstOffset, int count) =>
        "[" + string.Join(',', Enumerable.Range(firstOffset, count).Select(offset =>
            $$"""{"id":{{7000L + offset}},"item_id":{{300L + offset}},"price":{{10L + offset}},"quantity":{{20L + offset}},"created":"2026-01-02T03:04:05Z"}""")) + "]";

    private static string CoreCharacter(
        string name,
        string race = "Human",
        string gender = "Male",
        string profession = "Guardian",
        int level = 80,
        long age = 1,
        string created = "2020-01-02T03:04:05Z",
        string lastModified = "2026-01-02T03:04:05Z",
        long deaths = 0,
        string? additionalField = null) =>
        $$"""{"name":"{{name}}","race":"{{race}}","gender":"{{gender}}","profession":"{{profession}}","level":{{level}},"age":{{age}},"created":"{{created}}","last_modified":"{{lastModified}}","deaths":{{deaths}}{{(additionalField is null ? string.Empty : "," + additionalField)}}}""";

    public sealed record ResponseSpec(
        string Content,
        HttpStatusCode StatusCode = HttpStatusCode.OK,
        TimeSpan? RetryAfter = null,
        IReadOnlyDictionary<string, string>? Headers = null);

    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public List<TimeSpan> RequestedDelays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            RequestedDelays.Add(dueTime);
            callback(state);
            return new ImmediateTimer();
        }
    }

    private sealed class ImmediateTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingHandler(params object[] responses) : HttpMessageHandler
    {
        private readonly Queue<object> responses = new(responses);

        public List<string> RequestUris { get; } = [];
        public List<string?> AuthorizationHeaders { get; } = [];
        public Action? OnRequest { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.PathAndQuery);
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
            OnRequest?.Invoke();
            var response = responses.Dequeue() switch
            {
                string content => new ResponseSpec(content, responses.Count > 0 && responses.Peek() is HttpStatusCode status ? (HttpStatusCode)responses.Dequeue() : HttpStatusCode.OK),
                ResponseSpec specification => specification,
                Exception exception => throw exception,
                _ => throw new InvalidOperationException("Unsupported test response.")
            };
            var message = new HttpResponseMessage(response.StatusCode) { Content = new StringContent(response.Content, Encoding.UTF8, "application/json") };
            if (response.RetryAfter is { } retryAfter)
            {
                message.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
            }

            if (response.Headers is not null)
            {
                foreach (var (name, value) in response.Headers)
                {
                    message.Headers.TryAddWithoutValidation(name, value);
                }
            }

            return Task.FromResult(message);
        }
    }
}
