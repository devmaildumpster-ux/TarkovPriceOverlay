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

FIX: EXIT LIST (O KEY) TIMED V-EX & BRIGHT-SKY ROWS
- Active/timed vehicle extracts (such as Shoreline's "Road to North V-Ex 0:00:28") and rows sitting over bright
  sky could be dropped or misread when the O-key reader ran at 1x on the raw top-right crop.
- The exit-list reader now upscales small HUD rows (2x at 1080p/1440p, 1.5x at 4K), isolates bright/colored HUD
  text on dark translucent bars so sky background does not hide top rows, and also checks Tesseract on the
  second read when Windows OCR misses a row.
- Row matching now strips OCR-mangled row labels (case-insensitive, leading punctuation, spaced or glued numbers),
  right-column timers with "." or O/0 confusions, and "?????" noise, and matches space-collapsed OCR without
  letting a shorter extract ("Lighthouse") outscore a longer one ("Path to Lighthouse").

FIX: QUESTS WINDOW SEARCH SHOWED "SHOWING 1" WITH AN EMPTY LIST
- Searching for a quest (for example "postman pat") could leave the list area blank even though the count
  under the filters said "Showing 1" (or any number). The header and footer were added to a dock panel inside
  a padded border, so the list itself got no space and looked empty. Those panels are now docked correctly,
  and after a search the first matching row is scrolled into view.
- Search also treats dashes and punctuation as spaces, so "postman pat", "postman-pat" and an en-dash in
  "Postman Pat – Part 1" all find the same quests (Prapor's Part 1 on Factory and Therapist's Part 2).

ALSO
- Includes everything from 1.0.1–1.3.0 (ammo chart, Update now, exit-list fix, update check, outage handling, …).
