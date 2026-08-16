using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GW2AccountMCP.Gw2;
using GW2AccountMCP.Items;
using GW2AccountMCP.Prices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class McpEndpointTests : IClassFixture<McpEndpointTests.McpApplicationFactory>
{
    private readonly HttpClient client;

    public McpEndpointTests(McpApplicationFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task Mcp_discovery_exposes_exactly_seventeen_tools_including_get_character_equipment_tabs()
    {
        await InitializeAsync();

        using var response = await PostMcpAsync(2, "tools/list", new { });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));

        var tools = document.RootElement.GetProperty("result").GetProperty("tools");
        var discoveredTools = tools.EnumerateArray().OrderBy(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.Equal(["find_items", "get_account", "get_account_holdings", "get_achievement_progress", "get_character_build", "get_character_equipment", "get_character_equipment_tabs", "get_character_inventory", "get_characters", "get_item_prices", "get_items", "get_legendary_armory", "get_mastery_progress", "get_recipes", "get_trading_post_activity", "get_wallet", "value_items"], discoveredTools.Select(tool => tool.GetProperty("name").GetString()));
        foreach (var tool in discoveredTools)
        {
            var annotations = tool.GetProperty("annotations");
            Assert.True(annotations.GetProperty("readOnlyHint").GetBoolean());
            Assert.True(annotations.GetProperty("idempotentHint").GetBoolean());
            Assert.False(annotations.GetProperty("openWorldHint").GetBoolean());
            Assert.True(tool.TryGetProperty("outputSchema", out _));
        }

        var toolsByName = discoveredTools.ToDictionary(tool => tool.GetProperty("name").GetString()!);
        var achievementProgress = toolsByName["get_achievement_progress"];
        Assert.Equal(["achievementIds"], achievementProgress.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(property => property.Name));
        AssertSchemaRequired(achievementProgress.GetProperty("inputSchema"), "achievementIds");
        AssertSchemaRequired(achievementProgress.GetProperty("outputSchema"), "rows", "missingDefinitionIds", "areAllDefinitionsResolved", "warnings", "accountProgressAsOf", "definitionsAsOf", "asOf", "isAtomicSnapshot", "sourceStatement", "completenessStatement", "scopeStatement");
        var achievementRow = achievementProgress.GetProperty("outputSchema").GetProperty("properties").GetProperty("rows").GetProperty("items");
        AssertSchemaRequired(achievementRow, "id", "accountProgressStatus", "current", "max", "done", "repeated", "isUnlocked", "completedBits", "definitionStatus", "name", "description", "requirement", "lockedText", "type", "flags", "bitCount");
        AssertSchemaRequired(achievementRow.GetProperty("properties").GetProperty("completedBits").GetProperty("items"), "index", "isDefinitionResolved", "type", "id", "text");
        Assert.DoesNotContain("key", achievementProgress.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", achievementProgress.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var masteryProgress = toolsByName["get_mastery_progress"];
        Assert.Empty(masteryProgress.GetProperty("inputSchema").GetProperty("properties").EnumerateObject());
        AssertSchemaRequired(masteryProgress.GetProperty("outputSchema"), "tracks", "pointTotals", "metadataStatus", "missingMetadataTrackIds", "areAllMetadataTracksResolved", "warnings", "accountMasteriesAsOf", "masteryPointsAsOf", "metadataAsOf", "asOf", "isAtomicSnapshot", "sourceStatement", "completenessStatement", "scopeStatement");
        AssertSchemaRequired(masteryProgress.GetProperty("outputSchema").GetProperty("properties").GetProperty("tracks").GetProperty("items"), "id", "sourceLevel", "unlockedLevelCount", "metadataStatus", "name", "requirement", "region", "order", "levelCount", "currentLevel", "nextLevel");
        var masteryLevelSchema = masteryProgress.GetProperty("outputSchema").GetProperty("properties").GetProperty("tracks").GetProperty("items").GetProperty("properties").GetProperty("currentLevel");
        AssertSchemaRequired(masteryLevelSchema, "index", "name", "description", "instruction", "pointCost", "experienceCost");
        AssertSchemaRequired(masteryProgress.GetProperty("outputSchema").GetProperty("properties").GetProperty("pointTotals").GetProperty("items"), "region", "spent", "earned", "available");
        Assert.DoesNotContain("key", masteryProgress.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var activity = toolsByName["get_trading_post_activity"];
        Assert.Equal(["mode", "page", "pageSize"], activity.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(property => property.Name));
        AssertSchemaRequired(activity.GetProperty("inputSchema"), "mode");
        var activityOutput = activity.GetProperty("outputSchema");
        Assert.Equal(["mode", "currentBuys", "currentSells", "page", "pageSize", "pageCount", "totalCount", "returnedCount", "hasMore", "asOf", "isAtomicSnapshot", "sourceStatement", "freshnessStatement", "completenessStatement"], activityOutput.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        AssertSchemaRequired(activityOutput, "mode", "currentBuys", "currentSells", "page", "pageSize", "pageCount", "totalCount", "returnedCount", "hasMore", "asOf", "isAtomicSnapshot", "sourceStatement", "freshnessStatement", "completenessStatement");
        AssertSchemaRequired(activityOutput.GetProperty("properties").GetProperty("currentBuys"), "orders", "reservedCoinPageSubtotalCopper");
        AssertSchemaRequired(activityOutput.GetProperty("properties").GetProperty("currentBuys").GetProperty("properties").GetProperty("orders").GetProperty("items"), "itemId", "unitPriceCopper", "quantity", "createdAt", "reservedCoinCopper");
        AssertSchemaRequired(activityOutput.GetProperty("properties").GetProperty("currentSells"), "orders");
        AssertSchemaRequired(activityOutput.GetProperty("properties").GetProperty("currentSells").GetProperty("properties").GetProperty("orders").GetProperty("items"), "itemId", "unitPriceCopper", "quantity", "createdAt");
        var prices = toolsByName["get_item_prices"];
        AssertSchemaRequired(prices.GetProperty("inputSchema"), "itemIds");
        AssertSchemaRequired(prices.GetProperty("outputSchema"), "items", "sourceStartedAtUtc", "sourceCompletedAtUtc", "cacheGeneratedAtUtc", "asOf", "collectionDuration", "cacheAge", "freshnessStatus", "freshnessStatement", "isCompletePriceGeneration", "warnings");
        var items = toolsByName["get_items"];
        Assert.Equal(["itemIds", "includeMaterialCategories"], items.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(property => property.Name));
        AssertSchemaRequired(items.GetProperty("inputSchema"), "itemIds");
        AssertSchemaRequired(items.GetProperty("outputSchema"), "items", "isComplete", "missingItemIds", "materialCategoriesStatus", "materialCategoriesAsOf", "isAtomicSnapshot", "warnings", "asOf", "sourceStatement");
        var itemSchema = items.GetProperty("outputSchema").GetProperty("properties").GetProperty("items").GetProperty("items");
        AssertSchemaRequired(itemSchema, "status", "id", "name", "type", "rarity", "level", "vendorValue", "flags", "gameTypes", "restrictions", "materialCategories");
        AssertSchemaRequired(itemSchema.GetProperty("properties").GetProperty("materialCategories").GetProperty("items"), "id", "name", "order");
        Assert.DoesNotContain("key", items.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", items.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var recipes = toolsByName["get_recipes"];
        Assert.Equal(["mode", "recipeIds", "itemId", "offset", "limit", "includeAccountUnlocks"], recipes.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(property => property.Name));
        AssertSchemaRequired(recipes.GetProperty("inputSchema"), "mode");
        AssertSchemaRequired(recipes.GetProperty("outputSchema"), "mode", "recipes", "resolvedRecipeIds", "missingRecipeIds", "areAllRequestedDefinitionsResolved", "selectorAsOf", "recipesAsOf", "accountUnlocksAsOf", "asOf", "isAtomicSnapshot", "isSelectorComplete", "isPageComplete", "areAllSelectedDefinitionsResolved", "totalMatches", "offset", "limit", "returnedCount", "hasMore", "selectorStatement", "warnings", "sourceStatement", "scopeStatement");
        var recipeSchema = recipes.GetProperty("outputSchema").GetProperty("properties").GetProperty("recipes").GetProperty("items");
        AssertSchemaRequired(recipeSchema, "status", "id", "type", "output", "sourceOutputItemId", "minRating", "disciplines", "flags", "timeToCraftMs", "ingredients", "accountUnlockListContainsRecipe");
        AssertSchemaRequired(recipeSchema.GetProperty("properties").GetProperty("output"), "kind", "id", "count");
        AssertSchemaRequired(recipeSchema.GetProperty("properties").GetProperty("ingredients").GetProperty("items"), "kind", "id", "count");
        Assert.DoesNotContain("key", recipes.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", recipes.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.False(recipeSchema.GetProperty("properties").TryGetProperty("price", out _));
        var values = toolsByName["value_items"];
        AssertSchemaRequired(values.GetProperty("inputSchema"), "items");
        AssertSchemaRequired(values.GetProperty("outputSchema"), "items", "immediateSale", "acquisition", "hypotheticalListing", "feePolicy", "sourceStartedAtUtc", "sourceCompletedAtUtc", "cacheGeneratedAtUtc", "asOf", "collectionDuration", "cacheAge", "freshnessStatus", "freshnessStatement", "bestPriceExtrapolationStatement", "scopeStatement", "isCompletePriceGeneration", "warnings");
        var valueInputRow = values.GetProperty("inputSchema").GetProperty("properties").GetProperty("items").GetProperty("items");
        AssertSchemaRequired(valueInputRow, "itemId", "quantity");
        AssertSchemaRequired(values.GetProperty("outputSchema").GetProperty("properties").GetProperty("items").GetProperty("items"), "itemId", "name", "quantity", "priceResourceStatus", "immediateSale", "acquisition", "hypotheticalListing");
        AssertSchemaRequired(values.GetProperty("outputSchema").GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties").GetProperty("immediateSale"), "isAvailable", "availability", "unitQuote", "gross", "listingFee", "exchangeFee", "net");
        AssertSchemaRequired(values.GetProperty("outputSchema").GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties").GetProperty("acquisition"), "isAvailable", "availability", "unitQuote", "buyerTotalCost");
        AssertSchemaRequired(values.GetProperty("outputSchema").GetProperty("properties").GetProperty("immediateSale"), "isComplete", "missingItemIds", "gross", "listingFee", "exchangeFee", "net");
        var findItems = toolsByName["find_items"];
        var findItemsInputSchema = findItems.GetProperty("inputSchema");
        var findItemsInputProperties = findItemsInputSchema.GetProperty("properties");
        Assert.True(findItemsInputProperties.TryGetProperty("query", out _));
        Assert.True(findItemsInputProperties.TryGetProperty("limit", out _));
        Assert.Contains("query", findItemsInputSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var findItemsOutputSchema = findItems.GetProperty("outputSchema");
        var findItemsOutputProperties = findItemsOutputSchema.GetProperty("properties");
        Assert.True(findItemsOutputProperties.TryGetProperty("normalizedQuery", out _));
        Assert.True(findItemsOutputProperties.TryGetProperty("candidates", out var candidatesSchema));
        Assert.True(findItemsOutputProperties.TryGetProperty("hasMore", out _));
        var candidateProperties = candidatesSchema.GetProperty("items").GetProperty("properties");
        Assert.Equal("integer", candidateProperties.GetProperty("id").GetProperty("type").GetString());
        Assert.Equal("string", candidateProperties.GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("string", candidateProperties.GetProperty("type").GetProperty("type").GetString());
        Assert.Equal("string", candidateProperties.GetProperty("rarity").GetProperty("type").GetString());
        Assert.Equal("integer", candidateProperties.GetProperty("level").GetProperty("type").GetString());
        Assert.Equal("string", candidateProperties.GetProperty("matchKind").GetProperty("type").GetString());
        var candidateRequired = candidatesSchema.GetProperty("items").GetProperty("required").EnumerateArray().Select(value => value.GetString());
        Assert.Equal(["id", "name", "type", "rarity", "level", "matchKind"], candidateRequired);
        Assert.DoesNotContain("key", findItems.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", findItems.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var accountOutputSchema = toolsByName["get_account"].GetProperty("outputSchema").GetProperty("properties");
        Assert.True(accountOutputSchema.TryGetProperty("name", out _));
        Assert.True(accountOutputSchema.TryGetProperty("asOf", out _));
        var holdingsInputSchema = toolsByName["get_account_holdings"].GetProperty("inputSchema").GetProperty("properties");
        Assert.True(holdingsInputSchema.TryGetProperty("itemIds", out _));
        Assert.True(holdingsInputSchema.TryGetProperty("currencyIds", out _));
        var holdingsOutputSchema = toolsByName["get_account_holdings"].GetProperty("outputSchema").GetProperty("properties");
        Assert.True(holdingsOutputSchema.TryGetProperty("items", out _));
        Assert.True(holdingsOutputSchema.TryGetProperty("currencies", out _));
        Assert.True(holdingsOutputSchema.TryGetProperty("isComplete", out _));
        Assert.True(holdingsOutputSchema.TryGetProperty("queriedLocations", out _));
        Assert.True(holdingsOutputSchema.TryGetProperty("unavailableLocations", out _));
        Assert.True(holdingsOutputSchema.TryGetProperty("warnings", out _));
        Assert.True(holdingsOutputSchema.TryGetProperty("asOf", out _));
        Assert.DoesNotContain("key", holdingsOutputSchema.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", holdingsOutputSchema.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var legendaryArmory = toolsByName["get_legendary_armory"];
        Assert.Empty(legendaryArmory.GetProperty("inputSchema").GetProperty("properties").EnumerateObject());
        var legendaryArmoryOutput = legendaryArmory.GetProperty("outputSchema");
        Assert.Equal(["ownershipScope", "countSemantics", "entries", "isMetadataComplete", "warnings", "asOf"], legendaryArmoryOutput.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Equal(["ownershipScope", "countSemantics", "entries", "isMetadataComplete", "warnings", "asOf"], legendaryArmoryOutput.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        AssertSchemaRequired(legendaryArmoryOutput.GetProperty("properties").GetProperty("entries").GetProperty("items"), "id", "name", "type", "subtype", "weightClass", "armoryCount");
        AssertSchemaRequired(legendaryArmoryOutput.GetProperty("properties").GetProperty("warnings").GetProperty("items"), "code", "resolver", "referenceId");
        Assert.Contains("account Legendary Armory ownership", legendaryArmory.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("one equipment template", legendaryArmory.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("not physical holdings", legendaryArmory.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("must not be added to get_account_holdings", legendaryArmory.GetProperty("description").GetString(), StringComparison.Ordinal);
        var walletOutputSchema = toolsByName["get_wallet"].GetProperty("outputSchema").GetProperty("properties");
        Assert.True(walletOutputSchema.TryGetProperty("balances", out _));
        Assert.True(walletOutputSchema.TryGetProperty("warnings", out _));
        Assert.True(walletOutputSchema.TryGetProperty("asOf", out _));
        var characters = toolsByName["get_characters"];
        var charactersInputSchema = characters.GetProperty("inputSchema");
        Assert.Empty(charactersInputSchema.GetProperty("properties").EnumerateObject());
        var charactersOutputSchema = characters.GetProperty("outputSchema");
        Assert.Equal(["characters", "asOf"], charactersOutputSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var charactersOutputProperties = charactersOutputSchema.GetProperty("properties");
        Assert.Equal(["characters", "asOf"], charactersOutputProperties.EnumerateObject().Select(property => property.Name));
        var characterProperties = charactersOutputProperties.GetProperty("characters").GetProperty("items").GetProperty("properties");
        Assert.Equal(["name", "race", "gender", "profession", "level", "ageSeconds", "created", "lastModified", "deaths"], characterProperties.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["name", "race", "gender", "profession", "level", "ageSeconds", "created", "lastModified", "deaths"], charactersOutputProperties.GetProperty("characters").GetProperty("items").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain(new string('k', 16), characters.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("token", characters.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GW2_API_KEY", document.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var characterBuild = toolsByName["get_character_build"];
        var characterBuildInput = characterBuild.GetProperty("inputSchema");
        Assert.Equal(["characterName"], characterBuildInput.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Equal(["characterName"], characterBuildInput.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var characterBuildOutput = characterBuild.GetProperty("outputSchema").GetProperty("properties");
        Assert.Equal(["characterName", "tab", "buildName", "profession", "specializations", "skills", "pets", "legends", "isMetadataComplete", "warnings", "asOf"], characterBuildOutput.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["characterName", "tab", "buildName", "profession", "specializations", "skills", "pets", "legends", "isMetadataComplete", "warnings", "asOf"], characterBuild.GetProperty("outputSchema").GetProperty("required").EnumerateArray().Select(value => value.GetString()));

        var characterEquipment = toolsByName["get_character_equipment"];
        Assert.Equal(["characterName"], characterEquipment.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Equal(["characterName"], characterEquipment.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var characterEquipmentOutput = characterEquipment.GetProperty("outputSchema");
        Assert.Equal(["characterName", "tab", "equipmentTabName", "equipment", "isOwnershipData", "isMetadataComplete", "warnings", "asOf"], characterEquipmentOutput.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Equal(["characterName", "tab", "equipmentTabName", "equipment", "isOwnershipData", "isMetadataComplete", "warnings", "asOf"], characterEquipmentOutput.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var equipmentRowSchema = characterEquipmentOutput.GetProperty("properties").GetProperty("equipment").GetProperty("items");
        Assert.Equal(["slot", "item", "stats", "upgrades", "infusions", "skin", "binding", "boundTo", "location", "referenceKind"], equipmentRowSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var equipmentItemSchema = equipmentRowSchema.GetProperty("properties").GetProperty("item");
        Assert.Equal(["id", "name", "type", "subtype", "rarity", "level"], equipmentItemSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var characterEquipmentTabs = toolsByName["get_character_equipment_tabs"];
        Assert.Equal(["characterName"], characterEquipmentTabs.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(property => property.Name));
        AssertSchemaRequired(characterEquipmentTabs.GetProperty("inputSchema"), "characterName");
        AssertSchemaRequired(characterEquipmentTabs.GetProperty("outputSchema"), "characterName", "equipmentScope", "activeTab", "tabs", "isOwnershipData", "isMetadataComplete", "warnings", "equipmentTabsAsOf", "equipmentAsOf", "itemsAsOf", "itemStatsAsOf", "skinsAsOf", "asOf", "isAtomicSnapshot", "sourceStatement", "scopeStatement", "ownershipStatement");

        var characterInventory = toolsByName["get_character_inventory"];
        Assert.Equal(["characterName"], characterInventory.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Equal(["characterName"], characterInventory.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var inventoryOutput = characterInventory.GetProperty("outputSchema");
        Assert.Equal(["characterName", "inventoryScope", "capacity", "bags", "isMetadataComplete", "warnings", "asOf"], inventoryOutput.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Equal(["characterName", "inventoryScope", "capacity", "bags", "isMetadataComplete", "warnings", "asOf"], inventoryOutput.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var inventoryProperties = inventoryOutput.GetProperty("properties");
        AssertSchemaRequired(inventoryProperties.GetProperty("capacity"), "bagPositions", "equippedBags", "totalSlots", "occupiedSlots", "emptySlots");
        var bagSchema = inventoryProperties.GetProperty("bags").GetProperty("items");
        AssertSchemaRequired(bagSchema, "bagPosition", "bag", "slots");
        var bagDetailsSchema = bagSchema.GetProperty("properties").GetProperty("bag");
        AssertSchemaRequired(bagDetailsSchema, "id", "name", "size");
        var slotSchema = bagSchema.GetProperty("properties").GetProperty("slots").GetProperty("items");
        AssertSchemaRequired(slotSchema, "slotPosition", "stack");
        var stackSchema = slotSchema.GetProperty("properties").GetProperty("stack");
        AssertSchemaRequired(stackSchema, "item", "count", "charges", "stats", "upgrades", "infusions", "skin", "binding", "boundTo");
        AssertSchemaRequired(stackSchema.GetProperty("properties").GetProperty("item"), "id", "name", "type", "subtype", "rarity", "level");
        var statsSchema = stackSchema.GetProperty("properties").GetProperty("stats");
        AssertSchemaRequired(statsSchema, "id", "name", "source", "attributes");
        AssertSchemaRequired(statsSchema.GetProperty("properties").GetProperty("attributes").GetProperty("items"), "name", "value");
        AssertSchemaRequired(stackSchema.GetProperty("properties").GetProperty("upgrades").GetProperty("items"), "id", "name");
        AssertSchemaRequired(inventoryProperties.GetProperty("warnings").GetProperty("items"), "code", "resolver", "referenceId");
        Assert.DoesNotContain("ownedTotal", characterInventory.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GW2_CHARACTER_INVENTORY", characterInventory.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("must not be added as a second ownership source", characterInventory.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAchievementProgress_returns_compact_requested_rows_with_explicit_nulls()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(30, "tools/call", new { name = "get_achievement_progress", arguments = new { achievementIds = new[] { 999L, 1L } } });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);
        using var document = JsonDocument.Parse(payload);

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(["rows", "missingDefinitionIds", "areAllDefinitionsResolved", "warnings", "accountProgressAsOf", "definitionsAsOf", "asOf", "isAtomicSnapshot", "sourceStatement", "completenessStatement", "scopeStatement"], structured.EnumerateObject().Select(property => property.Name));
        var rows = structured.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal([999L, 1L], rows.Select(row => row.GetProperty("id").GetInt64()));
        Assert.Equal("NoAccountProgressRecord", rows[0].GetProperty("accountProgressStatus").GetString());
        Assert.Equal("NoPublicAchievementResource", rows[0].GetProperty("definitionStatus").GetString());
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("done").ValueKind);
        Assert.Equal("Found", rows[1].GetProperty("definitionStatus").GetString());
        Assert.False(structured.GetProperty("areAllDefinitionsResolved").GetBoolean());
        Assert.Equal([999L], structured.GetProperty("missingDefinitionIds").EnumerateArray().Select(id => id.GetInt64()));
        Assert.False(structured.GetProperty("isAtomicSnapshot").GetBoolean());
        Assert.DoesNotContain("GW2_API_KEY", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetMasteryProgress_invokes_without_arguments_and_returns_explicit_metadata_nulls()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(31, "tools/call", new { name = "get_mastery_progress", arguments = new { } });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(["tracks", "pointTotals", "metadataStatus", "missingMetadataTrackIds", "areAllMetadataTracksResolved", "warnings", "accountMasteriesAsOf", "masteryPointsAsOf", "metadataAsOf", "asOf", "isAtomicSnapshot", "sourceStatement", "completenessStatement", "scopeStatement"], structured.EnumerateObject().Select(property => property.Name));
        Assert.Empty(structured.GetProperty("tracks").EnumerateArray());
        Assert.Empty(structured.GetProperty("pointTotals").EnumerateArray());
        Assert.Equal("NotNeeded", structured.GetProperty("metadataStatus").GetString());
        Assert.Empty(structured.GetProperty("missingMetadataTrackIds").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("metadataAsOf").ValueKind);
        Assert.False(structured.GetProperty("isAtomicSnapshot").GetBoolean());
        Assert.DoesNotContain("GW2_API_KEY", structured.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTradingPostActivity_returns_minimal_structured_current_buy_page()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(28, "tools/call", new { name = "get_trading_post_activity", arguments = new { mode = "CurrentBuys" } });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);
        using var document = JsonDocument.Parse(payload);

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(["mode", "currentBuys", "currentSells", "page", "pageSize", "pageCount", "totalCount", "returnedCount", "hasMore", "asOf", "isAtomicSnapshot", "sourceStatement", "freshnessStatement", "completenessStatement"], structured.EnumerateObject().Select(property => property.Name));
        Assert.Equal("CurrentBuys", structured.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("currentSells").ValueKind);
        Assert.Equal((0, 50, 1, 1L, 1, false), (structured.GetProperty("page").GetInt32(), structured.GetProperty("pageSize").GetInt32(), structured.GetProperty("pageCount").GetInt32(), structured.GetProperty("totalCount").GetInt64(), structured.GetProperty("returnedCount").GetInt32(), structured.GetProperty("hasMore").GetBoolean()));
        var order = Assert.Single(structured.GetProperty("currentBuys").GetProperty("orders").EnumerateArray());
        Assert.Equal(["itemId", "unitPriceCopper", "quantity", "createdAt", "reservedCoinCopper"], order.EnumerateObject().Select(property => property.Name));
        Assert.Equal(200, order.GetProperty("reservedCoinCopper").GetInt64());
        Assert.Equal(200, structured.GetProperty("currentBuys").GetProperty("reservedCoinPageSubtotalCopper").GetInt64());
        Assert.False(structured.GetProperty("isAtomicSnapshot").GetBoolean());
        Assert.Contains("not owned", structured.GetProperty("sourceStatement").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("five minutes", structured.GetProperty("freshnessStatement").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("selected page only", structured.GetProperty("completenessStatement").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(order.TryGetProperty("id", out _));
        Assert.DoesNotContain("private", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTradingPostActivity_returns_minimal_structured_current_sell_page()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(29, "tools/call", new { name = "get_trading_post_activity", arguments = new { mode = "CurrentSells" } });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);
        using var document = JsonDocument.Parse(payload);

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("CurrentSells", structured.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("currentBuys").ValueKind);
        var order = Assert.Single(structured.GetProperty("currentSells").GetProperty("orders").EnumerateArray());
        Assert.Equal(["itemId", "unitPriceCopper", "quantity", "createdAt"], order.EnumerateObject().Select(property => property.Name));
        Assert.False(order.TryGetProperty("id", out _));
        Assert.Contains("listedForSale", structured.GetProperty("sourceStatement").GetString(), StringComparison.Ordinal);
        Assert.Contains("five minutes", structured.GetProperty("freshnessStatement").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("selected page", structured.GetProperty("completenessStatement").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(structured.GetProperty("isAtomicSnapshot").GetBoolean());
        Assert.DoesNotContain("private", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FindItems_returns_structured_candidates_without_private_configuration()
    {
        await InitializeAsync();

        using var response = await PostMcpAsync(9, "tools/call", new
        {
            name = "find_items",
            arguments = new { query = "  Beta\tBlade ", limit = 1 }
        });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);
        using var document = JsonDocument.Parse(payload);

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("Beta Blade", structured.GetProperty("normalizedQuery").GetString());
        var candidate = Assert.Single(structured.GetProperty("candidates").EnumerateArray());
        Assert.Equal(123, candidate.GetProperty("id").GetInt64());
        Assert.Equal("Beta Blade", candidate.GetProperty("name").GetString());
        Assert.Equal("Weapon", candidate.GetProperty("type").GetString());
        Assert.Equal("Rare", candidate.GetProperty("rarity").GetString());
        Assert.Equal(80, candidate.GetProperty("level").GetInt32());
        Assert.Equal(JsonValueKind.String, candidate.GetProperty("matchKind").ValueKind);
        Assert.Equal("Exact", candidate.GetProperty("matchKind").GetString());
        Assert.False(structured.GetProperty("hasMore").GetBoolean());
        Assert.False(document.RootElement.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean());
        Assert.DoesNotContain("key", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetItemPrices_returns_local_structured_price_facts()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(17, "tools/call", new { name = "get_item_prices", arguments = new { itemIds = new[] { 456L, 999L } } });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));
        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        var rows = structured.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal("Quoted", rows[0].GetProperty("status").GetString());
        Assert.Equal("Price Fixture", rows[0].GetProperty("name").GetString());
        Assert.Equal("NoPriceResourceInGeneration", rows[1].GetProperty("status").GetString());
        foreach (var property in new[] { "freeAccountTradable", "buyQuantity", "highestBuyUnitPrice", "sellQuantity", "lowestSellUnitPrice", "buyOrdersAvailable", "sellOrdersAvailable" })
        {
            Assert.Equal(JsonValueKind.Null, rows[1].GetProperty(property).ValueKind);
        }
        Assert.False(rows[1].GetProperty("isPriceResourceInGeneration").GetBoolean());
        Assert.Contains("tp --fresh", structured.GetProperty("freshnessStatement").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetItems_returns_public_request_time_facts_with_explicit_missing_nulls()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(21, "tools/call", new { name = "get_items", arguments = new { itemIds = new[] { 999L, 101L }, includeMaterialCategories = (bool?)null } });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);
        using var document = JsonDocument.Parse(payload);

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        var rows = structured.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(["NoPublicItemResource", "Found"], rows.Select(row => row.GetProperty("status").GetString()));
        foreach (var property in new[] { "name", "type", "rarity", "level", "vendorValue", "flags", "gameTypes", "restrictions" })
        {
            Assert.Equal(JsonValueKind.Null, rows[0].GetProperty(property).ValueKind);
        }
        Assert.Equal("Synthetic Item", rows[1].GetProperty("name").GetString());
        Assert.Equal("Weapon", rows[1].GetProperty("type").GetString());
        Assert.Empty(rows[1].GetProperty("flags").EnumerateArray());
        Assert.False(structured.GetProperty("isComplete").GetBoolean());
        Assert.Equal([999L], structured.GetProperty("missingItemIds").EnumerateArray().Select(value => value.GetInt64()));
        Assert.Equal("NotRequested", structured.GetProperty("materialCategoriesStatus").GetString());
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("materialCategoriesAsOf").ValueKind);
        Assert.False(structured.GetProperty("isAtomicSnapshot").GetBoolean());
        Assert.All(rows, row => Assert.Equal(JsonValueKind.Null, row.GetProperty("materialCategories").ValueKind));
        Assert.Equal("2026-08-12T12:00:00+00:00", structured.GetProperty("asOf").GetString());
        Assert.Contains("not a source publication", structured.GetProperty("sourceStatement").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GW2_API_KEY", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetItems_returns_requested_material_categories_for_found_and_missing_items()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(22, "tools/call", new
        {
            name = "get_items",
            arguments = new { itemIds = new[] { 999L, 101L }, includeMaterialCategories = true }
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("Available", structured.GetProperty("materialCategoriesStatus").GetString());
        Assert.Equal("2026-08-12T12:00:00+00:00", structured.GetProperty("materialCategoriesAsOf").GetString());
        Assert.False(structured.GetProperty("isAtomicSnapshot").GetBoolean());
        var rows = structured.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal([999L, 101L], rows.Select(row => row.GetProperty("id").GetInt64()));
        Assert.All(rows, row => Assert.Equal(1, row.GetProperty("materialCategories").GetArrayLength()));
        var category = rows[0].GetProperty("materialCategories")[0];
        Assert.Equal((1L, "Synthetic Materials", 2L), (category.GetProperty("id").GetInt64(), category.GetProperty("name").GetString(), category.GetProperty("order").GetInt64()));
    }

    [Fact]
    public async Task GetRecipes_ByIds_returns_public_recipe_facts_with_explicit_mode_nulls()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(23, "tools/call", new
        {
            name = "get_recipes",
            arguments = new { mode = "ByIds", recipeIds = new[] { 999L, 1L }, itemId = (long?)null, offset = (int?)null, limit = (int?)null, includeAccountUnlocks = (bool?)null }
        });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);
        using var document = JsonDocument.Parse(payload);

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("ByIds", structured.GetProperty("mode").GetString());
        var rows = structured.GetProperty("recipes").EnumerateArray().ToArray();
        Assert.Equal([999L, 1L], rows.Select(row => row.GetProperty("id").GetInt64()));
        Assert.Equal("NoPublicRecipeResource", rows[0].GetProperty("status").GetString());
        foreach (var property in new[] { "type", "output", "sourceOutputItemId", "minRating", "disciplines", "flags", "timeToCraftMs", "ingredients" })
        {
            Assert.Equal(JsonValueKind.Null, rows[0].GetProperty(property).ValueKind);
        }
        Assert.Equal("Item", rows[1].GetProperty("output").GetProperty("kind").GetString());
        Assert.Equal([999L], structured.GetProperty("missingRecipeIds").EnumerateArray().Select(value => value.GetInt64()));
        Assert.False(structured.GetProperty("areAllRequestedDefinitionsResolved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("selectorAsOf").ValueKind);
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("accountUnlocksAsOf").ValueKind);
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("isSelectorComplete").ValueKind);
        Assert.All(rows, row => Assert.Equal(JsonValueKind.Null, row.GetProperty("accountUnlockListContainsRecipe").ValueKind));
        Assert.False(structured.GetProperty("isAtomicSnapshot").GetBoolean());
        Assert.Contains("price", structured.GetProperty("scopeStatement").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRecipes_InputItem_returns_local_selector_page()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(24, "tools/call", new
        {
            name = "get_recipes",
            arguments = new { mode = "InputItem", itemId = 100L, offset = 1, limit = 1 }
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal([2L], structured.GetProperty("recipes").EnumerateArray().Select(row => row.GetProperty("id").GetInt64()));
        Assert.Equal(3, structured.GetProperty("totalMatches").GetInt32());
        Assert.Equal(1, structured.GetProperty("offset").GetInt32());
        Assert.Equal(1, structured.GetProperty("limit").GetInt32());
        Assert.True(structured.GetProperty("hasMore").GetBoolean());
        Assert.Contains("Item ingredients only", structured.GetProperty("selectorStatement").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRecipes_OutputItem_returns_guild_upgrade_semantics()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(25, "tools/call", new
        {
            name = "get_recipes",
            arguments = new { mode = "OutputItem", itemId = 202L }
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        var recipe = Assert.Single(structured.GetProperty("recipes").EnumerateArray());
        Assert.Equal("GuildUpgrade", recipe.GetProperty("output").GetProperty("kind").GetString());
        Assert.Equal(77, recipe.GetProperty("output").GetProperty("id").GetInt64());
        Assert.Equal(202, recipe.GetProperty("sourceOutputItemId").GetInt64());
        Assert.Equal(0, structured.GetProperty("offset").GetInt32());
        Assert.Equal(50, structured.GetProperty("limit").GetInt32());
        Assert.True(structured.GetProperty("areAllSelectedDefinitionsResolved").GetBoolean());
        Assert.Contains("bogus", structured.GetProperty("selectorStatement").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must not be treated", structured.GetProperty("selectorStatement").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(structured.GetProperty("warnings").EnumerateArray(), warning => warning.GetString()!.Contains("disclosure-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetRecipes_true_account_annotation_serializes_membership_and_account_time()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(26, "tools/call", new
        {
            name = "get_recipes",
            arguments = new { mode = "ByIds", recipeIds = new[] { 999L, 1L }, includeAccountUnlocks = true }
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        var rows = structured.GetProperty("recipes").EnumerateArray().ToArray();
        Assert.True(rows[0].GetProperty("accountUnlockListContainsRecipe").GetBoolean());
        Assert.False(rows[1].GetProperty("accountUnlockListContainsRecipe").GetBoolean());
        Assert.Equal("2026-08-12T12:00:00+00:00", structured.GetProperty("accountUnlocksAsOf").GetString());
        Assert.Equal(structured.GetProperty("accountUnlocksAsOf").GetString(), structured.GetProperty("asOf").GetString());
        Assert.Contains("absence does not mean", structured.GetProperty("scopeStatement").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRecipes_true_account_annotation_maps_redacted_total_failure()
    {
        using var factory = new AccountRecipeErrorMcpApplicationFactory();
        using var errorClient = factory.CreateClient();
        await InitializeAsync(errorClient);
        using var response = await PostMcpAsync(errorClient, 27, "tools/call", new
        {
            name = "get_recipes",
            arguments = new { mode = "ByIds", recipeIds = new[] { 1L }, includeAccountUnlocks = true }
        });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);

        Assert.Contains("account recipe unlocks are unavailable", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-account-recipe", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetItemPrices_maps_initial_cache_failure_to_a_redacted_actionable_mcp_error()
    {
        using var errorFactory = new ErrorMcpApplicationFactory();
        using var errorClient = errorFactory.CreateClient();
        await InitializeAsync(errorClient);
        using var response = await PostMcpAsync(errorClient, 18, "tools/call", new { name = "get_item_prices", arguments = new { itemIds = new[] { 456L } } });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);

        Assert.Contains("price cache is unavailable", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-price-cache", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValueItems_returns_structured_factual_quote_arithmetic_with_explicit_nulls()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(19, "tools/call", new
        {
            name = "value_items",
            arguments = new { items = new[] { new { itemId = 999L, quantity = 2L }, new { itemId = 456L, quantity = 3L } } }
        });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);
        using var document = JsonDocument.Parse(payload);

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        var rows = structured.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal("NoPriceResourceInGeneration", rows[0].GetProperty("priceResourceStatus").GetString());
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("name").ValueKind);
        Assert.Equal("Price Fixture", rows[1].GetProperty("name").GetString());
        foreach (var property in new[] { "unitQuote", "gross", "listingFee", "exchangeFee", "net" })
        {
            Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("immediateSale").GetProperty(property).ValueKind);
            Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("hypotheticalListing").GetProperty(property).ValueKind);
        }
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("acquisition").GetProperty("unitQuote").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("acquisition").GetProperty("buyerTotalCost").ValueKind);
        Assert.Equal(60, rows[1].GetProperty("immediateSale").GetProperty("gross").GetInt64());
        Assert.Equal(120, rows[1].GetProperty("acquisition").GetProperty("buyerTotalCost").GetInt64());
        Assert.False(structured.GetProperty("immediateSale").GetProperty("isComplete").GetBoolean());
        foreach (var property in new[] { "gross", "listingFee", "exchangeFee", "net" })
        {
            Assert.Equal(JsonValueKind.Null, structured.GetProperty("immediateSale").GetProperty(property).ValueKind);
            Assert.Equal(JsonValueKind.Null, structured.GetProperty("hypotheticalListing").GetProperty(property).ValueKind);
        }
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("acquisition").GetProperty("buyerTotalCost").ValueKind);
        Assert.Contains("not execution guarantees", structured.GetProperty("bestPriceExtrapolationStatement").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no buy", structured.GetProperty("scopeStatement").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GW2_API_KEY", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValueItems_rejects_a_null_row_with_a_controlled_redacted_tool_error()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(20, "tools/call", new
        {
            name = "value_items",
            arguments = new { items = new object?[] { null } }
        });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);

        Assert.Contains("Items must contain", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("NullReference", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GW2_API_KEY", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FindItems_does_not_call_a_configured_throwing_api_client()
    {
        using var errorFactory = new ErrorMcpApplicationFactory();
        using var errorClient = errorFactory.CreateClient();
        await InitializeAsync(errorClient);

        using var response = await PostMcpAsync(errorClient, 10, "tools/call", new { name = "find_items", arguments = new { query = "Beta" } });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);

        using var document = JsonDocument.Parse(payload);
        var candidate = Assert.Single(document.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("candidates").EnumerateArray());
        Assert.Equal(123, candidate.GetProperty("id").GetInt64());
        Assert.False(document.RootElement.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean());
        Assert.DoesNotContain("private catalog configuration detail", payload, StringComparison.Ordinal);

        using var noMatchResponse = await PostMcpAsync(errorClient, 11, "tools/call", new { name = "find_items", arguments = new { query = "Missing" } });
        noMatchResponse.EnsureSuccessStatusCode();
        using var noMatchDocument = JsonDocument.Parse(await ReadMcpResponseAsync(noMatchResponse));
        var noMatch = noMatchDocument.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Empty(noMatch.GetProperty("candidates").EnumerateArray());
        Assert.False(noMatchDocument.RootElement.GetProperty("result").TryGetProperty("isError", out var noMatchError) && noMatchError.GetBoolean());
    }

    [Fact]
    public async Task GetAccount_returns_structured_facts_and_as_of()
    {
        await InitializeAsync();

        using var response = await PostMcpAsync(3, "tools/call", new { name = "get_account", arguments = new { } });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);
        using var document = JsonDocument.Parse(payload);

        Assert.True(document.RootElement.GetProperty("result").TryGetProperty("structuredContent", out var structured), payload);
        Assert.Equal("Example.1234", structured.GetProperty("name").GetString());
        Assert.Equal(2206, structured.GetProperty("world").GetInt32());
        Assert.Equal("2026-08-12T12:00:00+00:00", structured.GetProperty("asOf").GetString());
        Assert.False(document.RootElement.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean());
    }

    [Fact]
    public async Task GetAccount_returns_redacted_actionable_configuration_error()
    {
        using var errorFactory = new ErrorMcpApplicationFactory();
        using var errorClient = errorFactory.CreateClient();
        await InitializeAsync(errorClient);

        using var response = await PostMcpAsync(errorClient, 4, "tools/call", new { name = "get_account", arguments = new { } });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);

        Assert.Contains("GW2_API_KEY", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('k', 16), payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetWallet_returns_structured_balances_warnings_and_one_as_of()
    {
        await InitializeAsync();

        using var response = await PostMcpAsync(5, "tools/call", new { name = "get_wallet", arguments = new { } });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        var balance = Assert.Single(structured.GetProperty("balances").EnumerateArray());
        Assert.Equal(1, balance.GetProperty("id").GetInt32());
        Assert.Equal("Coin", balance.GetProperty("name").GetString());
        Assert.Equal(42, balance.GetProperty("value").GetInt64());
        Assert.Empty(structured.GetProperty("warnings").EnumerateArray());
        Assert.Equal("2026-08-12T12:00:00+00:00", structured.GetProperty("asOf").GetString());
    }

    [Fact]
    public async Task GetWallet_maps_redacted_configuration_errors_to_mcp()
    {
        using var errorFactory = new ErrorMcpApplicationFactory();
        using var errorClient = errorFactory.CreateClient();
        await InitializeAsync(errorClient);

        using var response = await PostMcpAsync(errorClient, 6, "tools/call", new { name = "get_wallet", arguments = new { } });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);

        Assert.Contains("wallet permission", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(new string('k', 16), payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCharacters_returns_structured_core_summaries_and_as_of_without_private_configuration()
    {
        await InitializeAsync();

        using var response = await PostMcpAsync(12, "tools/call", new { name = "get_characters", arguments = new { } });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);
        using var document = JsonDocument.Parse(payload);

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        var character = Assert.Single(structured.GetProperty("characters").EnumerateArray());
        Assert.Equal("Synthetic Hero", character.GetProperty("name").GetString());
        Assert.Equal("Human", character.GetProperty("race").GetString());
        Assert.Equal("Female", character.GetProperty("gender").GetString());
        Assert.Equal("Guardian", character.GetProperty("profession").GetString());
        Assert.Equal(80, character.GetProperty("level").GetInt32());
        Assert.Equal(12, character.GetProperty("ageSeconds").GetInt64());
        Assert.Equal("2020-01-02T03:04:05+00:00", character.GetProperty("created").GetString());
        Assert.Equal("2026-01-02T03:04:05+00:00", character.GetProperty("lastModified").GetString());
        Assert.Equal(4, character.GetProperty("deaths").GetInt64());
        Assert.Equal("2026-08-12T12:00:00+00:00", structured.GetProperty("asOf").GetString());
        Assert.DoesNotContain("key", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCharacterBuild_returns_explicit_null_conditional_fields_and_fixed_slots()
    {
        await InitializeAsync();

        using var response = await PostMcpAsync(13, "tools/call", new { name = "get_character_build", arguments = new { characterName = "Synthetic Hero" } });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("Synthetic Hero", structured.GetProperty("characterName").GetString());
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("pets").ValueKind);
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("legends").ValueKind);
        var specializations = structured.GetProperty("specializations").EnumerateArray().ToArray();
        Assert.Equal(3, specializations.Length);
        Assert.Equal(3, specializations[0].GetProperty("selectedTraits").GetArrayLength());
        Assert.Equal(3, structured.GetProperty("skills").GetProperty("terrestrial").GetProperty("utilities").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("skills").GetProperty("terrestrial").GetProperty("heal").GetProperty("name").ValueKind);
        Assert.Equal("2026-08-12T12:00:00+00:00", structured.GetProperty("asOf").GetString());
    }

    [Fact]
    public async Task GetCharacterEquipment_returns_structured_compact_equipment()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(14, "tools/call", new { name = "get_character_equipment", arguments = new { characterName = "Synthetic Hero" } });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("Synthetic Hero", structured.GetProperty("characterName").GetString());
        Assert.False(structured.GetProperty("isOwnershipData").GetBoolean());
        var row = Assert.Single(structured.GetProperty("equipment").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("item").GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("stats").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("skin").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("binding").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("boundTo").ValueKind);
        Assert.Equal("2026-08-12T12:00:00+00:00", structured.GetProperty("asOf").GetString());
    }

    [Fact]
    public async Task Mcp_get_character_equipment_tabs_schema_has_only_required_characterName()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(140, "tools/list", new { });
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));
        var tool = document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "get_character_equipment_tabs");
        Assert.Equal(["characterName"], tool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Equal(["characterName"], tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task Mcp_get_character_equipment_tabs_invocation_preserves_nonempty_and_empty_shapes()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(141, "tools/call", new { name = "get_character_equipment_tabs", arguments = new { characterName = "Synthetic Hero" } });
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));
        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("AllEquipmentTabsPveWvwCombatReferences", structured.GetProperty("equipmentScope").GetString());
        Assert.False(structured.GetProperty("isOwnershipData").GetBoolean());
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("equipmentAsOf").ValueKind);
        Assert.Single(structured.GetProperty("tabs").EnumerateArray());
    }

    [Fact]
    public async Task Mcp_get_character_equipment_tabs_invocation_preserves_the_real_empty_shape()
    {
        using var emptyFactory = new McpApplicationFactory(emptyEquipmentTabs: true);
        using var emptyClient = emptyFactory.CreateClient();
        await InitializeAsync(emptyClient);
        using var response = await PostMcpAsync(emptyClient, 143, "tools/call", new { name = "get_character_equipment_tabs", arguments = new { characterName = "Synthetic Hero" } });
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));
        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("activeTab").ValueKind);
        Assert.Empty(structured.GetProperty("tabs").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("equipmentAsOf").ValueKind);
        Assert.True(structured.GetProperty("isMetadataComplete").GetBoolean());
    }

    [Fact]
    public async Task Mcp_get_character_equipment_remains_discoverable_and_unchanged()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(142, "tools/list", new { });
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));
        var equipment = document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "get_character_equipment");
        Assert.Equal(["characterName"], equipment.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task GetCharacterInventory_returns_selected_bag_scope_with_explicit_nulls()
    {
        await InitializeAsync();
        using var response = await PostMcpAsync(15, "tools/call", new { name = "get_character_inventory", arguments = new { characterName = "Synthetic Hero" } });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("SelectedCharacterPhysicalBags", structured.GetProperty("inventoryScope").GetString());
        var bag = Assert.Single(structured.GetProperty("bags").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, bag.GetProperty("bag").GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, Assert.Single(bag.GetProperty("slots").EnumerateArray()).GetProperty("stack").ValueKind);
        Assert.Equal("2026-08-12T12:00:00+00:00", structured.GetProperty("asOf").GetString());
    }

    [Fact]
    public async Task GetAccountHoldings_returns_safe_structured_complete_aggregation()
    {
        await InitializeAsync();

        using var response = await PostMcpAsync(7, "tools/call", new
        {
            name = "get_account_holdings",
            arguments = new { itemIds = new[] { 101L }, currencyIds = new[] { 1 } }
        });
        response.EnsureSuccessStatusCode();
        var payload = await ReadMcpResponseAsync(response);
        using var document = JsonDocument.Parse(payload);

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        var item = Assert.Single(structured.GetProperty("items").EnumerateArray());
        Assert.Equal(101, item.GetProperty("id").GetInt64());
        Assert.Equal(15, item.GetProperty("ownedTotal").GetInt64());
        var currency = Assert.Single(structured.GetProperty("currencies").EnumerateArray());
        Assert.Equal(1, currency.GetProperty("id").GetInt32());
        Assert.Equal(42, currency.GetProperty("ownedTotal").GetInt64());
        Assert.True(structured.GetProperty("isComplete").GetBoolean());
        Assert.Equal("2026-08-12T12:00:00+00:00", structured.GetProperty("asOf").GetString());
        Assert.DoesNotContain("GW2_API_KEY", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokeninfo", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAccountHoldings_preserves_required_nullable_fields_in_structured_content()
    {
        using var nullableFactory = new NullableHoldingsMcpApplicationFactory();
        using var nullableClient = nullableFactory.CreateClient();
        await InitializeAsync(nullableClient);

        using var response = await PostMcpAsync(nullableClient, 8, "tools/call", new
        {
            name = "get_account_holdings",
            arguments = new { itemIds = new[] { 101L }, currencyIds = new[] { 99, 100 } }
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        var item = Assert.Single(structured.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("onHand").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("inTradingPostDelivery").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("listedForSale").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("ownedTotal").ValueKind);

        var currencies = structured.GetProperty("currencies").EnumerateArray().ToArray();
        var currency = Assert.Single(currencies, candidate => candidate.GetProperty("id").GetInt32() == 99);
        Assert.Equal(JsonValueKind.Null, currency.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, currency.GetProperty("onHand").ValueKind);
        Assert.Equal(JsonValueKind.Null, currency.GetProperty("ownedTotal").ValueKind);
        var walletLocation = Assert.Single(
            Assert.Single(currencies, candidate => candidate.GetProperty("id").GetInt32() == 100)
                .GetProperty("locations")
                .EnumerateArray());
        Assert.Equal(JsonValueKind.Null, walletLocation.GetProperty("character").ValueKind);

        var warnings = structured.GetProperty("warnings").EnumerateArray().ToArray();
        Assert.NotEmpty(warnings);
        Assert.All(warnings, warning =>
        {
            Assert.True(warning.TryGetProperty("itemId", out _));
            Assert.True(warning.TryGetProperty("currencyId", out _));
        });
        Assert.Contains(warnings, warning => warning.GetProperty("itemId").ValueKind == JsonValueKind.Null);
        Assert.Contains(warnings, warning => warning.GetProperty("currencyId").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task GetLegendaryArmory_returns_no_argument_structured_account_ownership_with_explicit_nulls()
    {
        await InitializeAsync();

        using var response = await PostMcpAsync(16, "tools/call", new { name = "get_legendary_armory", arguments = new { } });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));

        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("AccountLegendaryArmory", structured.GetProperty("ownershipScope").GetString());
        Assert.Equal("AvailableForUseInSingleEquipmentTemplate", structured.GetProperty("countSemantics").GetString());
        var entry = Assert.Single(structured.GetProperty("entries").EnumerateArray());
        Assert.Equal(101, entry.GetProperty("id").GetInt64());
        Assert.Equal(0, entry.GetProperty("armoryCount").GetInt64());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("type").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("subtype").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("weightClass").ValueKind);
        Assert.False(structured.GetProperty("isMetadataComplete").GetBoolean());
        Assert.Equal(("metadata_unresolved", "items", "101"), Assert.Single(structured.GetProperty("warnings").EnumerateArray()) is var warning
            ? (warning.GetProperty("code").GetString(), warning.GetProperty("resolver").GetString(), warning.GetProperty("referenceId").GetString())
            : default);
        Assert.Equal("2026-08-12T12:00:00+00:00", structured.GetProperty("asOf").GetString());
    }

    [Fact]
    public async Task Mcp_route_rejects_non_mcp_get_requests()
    {
        using var response = await client.GetAsync("/mcp");

        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private Task InitializeAsync() => InitializeAsync(client);

    private async Task InitializeAsync(HttpClient httpClient)
    {
        using var response = await PostMcpAsync(httpClient, 1, "initialize", new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "test", version = "1.0" }
        });
        response.EnsureSuccessStatusCode();
    }

    private Task<HttpResponseMessage> PostMcpAsync(int id, string method, object parameters) => PostMcpAsync(client, id, method, parameters);

    private static Task<HttpResponseMessage> PostMcpAsync(HttpClient httpClient, int id, string method, object parameters)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id, method, @params = parameters })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (method != "initialize")
        {
            request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        }

        return httpClient.SendAsync(request);
    }

    private static void AssertSchemaRequired(JsonElement schema, params string[] names) =>
        Assert.Equal(names, schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));

    private static async Task<string> ReadMcpResponseAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]
            : body;
    }

    public sealed class McpApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly bool emptyEquipmentTabs;

        public McpApplicationFactory() { }
        internal McpApplicationFactory(bool emptyEquipmentTabs) => this.emptyEquipmentTabs = emptyEquipmentTabs;

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("GW2_API_BUDGET_LOCK_PATH", CreateTestLockPath());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IGw2ApiClient>();
                services.AddSingleton<IGw2ApiClient>(new FakeGw2ApiClient(emptyEquipmentTabs: emptyEquipmentTabs));
                services.RemoveAll<IItemCacheReader>();
                services.AddSingleton<IItemCacheReader>(new FakeCacheReader());
                services.RemoveAll<IPriceSnapshotProvider>();
                services.AddSingleton<IPriceSnapshotProvider>(new FakePriceSnapshotProvider());
                services.RemoveAll<IItemNameLookup>();
                services.AddSingleton<IItemNameLookup>(new FakeItemNameLookup());
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider());
            });
        }
    }

    private sealed class ErrorMcpApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("GW2_API_BUDGET_LOCK_PATH", CreateTestLockPath());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IGw2ApiClient>();
                services.AddSingleton<IGw2ApiClient>(new ErrorGw2ApiClient());
                services.RemoveAll<IItemCacheReader>();
                services.AddSingleton<IItemCacheReader>(new FakeCacheReader());
                services.RemoveAll<IPriceSnapshotProvider>();
                services.AddSingleton<IPriceSnapshotProvider>(new EndpointUnavailablePriceSnapshotProvider());
                services.RemoveAll<IItemNameLookup>();
                services.AddSingleton<IItemNameLookup>(new FakeItemNameLookup());
            });
        }
    }

    private sealed class NullableHoldingsMcpApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("GW2_API_BUDGET_LOCK_PATH", CreateTestLockPath());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IGw2ApiClient>();
                services.AddSingleton<IGw2ApiClient>(new NullableHoldingsGw2ApiClient());
                services.RemoveAll<IItemCacheReader>();
                services.AddSingleton<IItemCacheReader>(new FakeCacheReader());
                services.RemoveAll<IPriceSnapshotProvider>();
                services.AddSingleton<IPriceSnapshotProvider>(new FakePriceSnapshotProvider());
                services.RemoveAll<IItemNameLookup>();
                services.AddSingleton<IItemNameLookup>(new FakeItemNameLookup());
            });
        }
    }

    private sealed class AccountRecipeErrorMcpApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("GW2_API_BUDGET_LOCK_PATH", CreateTestLockPath());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IGw2ApiClient>();
                services.AddSingleton<IGw2ApiClient>(new FakeGw2ApiClient(failAccountRecipeUnlocks: true));
                services.RemoveAll<IItemCacheReader>();
                services.AddSingleton<IItemCacheReader>(new FakeCacheReader());
                services.RemoveAll<IPriceSnapshotProvider>();
                services.AddSingleton<IPriceSnapshotProvider>(new FakePriceSnapshotProvider());
                services.RemoveAll<IItemNameLookup>();
                services.AddSingleton<IItemNameLookup>(new FakeItemNameLookup());
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider());
            });
        }
    }

    private static string CreateTestLockPath() => Path.Combine(
        Path.GetTempPath(),
        "GW2AccountMCP.Tests",
        Guid.NewGuid().ToString("N"),
        "gw2-api-budget.lock");

    private sealed class FakeGw2ApiClient(bool failAccountRecipeUnlocks = false, bool emptyEquipmentTabs = false) : IGw2ApiClient
    {
        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2Account("Example.1234", 2206, DateTimeOffset.Parse("2020-01-02T03:04:05Z"), ["GuildWars2"]));

        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2Wallet([new Gw2WalletBalance(1, "Coin", 42)], []));

        public Task<Gw2Characters> GetCharactersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2Characters([new Gw2Character("Synthetic Hero", "Human", "Female", "Guardian", 80, 12, DateTimeOffset.Parse("2020-01-02T03:04:05Z"), DateTimeOffset.Parse("2026-01-02T03:04:05Z"), 4)]));

        public Task<Gw2CharacterBuild> GetCharacterBuildAsync(string characterName, CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2CharacterBuild(
                "Synthetic Hero", 1, "", "Guardian",
                [
                    new Gw2BuildSpecialization(null, [null, null, null]),
                    new Gw2BuildSpecialization(null, [null, null, null]),
                    new Gw2BuildSpecialization(null, [null, null, null])
                ],
                new Gw2BuildSkills(new Gw2NumericReference(20, null), [null, null, null], null),
                new Gw2BuildSkills(null, [null, null, null], null),
                null, null, false, [new Gw2MetadataWarning("metadata_unresolved", "skills", "20")]));

        public Task<Gw2CharacterEquipment> GetCharacterEquipmentAsync(string characterName, CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2CharacterEquipment("Synthetic Hero", 1, "", [
                new Gw2EquipmentRow("Helm", new Gw2EquipmentItem(1, null, null, null, null, null), null, [], [], null, null, null, "Equipped", "EquippedReference")
            ], true, []));
        public Task<Gw2CharacterEquipmentTabs> GetCharacterEquipmentTabsAsync(string characterName, CancellationToken cancellationToken) =>
            Task.FromResult(emptyEquipmentTabs
                ? new Gw2CharacterEquipmentTabs("Synthetic Hero", null, [], true, [], DateTimeOffset.Parse("2026-08-12T12:00:00Z"), null, null, null, null, DateTimeOffset.Parse("2026-08-12T12:00:00Z"))
                : new Gw2CharacterEquipmentTabs("Synthetic Hero", 1, [new Gw2CharacterEquipmentTab(1, "", true, [])], true, [], DateTimeOffset.Parse("2026-08-12T12:00:00Z"), null, null, null, null, DateTimeOffset.Parse("2026-08-12T12:00:00Z")));
        public Task<Gw2CharacterInventory> GetCharacterInventoryAsync(string characterName, CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2CharacterInventory("Synthetic Hero", new Gw2CharacterInventoryCapacity(1, 1, 1, 0, 1), [new Gw2CharacterInventoryBag(0, new Gw2InventoryBag(1, null, 1), [new Gw2CharacterInventorySlot(0, null)])], true, []));

        public Task<Gw2AccountStorage> GetAccountStorageAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2AccountStorage([new Gw2StorageStack(101, 2, Gw2StorageSource.Bank, 0)]));

        public Task<Gw2CharacterBags> GetCharacterBagsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2CharacterBags([new Gw2CharacterBagStack(101, 3, "Synthetic Hero", 0, 0)]));

        public Task<Gw2TradingPostDelivery> GetTradingPostDeliveryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2TradingPostDelivery(0, [new Gw2TradingPostDeliveryItem(101, 4)]));

        public Task<Gw2CurrentSells> GetCurrentSellsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2CurrentSells([new Gw2CurrentSellOrder(1, 101, 999, 6, DateTimeOffset.Parse("2026-01-01T00:00:00Z"))]));

        public Task<Gw2CurrentBuysPage> GetCurrentBuysPageAsync(int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2CurrentBuysPage(page, pageSize, 1, 1, [new Gw2CurrentBuyOrder(101, 100, 2, DateTimeOffset.Parse("2026-01-01T00:00:00Z"))]));

        public Task<Gw2CurrentSellsPage> GetCurrentSellsPageAsync(int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2CurrentSellsPage(page, pageSize, 1, 1, [new Gw2CurrentSellPageOrder(101, 100, 2, DateTimeOffset.Parse("2026-01-01T00:00:00Z"))]));

        public Task<Gw2Items> GetItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2Items([new Gw2Item(101, "Synthetic Item")], []));

        public Task<Gw2PublicItems> GetPublicItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2PublicItems([new Gw2PublicItem(101, "Synthetic Item", "Weapon", "Rare", 80, 0, [], [], [])], [], []));

        public Task<Gw2MaterialCategories> GetPublicMaterialCategoriesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2MaterialCategories([new Gw2MaterialCategory(1, "Synthetic Materials", 2, [101, 999])]));

        public Task<Gw2PublicRecipes> GetPublicRecipesAsync(IReadOnlyList<long> recipeIds, CancellationToken cancellationToken)
        {
            var recipes = recipeIds.Where(id => id != 999).Select(id => id == 2
                ? new Gw2PublicRecipe(2, "GuildDecoration", 202, 4, 77, 0, 500, ["Scribe"], [], [new Gw2RecipeIngredient("GuildUpgrade", 8, 1)])
                : new Gw2PublicRecipe(id, "Refinement", id + 100, 1, null, 0, 1000, [], [], [])).ToArray();
            return Task.FromResult(new Gw2PublicRecipes(recipes, recipeIds.Where(id => id == 999).ToArray()));
        }

        public Task<Gw2RecipeSelector> SearchPublicRecipesByInputItemAsync(long itemId, CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2RecipeSelector([3, 1, 2]));

        public Task<Gw2RecipeSelector> SearchPublicRecipesByOutputItemAsync(long itemId, CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2RecipeSelector([2]));

        public Task<Gw2AccountRecipeUnlocks> GetAccountRecipeUnlocksAsync(CancellationToken cancellationToken) =>
            failAccountRecipeUnlocks
                ? Task.FromException<Gw2AccountRecipeUnlocks>(new IOException("private-account-recipe"))
                : Task.FromResult(new Gw2AccountRecipeUnlocks([999, 2]));

        public Task<Gw2LegendaryArmory> GetLegendaryArmoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2LegendaryArmory([new Gw2LegendaryArmoryEntry(101, 0, null, null, null, null)], false, [new Gw2MetadataWarning("metadata_unresolved", "items", "101")]));

        public Task<Gw2AccountAchievementProgress> GetAccountAchievementProgressAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2AccountAchievementProgress([new Gw2AccountAchievementProgressEntry(1, 1, 1, true, null, true, [])]));
        public Task<Gw2PublicAchievements> GetPublicAchievementsAsync(IReadOnlyList<long> achievementIds, CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2PublicAchievements(achievementIds.Where(id => id == 1).Select(id => new Gw2PublicAchievement(id, "Synthetic achievement", "", "", "", "Basic", [], [])).ToArray(), achievementIds.Where(id => id != 1).ToArray()));
        public Task<Gw2AccountMasterySources> GetAccountMasterySourcesAsync(CancellationToken cancellationToken) => Task.FromResult(new Gw2AccountMasterySources([], [], DateTimeOffset.Parse("2026-08-12T12:00:00Z"), DateTimeOffset.Parse("2026-08-12T12:00:00Z")));
        public Task<Gw2PublicMasteries> GetPublicMasteriesAsync(IReadOnlyList<long> masteryIds, CancellationToken cancellationToken) => throw new NotSupportedException();

    }

    private sealed class ErrorGw2ApiClient : IGw2ApiClient
    {
        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");

        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required wallet permission. Create a key with the wallet permission.");

        public Task<Gw2Characters> GetCharactersAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required characters permission. Create a key with the characters permission.");

        public Task<Gw2CharacterBuild> GetCharacterBuildAsync(string characterName, CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required builds permission. Create a key with the builds permission.");

        public Task<Gw2CharacterEquipment> GetCharacterEquipmentAsync(string characterName, CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required builds permission. Create a key with the builds permission.");
        public Task<Gw2CharacterEquipmentTabs> GetCharacterEquipmentTabsAsync(string characterName, CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required builds permission. Create a key with the builds permission.");
        public Task<Gw2CharacterInventory> GetCharacterInventoryAsync(string characterName, CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required inventories permission. Create a key with the inventories permission.");

        public Task<Gw2AccountStorage> GetAccountStorageAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required inventories permission. Create a key with the inventories permission.");

        public Task<Gw2CharacterBags> GetCharacterBagsAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required characters permission. Create a key with the characters permission.");

        public Task<Gw2TradingPostDelivery> GetTradingPostDeliveryAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required tradingpost permission. Create a key with the tradingpost permission.");

        public Task<Gw2CurrentSells> GetCurrentSellsAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required tradingpost permission. Create a key with the tradingpost permission.");

        public Task<Gw2CurrentBuysPage> GetCurrentBuysPageAsync(int page, int pageSize, CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("private current-buys failure");

        public Task<Gw2CurrentSellsPage> GetCurrentSellsPageAsync(int page, int pageSize, CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("private current-sells failure");

        public Task<Gw2Items> GetItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2 item metadata request failed with HTTP 503. Try again later.");

        public Task<Gw2PublicItems> GetPublicItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2 public item request failed with HTTP 503. Try again later.");

        public Task<Gw2MaterialCategories> GetPublicMaterialCategoriesAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2 public material-category request failed with HTTP 503. Try again later.");

        public Task<Gw2PublicRecipes> GetPublicRecipesAsync(IReadOnlyList<long> recipeIds, CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2 public recipe request failed with HTTP 503. Try again later.");
        public Task<Gw2RecipeSelector> SearchPublicRecipesByInputItemAsync(long itemId, CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2 public recipe selector request failed with HTTP 503. Try again later.");
        public Task<Gw2RecipeSelector> SearchPublicRecipesByOutputItemAsync(long itemId, CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2 public recipe selector request failed with HTTP 503. Try again later.");
        public Task<Gw2AccountRecipeUnlocks> GetAccountRecipeUnlocksAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required unlocks permission. Create a key with the unlocks permission.");

        public Task<Gw2LegendaryArmory> GetLegendaryArmoryAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required unlocks permission. Create a key with the unlocks permission.");
        public Task<Gw2AccountAchievementProgress> GetAccountAchievementProgressAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required progression permission. Create a key with the progression permission.");
        public Task<Gw2PublicAchievements> GetPublicAchievementsAsync(IReadOnlyList<long> achievementIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountMasterySources> GetAccountMasterySourcesAsync(CancellationToken cancellationToken) => throw new Gw2ConfigurationException("GW2_API_KEY is missing the required progression permission. Create a key with the progression permission.");
        public Task<Gw2PublicMasteries> GetPublicMasteriesAsync(IReadOnlyList<long> masteryIds, CancellationToken cancellationToken) => throw new NotSupportedException();

    }

    private sealed class NullableHoldingsGw2ApiClient : IGw2ApiClient
    {
        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2Wallet([new Gw2WalletBalance(100, "Synthetic Currency", 1)], []));

        public Task<Gw2Characters> GetCharactersAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Gw2CharacterBuild> GetCharacterBuildAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterEquipment> GetCharacterEquipmentAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterEquipmentTabs> GetCharacterEquipmentTabsAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterInventory> GetCharacterInventoryAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Gw2AccountStorage> GetAccountStorageAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("Synthetic storage failure.");

        public Task<Gw2CharacterBags> GetCharacterBagsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2CharacterBags([new Gw2CharacterBagStack(101, 2, "Synthetic Hero", 0, 0)]));

        public Task<Gw2TradingPostDelivery> GetTradingPostDeliveryAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("Synthetic delivery failure.");

        public Task<Gw2CurrentSells> GetCurrentSellsAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("Synthetic current-sells failure.");

        public Task<Gw2CurrentBuysPage> GetCurrentBuysPageAsync(int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CurrentSellsPage> GetCurrentSellsPageAsync(int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Gw2Items> GetItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("Synthetic metadata failure.");

        public Task<Gw2PublicItems> GetPublicItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Gw2MaterialCategories> GetPublicMaterialCategoriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Gw2PublicRecipes> GetPublicRecipesAsync(IReadOnlyList<long> recipeIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2RecipeSelector> SearchPublicRecipesByInputItemAsync(long itemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2RecipeSelector> SearchPublicRecipesByOutputItemAsync(long itemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountRecipeUnlocks> GetAccountRecipeUnlocksAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Gw2LegendaryArmory> GetLegendaryArmoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountAchievementProgress> GetAccountAchievementProgressAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicAchievements> GetPublicAchievementsAsync(IReadOnlyList<long> achievementIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountMasterySources> GetAccountMasterySourcesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicMasteries> GetPublicMasteriesAsync(IReadOnlyList<long> masteryIds, CancellationToken cancellationToken) => throw new NotSupportedException();

    }

    private sealed class FakeCacheReader : IItemCacheReader
    {
        private static readonly ItemCacheSnapshot Snapshot = new(
            [new CachedItem(123, "Beta Blade", "Weapon", "Rare", 80)],
            new ItemCacheFingerprint(
                new ItemCachePathFingerprint("items.manifest.json", new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc), 1),
                new ItemCachePathFingerprint("items.csv", new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc), 1)),
            new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc));

        public ItemCacheFingerprint GetCurrentFingerprint() => Snapshot.Fingerprint;
        public ItemCacheSnapshot Load(CancellationToken cancellationToken) => Snapshot;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-12T12:00:00Z");
    }

    private sealed class FakePriceSnapshotProvider : IPriceSnapshotProvider
    {
        private static readonly PriceCacheSnapshot Snapshot = new([new CachedPrice(456, true, 10, 20, 30, 40)], new PriceCacheFingerprint("a".PadLeft(64, 'a'), "prices." + "a".PadLeft(64, 'a') + ".csv", DateTime.UnixEpoch, 1, DateTime.UnixEpoch, 1), DateTime.Parse("2026-08-12T11:00:00Z"), DateTime.Parse("2026-08-12T11:01:00Z"), DateTime.Parse("2026-08-12T11:02:00Z"));
        public Task<PriceSnapshotResult> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new PriceSnapshotResult(Snapshot, null));
    }
    private sealed class EndpointUnavailablePriceSnapshotProvider : IPriceSnapshotProvider
    {
        public Task<PriceSnapshotResult> GetSnapshotAsync(CancellationToken cancellationToken) => throw new PriceCacheException("private-price-cache");
    }
    private sealed class FakeItemNameLookup : IItemNameLookup
    {
        public Task<IReadOnlyDictionary<long, string>> GetNamesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string> { [456] = "Price Fixture" });
    }
}
