using GW2AccountMCP.Gw2;
using GW2AccountMCP.Items;
using GW2AccountMCP.Tools;

var builder = WebApplication.CreateBuilder(args);

var gw2Options = new Gw2ApiOptions(
    builder.Configuration["GW2_API_KEY"] ?? string.Empty,
    builder.Configuration["GW2_API_BASE_URL"] ?? "https://api.guildwars2.com");
var gw2ApiBudgetLeaseOptions = new Gw2ApiBudgetLeaseOptions(
    builder.Configuration["GW2_API_BUDGET_LOCK_PATH"] ?? "data/gw2-api-budget.lock");
var itemCacheOptions = new ItemCacheOptions(
    builder.Configuration["GW2_PUBLIC_CACHE_PATH"] ?? "data/public-cache");

builder.Services.AddSingleton(gw2Options);
builder.Services.AddSingleton(gw2ApiBudgetLeaseOptions);
builder.Services.AddSingleton(itemCacheOptions);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<Gw2ApiStartGate>();
builder.Services.AddTransient<Gw2ApiBudgetHandler>();
builder.Services.AddHttpClient<IGw2ApiClient, Gw2ApiClient>(client =>
{
    client.BaseAddress = new Uri(gw2Options.BaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(15);
})
    .AddHttpMessageHandler<Gw2ApiBudgetHandler>();
builder.Services.AddSingleton<IItemCacheReader, ItemCacheReader>();
builder.Services.AddSingleton<IItemSearchIndex, ItemSearchIndex>();
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<FindItemsTool>()
    .WithTools<GetAccountTool>()
    .WithTools<GetAccountHoldingsTool>()
    .WithTools<GetCharacterBuildTool>()
    .WithTools<GetCharactersTool>()
    .WithTools<GetWalletTool>();

var app = builder.Build();

app.MapMcp("/mcp");

using var gw2ApiBudgetLease = Gw2ApiBudgetLease.Acquire(gw2ApiBudgetLeaseOptions);
await app.RunAsync();

public partial class Program;
