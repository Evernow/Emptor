# Emptor

A Dalamud plugin that buys a shopping list off the FFXIV market board. For each
entry `(item, quantity, max unit price, quality)` it buys the cheapest acceptable
listings until the requested quantity is met, the price ceiling is hit, or
listings run out. Targets vanilla (retail) FFXIV.

- **In game** — `/emptor` opens a window with an editable shopping-list table
  and a **Start** button. The status panel narrates every step.
- **From another plugin** — a Dalamud IPC API (see below).

Developed openly. Issues and PRs welcome.

## Install

Add this custom repository in **Dalamud Settings → Experimental → Custom Plugin
Repositories**:

```
https://raw.githubusercontent.com/Evernow/DalamudPlugins/main/pluginmaster.json
```

Then install **Emptor** from `/xlplugins`.

## Build from source

```
dotnet build -c Debug
```

Debug builds copy themselves to `%AppData%\XIVLauncher\devPlugins\Emptor\`; in
game, Dalamud Settings → Experimental → rescan dev plugins, then enable **Emptor**.
Needs the .NET 10 SDK and the XIVLauncher/Dalamud dev libraries at
`%AppData%\XIVLauncher\addon\Hooks\dev`.

Releases: publish a GitHub Release (tag `vX.Y.Z`). CI
([`.github/workflows/build.yml`](.github/workflows/build.yml)) builds it and
attaches `Emptor.zip` + `Emptor.json`; the
[DalamudPlugins](https://github.com/Evernow/DalamudPlugins) repo's sync job then
picks up the new version.

## How buying works

Per shopping-list item the runner:

1. Dismounts, finds a real **Market Board** object and interacts with it. If none
   is in range it will, when travel is allowed:
   - walk to one within ~60 y with **vnavmesh**, and/or
   - use **Lifestream** to travel to a board — `/li mb` (Ul'dah) by default, or a
     specific city's board when the request pins one (see `city` below),
   then interact. It never opens the board UI "cold" — if it still can't reach a
   real board the order stops (`NoBoardInZone` / `TravelFailed`). With
   `skipTravel: true` it skips all of that and only uses a board it is already
   standing at (`OpenFailed` otherwise).
2. Focuses the search box and types the item name with real keystrokes, then
   presses Enter. (Nothing else triggers the client's search.)
3. Clicks the item in the results to open its listings.
4. Reads the listings, filters by quality and price ceiling, sorts cheapest
   first.
5. Buys whole listings cheapest-first (the market board sells whole stacks),
   confirming the "Purchase for X gil?" dialog, until the quantity is met.
6. Records what it bought, the total gil, and the next-cheapest listing it did
   **not** buy.

Every step has a human-variable delay. Typing uses a per-keystroke model
calibrated to the Dhakal et al. 2018 typing study (log-normal inter-key
intervals, bigram effects, initiation latency). See `Buying/HumanTiming.cs` and
`Buying/TypingModel.cs`.

### Overshoot

If you need 20 and the cheapest listing is a stack of 50:

- **Buy anyway** (default) — takes it, ends at 50.
- **Skip big stacks** — only takes listings that fit the remaining need.
- **Limit %** — takes an overshooting stack only if it exceeds the remaining need
  by no more than the configured percentage.

## IPC API

Prefix `Emptor.`. All payloads are JSON strings. Job-based: submit, then poll.

| Gate | Signature | Purpose |
|---|---|---|
| `Emptor.ApiVersion` | `Func<int>` | currently `5` |
| `Emptor.IsBusy` | `Func<bool>` | an order is running |
| `Emptor.GetCities` | `Func<string>` | JSON array of `{key, display, route}` — valid `city` values |
| `Emptor.GetReachableWorlds` | `Func<string>` | JSON: home/current world, region, and every DC + world the character can travel to |
| `Emptor.SubmitOrder` | `Func<string,string>` | request JSON → order JSON (queued) |
| `Emptor.GetOrder` | `Func<string,string>` | orderId → order JSON (live) |
| `Emptor.CancelOrder` | `Func<string,bool>` | request stop |
| `Emptor.OrderCompleted` | `Action<string>` | fires with the orderId on a terminal state |
| `Emptor.LookupPrices` | `Func<string,string>` | Universalis price lookup — no Market Board needed (see [Prices](#prices)) |

### Request

```json
{
  "clientRequestId": "optional",
  "totalGilBudget": 5000000,
  "skipTravel": false,
  "city": "kugane",
  "world": "Gilgamesh",
  "returnToHomeWorld": false,
  "items": [{
    "itemId": 44096,
    "itemName": "Grade 8 Tincture of Strength",
    "maxUnitPrice": 90000,
    "quantity": 20,
    "quality": "either",
    "overshoot": "allow",
    "overshootLimitPercent": 25
  }]
}
```

- Give `itemId` **or** `itemName` (exact, case-insensitive). `itemId` wins.
- `quality`: `"either"` (default) | `"nq"` | `"hq"`.
- `overshoot`: `"allow"` (default) | `"skip"` | `"limit"`.
- `quantity: 0` → **discovery only**: no purchases, but the result reports the
  full ranked listing set in `availableListings` plus `nextLowest*`.
- `skipTravel` omitted / `false` (default) → Emptor gets itself to a board:
  vnavmesh walk for one nearby, else Lifestream travel. Choosing the world / data
  centre is still the caller's job.
- `skipTravel: true` → the caller has already put the character at a Market Board.
  Emptor won't pathfind or travel — it interacts with a board already in range
  and stops with `OpenFailed` otherwise.
- `city` (optional) → pin travel to one city's Market Board instead of the `/li mb`
  default (Ul'dah). Accepts the key or the display name (case-insensitive), e.g.
  `"kugane"` or `"Kugane"`. Market Board listings are world-wide identical, so this
  only chooses **where Emptor travels** — a board already in reach is used as-is,
  and it is ignored when `skipTravel` is set. Valid values:

  | `city` key | Board | How Emptor travels there |
  |---|---|---|
  | `uldah` (default) | Ul'dah — Sapphire Avenue Exchange | Lifestream `/li mb` |
  | `limsa` | Limsa Lominsa | teleport to Limsa Lominsa Lower Decks, walk |
  | `gridania` | Old Gridania | teleport to New Gridania → aethernet to Leatherworkers' Guild & Shaded Bower |
  | `ishgard` | Foundation | teleport to Foundation → aethernet to The Jeweled Crozier |
  | `kugane` | Kugane | teleport to Kugane → aethernet to Kogane Dori Markets |
  | `crystarium` | The Crystarium | teleport to The Crystarium → aethernet to Musica Universalis Markets |
  | `sharlayan` | Old Sharlayan | teleport to Old Sharlayan, walk |
  | `tuliyollal` | Tuliyollal | teleport to Tuliyollal → aethernet to Bayside Bevy Marketplace |

  `Emptor.GetCities` returns this list as JSON: `[{ "key": "kugane", "display":
  "Kugane", "route": "AethernetHop" }, …]` where `route` is `LiMarketBoard`,
  `Teleport`, or `AethernetHop`.
- `world` (optional) → travel to this world first (name or numeric id). Must be a
  world the character can reach — every data centre in the character's **home**
  region, plus **Materia** (a Materia character can only reach Materia). Anything
  else is rejected at submit time. Requires Lifestream; ignored when `skipTravel`
  is set. `Emptor.GetReachableWorlds` lists exactly what's allowed.
- `returnToHomeWorld` (optional, default `false`) → after a `world` hop, travel
  back to the home world once the order finishes.

If a teleport is cancelled (combat, movement, …) Emptor announces it in chat and
keeps retrying every 5 s for `TravelRetrySeconds` (default 30) before it gives up
with `TravelFailed`.

The three stop conditions a caller usually cares about map directly onto the
request: **max count** = `quantity`, **max total gil** = `totalGilBudget`, **max
price each** = `maxUnitPrice`. The result's `stoppedReason` says which one
triggered, and `nextLowestUnitPrice` is what the next unit would have cost.

### Order result

```json
{
  "orderId": "…",
  "state": "queued|running|completed|cancelled|rejected|failed",
  "message": "…",
  "city": "kugane",
  "world": "Gilgamesh",
  "totalGilSpent": 1750000,
  "items": [{
    "itemId": 44096,
    "itemName": "Grade 8 Tincture of Strength",
    "requestedQuantity": 20,
    "purchasedQuantity": 20,
    "totalGilSpent": 1750000,
    "purchases": [
      { "unitPrice": 85000, "quantity": 5, "hq": false, "totalGil": 425000, "retainerId": "…" }
    ],
    "nextLowestUnitPrice": 91000,
    "nextLowestQuantity": 12,
    "nextLowestHq": false,
    "listingsExhausted": false,
    "stoppedReason": "QuantityMet"
  }]
}
```

### Errors & non-success responses

**No IPC gate throws.** Every failure comes back inside the JSON string you
already parse — inspect the discriminator, don't wrap calls in try/catch for
Emptor's sake. (Dalamud itself throws `IpcNotReadyError` if Emptor isn't loaded;
call `Emptor.ApiVersion` first, or check the installed-plugins list.)
`ApiVersion`, `IsBusy` and `GetCities` always return a value.

**`SubmitOrder` rejection** — `{ "state": "rejected", "message": "…" }` with **no
`orderId`**. Test for `orderId` / `state == "rejected"`. Causes:

| `message` | Meaning |
|---|---|
| `Invalid request JSON: …` | payload didn't parse |
| `Request had no items.` | `items` missing or empty |
| `Could not resolve item '…'.` | `itemName` matched no marketable item and no valid `itemId` given |
| `Item <id> is not marketable.` | the `itemId` exists but has no market-board category |
| `maxUnitPrice and quantity must be >= 0.` | negative number in an item |
| `Unknown city '…'. Known: …` | `city` isn't a key/name from `GetCities` |
| `Unknown world '…'.` | `world` isn't a real world name/id |
| `World '…' (…) is not reachable — …` | `world` is outside the character's home region + Materia |

**Order terminal `state`** — `queued` · `running` · `completed` · `cancelled` ·
`rejected` · `failed`.

- **`completed`** — the runner reached the end of the list. This does **not** mean
  everything was bought: check each item's `purchasedQuantity` and `stoppedReason`.
  A single-item order that never reached a board is still `completed` with
  `purchasedQuantity: 0`.
- **`failed`** — the whole order aborted (mostly world-travel problems or an
  internal error); `message` says why. E.g. `This order pins a world, but
  Lifestream is not installed.` · `<World> (<DC>) isn't reachable — …` · `Lifestream
  can't currently travel to <World> …` · `Still <condition> after retrying world
  travel for <N>s.` · `Timed out travelling to the target world.` · `Internal
  error: …` (a bug — please report).
- **`cancelled`** — `CancelOrder` was called, or the plugin unloaded mid-order.
- **`rejected`** — only ever from `SubmitOrder`; an order never becomes `rejected` later.

**Per-item `stoppedReason`** — the raw enum name, **PascalCase** (unlike `state`,
which is lowercased). Set on every item once the order is terminal. Match it
case-insensitively to be safe.

| group | values | check next |
|---|---|---|
| bought everything | `QuantityMet` | — |
| reached the board, bought 0–some | `PriceExceeded`, `NoListings`, `BudgetExceeded`, `Overshoot` | `purchasedQuantity`, `listingsExhausted`, `nextLowestUnitPrice` |
| couldn't reach a board | `NoBoardInZone`, `TravelFailed`, `OpenFailed` | market unknown — retry later / elsewhere |
| purchase went wrong | `SearchFailed`, `PromptMismatch`, `Indeterminate` | usually transient — retry (for `Indeterminate`, check inventory first — a buy packet went out but wasn't confirmed) |
| precondition failed | `ItemUnresolved`, `Blocked` | bad name / stuck in a blocking condition too long |
| caller asked | `Cancelled` | — |
| `None` | item never ran (shouldn't happen on a terminal order) | |

**`GetOrder`** — unknown id → `{ "state": "rejected", "message": "No order with id
'…'." }`. Only the last ~50 finished orders are kept.

**`CancelOrder`** — `bool`: `true` if cancelled / stop requested, `false` if the id
is unknown or the order already finished.

**`GetReachableWorlds`** — `{ "error": "Not logged in." }` with no character loaded.

### Example caller

```csharp
var submit = pluginInterface.GetIpcSubscriber<string, string>("Emptor.SubmitOrder");
var get    = pluginInterface.GetIpcSubscriber<string, string>("Emptor.GetOrder");

var orderJson = submit.InvokeFunc("""
    { "items": [ { "itemName": "Fire Crystal", "maxUnitPrice": 20, "quantity": 999 } ] }
    """);
var orderId = JsonDocument.Parse(orderJson).RootElement.GetProperty("orderId").GetString();

// later / on Emptor.OrderCompleted:
var result = get.InvokeFunc(orderId);
```

## Prices

`Emptor.LookupPrices` returns [Universalis](https://universalis.app) market data
for items **without needing a Market Board** — usable anywhere. Also available
in-game under the **Prices** tab of the `/emptor` window.

```json
{
  "scope": "datacenter",
  "target": "Aether",
  "refresh": false,
  "items": [ { "itemName": "Darksteel Ore" }, { "itemId": 5111 } ]
}
```

- `scope`: `"world"` | `"datacenter"` | `"region"` | `"reachable"`. Omitted → the
  configured default (data centre). `reachable` = the player's region **+ Materia**.
- `target`: for `world` / `datacenter` / `region`, which one (name or id). Omitted
  → the player's current world / DC / region. Ignored for `reachable`.
- `refresh`: bypass the 45-minute cache.

The reply carries cached items immediately and lists the rest as `pending` while a
background fetch runs — call again in ~1 s for those.

```json
{
  "scope": "datacenter",
  "pending": [ 5111 ],
  "items": [{
    "itemId": 12537, "itemName": "…", "scope": "datacenter",
    "fetchedUnixMs": 1787900000000,
    "levels": [
      { "level": "datacenter", "location": "Aether",
        "nq": { "minListing": { "price": 480, "world": "Gilgamesh", "age": "2h ago" },
                "recentPurchase": { "price": 500, "world": "Jenova", "age": "18m ago" },
                "averageSalePrice": 512.4, "dailySaleVelocity": 37.1 },
        "hq": { … } },
      { "level": "region", "location": "North-America", "nq": { … }, "hq": { … } }
    ]
  }]
}
```

A `world` query returns world + DC + region levels; a `datacenter` query returns
DC + region; `region` and `reachable` return the region level (`reachable` merges
the cheapest across the region and Materia).

**`LookupPrices` errors.** Reply-level `error` (whole call failed, no `items`):
`Invalid JSON: …` · `No items.` · `Item '…' is not a marketable item.` · `Unknown
world '…'` / `Unknown data centre '…'` / `Unknown region '…'` (bad `target` for the
scope) · `Not logged in.` (`reachable`/omitted scope with no character). A
per-**item** `error` means just that item resolved but has no data: `Universalis
unavailable.` (every HTTP call failed — retry) or `No market data for this item at
that scope.` `LookupPrices` never throws.

## Status

Core buy loop works end to end on retail. Rough edges remain (multi-item /
quantity > 1 paths, timing calibration). Behaviour is recorded to
`%AppData%\XIVLauncher\pluginConfigs\Emptor\captures\` when "Capture" is enabled,
for tuning the human emulation.
