using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GW2AccountMCP.Items;

public sealed record ItemCacheOptions(string DirectoryPath);
public sealed record ItemCachePathFingerprint(string NormalizedPath, DateTime LastWriteTimeUtc, long Length);
public sealed record ItemCacheFingerprint(ItemCachePathFingerprint Manifest, ItemCachePathFingerprint Csv);
public sealed record CachedItem(long Id, string Name, string Type, string Rarity, int Level);
public sealed record ItemCacheSnapshot(IReadOnlyList<CachedItem> Items, ItemCacheFingerprint Fingerprint, DateTime GeneratedAtUtc);

public interface IItemCacheReader
{
    ItemCacheFingerprint GetCurrentFingerprint();
    ItemCacheSnapshot Load(CancellationToken cancellationToken);
}

public class ItemCacheException : Exception
{
    public ItemCacheException(string message) : base(message) { }
}

public sealed class ItemCacheReader : IItemCacheReader
{
    private const string ManifestFileName = "items.manifest.json";
    private const string UnavailableMessage = "The item cache is unavailable.";
    private static readonly Regex CsvFileNamePattern = new("^items\\.([0-9a-f]{64})\\.csv$", RegexOptions.CultureInvariant);
    private readonly string directoryPath;
    private readonly string manifestPath;

    public ItemCacheReader(ItemCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DirectoryPath))
        {
            throw new ItemCacheException("The item cache location is not configured.");
        }

        try
        {
            directoryPath = Path.GetFullPath(options.DirectoryPath);
            manifestPath = Path.Combine(directoryPath, ManifestFileName);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ItemCacheException("The item cache location is invalid.");
        }
    }

    public ItemCacheFingerprint GetCurrentFingerprint()
    {
        var manifestBefore = CaptureFingerprint(manifestPath);
        var manifest = ReadManifest(ReadAllBytes(manifestPath, CancellationToken.None, false));
        var csvPath = ResolveCsvPath(manifest);
        var csvBefore = CaptureFingerprint(csvPath);
        var manifestAfter = CaptureFingerprint(manifestPath);
        var csvAfter = CaptureFingerprint(csvPath);
        if (manifestBefore != manifestAfter || csvBefore != csvAfter)
        {
            throw Unavailable();
        }

        return new ItemCacheFingerprint(manifestBefore, csvBefore);
    }

    public ItemCacheSnapshot Load(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        for (var attempt = 0; attempt != 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return LoadOnce(cancellationToken);
            }
            catch (CacheTransitionException) when (attempt == 0)
            {
            }
            catch (CacheTransitionException)
            {
                throw Unavailable();
            }
        }

        throw Unavailable();
    }

    private ItemCacheSnapshot LoadOnce(CancellationToken cancellationToken)
    {
        var manifestBefore = CaptureFingerprint(manifestPath);
        Manifest manifest;
        try
        {
            manifest = ReadManifest(ReadAllBytes(manifestPath, cancellationToken, true));
        }
        catch (InvalidCacheException)
        {
            if (ManifestChanged(manifestBefore)) throw new CacheTransitionException();
            throw;
        }

        var csvPath = ResolveCsvPath(manifest);
        var csvBefore = CaptureFingerprint(csvPath);
        try
        {
            var csvBytes = ReadAllBytes(csvPath, cancellationToken, true);
            cancellationToken.ThrowIfCancellationRequested();
            var actualHash = Convert.ToHexString(SHA256.HashData(csvBytes)).ToLowerInvariant();
            cancellationToken.ThrowIfCancellationRequested();
            if (actualHash != manifest.CsvSha256)
            {
                throw Invalid();
            }

            var items = ReadCsv(csvBytes, manifest.RowCount, cancellationToken);
            var manifestAfter = CaptureFingerprint(manifestPath, true);
            var csvAfter = CaptureFingerprint(csvPath, true);
            if (manifestBefore != manifestAfter || csvBefore != csvAfter)
            {
                throw new CacheTransitionException();
            }

            return new ItemCacheSnapshot(items, new ItemCacheFingerprint(manifestBefore, csvBefore), manifest.GeneratedAtUtc);
        }
        catch (InvalidCacheException)
        {
            if (FilesChanged(manifestBefore, csvBefore, csvPath)) throw new CacheTransitionException();
            throw;
        }
    }

    private bool ManifestChanged(ItemCachePathFingerprint manifestBefore)
    {
        try
        {
            return manifestBefore != CaptureFingerprint(manifestPath);
        }
        catch (ItemCacheException)
        {
            return true;
        }
    }

    private bool FilesChanged(ItemCachePathFingerprint manifestBefore, ItemCachePathFingerprint csvBefore, string csvPath)
    {
        try
        {
            return manifestBefore != CaptureFingerprint(manifestPath) || csvBefore != CaptureFingerprint(csvPath);
        }
        catch (ItemCacheException)
        {
            return true;
        }
    }

    private string ResolveCsvPath(Manifest manifest)
    {
        var match = CsvFileNamePattern.Match(manifest.CsvFileName);
        if (!match.Success || match.Groups[1].Value != manifest.CsvSha256)
        {
            throw Invalid();
        }

        var candidate = Path.GetFullPath(Path.Combine(directoryPath, manifest.CsvFileName));
        var directoryPrefix = directoryPath.EndsWith(Path.DirectorySeparatorChar) || directoryPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? directoryPath
            : directoryPath + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(directoryPrefix, comparison))
        {
            throw Invalid();
        }

        return candidate;
    }

    private static Manifest ReadManifest(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid();
            }

            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!properties.TryAdd(property.Name, property.Value))
                {
                    throw Invalid();
                }
            }

            if (properties.Count != 7
                || !TryInt(properties, "formatVersion", out var formatVersion) || formatVersion != 1
                || !TryString(properties, "generatedAtUtc", out var generatedAtUtcText)
                || !TryString(properties, "gw2SchemaVersion", out var schemaVersion) || string.IsNullOrWhiteSpace(schemaVersion)
                || !TryString(properties, "language", out var language) || language != "en"
                || !TryInt(properties, "rowCount", out var rowCount) || rowCount <= 0
                || !TryString(properties, "csvFileName", out var csvFileName)
                || !TryString(properties, "csvSha256", out var csvSha256)
                || !CsvFileNamePattern.IsMatch(csvFileName)
                || !IsLowercaseSha256(csvSha256))
            {
                throw Invalid();
            }

            if (!DateTime.TryParseExact(generatedAtUtcText, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var generatedAtUtc)
                || generatedAtUtc.Kind != DateTimeKind.Utc
                || generatedAtUtc.ToString("O", CultureInfo.InvariantCulture) != generatedAtUtcText)
            {
                throw Invalid();
            }

            return new Manifest(generatedAtUtc, rowCount, csvFileName, csvSha256);
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    private static bool TryInt(IReadOnlyDictionary<string, JsonElement> properties, string name, out int value)
    {
        value = 0;
        return properties.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    private static bool TryString(IReadOnlyDictionary<string, JsonElement> properties, string name, out string value)
    {
        value = string.Empty;
        if (!properties.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString()!;
        return true;
    }

    private static IReadOnlyList<CachedItem> ReadCsv(byte[] bytes, int expectedRows, CancellationToken cancellationToken)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            throw Invalid();
        }

        string csv;
        try
        {
            csv = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Invalid();
        }

        var records = ParseCsv(csv, cancellationToken);
        if (records.Count == 0 || !records[0].SequenceEqual(["id", "name", "type", "rarity", "level"]))
        {
            throw Invalid();
        }

        var ids = new HashSet<long>();
        var items = new List<CachedItem>(records.Count - 1);
        for (var recordIndex = 1; recordIndex < records.Count; recordIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = records[recordIndex];
            if (record.Length != 5
                || !long.TryParse(record[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || !ids.Add(id)
                || string.IsNullOrWhiteSpace(record[1]) || string.IsNullOrWhiteSpace(record[2]) || string.IsNullOrWhiteSpace(record[3])
                || !int.TryParse(record[4], NumberStyles.None, CultureInfo.InvariantCulture, out var level) || level < 0)
            {
                throw Invalid();
            }

            items.Add(new CachedItem(id, record[1], record[2], record[3], level));
        }

        if (items.Count != expectedRows)
        {
            throw Invalid();
        }

        return items;
    }

    private static List<string[]> ParseCsv(string csv, CancellationToken cancellationToken)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var state = CsvState.StartField;
        for (var position = 0; position < csv.Length; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var character = csv[position];
            switch (state)
            {
                case CsvState.StartField:
                    if (character == ',') fields.Add(string.Empty);
                    else if (character == '"') state = CsvState.Quoted;
                    else if (character == '\r') { CompleteRecord(csv, ref position, fields, field, records); }
                    else if (character == '\n') throw Invalid();
                    else { field.Append(character); state = CsvState.Unquoted; }
                    break;
                case CsvState.Unquoted:
                    if (character == ',') { fields.Add(field.ToString()); field.Clear(); state = CsvState.StartField; }
                    else if (character == '\r') { CompleteRecord(csv, ref position, fields, field, records); state = CsvState.StartField; }
                    else if (character is '\n' or '"') throw Invalid();
                    else field.Append(character);
                    break;
                case CsvState.Quoted:
                    if (character == '"') state = CsvState.AfterQuote;
                    else if (character == '\r')
                    {
                        if (++position >= csv.Length || csv[position] != '\n') throw Invalid();
                        field.Append("\r\n");
                    }
                    else field.Append(character);
                    break;
                case CsvState.AfterQuote:
                    if (character == '"') { field.Append(character); state = CsvState.Quoted; }
                    else if (character == ',') { fields.Add(field.ToString()); field.Clear(); state = CsvState.StartField; }
                    else if (character == '\r') { CompleteRecord(csv, ref position, fields, field, records); state = CsvState.StartField; }
                    else throw Invalid();
                    break;
            }
        }

        if (state == CsvState.Quoted)
        {
            throw Invalid();
        }

        if (state != CsvState.StartField || fields.Count != 0)
        {
            fields.Add(field.ToString());
            records.Add(fields.ToArray());
        }

        return records;
    }

    private static void CompleteRecord(string csv, ref int position, List<string> fields, StringBuilder field, List<string[]> records)
    {
        if (++position >= csv.Length || csv[position] != '\n')
        {
            throw Invalid();
        }

        fields.Add(field.ToString());
        field.Clear();
        records.Add(fields.ToArray());
        fields.Clear();
    }

    private static ItemCachePathFingerprint CaptureFingerprint(string path, bool changedDuringRead = false)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw changedDuringRead ? new CacheTransitionException() : Unavailable();
            }

            return new ItemCachePathFingerprint(path, info.LastWriteTimeUtc, info.Length);
        }
        catch (CacheTransitionException)
        {
            throw;
        }
        catch (ItemCacheException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (changedDuringRead) throw new CacheTransitionException();
            throw Unavailable();
        }
    }

    private static byte[] ReadAllBytes(string path, CancellationToken cancellationToken, bool changedDuringRead)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var memory = new MemoryStream();
            var buffer = new byte[81_920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                memory.Write(buffer, 0, read);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return memory.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            if (changedDuringRead) throw new CacheTransitionException();
            throw Unavailable();
        }
        catch (UnauthorizedAccessException)
        {
            if (changedDuringRead) throw new CacheTransitionException();
            throw Unavailable();
        }
    }

    private static bool IsLowercaseSha256(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static InvalidCacheException Invalid() => new();
    private static ItemCacheException Unavailable() => new(UnavailableMessage);

    private sealed record Manifest(DateTime GeneratedAtUtc, int RowCount, string CsvFileName, string CsvSha256);
    private sealed class InvalidCacheException : ItemCacheException { public InvalidCacheException() : base(UnavailableMessage) { } }
    private sealed class CacheTransitionException : ItemCacheException { public CacheTransitionException() : base(UnavailableMessage) { } }
    private enum CsvState { StartField, Unquoted, Quoted, AfterQuote }
}
