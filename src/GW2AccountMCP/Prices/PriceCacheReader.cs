using System.Globalization;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GW2AccountMCP.Prices;

public sealed record PriceCacheOptions(string DirectoryPath);
public sealed record PriceCacheFingerprint(string ManifestSha256, string CsvFileName, DateTime ManifestLastWriteTimeUtc, long ManifestLength, DateTime CsvLastWriteTimeUtc, long CsvLength);
public sealed record CachedPrice(long Id, bool Whitelisted, long BuyQuantity, long BuyUnitPrice, long SellQuantity, long SellUnitPrice);
public sealed record PriceCacheSnapshot(IReadOnlyList<CachedPrice> Prices, PriceCacheFingerprint Fingerprint, DateTime SourceStartedAtUtc, DateTime SourceCompletedAtUtc, DateTime CacheGeneratedAtUtc);

public interface IPriceCacheReader
{
    PriceCacheFingerprint GetCurrentFingerprint();
    PriceCacheSnapshot Load(CancellationToken cancellationToken);
}

public class PriceCacheException(string message) : Exception(message);

public sealed class PriceCacheReader : IPriceCacheReader
{
    private const int ManifestLimit = 64 * 1024;
    private const int CsvLimit = 16 * 1024 * 1024;
    private const string UnavailableMessage = "The price cache is unavailable.";
    private static readonly Regex CsvName = new("^prices\\.([0-9a-f]{64})\\.csv$", RegexOptions.CultureInvariant);
    private readonly string directoryPath;
    private readonly string manifestPath;

    public PriceCacheReader(PriceCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            if (string.IsNullOrWhiteSpace(options.DirectoryPath)) throw new ArgumentException();
            directoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DirectoryPath));
            manifestPath = Path.Combine(directoryPath, "prices.manifest.json");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            throw Unavailable();
        }
    }

    public PriceCacheFingerprint GetCurrentFingerprint()
    {
        try
        {
            var before = ReadManifestWithFingerprint(CancellationToken.None);
            var after = ReadManifestWithFingerprint(CancellationToken.None);
            if (before.Fingerprint != after.Fingerprint || before.Manifest != after.Manifest) throw Unavailable();
            return before.Fingerprint;
        }
        catch (PriceCacheException) { throw; }
        catch (Exception exception) when (IsExpectedCacheFailure(exception)) { throw Unavailable(); }
    }

    public PriceCacheSnapshot Load(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var attempt = 0; attempt != 2; attempt++)
            {
                try { return LoadOnce(cancellationToken); }
                catch (TransitionException) when (attempt == 0) { }
                catch (TransitionException) { throw Unavailable(); }
            }
            throw Unavailable();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PriceCacheException) { throw; }
        catch (Exception exception) when (IsExpectedCacheFailure(exception)) { throw Unavailable(); }
    }

    private PriceCacheSnapshot LoadOnce(CancellationToken cancellationToken)
    {
        var before = ReadManifestWithFingerprint(cancellationToken);
        var csvPath = ResolveCsvPath(before.Manifest);
        try
        {
            var csv = ReadBoundedFile(csvPath, CsvLimit, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (csv.Length != before.Manifest.CsvByteLength || Hash(csv, cancellationToken) != before.Manifest.CsvSha256) throw Invalid();
            cancellationToken.ThrowIfCancellationRequested();
            var prices = ParseCsv(csv, before.Manifest.RowCount, cancellationToken);
            var after = ReadManifestWithFingerprint(cancellationToken);
            if (before.Fingerprint != after.Fingerprint || before.Manifest != after.Manifest) throw new TransitionException();
            return new PriceCacheSnapshot(prices, before.Fingerprint, before.Manifest.SourceStartedAtUtc, before.Manifest.SourceCompletedAtUtc, before.Manifest.CacheGeneratedAtUtc);
        }
        catch (PriceCacheException exception) when (exception is not TransitionException)
        {
            try
            {
                if (before.Fingerprint != ReadManifestWithFingerprint(CancellationToken.None).Fingerprint) throw new TransitionException();
            }
            catch (TransitionException) { throw; }
            catch (Exception recheckException) when (IsExpectedCacheFailure(recheckException)) { }
            throw;
        }
    }

    private (Manifest Manifest, PriceCacheFingerprint Fingerprint) ReadManifestWithFingerprint(CancellationToken cancellationToken)
    {
        EnsureRoot();
        var bytes = ReadBoundedFile(manifestPath, ManifestLimit, cancellationToken);
        var info = new FileInfo(manifestPath);
        var manifest = ParseManifest(bytes);
        var csvPath = ResolveCsvPath(manifest);
        EnsureRegularFile(csvPath);
        var csvInfo = new FileInfo(csvPath);
        return (manifest, new PriceCacheFingerprint(Hash(bytes, cancellationToken), manifest.CsvFileName, info.LastWriteTimeUtc, info.Length, csvInfo.LastWriteTimeUtc, csvInfo.Length));
    }

    private void EnsureRoot()
    {
        try
        {
            var root = Path.GetPathRoot(directoryPath);
            if (string.IsNullOrEmpty(root)) throw Unavailable();
            var current = Path.TrimEndingDirectorySeparator(root);
            if (string.IsNullOrEmpty(current)) current = root;
            EnsureDirectory(current);
            var relative = directoryPath[root.Length..];
            foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                EnsureDirectory(current);
            }
        }
        catch (PriceCacheException) { throw; }
        catch (Exception exception) when (IsExpectedCacheFailure(exception)) { throw Unavailable(); }
    }

    private static void EnsureDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw Unavailable();
    }

    private string ResolveCsvPath(Manifest manifest)
    {
        var match = CsvName.Match(manifest.CsvFileName);
        if (!match.Success || match.Groups[1].Value != manifest.CsvSha256) throw Invalid();
        var candidate = Path.GetFullPath(Path.Combine(directoryPath, manifest.CsvFileName));
        if (!IsContainedPath(directoryPath, candidate)) throw Invalid();
        return candidate;
    }

    internal static bool IsContainedPath(string directoryPath, string candidate)
    {
        var prefix = directoryPath.EndsWith(Path.DirectorySeparatorChar) || directoryPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? directoryPath
            : directoryPath + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.StartsWith(prefix, comparison);
    }

    private static Manifest ParseManifest(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject()) if (!properties.TryAdd(property.Name, property.Value)) throw Invalid();
            if (properties.Count != 10
                || !Int(properties, "formatVersion", out var version) || version != 1
                || !String(properties, "scope", out var scope) || scope != "production"
                || !Bool(properties, "isComplete", out var complete) || !complete
                || !String(properties, "sourceStartedAtUtc", out var startedText) || !Utc(startedText, out var started)
                || !String(properties, "sourceCompletedAtUtc", out var completedText) || !Utc(completedText, out var completed)
                || !String(properties, "cacheGeneratedAtUtc", out var generatedText) || !Utc(generatedText, out var generated)
                || started > completed || completed > generated
                || !Int(properties, "rowCount", out var rows) || rows is < 1 or > 100_000
                || !Long(properties, "csvByteLength", out var length) || length is < 1 or > CsvLimit
                || !String(properties, "csvFileName", out var name)
                || !String(properties, "csvSha256", out var hash) || !LowerHash(hash)) throw Invalid();
            return new Manifest(started, completed, generated, rows, length, name, hash);
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static IReadOnlyList<CachedPrice> ParseCsv(byte[] bytes, int expectedRows, CancellationToken cancellationToken)
    {
        ScanRecordBoundaries(bytes, expectedRows, cancellationToken);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw Invalid(); }
        var records = text[..^2].Split("\r\n", StringSplitOptions.None);
        if (records.Length < 2 || records[0] != "id,whitelisted,buyQuantity,buyUnitPrice,sellQuantity,sellUnitPrice") throw Invalid();
        var prices = new List<CachedPrice>(records.Length - 1);
        long previousId = 0;
        for (var index = 1; index < records.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = records[index].Split(',');
            if (fields.Length != 6 || fields.Any(field => field.Length == 0 || field.Contains('"'))) throw Invalid();
            if (!Positive(fields[0], out var id) || id <= previousId
                || !TryBoolean(fields[1], out var whitelisted)
                || !Nonnegative(fields[2], out var buyQuantity) || !Nonnegative(fields[3], out var buyUnitPrice)
                || !Nonnegative(fields[4], out var sellQuantity) || !Nonnegative(fields[5], out var sellUnitPrice)
                || (buyQuantity == 0) != (buyUnitPrice == 0) || (sellQuantity == 0) != (sellUnitPrice == 0)) throw Invalid();
            prices.Add(new CachedPrice(id, whitelisted, buyQuantity, buyUnitPrice, sellQuantity, sellUnitPrice));
            previousId = id;
        }
        if (prices.Count != expectedRows) throw Invalid();
        return prices;
    }

    private static void ScanRecordBoundaries(byte[] bytes, int expectedRows, CancellationToken cancellationToken)
    {
        if (bytes.Length is < 2 || bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) throw Invalid();
        var records = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if ((index & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (bytes[index] == (byte)'\r')
            {
                if (++index >= bytes.Length || bytes[index] != (byte)'\n') throw Invalid();
                if (++records > expectedRows + 1 || records > 100_001) throw Invalid();
            }
            else if (bytes[index] == (byte)'\n') throw Invalid();
        }
        if (records != expectedRows + 1 || bytes[^2] != (byte)'\r' || bytes[^1] != (byte)'\n') throw Invalid();
    }

    private static byte[] ReadBoundedFile(string path, int limit, CancellationToken cancellationToken)
    {
        try
        {
            EnsureRegularFile(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > limit) throw Invalid();
            using var memory = new MemoryStream((int)stream.Length);
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (memory.Length > limit - read) throw Invalid();
                memory.Write(buffer, 0, read);
            }
            return memory.ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (PriceCacheException) { throw; }
        catch (Exception exception) when (IsExpectedCacheFailure(exception)) { throw Unavailable(); }
    }

    private static void EnsureRegularFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw Unavailable();
    }
    private static bool IsExpectedCacheFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException or PathTooLongException;
    private static bool Int(IReadOnlyDictionary<string, JsonElement> source, string name, out int value) { value = 0; return source.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value); }
    private static bool Long(IReadOnlyDictionary<string, JsonElement> source, string name, out long value) { value = 0; return source.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value); }
    private static bool String(IReadOnlyDictionary<string, JsonElement> source, string name, out string value) { value = string.Empty; return source.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String && (value = element.GetString()!) is not null; }
    private static bool Bool(IReadOnlyDictionary<string, JsonElement> source, string name, out bool value) { value = false; return source.TryGetValue(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False && (value = element.GetBoolean()) == element.GetBoolean(); }
    private static bool Utc(string value, out DateTime result) => DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result) && result.Kind == DateTimeKind.Utc && result.ToString("O", CultureInfo.InvariantCulture) == value;
    private static bool LowerHash(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool TryBoolean(string value, out bool result) { result = value == "true"; return result || value == "false"; }
    private static string Hash(byte[] bytes, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int blockSize = 81_920;
        for (var offset = 0; offset < bytes.Length; offset += blockSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(bytes, offset, Math.Min(blockSize, bytes.Length - offset));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static bool Positive(string value, out long result) { result = 0; return CanonicalDecimal(value) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0; }
    private static bool Nonnegative(string value, out long result) { result = 0; return CanonicalDecimal(value) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0; }
    private static bool CanonicalDecimal(string value) => value.Length != 0 && value.All(character => character is >= '0' and <= '9') && (value.Length == 1 || value[0] != '0');
    private static PriceCacheException Unavailable() => new(UnavailableMessage);
    private static PriceCacheException Invalid() => Unavailable();
    private sealed record Manifest(DateTime SourceStartedAtUtc, DateTime SourceCompletedAtUtc, DateTime CacheGeneratedAtUtc, int RowCount, long CsvByteLength, string CsvFileName, string CsvSha256);
    private sealed class TransitionException : PriceCacheException { public TransitionException() : base(UnavailableMessage) { } }
}
