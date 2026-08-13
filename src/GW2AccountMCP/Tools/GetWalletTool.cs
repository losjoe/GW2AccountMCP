using System.ComponentModel;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetWalletTool(IGw2ApiClient gw2ApiClient, TimeProvider timeProvider)
{
    [McpServerTool(
        Name = "get_wallet",
        Title = "Get wallet",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Gets Guild Wars 2 wallet balances. Requires a locally configured GW2 API key with account and wallet permissions.")]
    public async Task<WalletResult> GetWalletAsync(CancellationToken cancellationToken)
    {
        try
        {
            var wallet = await gw2ApiClient.GetWalletAsync(cancellationToken);
            return new WalletResult(
                wallet.Balances.Select(balance => new WalletBalanceResult(balance.Id, balance.Name, balance.Value)).ToArray(),
                wallet.Warnings.Select(warning => new WalletWarningResult(warning.Code, warning.CurrencyId)).ToArray(),
                timeProvider.GetUtcNow());
        }
        catch (Gw2ConfigurationException exception)
        {
            throw new McpException(exception.Message, exception);
        }
    }
}

public sealed record WalletResult(IReadOnlyList<WalletBalanceResult> Balances, IReadOnlyList<WalletWarningResult> Warnings, DateTimeOffset AsOf);
public sealed record WalletBalanceResult(int Id, string? Name, long Value);
public sealed record WalletWarningResult(string Code, int CurrencyId);
