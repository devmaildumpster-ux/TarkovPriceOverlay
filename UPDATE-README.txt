Tarkov Price Overlay 1.2.5 (applies over 1.0.0 – 1.2.4, self-contained build)

INSTALL
1. Close Tarkov Price Overlay (if "Minimize to tray" is on: right-click the tray icon › Quit).
2. Copy every file and folder in this zip (including "licenses") into
   <your install folder>\TarkovPriceOverlay\self-contained
   and choose "Replace the files in the destination". Copy everything, not just the exe.
3. Start TarkovPriceOverlay.exe (overlay.log / smoke test says 1.2.5.0).
   Settings, tracked items, quest progress and map settings are kept.

NEW: WHEN TARKOV.DEV IS DOWN
- Counts as down: server errors (HTTP 500-599), "too many requests" (429), an error reply with no data
  (e.g. "GraphQL server unavailable. Try again later."), timeouts and refused connections. If your PC can't
  find tarkov.dev at all (no DNS / no network), it says "No internet connection" instead.
  Not counted: an item without price history (404), or a reply that has data plus a few errors.
- While it's down the app sends NO normal requests (prices, quests, hideout, crafts, barters, map/quest feeds,
  price history). It checks with one small request after 1, 2, 5, 10 minutes, then every 15 minutes
  (each ±20 %, never past 15 min; longer only if tarkov.dev sends Retry-After, up to 60 min).
- When a check succeeds it downloads prices once, clears the message and logs it.
- Refresh prices while down = one check now, at most once every 30 s ("checked under 30 s ago" otherwise).
- Top bar: "tarkov.dev is down — showing prices from 10:25 AM (2 h ago) · next check in 4 min" (gold);
  hover it for details. Quick search bar footer and price cards: "⚠ tarkov.dev down · prices from 2 h ago".
  The cards keep showing your last prices. Nothing pops up; nothing takes focus.
- With no saved prices at all: "tarkov.dev is down and no prices are saved yet — they load by themselves when
  it's back", and the price key shows a card saying the same.
- Your last prices are saved on disk and load at startup even during an outage, with their age.

NEW: "FLEA SCANS STOPPED"
- Today (Nov 24) tarkov.dev's files kept downloading fine, but its flea-market scanner had stopped at about
  2:28 AM EST, so every price was more than 2 hours old and every card said "stale" — even right after
  Refresh prices. The top bar now says so: "Prices downloaded just now · tarkov.dev's flea scans stopped at
  2:28 AM (10 h ago)", and cards show "tarkov.dev not updating · last scan 10 h ago". New scans are picked up
  by the normal 20-minute refresh.

OVERLAY.LOG
  tarkov.dev: DOWN (tarkov.dev is down: "GraphQL server unavailable…" on /regular/items); normal requests paused; next check 12:40:19 (in 70 s)
  tarkov.dev: check failed (…); next check …
  tarkov.dev: RECOVERED after 1 min (1 check(s)); refreshing prices once
  tarkov.dev: prices refreshed after the outage (Fresh)

ALSO
- Includes everything from 1.0.1-1.2.4 (fewer "possible match" cards, controls window, auto floor switching, …).
