using GW2AccountMCP.Gw2;
using GW2AccountMCP.Items;
using GW2AccountMCP.Prices;
using GW2AccountMCP.Tools;

var builder = WebApplication.CreateBuilder(args);

var characterInventoryLimits = CharacterInventoryLimits.FromConfiguration(builder.Configuration);
var gw2Options = new Gw2ApiOptions(
    builder.Configuration["GW2_API_KEY"] ?? string.Empty,
    builder.Configuration["GW2_API_BASE_URL"] ?? "https://api.guildwars2.com",
    characterInventoryLimits);
var gw2ApiBudgetLeaseOptions = new Gw2ApiBudgetLeaseOptions(
    builder.Configuration["GW2_API_BUDGET_LOCK_PATH"] ?? "data/gw2-api-budget.lock");
var itemCacheOptions = new ItemCacheOptions(
    builder.Configuration["GW2_PUBLIC_CACHE_PATH"] ?? "data/public-cache");

builder.Services.AddSingleton(gw2Options);
builder.Services.AddSingleton(gw2ApiBudgetLeaseOptions);
builder.Services.AddSingleton(itemCacheOptions);
builder.Services.AddSingleton(new PriceCacheOptions(builder.Configuration["GW2_PUBLIC_CACHE_PATH"] ?? "data/public-cache"));
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
builder.Services.AddSingleton<IPriceCacheReader, PriceCacheReader>();
builder.Services.AddSingleton<IPriceSnapshotProvider, PriceSnapshotProvider>();
builder.Services.AddSingleton<IItemNameLookup, ItemNameLookup>();
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<FindItemsTool>()
    .WithTools<GetItemsTool>()
    .WithTools<GetRecipesTool>()
    .WithTools<GetItemPricesTool>()
    .WithTools<ValueItemsTool>()
    .WithTools<GetAccountTool>()
    .WithTools<GetAccountHoldingsTool>()
    .WithTools<GetTradingPostActivityTool>()
    .WithTools<GetAchievementProgressTool>()
    .WithTools<GetMasteryProgressTool>()
    .WithTools<GetLegendaryArmoryTool>()
    .WithTools<GetCharacterBuildTool>()
    .WithTools<GetCharacterEquipmentTool>()
    .WithTools<GetCharacterInventoryTool>()
    .WithTools<GetCharactersTool>()
    .WithTools<GetWalletTool>();

var app = builder.Build();

app.MapMcp("/mcp");

using var gw2ApiBudgetLease = Gw2ApiBudgetLease.Acquire(gw2ApiBudgetLeaseOptions);
await app.RunAsync();

public partial class Program;
