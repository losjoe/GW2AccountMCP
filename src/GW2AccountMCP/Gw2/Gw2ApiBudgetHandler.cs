namespace GW2AccountMCP.Gw2;

public sealed class Gw2ApiStartGate(TimeProvider timeProvider)
{
    private static readonly TimeSpan MinimumStartInterval = TimeSpan.FromMilliseconds(250);
    private readonly SemaphoreSlim synchronization = new(1, 1);
    private DateTimeOffset? lastStart;

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await synchronization.WaitAsync(cancellationToken);
        try
        {
            while (lastStart is { } previousStart)
            {
                var waitDuration = previousStart + MinimumStartInterval - timeProvider.GetUtcNow();
                if (waitDuration <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(waitDuration, timeProvider, cancellationToken);
            }

            lastStart = timeProvider.GetUtcNow();
        }
        finally
        {
            synchronization.Release();
        }
    }
}

public sealed class Gw2ApiBudgetHandler(Gw2ApiStartGate startGate) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await startGate.WaitAsync(cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
