using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GW2AccountMCP.Gw2;
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
    public async Task Mcp_route_discovers_exactly_read_only_get_account_and_get_wallet_with_structured_output_schemas()
    {
        await InitializeAsync();

        using var response = await PostMcpAsync(2, "tools/list", new { });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadMcpResponseAsync(response));

        var tools = document.RootElement.GetProperty("result").GetProperty("tools");
        var discoveredTools = tools.EnumerateArray().OrderBy(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.Equal(["get_account", "get_wallet"], discoveredTools.Select(tool => tool.GetProperty("name").GetString()));
        foreach (var tool in discoveredTools)
        {
            var annotations = tool.GetProperty("annotations");
            Assert.True(annotations.GetProperty("readOnlyHint").GetBoolean());
            Assert.True(annotations.GetProperty("idempotentHint").GetBoolean());
            Assert.False(annotations.GetProperty("openWorldHint").GetBoolean());
            Assert.True(tool.TryGetProperty("outputSchema", out _));
        }

        var accountOutputSchema = discoveredTools[0].GetProperty("outputSchema").GetProperty("properties");
        Assert.True(accountOutputSchema.TryGetProperty("name", out _));
        Assert.True(accountOutputSchema.TryGetProperty("asOf", out _));
        var walletOutputSchema = discoveredTools[1].GetProperty("outputSchema").GetProperty("properties");
        Assert.True(walletOutputSchema.TryGetProperty("balances", out _));
        Assert.True(walletOutputSchema.TryGetProperty("warnings", out _));
        Assert.True(walletOutputSchema.TryGetProperty("asOf", out _));
        Assert.DoesNotContain("GW2_API_KEY", document.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
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
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IGw2ApiClient>();
                services.AddSingleton<IGw2ApiClient>(new FakeGw2ApiClient());
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider());
            });
        }
    }

    private sealed class ErrorMcpApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IGw2ApiClient>();
                services.AddSingleton<IGw2ApiClient>(new ErrorGw2ApiClient());
            });
        }
    }

    private sealed class FakeGw2ApiClient : IGw2ApiClient
    {
        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2Account("Example.1234", 2206, DateTimeOffset.Parse("2020-01-02T03:04:05Z"), ["GuildWars2"]));

        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2Wallet([new Gw2WalletBalance(1, "Coin", 42)], []));
    }

    private sealed class ErrorGw2ApiClient : IGw2ApiClient
    {
        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is not configured. Set it with user-secrets or an environment variable.");

        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) =>
            throw new Gw2ConfigurationException("GW2_API_KEY is missing the required wallet permission. Create a key with the wallet permission.");
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-12T12:00:00Z");
    }
}
