using System.Security.Cryptography;
using System.Text;
using GW2AccountMCP.Items;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class ItemCacheReaderTests
{
    [Fact]
    public void Load_returns_validated_items_generation_and_fingerprints()
    {
        using var fixture = CacheFixture.Create(
            "id,name,type,rarity,level\r\n101,\"Sword, \"\"Dawn\"\"\",Weapon,Rare,80\r\n202,\"Line one\r\nLine two Ω\",Armor,Exotic,0\r\n");

        var snapshot = new ItemCacheReader(new ItemCacheOptions(fixture.Directory)).Load(CancellationToken.None);

        Assert.Equal([(101L, "Sword, \"Dawn\"", "Weapon", "Rare", 80), (202L, "Line one\r\nLine two Ω", "Armor", "Exotic", 0)], snapshot.Items.Select(item => (item.Id, item.Name, item.Type, item.Rarity, item.Level)));
        Assert.Equal(fixture.GeneratedAtUtc, snapshot.GeneratedAtUtc);
        Assert.Equal(Path.GetFullPath(fixture.ManifestPath), snapshot.Fingerprint.Manifest.NormalizedPath);
        Assert.Equal(Path.GetFullPath(fixture.CsvPath), snapshot.Fingerprint.Csv.NormalizedPath);
        Assert.Equal(snapshot.Fingerprint, new ItemCacheReader(new ItemCacheOptions(fixture.Directory)).GetCurrentFingerprint());
    }

    [Fact]
    public void Load_preserves_a_bare_line_feed_inside_a_quoted_name()
    {
        using var fixture = CacheFixture.Create(BasicCsv);
        fixture.WriteGeneration("id,name,type,rarity,level\r\n1,\"Line one\nLine two\",Weapon,Rare,80\r\n", 1);

        var snapshot = new ItemCacheReader(new ItemCacheOptions(fixture.Directory)).Load(CancellationToken.None);

        Assert.Equal("Line one\nLine two", Assert.Single(snapshot.Items).Name);
    }

    [Fact]
    public void Load_preserves_CRLF_and_bare_line_feed_inside_a_quoted_name()
    {
        using var fixture = CacheFixture.Create(BasicCsv);
        fixture.WriteGeneration("id,name,type,rarity,level\r\n1,\"Line one\r\nLine two\nLine three\",Weapon,Rare,80\r\n", 1);

        var snapshot = new ItemCacheReader(new ItemCacheOptions(fixture.Directory)).Load(CancellationToken.None);

        Assert.Equal("Line one\r\nLine two\nLine three", Assert.Single(snapshot.Items).Name);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("wrong-type")]
    [InlineData("version")]
    [InlineData("language")]
    [InlineData("noncanonical-time")]
    [InlineData("nonpositive-count")]
    [InlineData("bad-hash")]
    [InlineData("bad-filename")]
    [InlineData("hash-filename-mismatch")]
    [InlineData("path-traversal")]
    public void Load_rejects_invalid_manifest_without_disclosing_paths_or_content(string problem)
    {
        using var fixture = CacheFixture.Create(BasicCsv);
        fixture.WriteManifest(problem switch
        {
            "missing" => "{}",
            "unknown" => fixture.ManifestJson("\"extra\":true"),
            "wrong-type" => fixture.ManifestJson("\"rowCount\":\"1\"", replace: "\"rowCount\":1"),
            "version" => fixture.ManifestJson("\"formatVersion\":2", replace: "\"formatVersion\":1"),
            "language" => fixture.ManifestJson("\"language\":\"fr\"", replace: "\"language\":\"en\""),
            "noncanonical-time" => fixture.ManifestJson("\"generatedAtUtc\":\"2026-08-14T12:34:56.0000000+00:00\"", replace: $"\"generatedAtUtc\":\"{fixture.GeneratedAtUtc:O}\""),
            "nonpositive-count" => fixture.ManifestJson("\"rowCount\":0", replace: "\"rowCount\":1"),
            "bad-hash" => fixture.ManifestJson("\"csvSha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"", replace: $"\"csvSha256\":\"{fixture.Hash}\""),
            "bad-filename" => fixture.ManifestJson("\"csvFileName\":\"items.not-a-hash.csv\"", replace: $"\"csvFileName\":\"items.{fixture.Hash}.csv\""),
            "hash-filename-mismatch" => fixture.ManifestJson("\"csvFileName\":\"items.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.csv\"", replace: $"\"csvFileName\":\"items.{fixture.Hash}.csv\""),
            _ => fixture.ManifestJson("\"csvFileName\":\"../secret.csv\"", replace: $"\"csvFileName\":\"items.{fixture.Hash}.csv\"")
        });

        var error = Assert.ThrowsAny<ItemCacheException>(() => new ItemCacheReader(new ItemCacheOptions(fixture.Directory)).Load(CancellationToken.None));

        Assert.Equal("The item cache is unavailable.", error.Message);
        Assert.DoesNotContain(fixture.Directory, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("bom")]
    [InlineData("invalid-utf8")]
    [InlineData("header")]
    [InlineData("extra-column")]
    [InlineData("missing-column")]
    [InlineData("malformed-quote")]
    [InlineData("unterminated-quote")]
    [InlineData("unquoted-bare-line-feed")]
    [InlineData("lone-carriage-return")]
    [InlineData("blank-required")]
    [InlineData("fractional-id")]
    [InlineData("overflow-id")]
    [InlineData("duplicate-id")]
    [InlineData("nonpositive-id")]
    [InlineData("negative-level")]
    [InlineData("overflow-level")]
    [InlineData("count-mismatch")]
    public void Load_rejects_invalid_csv_records(string problem)
    {
        using var fixture = CacheFixture.Create(BasicCsv);
        var csv = problem switch
        {
            "bom" => new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(BasicCsv)).ToArray(),
            "invalid-utf8" => new byte[] { 0x69, 0x64, 0x2C, 0x6E, 0x61, 0x6D, 0x65, 0x2C, 0x74, 0x79, 0x70, 0x65, 0x2C, 0x72, 0x61, 0x72, 0x69, 0x74, 0x79, 0x2C, 0x6C, 0x65, 0x76, 0x65, 0x6C, 0x0D, 0x0A, 0x31, 0x2C, 0xFF },
            _ => new UTF8Encoding(false).GetBytes(problem switch
            {
                "header" => "id,name,type,rarity,Level\r\n1,Alpha,Weapon,Rare,80\r\n",
                "extra-column" => "id,name,type,rarity,level\r\n1,Alpha,Weapon,Rare,80,extra\r\n",
                "missing-column" => "id,name,type,rarity,level\r\n1,Alpha,Weapon,Rare\r\n",
                "malformed-quote" => "id,name,type,rarity,level\r\n1,\"Alpha\"tail,Weapon,Rare,80\r\n",
                "unterminated-quote" => "id,name,type,rarity,level\r\n1,\"Alpha,Weapon,Rare,80\r\n",
                "unquoted-bare-line-feed" => "id,name,type,rarity,level\r\n1,Alpha,Weapon,Rare,80\n",
                "lone-carriage-return" => "id,name,type,rarity,level\r\n1,Alpha,Weapon,Rare,80\r",
                "blank-required" => "id,name,type,rarity,level\r\n1, ,Weapon,Rare,80\r\n",
                "fractional-id" => "id,name,type,rarity,level\r\n1.5,Alpha,Weapon,Rare,80\r\n",
                "overflow-id" => "id,name,type,rarity,level\r\n9223372036854775808,Alpha,Weapon,Rare,80\r\n",
                "duplicate-id" => "id,name,type,rarity,level\r\n1,Alpha,Weapon,Rare,80\r\n1,Beta,Armor,Exotic,0\r\n",
                "nonpositive-id" => "id,name,type,rarity,level\r\n0,Alpha,Weapon,Rare,80\r\n",
                "negative-level" => "id,name,type,rarity,level\r\n1,Alpha,Weapon,Rare,-1\r\n",
                "overflow-level" => "id,name,type,rarity,level\r\n1,Alpha,Weapon,Rare,2147483648\r\n",
                _ => BasicCsv
            })
        };
        fixture.WriteGeneration(csv, problem == "count-mismatch" ? 2 : 1);

        Assert.ThrowsAny<ItemCacheException>(() => new ItemCacheReader(new ItemCacheOptions(fixture.Directory)).Load(CancellationToken.None));
    }

    [Fact]
    public void Load_rejects_a_csv_hash_mismatch()
    {
        using var fixture = CacheFixture.Create(BasicCsv);
        File.WriteAllText(fixture.CsvPath, "id,name,type,rarity,level\r\n1,Changed,Weapon,Rare,80\r\n", new UTF8Encoding(false));

        Assert.ThrowsAny<ItemCacheException>(() => new ItemCacheReader(new ItemCacheOptions(fixture.Directory)).Load(CancellationToken.None));
    }

    [Fact]
    public void Load_propagates_cancellation()
    {
        using var fixture = CacheFixture.Create(BasicCsv);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new ItemCacheReader(new ItemCacheOptions(fixture.Directory)).Load(cancellation.Token));
    }

    [Fact]
    public void GetCurrentFingerprint_follows_the_manifest_generation_and_rejects_an_unsafe_manifest()
    {
        using var fixture = CacheFixture.Create(BasicCsv);
        var reader = new ItemCacheReader(new ItemCacheOptions(fixture.Directory));
        var first = reader.GetCurrentFingerprint();
        fixture.WriteGeneration("id,name,type,rarity,level\r\n2,Beta,Armor,Exotic,0\r\n", 1);

        var second = reader.GetCurrentFingerprint();

        Assert.NotEqual(first.Csv, second.Csv);
        fixture.WriteManifest(fixture.ManifestJson("\"csvFileName\":\"../secret.csv\"", replace: $"\"csvFileName\":\"items.{fixture.Hash}.csv\""));
        Assert.ThrowsAny<ItemCacheException>(() => reader.GetCurrentFingerprint());
    }

    [Fact]
    public async Task Load_does_not_mix_generations_when_the_manifest_is_atomically_replaced_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int rowCount = 150_000;
        using var fixture = CacheFixture.Create(BasicCsv);
        fixture.WriteGeneration(GeneratedCsv(1, rowCount), rowCount);
        var replacementManifest = fixture.PrepareGeneration(GeneratedCsv(1_000_001, rowCount), rowCount);
        var load = Task.Run(() => new ItemCacheReader(new ItemCacheOptions(fixture.Directory)).Load(CancellationToken.None));

        await Task.Delay(10);
        File.Replace(replacementManifest, fixture.ManifestPath, null);
        var snapshot = await load;

        var firstId = snapshot.Items[0].Id;
        Assert.True(firstId is 1 or 1_000_001);
        Assert.All(snapshot.Items, item => Assert.InRange(item.Id, firstId, firstId + rowCount - 1));
    }

    [Fact]
    public async Task Load_fails_when_the_manifest_remains_unstable_across_its_bounded_retry_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int rowCount = 300_000;
        using var fixture = CacheFixture.Create(BasicCsv);
        fixture.WriteGeneration(GeneratedCsv(1, rowCount), rowCount);
        var firstManifest = File.ReadAllText(fixture.PrepareGeneration(GeneratedCsv(1, rowCount), rowCount, "a"));
        var secondManifest = File.ReadAllText(fixture.PrepareGeneration(GeneratedCsv(1_000_001, rowCount), rowCount, "longer-schema-version"));
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            var manifest = firstManifest;
            while (!stop.IsCancellationRequested)
            {
                fixture.ReplaceManifest(manifest);
                manifest = manifest == firstManifest ? secondManifest : firstManifest;
            }
        });

        try
        {
            var error = await Record.ExceptionAsync(() => Task.Run(() => new ItemCacheReader(new ItemCacheOptions(fixture.Directory)).Load(CancellationToken.None)));
            Assert.IsAssignableFrom<ItemCacheException>(error);
            Assert.Equal("The item cache is unavailable.", error.Message);
        }
        finally
        {
            stop.Cancel();
            await writer;
        }
    }

    private const string BasicCsv = "id,name,type,rarity,level\r\n1,Alpha,Weapon,Rare,80\r\n";

    private static string GeneratedCsv(long firstId, int rowCount)
    {
        var csv = new StringBuilder("id,name,type,rarity,level\r\n");
        for (var index = 0; index < rowCount; index++)
        {
            csv.Append(firstId + index).Append(",Item,Weapon,Rare,80\r\n");
        }

        return csv.ToString();
    }

    private sealed class CacheFixture : IDisposable
    {
        private CacheFixture(string directory, string manifestPath, string csvPath, DateTime generatedAtUtc, string hash)
        {
            Directory = directory;
            ManifestPath = manifestPath;
            CsvPath = csvPath;
            GeneratedAtUtc = generatedAtUtc;
            Hash = hash;
        }

        public string Directory { get; }
        public string ManifestPath { get; }
        public string CsvPath { get; private set; }
        public DateTime GeneratedAtUtc { get; }
        public string Hash { get; private set; }

        public static CacheFixture Create(string csv)
        {
            var directory = Path.Combine(Path.GetTempPath(), "GW2AccountMCP", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var bytes = new UTF8Encoding(false, true).GetBytes(csv);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var csvPath = Path.Combine(directory, $"items.{hash}.csv");
            File.WriteAllBytes(csvPath, bytes);
            var generatedAtUtc = new DateTime(2026, 8, 14, 12, 34, 56, DateTimeKind.Utc).AddTicks(7);
            var manifestPath = Path.Combine(directory, "items.manifest.json");
            File.WriteAllText(manifestPath, Manifest(generatedAtUtc, 2, hash), new UTF8Encoding(false));
            return new CacheFixture(directory, manifestPath, csvPath, generatedAtUtc, hash);
        }

        public string ManifestJson(string replacement, string? replace = null)
        {
            var json = Manifest(GeneratedAtUtc, 1, Hash);
            return replace is null ? json[..^1] + "," + replacement + "}" : json.Replace(replace, replacement, StringComparison.Ordinal);
        }

        public void WriteManifest(string json) => File.WriteAllText(ManifestPath, json, new UTF8Encoding(false));

        public void WriteGeneration(byte[] csv, int rowCount)
        {
            var hash = Convert.ToHexString(SHA256.HashData(csv)).ToLowerInvariant();
            var csvPath = Path.Combine(Directory, $"items.{hash}.csv");
            File.WriteAllBytes(csvPath, csv);
            File.WriteAllText(ManifestPath, Manifest(GeneratedAtUtc, rowCount, hash), new UTF8Encoding(false));
            CsvPath = csvPath;
            Hash = hash;
        }

        public void WriteGeneration(string csv, int rowCount) => WriteGeneration(new UTF8Encoding(false).GetBytes(csv), rowCount);

        public string PrepareGeneration(string csv, int rowCount, string schemaVersion = "2026-08-14")
        {
            var bytes = new UTF8Encoding(false).GetBytes(csv);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(Directory, $"items.{hash}.csv"), bytes);
            var replacementManifest = Path.Combine(Directory, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(replacementManifest, Manifest(GeneratedAtUtc, rowCount, hash, schemaVersion), new UTF8Encoding(false));
            return replacementManifest;
        }

        public void ReplaceManifest(string manifest)
        {
            var replacementManifest = Path.Combine(Directory, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(replacementManifest, manifest, new UTF8Encoding(false));
            File.Replace(replacementManifest, ManifestPath, null);
        }

        private static string Manifest(DateTime generatedAtUtc, int rowCount, string hash, string schemaVersion = "2026-08-14") => $$"""{"formatVersion":1,"generatedAtUtc":"{{generatedAtUtc:O}}","gw2SchemaVersion":"{{schemaVersion}}","language":"en","rowCount":{{rowCount}},"csvFileName":"items.{{hash}}.csv","csvSha256":"{{hash}}"}""";

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, true);
            }
        }
    }
}
