using GW2AccountMCP.Gw2;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class CharacterInventoryLimitsTests
{
    [Fact]
    public void Default_is_the_single_source_for_default_inventory_limits()
    {
        var defaults = CharacterInventoryLimits.Default;
        var options = new Gw2ApiOptions("key", "https://example.test");

        Assert.Equal((20, 40, 640, 1024, 2048), (defaults.MaxBagPositions, defaults.MaxSlotsPerBag, defaults.MaxTotalSlots, defaults.MaxItemReferences, defaults.MaxStatAttributes));
        Assert.Same(defaults, options.Limits);
    }

    [Fact]
    public void Missing_and_blank_values_fall_back_to_defaults_and_each_override_uses_invariant_integer_parsing()
    {
        var defaults = CharacterInventoryLimits.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection().Build());
        var configured = CharacterInventoryLimits.FromConfiguration(Configuration(
            ("GW2_CHARACTER_INVENTORY_MAX_BAG_POSITIONS", "21"),
            ("GW2_CHARACTER_INVENTORY_MAX_SLOTS_PER_BAG", "41"),
            ("GW2_CHARACTER_INVENTORY_MAX_TOTAL_SLOTS", "641"),
            ("GW2_CHARACTER_INVENTORY_MAX_ITEM_REFERENCES", "1024"),
            ("GW2_CHARACTER_INVENTORY_MAX_STAT_ATTRIBUTES", "2049")));
        var blank = CharacterInventoryLimits.FromConfiguration(Configuration(("GW2_CHARACTER_INVENTORY_MAX_BAG_POSITIONS", " \t")));

        Assert.Equal(CharacterInventoryLimits.Default, defaults);
        Assert.Equal((21, 41, 641, 1024, 2049), (configured.MaxBagPositions, configured.MaxSlotsPerBag, configured.MaxTotalSlots, configured.MaxItemReferences, configured.MaxStatAttributes));
        Assert.Equal(CharacterInventoryLimits.Default, blank);
    }

    [Theory]
    [InlineData("GW2_CHARACTER_INVENTORY_MAX_BAG_POSITIONS", "zero")]
    [InlineData("GW2_CHARACTER_INVENTORY_MAX_SLOTS_PER_BAG", "0")]
    [InlineData("GW2_CHARACTER_INVENTORY_MAX_TOTAL_SLOTS", "-1")]
    [InlineData("GW2_CHARACTER_INVENTORY_MAX_ITEM_REFERENCES", "2147483648")]
    [InlineData("GW2_CHARACTER_INVENTORY_MAX_STAT_ATTRIBUTES", "1.5")]
    public void Invalid_setting_values_are_redacted_and_name_the_invalid_setting(string setting, string value)
    {
        var error = Assert.Throws<Gw2ConfigurationException>(() => CharacterInventoryLimits.FromConfiguration(Configuration((setting, value))));

        Assert.Contains(setting, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InconsistentSettings))]
    public void Inconsistent_settings_are_rejected(string setting, string value)
    {
        var error = Assert.Throws<Gw2ConfigurationException>(() => CharacterInventoryLimits.FromConfiguration(Configuration((setting, value))));

        Assert.Contains(setting, error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> InconsistentSettings => new()
    {
        { "GW2_CHARACTER_INVENTORY_MAX_TOTAL_SLOTS", "39" },
        { "GW2_CHARACTER_INVENTORY_MAX_TOTAL_SLOTS", "801" },
        { "GW2_CHARACTER_INVENTORY_MAX_ITEM_REFERENCES", "659" }
    };

    [Fact]
    public void Program_registers_the_non_default_limits_value()
    {
        using var factory = new LimitsApplicationFactory("GW2_CHARACTER_INVENTORY_MAX_BAG_POSITIONS", "21");

        var options = factory.Services.GetRequiredService<Gw2ApiOptions>();

        Assert.Equal(21, options.Limits.MaxBagPositions);
        Assert.Equal(CharacterInventoryLimits.Default.MaxSlotsPerBag, options.Limits.MaxSlotsPerBag);
    }

    [Fact]
    public void Invalid_program_configuration_fails_before_the_application_starts()
    {
        using var factory = new LimitsApplicationFactory("GW2_CHARACTER_INVENTORY_MAX_TOTAL_SLOTS", "39");

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("GW2_CHARACTER_INVENTORY_MAX_TOTAL_SLOTS", error.ToString(), StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(setting => setting.Key, setting => (string?)setting.Value)).Build();

    private sealed class LimitsApplicationFactory(string setting, string value) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("GW2_API_BUDGET_LOCK_PATH", Path.Combine(Path.GetTempPath(), "GW2AccountMCP.Tests", Guid.NewGuid().ToString("N"), "budget.lock"));
            builder.UseSetting(setting, value);
        }
    }
}
