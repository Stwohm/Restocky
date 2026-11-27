# Restocky

<p align="center"><img src="images/icon.png" alt="Restocky icon" width="160"></p>

A Dalamud plugin for FFXIV that keeps your retainer market listings priced, tracks what sells and
warns you when stock runs low.

Restocky is a fork of [yagi2/Restocker](https://github.com/yagi2/Restocker) (MIT, © yagi2). The
upstream plugin is no longer maintained. This fork fixes its known bugs and adds pricing rules, sales
and cost tracking, and an optional auto cycle at the summoning bell.

> **ToS warning.** Timed automatic repricing is against the FFXIV Terms of Service. Use it at your own risk.

## Install

1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, add this URL, tick **Enabled** and save:

   ```
   https://raw.githubusercontent.com/Stwohm/Restocky/main/repo.json
   ```

3. Type `/xlplugins`, search for **Restocky** and install it.

Open the plugin with `/restocky`, or open a summoning bell. The window opens automatically when the
retainer list appears. You get updates from `/xlplugins` like any other plugin.

## Features

### Update prices

- **Full cycle (once):** visits every retainer, reads live market prices, applies the price rules and
  updates every listing without asking.
- **Full cycle (I set prices):** fetches prices and fills the **New** column using the same rules, but
  applies nothing. Review the rows, then press **Apply**.
- **Auto cycle every N min:** repeats the full cycle while you stand at the bell. It stops when you
  move, leave the bell, enter combat, a duty or a cutscene, change zone, get a duty pop, or after
  3 failed cycles. It is off every time the plugin starts.
- **AutoRetainer:** the cycle waits while AutoRetainer is busy or a venture is about to return, and
  pauses AutoRetainer while it runs.
- **Hold:** tick Hold on an item and automatic cycles never change its price.

### Price rules

- New price = the lowest competitor price minus the undercut (default 1 gil). Your own retainers are
  ignored. Restocky uses offers from your home world.
- **Match quality** is on by default, including when upgrading. Turn it off per item to let HQ
  compete with NQ prices. The HQ and NQ checkboxes beside each item's floor independently override
  **Match quality by default** in Settings.
  **Default** restores inheritance for that quality.
- With matching enabled, Full cycle, Auto cycle and I set prices keep your asking price when no
  same-quality competitor exists and report the reason in chat. Stock tracking offers no market suggestion
  in that case. Bait checks and new rival observations use the same competitor pool.
- Bait listings are skipped and reported in chat. A bait listing is one or two listings that are at
  least 1.5× cheaper than the next group.
- **Floor:** a price never goes below the item's floor.
  - If you know the purchase cost, the floor is the break-even price after sales tax:
    `cost × (1 + minimum profit %) ÷ (1 − sales tax %)`, rounded up. Cost is the total remaining lot cost divided by held units of the same
    quality, including listings, retainer inventories, bags and saddlebags. Partial scans use the
    listed and otherwise known units as a lower bound. Sales still consume bought lots first in FIFO order.
  - A floor you type in **Settings** replaces the break-even floor, even when it is lower. Press
    **Auto** (or type 0) to return to the break-even floor.
- **Max drop / max raise:** an automatic cycle does not apply a change bigger than 30% (configurable).
  The I set prices table marks these rows for review.
- If every competitor is more expensive than you, the price is raised.

### Stock tracking

- Watch an item. When the listed quantity falls to your threshold, Restocky warns you in chat and in
  the window header.
- **Restock** can top up partial listings at their existing price, starting with the fullest stack.
  Review the plan before it runs. Remaining units can create new listings up to the target.
  Turn off **Top up partial stacks on Restock** in Settings to add new listings only.
- **List & price:** if nothing is listed yet, Restocky opens the game's sale window. You set the price
  there, and Restocky lists the remaining stacks at that price.
- **Sell on** lets you choose a retainer per item and quality. Auto prefers a retainer that already
  sells the item and is used as a fallback when the chosen retainer cannot take a listing.
- Add **Flip**, **Gather**, or **Craft** and **Regular** or **Trial** tags to filter the list.

### Sales, costs and profit

- Sales are read from each retainer's sale history during a cycle (buyer, time and exact proceeds).
- Market board purchases are recorded as cost lots automatically. You can also add lots by hand in the
  **Costs** tab. A sale consumes available lots of the same item and quality first, oldest purchase first.
  **Profit** is the net proceeds on covered units minus their recorded cost. The remaining units have
  zero purchase cost and their net proceeds appear under **Earning**. Once the last lot is consumed,
  the automatic cost floor disappears. Flip, Gather and Craft are manual stock tracking tags.
- Reprice estimates share the remaining lots across listings in retainer-key and listing-slot order.
  Sorting, filtering or collapsing the table keeps that allocation stable. Actual sales use the
  order in which sales are recorded and only consume lots acquired by the sale time.
- In **Sales**, choose **Today**, **3 days** or **7 days** for unmatched market sales. The range uses
  local calendar dates and the latest market history received this session for each scanned item
  (about 20 sales per item). Busy items can therefore cover only part of the selected period.
- **Profit** column, realized profit, **Stuck** listings and sales **Velocity** in the Sales tab.
- A tooltip on each listing shows cheaper sellers and how often each rival undercut you in the last
  24 h, 3 days and 1 week.

### Records

Records are saved in `pluginConfigs\Restocky\`. Open the folder from the **Sales** tab.

| File | Contents |
|---|---|
| `price-history.csv` | Every price decision: old price, lowest competitor, new price, whether it was applied, and the reason |
| `listings.csv` / `listings.json` | Each listing from first price to sale: price changes, undercuts, days listed, gil received |
| `competitors.csv` | Summary per item and rival |
| `cost-ledger.json` | Cost lots (market board and manual) |

### Settings

Undercut amount, max drop %, max raise %, automatic cost floor, sales tax % (default 5), minimum
profit % (default 0), per-item floors, sale history fetch, chat channel (default: Retainer Sale
Notifications), auto-open on bell, and language (English, 日本語, Deutsch, Français, 简体中文, 한국어).

## Build and test

Requires the .NET 10 SDK.

```
dotnet build -c Release
dotnet test
```

`tests/Restocker.Tests/` is a pure-logic xUnit project and needs no game. Code that touches the game
(the executor, the windows) is tested in game.

A release is made by creating a GitHub release. The workflow builds the plugin and attaches
`latest.zip`, which `repo.json` points to.

## License

MIT. The original Restocker is © yagi2.
