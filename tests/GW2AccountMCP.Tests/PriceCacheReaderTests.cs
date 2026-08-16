using System.Security.Cryptography;
using System.Text;
using GW2AccountMCP.Prices;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class PriceCacheReaderTests
{
    [Fact]
    public void Load_accepts_an_exact_production_generation()
    {
        using var fixture = PriceCacheFixture.Create("id,whitelisted,buyQuantity,buyUnitPrice,sellQuantity,sellUnitPrice\r\n1,true,12,34,56,78\r\n2,false,0,0,0,0\r\n");

        var snapshot = new PriceCacheReader(new PriceCacheOptions(fixture.Directory)).Load(CancellationToken.None);

        Assert.Equal([(1L, true, 12L, 34L, 56L, 78L), (2L, false, 0L, 0L, 0L, 0L)], snapshot.Prices.Select(price => (price.Id, price.Whitelisted, price.BuyQuantity, price.BuyUnitPrice, price.SellQuantity, price.SellUnitPrice)));
        Assert.Equal(fixture.CompletedAtUtc, snapshot.SourceCompletedAtUtc);
        Assert.Equal(snapshot.Fingerprint, new PriceCacheReader(new PriceCacheOptions(fixture.Directory)).GetCurrentFingerprint());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("scope")]
    [InlineData("incomplete")]
    [InlineData("time")]
    [InlineData("length")]
    [InlineData("hash-name")]
    public void Load_rejects_invalid_manifest_contracts(string problem)
    {
        using var fixture = PriceCacheFixture.Create(PriceCacheFixture.BasicCsv);
        fixture.WriteManifest(problem switch
        {
            "unknown" => fixture.ManifestJson("\"unexpected\":true"),
            "scope" => fixture.ManifestJson("\"scope\":\"test\"", "\"scope\":\"production\""),
            "incomplete" => fixture.ManifestJson("\"isComplete\":false", "\"isComplete\":true"),
            "time" => fixture.ManifestJson($"\"sourceCompletedAtUtc\":\"{fixture.StartedAtUtc.AddTicks(-1):O}\"", $"\"sourceCompletedAtUtc\":\"{fixture.CompletedAtUtc:O}\""),
            "length" => fixture.ManifestJson("\"csvByteLength\":1", $"\"csvByteLength\":{fixture.CsvLength}"),
            _ => fixture.ManifestJson("\"csvFileName\":\"prices.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.csv\"", $"\"csvFileName\":\"prices.{fixture.Hash}.csv\"")
        });

        var error = Assert.Throws<PriceCacheException>(() => new PriceCacheReader(new PriceCacheOptions(fixture.Directory)).Load(CancellationToken.None));

        Assert.Equal("The price cache is unavailable.", error.Message);
        Assert.DoesNotContain(fixture.Directory, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("bom")]
    [InlineData("lf")]
    [InlineData("quoted")]
    [InlineData("duplicate")]
    [InlineData("unsorted")]
    [InlineData("boolean")]
    [InlineData("number")]
    [InlineData("side")]
    public void Load_rejects_invalid_csv_contracts(string problem)
    {
        using var fixture = PriceCacheFixture.Create(PriceCacheFixture.BasicCsv);
        var csv = problem switch
        {
            "bom" => "\uFEFF" + PriceCacheFixture.BasicCsv,
            "lf" => PriceCacheFixture.BasicCsv.Replace("\r\n", "\n", StringComparison.Ordinal),
            "quoted" => "id,whitelisted,buyQuantity,buyUnitPrice,sellQuantity,sellUnitPrice\r\n\"1\",true,1,2,3,4\r\n",
            "duplicate" => "id,whitelisted,buyQuantity,buyUnitPrice,sellQuantity,sellUnitPrice\r\n1,true,1,2,3,4\r\n1,false,1,2,3,4\r\n",
            "unsorted" => "id,whitelisted,buyQuantity,buyUnitPrice,sellQuantity,sellUnitPrice\r\n2,true,1,2,3,4\r\n1,false,1,2,3,4\r\n",
            "boolean" => "id,whitelisted,buyQuantity,buyUnitPrice,sellQuantity,sellUnitPrice\r\n1,True,1,2,3,4\r\n",
            "number" => "id,whitelisted,buyQuantity,buyUnitPrice,sellQuantity,sellUnitPrice\r\n1,true,-1,2,3,4\r\n",
            _ => "id,whitelisted,buyQuantity,buyUnitPrice,sellQuantity,sellUnitPrice\r\n1,true,1,0,3,4\r\n"
        };
        fixture.WriteGeneration(csv, csv.Count(character => character == '\n') - 1);

        Assert.Throws<PriceCacheException>(() => new PriceCacheReader(new PriceCacheOptions(fixture.Directory)).Load(CancellationToken.None));
    }

    [Fact]
    public void GetCurrentFingerprint_changes_when_only_manifest_timestamps_change()
    {
        using var fixture = PriceCacheFixture.Create(PriceCacheFixture.BasicCsv);
        var reader = new PriceCacheReader(new PriceCacheOptions(fixture.Directory));
        var before = reader.GetCurrentFingerprint();
        fixture.WriteManifest(fixture.Manifest(fixture.StartedAtUtc.AddMinutes(1), fixture.CompletedAtUtc.AddMinutes(1), fixture.GeneratedAtUtc.AddMinutes(1), 1, fixture.CsvLength, fixture.Hash));

        Assert.NotEqual(before, reader.GetCurrentFingerprint());
    }

    [Fact]
    public void Csv_containment_prefix_accepts_drive_and_unc_roots_without_accepting_siblings()
    {
        var separator = Path.DirectorySeparatorChar;
        var driveRoot = $"X:{separator}";
        var uncRoot = $"{separator}{separator}server{separator}share{separator}";

        Assert.True(PriceCacheReader.IsContainedPath(driveRoot, driveRoot + "prices.csv"));
        Assert.True(PriceCacheReader.IsContainedPath(uncRoot, uncRoot + "prices.csv"));
        Assert.False(PriceCacheReader.IsContainedPath(driveRoot, $"Y:{separator}other{separator}prices.csv"));
        Assert.False(PriceCacheReader.IsContainedPath(uncRoot, $"{separator}{separator}server{separator}other{separator}prices.csv"));
    }

    [Fact]
    public void Load_rejects_a_cache_directory_reached_through_an_ancestor_reparse_point()
    {
        using var fixture = PriceCacheFixture.Create(PriceCacheFixture.BasicCsv);
        var link = Path.Combine(Path.GetTempPath(), "GW2AccountMCP", Guid.NewGuid().ToString("N"));
        try
        {
            try { Directory.CreateSymbolicLink(link, Path.GetDirectoryName(fixture.Directory)!); }
            catch (UnauthorizedAccessException) { return; }
            catch (IOException) { return; }
            catch (PlatformNotSupportedException) { return; }

            var throughLink = Path.Combine(link, Path.GetFileName(fixture.Directory));
            Assert.Throws<PriceCacheException>(() => new PriceCacheReader(new PriceCacheOptions(throughLink)).Load(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    [Fact]
    public void Load_rejects_many_records_beyond_the_declared_count()
    {
        using var fixture = PriceCacheFixture.Create(PriceCacheFixture.BasicCsv);
        var csv = new StringBuilder("id,whitelisted,buyQuantity,buyUnitPrice,sellQuantity,sellUnitPrice\r\n");
        for (var index = 1; index <= 100_001; index++) csv.Append(index).Append(",true,1,1,1,1\r\n");
        fixture.WriteGeneration(csv.ToString(), 1);

        Assert.Throws<PriceCacheException>(() => new PriceCacheReader(new PriceCacheOptions(fixture.Directory)).Load(CancellationToken.None));
    }

    [Fact]
    public void Load_normalizes_a_manifest_path_replaced_by_a_directory()
    {
        using var fixture = PriceCacheFixture.Create(PriceCacheFixture.BasicCsv);
        File.Delete(fixture.ManifestPath);
        Directory.CreateDirectory(fixture.ManifestPath);

        var error = Assert.Throws<PriceCacheException>(() => new PriceCacheReader(new PriceCacheOptions(fixture.Directory)).Load(CancellationToken.None));

        Assert.Equal("The price cache is unavailable.", error.Message);
        Assert.DoesNotContain(fixture.Directory, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_never_mixes_rows_when_the_manifest_transitions()
    {
        if (!OperatingSystem.IsWindows()) return;
        const int rows = 100_000;
        using var fixture = PriceCacheFixture.Create(PriceCacheFixture.BasicCsv);
        fixture.WriteGeneration(GeneratedCsv(1, rows), rows);
        var replacement = fixture.PrepareGeneration(GeneratedCsv(1_000_001, rows), rows);
        var load = Task.Run(() => new PriceCacheReader(new PriceCacheOptions(fixture.Directory)).Load(CancellationToken.None));
        await Task.Delay(1);
        File.Replace(replacement, fixture.ManifestPath, null);

        var snapshot = await load;
        var first = snapshot.Prices[0].Id;
        Assert.True(first is 1 or 1_000_001);
        Assert.All(snapshot.Prices, price => Assert.InRange(price.Id, first, first + rows - 1));
    }

    private static string GeneratedCsv(long firstId, int rows)
    {
        var csv = new StringBuilder("id,whitelisted,buyQuantity,buyUnitPrice,sellQuantity,sellUnitPrice\r\n");
        for (var index = 0; index < rows; index++) csv.Append(firstId + index).Append(",true,1,1,1,1\r\n");
        return csv.ToString();
    }

    internal sealed class PriceCacheFixture : IDisposable
    {
        internal const string BasicCsv = "id,whitelisted,buyQuantity,buyUnitPrice,sellQuantity,sellUnitPrice\r\n1,true,12,34,56,78\r\n";
        private PriceCacheFixture(string directory, string manifestPath, string csvPath, string hash, int csvLength) { Directory = directory; ManifestPath = manifestPath; CsvPath = csvPath; Hash = hash; CsvLength = csvLength; }
        public string Directory { get; }
        public string ManifestPath { get; }
        public string CsvPath { get; private set; }
        public string Hash { get; private set; }
        public int CsvLength { get; private set; }
        public DateTime StartedAtUtc { get; } = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        public DateTime CompletedAtUtc { get; } = new(2026, 8, 15, 12, 1, 0, DateTimeKind.Utc);
        public DateTime GeneratedAtUtc { get; } = new(2026, 8, 15, 12, 2, 0, DateTimeKind.Utc);
        public static PriceCacheFixture Create(string csv)
        {
            var directory = Path.Combine(Path.GetTempPath(), "GW2AccountMCP", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var bytes = new UTF8Encoding(false).GetBytes(csv);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var csvPath = Path.Combine(directory, $"prices.{hash}.csv");
            File.WriteAllBytes(csvPath, bytes);
            var fixture = new PriceCacheFixture(directory, Path.Combine(directory, "prices.manifest.json"), csvPath, hash, bytes.Length);
            fixture.WriteManifest(fixture.Manifest(fixture.StartedAtUtc, fixture.CompletedAtUtc, fixture.GeneratedAtUtc, csv.Count(character => character == '\n') - 1, bytes.Length, hash));
            return fixture;
        }
        public string ManifestJson(string replacement, string? replace = null) { var json = Manifest(StartedAtUtc, CompletedAtUtc, GeneratedAtUtc, 1, CsvLength, Hash); return replace is null ? json[..^1] + "," + replacement + "}" : json.Replace(replace, replacement, StringComparison.Ordinal); }
        public string Manifest(DateTime started, DateTime completed, DateTime generated, int rows, int length, string hash) => $$"""{"formatVersion":1,"scope":"production","isComplete":true,"sourceStartedAtUtc":"{{started:O}}","sourceCompletedAtUtc":"{{completed:O}}","cacheGeneratedAtUtc":"{{generated:O}}","rowCount":{{rows}},"csvByteLength":{{length}},"csvFileName":"prices.{{hash}}.csv","csvSha256":"{{hash}}"}""";
        public void WriteManifest(string json) => File.WriteAllText(ManifestPath, json, new UTF8Encoding(false));
        public void WriteGeneration(string csv, int rowCount) { var bytes = new UTF8Encoding(false).GetBytes(csv); Hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); CsvLength = bytes.Length; CsvPath = Path.Combine(Directory, $"prices.{Hash}.csv"); File.WriteAllBytes(CsvPath, bytes); WriteManifest(Manifest(StartedAtUtc, CompletedAtUtc, GeneratedAtUtc, rowCount, CsvLength, Hash)); }
        public string PrepareGeneration(string csv, int rowCount) { var bytes = new UTF8Encoding(false).GetBytes(csv); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); File.WriteAllBytes(Path.Combine(Directory, $"prices.{hash}.csv"), bytes); var replacement = Path.Combine(Directory, Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(replacement, Manifest(StartedAtUtc, CompletedAtUtc, GeneratedAtUtc, rowCount, bytes.Length, hash), new UTF8Encoding(false)); return replacement; }
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
}
