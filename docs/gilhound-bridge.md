# Restocky bridge (Gilhound → game commands)

Gilhound's **Go** button asks the Restocky Dalamud plugin to run a Lifestream
travel command inside the game. No keystrokes are sent to the game window.

Directory: `%APPDATA%\XIVLauncher\pluginConfigs\Restocky\bridge\`

| File | Writer | Content |
|---|---|---|
| `heartbeat.json` | Restocky, every 5 s while loaded | `{"version":1,"plugin_version":"0.3.9.0","ts":<epoch s>,"logged_in":true,"world":"Cerberus","enabled":true}` |
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
