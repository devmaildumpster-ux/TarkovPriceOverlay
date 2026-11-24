Tarkov Price Overlay 1.3.1 (applies over 1.0.0 – 1.3.0, self-contained build)

INSTALL (update)
From 1.2.8 on, you can use Update now and the overlay installs this for you and restarts. To install by hand:
1. Close Tarkov Price Overlay (if "Minimize to tray" is on: right-click the tray icon › Quit).
2. Copy every file and folder in TarkovPriceOverlay-update-1.3.1.zip (including "licenses") into
   <your install folder>\TarkovPriceOverlay\self-contained
   and choose "Replace the files in the destination". Copy everything, not just the exe.
3. Start TarkovPriceOverlay.exe (overlay.log / smoke test says 1.3.1.0).
   Settings, tracked items, quest progress and map settings are kept.
New users: download TarkovPriceOverlay-win-x64.zip and run TarkovPriceOverlay\self-contained\TarkovPriceOverlay.exe.
Framework-dependent users: use Update now (it takes the full zip) or replace your framework-dependent folder from the full zip.

NEW: ICON SHORT-NAME TIEBREAKER
- When a hover would have shown a "Possible match" or "Several possible matches" card, the overlay also reads
  the short name painted on the item icon (preferring a frame taken before the name tooltip covered it).
- A unique short name, or one that only one of the possible items has, resolves the card on its own so you
  don't have to check in the main window.
- A verified full-name match is never changed. If the icon text disagrees with the tooltip candidates, the
  tooltip result is kept (a bad icon read will not override a better name).
- Items that share a short name — for example loose 5.56x45mm M995 and its 50- and 100-round packs, all labeled
  "M995" on the icon — still show the picker.
- After a match is resolved this way, stack counts are read as usual.

ALSO
- Includes everything from 1.0.1–1.3.0 (ammo chart, Update now, exit-list fix, update check, outage handling, …).
