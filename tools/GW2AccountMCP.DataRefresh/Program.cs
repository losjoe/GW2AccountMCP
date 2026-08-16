using GW2AccountMCP.DataRefresh;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
var lockPath = Environment.GetEnvironmentVariable("GW2_API_BUDGET_LOCK_PATH") ?? Path.Combine(Directory.GetCurrentDirectory(), "data", "gw2-api-budget.lock");
var baseUrl = Environment.GetEnvironmentVariable("GW2_API_BASE_URL") ?? "https://api.guildwars2.com";
var itemCommand = new ItemRefreshCommand(() =>
{
    var http = new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute), Timeout = Timeout.InfiniteTimeSpan };
    return new ItemCacheRefreshService(new ItemCatalogDownloadClient(http), new ItemCachePublisher(TimeProvider.System));
}, new UpdaterLeaseFactory(lockPath), summary => Console.WriteLine(ItemRefreshCommand.FormatSuccess(summary)), Console.Error.WriteLine);
var priceCommand = new PriceRefreshCommand(() =>
{
    var http = new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute), Timeout = Timeout.InfiniteTimeSpan };
    return new PriceCacheRefreshService(new PriceCatalogDownloadClient(http), new PriceCachePublisher(TimeProvider.System));
}, new UpdaterLeaseFactory(lockPath), summary => Console.WriteLine(PriceRefreshCommand.FormatSuccess(summary)), Console.Error.WriteLine);
var exitCode = args.FirstOrDefault() is "tp" or "tp-test"
    ? await priceCommand.RunAsync(args, cancellation.Token)
    : await itemCommand.RunAsync(args, cancellation.Token);
return exitCode;
