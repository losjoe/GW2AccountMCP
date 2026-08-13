using GW2AccountMCP.Gw2;
using GW2AccountMCP.Tools;

var builder = WebApplication.CreateBuilder(args);

var gw2Options = new Gw2ApiOptions(
    builder.Configuration["GW2_API_KEY"] ?? string.Empty,
    builder.Configuration["GW2_API_BASE_URL"] ?? "https://api.guildwars2.com");

builder.Services.AddSingleton(gw2Options);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddHttpClient<IGw2ApiClient, Gw2ApiClient>(client =>
{
    client.BaseAddress = new Uri(gw2Options.BaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<GetAccountTool>()
    .WithTools<GetWalletTool>();

var app = builder.Build();

app.MapMcp("/mcp");

app.Run();

public partial class Program;
