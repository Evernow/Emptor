# Dalamud Plugins

Custom Dalamud plugin repository.

## Install

In game: **Dalamud Settings → Experimental → Custom Plugin Repositories**, add:

```
https://raw.githubusercontent.com/Evernow/DalamudPlugins/main/pluginmaster.json
```

Save, then find the plugins under `/xlplugins`.

## Plugins

### Emptor

Buys a shopping list off the FFXIV market board. For each entry
`(item, quantity, max unit price, quality)` it buys the cheapest acceptable
listings until the requested quantity is met, the price ceiling is hit, or
listings run out. Targets vanilla (retail) FFXIV.

- **In game** — `/emptor` opens a window with an editable shopping-list table
  and a **Start** button.
- **From another plugin** — a Dalamud IPC API (see below).

---

## Building from source

```
dotnet build -c Debug
```

Debug builds copy themselves to `%AppData%\XIVLauncher\devPlugins\Emptor\`.
In game: Dalamud Settings → Experimental → rescan dev plugins, then enable
**Emptor** in `/xlplugins`.

Requires the XIVLauncher/Dalamud dev libraries at
`%AppData%\XIVLauncher\addon\Hooks\dev` and the .NET 10 SDK. Releases are built
by [`.github/workflows/build.yml`](.github/workflows/build.yml): publish a
GitHub Release and it packages `Emptor.zip`, attaches it, and refreshes
`pluginmaster.json`.

## How buying works

Per shopping-list item the runner:

1. Dismounts, walks to the nearest **Market Board** object (via vnavmesh if it's
   far), and interacts with it. You must be somewhere a Market Board is reachable
   — a retainer / summoning bell is not enough.
2. Focuses the search box and types the item name with real keystrokes, then
   presses Enter. (Nothing else triggers the client's search.)
3. Clicks the item in the results to open its listings.
4. Reads `InfoProxyItemSearch.Listings`, filters by quality and price ceiling,
   sorts cheapest first.
5. Buys whole listings cheapest-first (the marketboard sells whole stacks),
   confirming the "Purchase for X gil?" dialog, until the quantity is met.
6. Records what it bought, the total gil, and the next-cheapest listing it did
   **not** buy.

Every step has a human-variable delay (see `HumanTiming` / `TypingModel`).

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

## Verification (do in order)

1. **Board opens** — `/emptor`, add a row, Start. Confirm the marketboard
   window opens while standing at a Market Board.
2. **Search + read** — add a row with `quantity 0` for a common item, Start.
   The log should list every listing (price × qty). Compare with the game UI.
3. **Single buy** — one cheap item, `quantity 1`, generous max price. Confirm one
   purchase, gil drop, item in inventory, sane `nextLowest`.
4. **Ceiling + multi-buy** — quantity needing 3–4 listings with a max price that
   excludes the top ones. Confirm it stops at `priceExceeded` and paces between
   buys.
5. **Overshoot** — try each policy against big-stack-only listings.
6. **IPC** — submit/poll/cancel from `/xldev` IPC tester or a scratch plugin.
7. **Safety** — mount mid-run → `blocked`; disable the plugin mid-run → no stray
   dialogs left open.
