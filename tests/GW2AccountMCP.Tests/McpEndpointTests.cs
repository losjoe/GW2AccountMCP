using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GW2AccountMCP.Gw2;
using GW2AccountMCP.Items;
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
    public async Task Mcp_route_discovers_exactly_four_read_only_structured_tools()
    {
        await InitializeAsync();

        using var response = await PostMcpAsync(2, "tools/list", new { });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));

        var tools = document.RootElement.GetProperty("result").GetProperty("tools");
        var discoveredTools = tools.EnumerateArray().OrderBy(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.Equal(["find_items", "get_account", "get_account_holdings", "get_wallet"], discoveredTools.Select(tool => tool.GetProperty("name").GetString()));
        foreach (var tool in discoveredTools)
        {
            var annotations = tool.GetProperty("annotations");
            Assert.True(annotations.GetProperty("readOnlyHint").GetBoolean());
            Assert.True(annotations.GetProperty("idempotentHint").GetBoolean());
            Assert.False(annotations.GetProperty("openWorldHint").GetBoolean());
            Assert.True(tool.TryGetProperty("outputSchema", out _));
        }

        var toolsByName = discoveredTools.ToDictionary(tool => tool.GetProperty("name").GetString()!);
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
        var walletOutputSchema = toolsByName["get_wallet"].GetProperty("outputSchema").GetProperty("properties");
        Assert.True(walletOutputSchema.TryGetProperty("balances", out _));
        Assert.True(walletOutputSchema.TryGetProperty("warnings", out _));
        Assert.True(walletOutputSchema.TryGetProperty("asOf", out _));
        Assert.DoesNotContain("GW2_API_KEY", document.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
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

    private static async Task<string> ReadMcpResponseAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]
            : body;
    }

    public sealed class McpApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("GW2_API_BUDGET_LOCK_PATH", CreateTestLockPath());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IGw2ApiClient>();
                services.AddSingleton<IGw2ApiClient>(new FakeGw2ApiClient());
                services.RemoveAll<IItemCacheReader>();
                services.AddSingleton<IItemCacheReader>(new FakeCacheReader());
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
            });
        }
    }

    private static string CreateTestLockPath() => Path.Combine(
        Path.GetTempPath(),
        "GW2AccountMCP.Tests",
        Guid.NewGuid().ToString("N"),
        "gw2-api-budget.lock");

    private sealed class FakeGw2ApiClient : IGw2ApiClient
    {
        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2Account("Example.1234", 2206, DateTimeOffset.Parse("2020-01-02T03:04:05Z"), ["GuildWars2"]));

        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2Wallet([new Gw2WalletBalance(1, "Coin", 42)], []));

        public Task<Gw2AccountStorage> GetAccountStorageAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2AccountStorage([new Gw2StorageStack(101, 2, Gw2StorageSource.Bank, 0)]));

        public Task<Gw2CharacterBags> GetCharacterBagsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2CharacterBags([new Gw2CharacterBagStack(101, 3, "Synthetic Hero", 0, 0)]));

        public Task<Gw2TradingPostDelivery> GetTradingPostDeliveryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2TradingPostDelivery(0, [new Gw2TradingPostDeliveryItem(101, 4)]));

        public Task<Gw2CurrentSells> GetCurrentSellsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2CurrentSells([new Gw2CurrentSellOrder(1, 101, 999, 6, DateTimeOffset.Parse("2026-01-01T00:00:00Z"))]));

        public Task<Gw2Items> GetItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2Items([new Gw2Item(101, "Synthetic Item")], []));

    }

    private sealed class ErrorGw2ApiClient : IGw2ApiClient
    {
        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");

        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required wallet permission. Create a key with the wallet permission.");

        public Task<Gw2AccountStorage> GetAccountStorageAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required inventories permission. Create a key with the inventories permission.");

        public Task<Gw2CharacterBags> GetCharacterBagsAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required characters permission. Create a key with the characters permission.");

        public Task<Gw2TradingPostDelivery> GetTradingPostDeliveryAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required tradingpost permission. Create a key with the tradingpost permission.");

        public Task<Gw2CurrentSells> GetCurrentSellsAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required tradingpost permission. Create a key with the tradingpost permission.");

        public Task<Gw2Items> GetItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2 item metadata request failed with HTTP 503. Try again later.");

    }

    private sealed class NullableHoldingsGw2ApiClient : IGw2ApiClient
    {
        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2Wallet([new Gw2WalletBalance(100, "Synthetic Currency", 1)], []));

        public Task<Gw2AccountStorage> GetAccountStorageAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("Synthetic storage failure.");

        public Task<Gw2CharacterBags> GetCharacterBagsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2CharacterBags([new Gw2CharacterBagStack(101, 2, "Synthetic Hero", 0, 0)]));

        public Task<Gw2TradingPostDelivery> GetTradingPostDeliveryAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("Synthetic delivery failure.");

        public Task<Gw2CurrentSells> GetCurrentSellsAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("Synthetic current-sells failure.");

        public Task<Gw2Items> GetItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("Synthetic metadata failure.");

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
}
