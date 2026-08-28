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

1. Dismounts, walks to the nearest **Market Board** object (via vnavmesh if it's
   far), and interacts with it. A retainer / summoning bell is not enough.
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
| `Emptor.ApiVersion` | `Func<int>` | currently `1` |
| `Emptor.IsBusy` | `Func<bool>` | an order is running |
| `Emptor.SubmitOrder` | `Func<string,string>` | request JSON → order JSON (queued) |
| `Emptor.GetOrder` | `Func<string,string>` | orderId → order JSON (live) |
| `Emptor.CancelOrder` | `Func<string,bool>` | request stop |
| `Emptor.OrderCompleted` | `Action<string>` | fires with the orderId on a terminal state |

### Request

```json
{
  "clientRequestId": "optional",
  "totalGilBudget": 5000000,
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

### Order result

```json
{
  "orderId": "…",
  "state": "queued|running|completed|cancelled|rejected|failed",
  "message": "…",
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
    "stoppedReason": "quantityMet"
  }]
}
```

`stoppedReason` ∈ `quantityMet | priceExceeded | noListings | budgetExceeded |
overshoot | promptMismatch | blocked | cancelled | indeterminate | itemUnresolved
| openFailed | searchFailed`.

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

## Status

Core buy loop works end to end on retail. Rough edges remain (multi-item /
quantity > 1 paths, timing calibration). Behaviour is recorded to
`%AppData%\XIVLauncher\pluginConfigs\Emptor\captures\` when "Capture" is enabled,
for tuning the human emulation.
