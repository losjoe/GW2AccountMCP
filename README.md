# GW2AccountMCP

Local, read-only Guild Wars 2 account facts over stateless Streamable HTTP MCP.

## Phase 0 scope

The only tool is `get_account`. It validates the configured key through `/v2/tokeninfo`, requires the `account` permission, then calls `/v2/account`. It returns basic account facts and an `asOf` timestamp; it never returns token metadata or the key.

The accepted v1 key scopes are `account`, `wallet`, `inventories`, `characters`, `builds`, `progression`, `unlocks`, and `tradingpost`. Phase 0 uses only the `account` scope and account endpoints.

## Configure and run

Copy no secret into a repository file. Set `GW2_API_KEY` with either development user-secrets (replace the placeholder locally):

```powershell
dotnet user-secrets set "GW2_API_KEY" "<your-GW2-key>" --project src/GW2AccountMCP
```

or an environment variable for the current PowerShell session:

```powershell
$env:GW2_API_KEY = "<your-GW2-key>"
```

`appsettings.example.json` has only non-secret configuration. `GW2_API_BASE_URL` defaults to `https://api.guildwars2.com` and may point to a local fake server in tests.

From the repository root:

```powershell
dotnet restore
dotnet run --project src/GW2AccountMCP
```

The documented default listener is `http://127.0.0.1:5288`; the stateless MCP endpoint is `http://127.0.0.1:5288/mcp`.

After the `gw2-account` tunnel profile is configured with a persistent file-backed runtime key, launch the server and tunnel together from the repository root:

```powershell
.\start.ps1
```

The script builds the project, starts both processes, waits for MCP and tunnel readiness, opens the tunnel UI, and stops both owned process trees when you press Ctrl+C. It does not read or store either API key.

If either default port is occupied, choose alternate loopback ports for that launch:

```powershell
.\start.ps1 -McpPort 6288 -TunnelHealthPort 9080
```

`-McpPort` controls the MCP server and tunnel target. `-TunnelHealthPort` controls the tunnel health endpoint and admin UI. The ports must differ.

Persistent runtime-key setup is documented in `D:\Obsidian\Code\GW2AccountMCP\GW2 Account MCP - Persistent Tunnel Key Setup.md`. The runtime key remains outside the repository; do not put it in an environment variable for routine launches.

## Test

```powershell
dotnet build --no-restore
dotnet test --no-build
```

Tests use in-process fake HTTP responses and do not call Guild Wars 2.

## Local MCP Inspector

With the server running, use the current official Inspector CLI flow to list tools:

```powershell
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/list
```

Then invoke `get_account` through Inspector. Invocation needs a locally configured valid GW2 key; do not paste it into Inspector arguments or chat.

```powershell
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_account --format json
```

## Secure MCP Tunnel and ChatGPT smoke test

Create and associate the tunnel in the OpenAI Platform/ChatGPT UI. Do not create tunnel resources until local checks pass. Download `tunnel-client` from the current official latest release; do not pin a stale client version. Start with `tunnel-client help quickstart`, then initialize a named profile using the tunnel ID shown by Platform:

```powershell
tunnel-client init --sample sample_mcp_remote_no_auth --profile gw2-account --tunnel-id <tunnel-id> --mcp-server-url http://127.0.0.1:5288/mcp
```

Configure the profile's reusable runtime key using the persistent-key guide, then run `.\start.ps1`. In ChatGPT web Developer Mode, create a read-only draft app using the tunnel connection, verify that only `get_account` is discovered, and invoke it. Keep the launcher running while using the app.
