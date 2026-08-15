namespace GW2AccountMCP.Gw2;

public sealed record Gw2ApiBudgetLeaseOptions(string LockPath);

public sealed class Gw2ApiBudgetLease : IDisposable
{
    private FileStream? stream;

    private Gw2ApiBudgetLease(string lockPath, FileStream stream)
    {
        LockPath = lockPath;
        this.stream = stream;
    }

    public string LockPath { get; }

    public static Gw2ApiBudgetLease Acquire(Gw2ApiBudgetLeaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.LockPath))
        {
            throw new InvalidOperationException("GW2 API budget lease is not configured. Set GW2_API_BUDGET_LOCK_PATH to a lock-file path.");
        }

        try
        {
            var lockPath = Path.GetFullPath(options.LockPath);
            var parentDirectory = Path.GetDirectoryName(lockPath)
                ?? throw new InvalidOperationException("GW2 API budget lease is not configured. Set GW2_API_BUDGET_LOCK_PATH to a lock-file path.");
            Directory.CreateDirectory(parentDirectory);
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new Gw2ApiBudgetLease(lockPath, stream);
        }
        catch (IOException)
        {
            throw new InvalidOperationException("GW2 API budget lease could not be acquired. Stop another server using the budget or configure GW2_API_BUDGET_LOCK_PATH.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("GW2 API budget lease could not be acquired. Verify GW2_API_BUDGET_LOCK_PATH and its parent directory permissions.");
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("GW2 API budget lease is not configured. Set GW2_API_BUDGET_LOCK_PATH to a valid lock-file path.");
        }
    }

    public void Dispose() => Interlocked.Exchange(ref stream, null)?.Dispose();
}
