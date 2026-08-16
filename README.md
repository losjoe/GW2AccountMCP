# GW2AccountMCP

Local, read-only Guild Wars 2 account facts over stateless Streamable HTTP MCP.

## Current scope

The server exposes seventeen tools:

- `find_items` resolves a bounded English item-name fragment from the generated local public cache. It returns exact matches before contains matches with canonical ID, name, type, rarity, level, and match kind. It does not call the GW2 API at request time or choose among ambiguous names.
- `get_items` accepts 1-100 distinct positive canonical item IDs and makes one unauthenticated public `/v2/items` request at runtime. It returns caller-ordered item facts—nullable name, type, rarity, level, vendor value, flags, game types, and restrictions—with explicit unavailable-resource rows and nullable source facts when a requested public item resource is absent. Optional `includeMaterialCategories: true` adds one unauthenticated `/v2/materials?ids=all` request and compact per-item public category memberships sorted by category order then ID. `NotRequested` and `Unavailable` use explicit null category data; `Available` uses non-null lists, where `[]` means the complete material response reported no membership. Material failure preserves item facts and item completeness. Item `asOf` and nullable `materialCategoriesAsOf` are independent response-completion observations, and `isAtomicSnapshot: false` discloses that the sources are not one atomic snapshot. The tool does not use account data, caches, recipes, or Trading Post prices.
- `get_recipes` returns bounded public player-discovered crafting recipe facts in `ByIds`, `InputItem`, or `OutputItem` mode. `ByIds` accepts 1-100 distinct positive recipe IDs in caller order. Selector modes accept one positive item ID, sort the complete bounded selector result, and apply local `offset`/`limit` paging before one definition lookup. Optional `includeAccountUnlocks: true` requires only `account` and `unlocks`, reads one complete bounded `/v2/account/recipes` list, and adds a boolean `accountUnlockListContainsRecipe` to every requested or selected ID, including missing public definitions; omitted, null, or false remains key-independent and emits explicit null membership and `accountUnlocksAsOf`. `true` means only list presence, while `false` means only absence from that accepted complete list and never inability to craft. Public selector/definition times and nullable account completion time remain separate, top-level `asOf` is the final required completion, and `isAtomicSnapshot: false` discloses the non-atomic join. Results preserve missing definitions, Item versus GuildUpgrade outputs, raw source output IDs, and source-order typed ingredients. Input search indexes Item ingredients only. Output search follows raw source `output_item_id`; that value can be bogus for guild recipes and must not be treated as their semantic Item output. The tool excludes Mystic Forge, vendors, item enrichment, caches, Trading Post data, prices, costs, and recommendations.
- `get_item_prices` accepts 1-100 distinct positive canonical item IDs and returns caller-ordered factual Trading Post price summaries from the immutable local `prices.manifest.json` generation. It reports nullable English names from the independently published item cache, quote-side aggregate quantities and best unit prices in copper, explicit zero-side and absent-resource semantics, source/cache timestamps, and manual-cache freshness disclosure. It makes no runtime network request, does not return depth or executable volume, and does not recommend an action. Prices may have changed; run `tp --fresh` to collect a newer source snapshot.
- `value_items` accepts 1-100 caller-ordered distinct positive canonical item IDs with quantities from 1 through `Int32.MaxValue`. From exactly one local price snapshot, it returns nullable best-price factual arithmetic for each row: highest-buy immediate-sale gross, independent 5% listing and 10% exchange fees, and net; lowest-sell buyer cost; and lowest-sell hypothetical-listing gross, fees, and net. Each positive-gross fee is independently rounded half up on that row's aggregate gross and is at least one copper. The three aggregate views return numeric totals only when every requested row has its required quote; otherwise they are explicitly incomplete with null totals and caller-order missing IDs. Best-price values are quote extrapolations without best-level volume, depth, liquidity, or execution guarantees. It does not calculate recipe costs and makes no buy, sell, craft, keep, profit, flip, trend, forecast, velocity, history, or other recommendation.
- `get_account` validates the configured key through `/v2/tokeninfo`, requires `account`, and returns basic account facts with an `asOf` timestamp.
- `get_achievement_progress` accepts 1-20 caller-ordered distinct positive achievement IDs, requires `account` and `progression` for the complete account progress list, then makes one separate optional public English `/v2/achievements` explicit-ID lookup. It returns requested rows only, explicit account-absence and public-definition states/nulls, completed account bit indexes with compact resolved public bit details where available, independent source completion times, bounded warnings, and `isAtomicSnapshot: false`. Public definition failure retains account facts as `PublicDefinitionsUnavailable`; it does not scan the achievement catalog or return categories, groups, tiers, rewards, or recommendations.
- `get_mastery_progress` takes no arguments, requires `account` and `progression`, and sequentially reads complete bounded account mastery tracks and mastery-point totals. When account tracks are nonempty, it makes one optional public English `/v2/masteries` lookup for those ascending explicit IDs. It returns canonical track and ordinal-region point-total order, explicit metadata states/nulls, separate source completion observations, bounded warnings, and `isAtomicSnapshot: false`. `sourceLevel` uses this tool's product-owned zero-based highest-trained-level interpretation; omitted account rows or levels do not infer unstarted state. Public metadata failure preserves authenticated facts. It excludes raw unlocked point IDs, public catalog rows, point labels, full level catalogs, region mappings, recommendations, and planning.
- `get_character_build` accepts one exact character name from `get_characters`, requires `account`, `characters`, and `builds`, verifies that name against the complete roster, and returns only that character's active build tab. It preserves fixed specialization, trait, terrestrial/aquatic skill, Ranger pet, and Revenant legend slots; resolves referenced public metadata to compact names where available; and retains unresolved IDs with deterministic warnings and `isMetadataComplete: false`. It does not return inventory, equipment, inactive tabs, or ownership quantities.
- `get_character_equipment` accepts one exact character name from `get_characters`, requires `account`, `characters`, `builds`, and `inventories`, and returns that character's active PvE/WvW combat-equipment references. It uses the active equipment tab plus a conditional current-equipment lookup for the API's missing Relic, resolves compact item, prefix, upgrade, infusion, and skin metadata, preserves unresolved canonical IDs with warnings, and explicitly marks the result as non-ownership data. It includes terrestrial and aquatic combat slots but excludes PvP, dyes, gathering, fishing, Jade Bot equipment, inventory, inactive tabs, quantities, and Legendary Armory ownership.
- `get_character_equipment_tabs` accepts one exact character name from `get_characters`, requires `account`, `characters`, `builds`, and `inventories`, and returns every reported PvE/WvW combat equipment tab with an explicit active marker. It reports equipment references rather than ownership, preserves unresolved compact metadata as explicit nulls with warnings, and does not alter the active-equipment, holdings, inventory, or Legendary Armory surfaces.
- `get_character_inventory` accepts one exact character name from `get_characters`, requires `account`, `characters`, and `inventories`, and returns that character's complete bounded physical equipped-bag layout. It preserves zero-based bag and slot positions, absent bags, empty slots, per-slot stack counts and charges, binding, selected/default stats, upgrades, infusions, and skins, with compact public names where available and deterministic metadata warnings otherwise. Its physical stack counts are already represented by `get_account_holdings` character-bag contributions and must not be added as a second ownership source; it returns no per-item totals and excludes account storage, equipment references, inactive tabs, and Legendary Armory ownership.
- `get_characters` requires `account` and `characters`, retrieves the complete character list and each character's core record, and returns name-ordered summaries with name, race, gender, profession, level, playtime seconds, creation and last-modified timestamps, and deaths. The result is complete or the whole operation fails; it does not include inventory, equipment, builds, guild, or title data.
- `get_legendary_armory` takes no arguments, requires `account`, `inventories`, and `unlocks`, and returns the complete bounded set of entries reported by `/v2/account/legendaryarmory`, ordered by item ID. `armoryCount` means the count available for use in one equipment template; it is reusable account Legendary Armory ownership, not a physical stack, `onHand` quantity, equipped occurrence, or extra physical copy, and must not be added to `get_account_holdings`. Compact English item names, types, subtypes, and armor weight classes come from `/v2/items`; unresolved public metadata leaves the authenticated ID/count intact with explicit nulls and deterministic warnings. The tool does not return unowned catalog rows or public Armory capacity limits.
- `get_wallet` requires `account` and `wallet`, retrieves `/v2/account/wallet`, and joins each canonical currency ID to its English `/v2/currencies` name. It returns `long` values, one `asOf` timestamp, and warnings that retain the ID and value when metadata is unavailable. An empty wallet is returned as an empty balance list.
- `get_account_holdings` accepts separate optional `itemIds` and `currencyIds` arrays, requires at least one ID, and permits at most 20 combined positive IDs with no duplicates within either array. It preserves caller order and treats item and currency IDs as separate canonical namespaces, so the same numeric ID may appear once in each array.
- `get_trading_post_activity` supports `CurrentBuys` and `CurrentSells` and requires `account` and `tradingpost`. It returns one complete caller-selected page with canonical item ID, unit price in copper, unfulfilled quantity, and creation time. `CurrentBuys` adds checked reserved coin per row and a selected-page subtotal; those quantities are pending orders, not owned items. `CurrentSells` quantities are currently unfulfilled listed assets already represented by `get_account_holdings` as `listedForSale`; they are not on-hand and must not be added as another ownership source. `page` defaults to 0, `pageSize` defaults to 50, and page size is limited to 1-200. Results preserve source order but do not claim chronological or global ordering or cross-call pagination stability; transaction data is documented as cached for about five minutes, and `asOf` is response completion rather than a source timestamp. Upstream transaction IDs are not returned. Model-facing Delivery and previous-90-day history modes are deferred pending demonstrated need; `get_account_holdings` continues to include TP-delivery item quantities in ownership results.

`get_account_holdings` queries only the sources relevant to its inputs. Currency requests require `account` and `wallet`. Item requests use bank, material storage, and shared inventory (`account`, `inventories`); every character bag (`account`, `characters`, `inventories`); and Trading Post delivery and current sells (`account`, `tradingpost`). English item names are requested only for the supplied item IDs from the public `/v2/items` endpoint.

Holdings results distinguish `onHand`, `inTradingPostDelivery`, `listedForSale`, and `ownedTotal`. A successfully exhausted source contributes an authoritative value, including zero. A failed source or absent wallet balance produces a nullable quantity, explicit `unavailableLocations` or warning evidence, and `isComplete: false`; a known partial subtotal is never presented as a complete total. Item metadata failure leaves canonical IDs and known quantities intact with a null name and warning.

No tool returns token metadata or the key. The accepted v1 key scopes remain `account`, `wallet`, `inventories`, `characters`, `builds`, `progression`, `unlocks`, and `tradingpost`; each enabled tool validates only its required scopes.

## Configure and run

Copy no secret into a repository file. Set `GW2_API_KEY` with either development user-secrets (replace the placeholder locally):

```powershell
dotnet user-secrets set "GW2_API_KEY" "<your-GW2-key>" --project src/GW2AccountMCP
```

or an environment variable for the current PowerShell session:

```powershell
$env:GW2_API_KEY = "<your-GW2-key>"
```

`appsettings.example.json` has only non-secret configuration. `GW2_API_BASE_URL` defaults to `https://api.guildwars2.com` and may point to a local fake server in tests. `GW2_PUBLIC_CACHE_PATH` defaults to `data/public-cache`, and `GW2_API_BUDGET_LOCK_PATH` defaults to `data/gw2-api-budget.lock`; both paths are resolved from the repository-root working directory used by the commands below.

Selected-character inventory has these server-owned safety limits:

| Setting | Default |
|---|---:|
| `GW2_CHARACTER_INVENTORY_MAX_BAG_POSITIONS` | 20 |
| `GW2_CHARACTER_INVENTORY_MAX_SLOTS_PER_BAG` | 40 |
| `GW2_CHARACTER_INVENTORY_MAX_TOTAL_SLOTS` | 640 |
| `GW2_CHARACTER_INVENTORY_MAX_ITEM_REFERENCES` | 1024 |
| `GW2_CHARACTER_INVENTORY_MAX_STAT_ATTRIBUTES` | 2048 |

The defaults cover the current documented maximum of 16 bags with 32 slots each and leave modest headroom. Raise them only after verifying an ArenaNet capacity change. Values must be positive 32-bit integers; total slots must be at least slots per bag and no greater than bag positions multiplied by slots per bag, and item references must be at least bag positions plus total slots. Changes take effect after a server restart. Higher values can materially increase response size, metadata request count, latency, and model-context use. No hard upper ceiling is imposed because these are trusted server-owner settings; per-stack structural limits remain fixed.

## Refresh the public item cache

Stop MCP and close or pause Excel refreshes before running either refresh command. MCP and the updater each cap all GW2 API request attempts, including retries, at four starts per second (240/minute) and use the same exclusive lock file. A lease error means another MCP/updater process is active. The lock file persists after release; its existence is normal.

Start or resume a production snapshot from the repository root:

```powershell
dotnet run --project tools/GW2AccountMCP.DataRefresh -- items --output data/public-cache
```

The full public refresh makes hundreds of bounded requests and can take multiple minutes. Validated 200-item batches are committed under the updater-owned `.items-staging` directory as they complete. If an ordinary network or batch failure occurs, rerun the same command without `--fresh`; it verifies the current sorted root-ID count and hash, reuses matching staged batches, and downloads only what remains. The incomplete result reports aggregate staged progress and safe timeout, transport, HTTP, and invalid-response counts without item IDs or response contents.

The updater validates every source ID but excludes blank or whitespace-only names from the searchable CSV. It publishes one immutable `items.<sha256>.csv` generation and atomically updates `items.manifest.json` only after every batch is validated; manifest `rowCount` is the number of published named rows. A failed or incomplete refresh leaves the prior referenced generation intact. MCP never refreshes this cache automatically.

After publication, the validated shards and exact publishing state remain in `.items-staging` as a bounded repair source. Running the plain command again validates or reconstructs the exact generation and manifest without GW2 API calls. Use `--fresh` to discard that retained snapshot and deliberately download a new one:

```powershell
dotnet run --project tools/GW2AccountMCP.DataRefresh -- items --output data/public-cache --fresh
```

Use `--fresh` after a known Guild Wars 2 patch, a deliberate catalog update, or incompatible staged data even when the root item count and ID hash appear unchanged, because definitions for existing IDs can change without changing that root list. `--fresh` removes only the retained staging after a complete ownership preflight; it never removes the currently published manifest or generation before the replacement is ready. It refuses to delete anything if `.items-staging` contains an unrecognized entry; resolve that entry before retrying.

Use the isolated one-page command for a fast updater-to-reader test:

```powershell
dotnet run --project tools/GW2AccountMCP.DataRefresh -- items-test --output data/public-cache-test
$env:GW2_PUBLIC_CACHE_PATH = "data/public-cache-test"
```

`items-test` validates the complete root catalog but downloads only the first 200 sorted item definitions in memory, excludes blank names, and publishes no resumable staging. Its normalized output directory name must end in `-test`; it cannot target the production directory. The standard manifest/CSV pair supports a real MCP round trip, but it is deliberately incomplete and must not be used as the production cache. Clear the temporary environment override before a production launch:

```powershell
Remove-Item Env:GW2_PUBLIC_CACHE_PATH
```

Production and test cache directories, retained staging, lock files, and the local workbook are ignored by Git.

The index loads lazily on the first `find_items` call. It checks for a changed cache only after a no-match, then reloads once and reruns that search. `find_items`, `get_item_prices`, and `value_items` use only local published caches at runtime; `get_items` is the separate public request-time catalog lookup.

### Refresh the public price cache

Publish or resume the independent production price cache from the repository root:

```powershell
dotnet run --project tools/GW2AccountMCP.DataRefresh -- tp --output data/public-cache
```

The command publishes an immutable `prices.<sha256>.csv` and manifest-last `prices.manifest.json`. A normal rerun resumes retained `.prices-staging` work or repairs a completed publication without re-fetching it. Use `--fresh` only to discard recognized price staging and collect a newer source snapshot; it never deletes the published generation:

```powershell
dotnet run --project tools/GW2AccountMCP.DataRefresh -- tp --output data/public-cache --fresh
```

For an isolated structural test cache only, use an output directory ending in `-test`:

```powershell
dotnet run --project tools/GW2AccountMCP.DataRefresh -- tp-test --output data/public-cache-test
```

`tp-test` deliberately publishes only the first bounded page and is not a production price cache.

### Excel `GetItemNames` query

Replace the existing Excel Power Query named `GetItemNames` with the following query. Adjust `CacheDirectory` only if the production cache is elsewhere. This reads the manifest's committed generation and preserves the downstream `Id` and `Name` columns.

```powerquery
let
    CacheDirectory = "D:\Code\GW2AccountMCP\data\public-cache",
    Manifest = Json.Document(File.Contents(CacheDirectory & "\items.manifest.json")),
    CsvFileName = Text.From(Record.Field(Manifest, "csvFileName")),
    Source = Csv.Document(
        File.Contents(CacheDirectory & "\" & CsvFileName),
        [Delimiter = ",", Columns = 5, Encoding = 65001, QuoteStyle = QuoteStyle.Csv]
    ),
    PromotedHeaders = Table.PromoteHeaders(Source, [PromoteAllScalars = true]),
    TypedRows = Table.TransformColumnTypes(
        PromotedHeaders,
        {{"id", Int64.Type}, {"name", type text}, {"type", type text}, {"rarity", type text}, {"level", Int64.Type}}
    ),
    SelectedColumns = Table.SelectColumns(TypedRows, {"id", "name"}),
    RenamedColumns = Table.RenameColumns(SelectedColumns, {{"id", "Id"}, {"name", "Name"}})
in
    RenamedColumns
```

### Excel `GetListings` query

Replace the existing Excel Power Query named `GetListings` with the following query. Adjust `CacheDirectory` only if the production cache is elsewhere. This reads the price manifest's committed generation and preserves the downstream `id`, `Highest Buy Price`, `Demand`, `Lowest Sell Price`, and `Supply` columns. `GetListings` no longer invokes the paged `Listings` function or `NumListings`.

```powerquery
let
    CacheDirectory = "D:\Code\GW2AccountMCP\data\public-cache",
    Manifest = Json.Document(File.Contents(CacheDirectory & "\prices.manifest.json")),
    CsvFileName = Text.From(Record.Field(Manifest, "csvFileName")),
    Source = Csv.Document(
        File.Contents(CacheDirectory & "\" & CsvFileName),
        [Delimiter = ",", Columns = 6, Encoding = 65001, QuoteStyle = QuoteStyle.Csv]
    ),
    PromotedHeaders = Table.PromoteHeaders(Source, [PromoteAllScalars = true]),
    SelectedColumns = Table.SelectColumns(
        PromotedHeaders,
        {"id", "buyUnitPrice", "buyQuantity", "sellUnitPrice", "sellQuantity"}
    ),
    TypedRows = Table.TransformColumnTypes(
        SelectedColumns,
        {
            {"id", Int64.Type},
            {"buyUnitPrice", Int64.Type},
            {"buyQuantity", Int64.Type},
            {"sellUnitPrice", Int64.Type},
            {"sellQuantity", Int64.Type}
        }
    ),
    RenamedColumns = Table.RenameColumns(
        TypedRows,
        {
            {"buyUnitPrice", "Highest Buy Price"},
            {"buyQuantity", "Demand"},
            {"sellUnitPrice", "Lowest Sell Price"},
            {"sellQuantity", "Supply"}
        }
    )
in
    RenamedColumns
```

After a successful production cache refresh, use Excel Refresh All and save the workbook. The cache updater and Excel read the generation selected by the manifest; the updater does not automate or modify the workbook. Any remaining direct GW2 API queries in the workbook must not overlap MCP, the updater, or another bulk GW2 client.

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

Then invoke a tool through Inspector. Account-backed tools need a locally configured valid GW2 key with the required scopes; do not paste it into Inspector arguments or chat. `find_items`, `get_item_prices`, and `value_items` use only local public caches; `get_items` and default `get_recipes` calls are public and request-time, while `get_recipes` with `includeAccountUnlocks: true` additionally requires `account` and `unlocks`. `get_achievement_progress` requires `account` and `progression` and accepts canonical achievement IDs. `get_mastery_progress` takes no arguments and requires `account` and `progression`. `get_trading_post_activity` accepts `CurrentBuys` or `CurrentSells` and requires `account` and `tradingpost`. Holdings still accepts canonical IDs, so resolve names first when needed.

```powershell
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name find_items --tool-args-json '{"query":"Mystic Coin"}' --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_items --tool-args-json '{"itemIds":[19976]}' --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_recipes --tool-args-json '{"mode":"InputItem","itemId":19976}' --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_item_prices --tool-args-json '{"itemIds":[19976]}' --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name value_items --tool-args-json '{"items":[{"itemId":19976,"quantity":3}]}' --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_account --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_achievement_progress --tool-args-json '{"achievementIds":[1,2]}' --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_mastery_progress --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_character_build --tool-args-json '{"characterName":"<exact name returned by get_characters>"}' --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_character_equipment --tool-args-json '{"characterName":"<exact name returned by get_characters>"}' --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_character_equipment_tabs --tool-args-json '{"characterName":"<exact name returned by get_characters>"}' --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_character_inventory --tool-args-json '{"characterName":"<exact name returned by get_characters>"}' --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_characters --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_legendary_armory --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_wallet --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_account_holdings --tool-args-json '{"itemIds":[101,202],"currencyIds":[3]}' --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_trading_post_activity --tool-args-json '{"mode":"CurrentBuys","page":0,"pageSize":50}' --format json
npx @modelcontextprotocol/inspector --cli http://127.0.0.1:5288/mcp --transport http --method tools/call --tool-name get_trading_post_activity --tool-args-json '{"mode":"CurrentSells","page":0,"pageSize":50}' --format json
```

## Secure MCP Tunnel and ChatGPT smoke test

The holdings IDs above are placeholders only. Confirm the current Inspector argument syntax with `npx @modelcontextprotocol/inspector --help` if the installed CLI changes.

Create and associate the tunnel in the OpenAI Platform/ChatGPT UI. Do not create tunnel resources until local checks pass. Download `tunnel-client` from the current official latest release; do not pin a stale client version. Start with `tunnel-client help quickstart`, then initialize a named profile using the tunnel ID shown by Platform:

```powershell
tunnel-client init --sample sample_mcp_remote_no_auth --profile gw2-account --tunnel-id <tunnel-id> --mcp-server-url http://127.0.0.1:5288/mcp
```

Configure the profile's reusable runtime key using the persistent-key guide, then run `.\start.ps1`. In ChatGPT web Developer Mode, create a read-only draft app using the tunnel connection, verify that exactly sixteen tools are discovered: `find_items`, `get_items`, `get_recipes`, `get_item_prices`, `value_items`, `get_account`, `get_account_holdings`, `get_achievement_progress`, `get_mastery_progress`, `get_trading_post_activity`, `get_character_build`, `get_character_equipment`, `get_character_inventory`, `get_characters`, `get_legendary_armory`, and `get_wallet`. Keep the launcher running while using the app.
