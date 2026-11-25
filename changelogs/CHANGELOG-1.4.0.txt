Tarkov Price Overlay 1.4.0 (applies over 1.0.0 – 1.3.1, self-contained build)

INSTALL (update)
From 1.2.8 on, you can use Update now and the overlay installs this for you and restarts. To install by hand:
1. Close Tarkov Price Overlay (if "Minimize to tray" is on: right-click the tray icon › Quit).
2. Copy every file and folder in TarkovPriceOverlay-update-1.4.0.zip (including "licenses") into
   <your install folder>\TarkovPriceOverlay\self-contained
   and choose "Replace the files in the destination". Copy everything, not just the exe.
3. Start TarkovPriceOverlay.exe (overlay.log / smoke test says 1.4.0.0).
   Settings, tracked items, quest progress and map settings are kept.
New users: download TarkovPriceOverlay-win-x64.zip and run TarkovPriceOverlay\self-contained\TarkovPriceOverlay.exe.
Framework-dependent users: use Update now (it takes the full zip) or replace your framework-dependent folder from the full zip.

NEW: QUEST & HIDEOUT FLAGS ON PRICE CARDS
- Hovering an item now flags quest and hideout needs right at the top of the card (above the price rows) so you
  don't have to scroll down past crafts and barters to see whether to keep it:
  - Active quests (gold accent): quests currently active in Tarkov's game logs or Map › Quests…, with count and FIR
    status (e.g. "ACTIVE QUEST · 3× FIR · Shortage"). Active quests are also sorted first and labeled "Active" in the
    Quests section below.
  - Not-yet-done quests (amber accent): remaining future quests you haven't finished or failed yet, with count and
    FIR status (e.g. "LATER QUESTS · +9× FIR · First Aid +1", or "QUEST · 8× FIR · Car Repair").
  - Hideout upgrades (blue accent): unbuilt hideout station levels still needing the item, with count and FIR status
    when required (e.g. "HIDEOUT · 16× · Generator L3").
- Works for both PvE and PvP tarkov.dev data and progress, and also appears on "Possible match" cards.
- Respects your progress filters, "Show non-FIR needs", and any manual remaining-count adjustments in Tracked needs.
- New settings toggle: Settings… › Overlay & Card › Card fields › "Quest & hideout flags" (on by default).
- Note on progress sources: quest active/done/failed states update automatically from Tarkov's game logs (and can be
  edited in Map › Quests…); Tarkov's logs do not record hideout upgrades, so built station levels are set manually
  in Settings… › Hideout levels… (or Map › Hideout…).

ALSO
- Includes everything from 1.0.1–1.3.1 (icon tiebreaker, exit-list fix, ammo chart, Update now, update check, …).
