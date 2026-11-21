# Restocky bridge (Gilhound → game commands)

Gilhound's **Go** button asks the Restocky Dalamud plugin to run a Lifestream
travel command inside the game. No keystrokes are sent to the game window.

Directory: `%APPDATA%\XIVLauncher\pluginConfigs\Restocky\bridge\`

| File | Writer | Content |
|---|---|---|
| `heartbeat.json` | Restocky, every 5 s while loaded | `{"version":1,"plugin_version":"0.3.10.0","ts":<epoch s>,"logged_in":true,"world":"Cerberus","enabled":true,"features":["travel","mb_search_name"]}` |
| `inbox/<id>.json` | Gilhound | `{"version":1,"id":"<uuid4>","command":"/li Raiden market","created_ts":<epoch s>}` — written as `<id>.tmp` then renamed |
| `outbox/<id>.json` | Restocky | `{"version":1,"id":"<uuid4>","status":"ok"\|"rejected"\|"error","reason":"…","ts":<epoch s>}` |

Rules (enforced by Restocky):
- Only `^/li [A-Za-z][A-Za-z' -]{1,30} market$` is accepted. Anything else → `rejected`.
- Requests older than 30 s → `rejected` ("expired").
- Not while in combat, a duty, a cutscene, between areas, or not logged in → `rejected` with the reason.
- At most one command per 2 s; one inbox file processed per tick, oldest first.
- Setting "Allow Gilhound travel commands" (default on) — when off, heartbeat has `"enabled":false` and every request is `rejected`.
- Inbox file deleted after processing; outbox files older than 10 min are pruned.

Gilhound treats the bridge as available when `heartbeat.json` is younger than
15 s and `enabled` is true; otherwise Go falls back to copying the command.
Gilhound waits up to 5 s for the outbox reply and shows its status.

## v2 addition — item name search

Restocky 0.3.10.0 accepts an optional `item` in the version 1 request envelope.

```json
{"version":1,"id":"<uuid4>","command":"/li Raiden market","created_ts":1795228800,"item":{"id":50971,"hq":false,"name":"Ascendant Twine"}}
```

- Supply a positive integer Item sheet ID. The optional boolean `hq` defaults to false. Both qualities use the same search text. The supplied `name` is informational.
- A successful Go replaces the previous pending search. A travel-only Go clears it. The pending search expires ten minutes after dispatch. The outbox reply reports travel dispatch.
- When Lifestream or the player opens the market board on the target world, Restocky resolves the ID in the client's localized Item sheet and searches once. It stops at the matching-item list and prints `Gilhound: searched <item> on the market board`. Closing the board before the search cancels it silently. An unknown ID produces a warning in the plugin log.
- The heartbeat feature `mb_search_name` advertises support. The default-on setting “Search Gilhound item when the market board opens” controls whether Restocky performs the search.
