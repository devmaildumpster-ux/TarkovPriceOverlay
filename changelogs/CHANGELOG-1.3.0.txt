Tarkov Price Overlay 1.3.0 (applies over 1.0.0 – 1.2.8, self-contained build)

INSTALL (update)
From 1.2.8 on, you can use Update now and the overlay installs this for you and restarts. To install by hand:
1. Close Tarkov Price Overlay (if "Minimize to tray" is on: right-click the tray icon › Quit).
2. Copy every file and folder in TarkovPriceOverlay-update-1.3.0.zip (including "licenses") into
   <your install folder>\TarkovPriceOverlay\self-contained
   and choose "Replace the files in the destination". Copy everything, not just the exe.
3. Start TarkovPriceOverlay.exe (overlay.log / smoke test says 1.3.0.0).
   Settings, tracked items, quest progress and map settings are kept.
New users: download TarkovPriceOverlay-win-x64.zip and run TarkovPriceOverlay\self-contained\TarkovPriceOverlay.exe.
Framework-dependent users: use Update now (it takes the full zip) or replace your framework-dependent folder from the full zip.

NEW: AMMO CHART
- Ammo… in the main window (and More ▾ › Ammo chart…) opens a chart of every round in a caliber, side by side.
- Columns: damage, penetration, armor damage %, frag chance, speed, tracer, flea price, trader buy (name + loyalty
  level + price) for the current PvP / PvE mode, and color-coded cells for armor classes 1–6
  (green = usually pens, yellow = 50/50, red = almost never). The scale compares penetration to 10 × class; it is
  for telling rounds apart at a glance, not a simulation of an in-raid roll.
- Pick a caliber, type to search, sort by any column, and optionally show only tracers or only rounds a trader sells.
- Shortcut Ctrl+Alt+A (changeable in Settings… › Overlay & Card) opens the chart for the caliber of the last ammo
  you hovered or looked up, without taking focus from the game. Esc closes it. If the last item wasn't ammo, the
  caliber picker opens instead.
- The price card for a loose round (or an ammo pack) now shows its caliber, pen, damage, armor damage and a small
  class 1–6 bar, plus frag chance, speed and tracer when they apply, so you can tell types apart without opening
  the chart. Those lines wrap inside the normal card width, so a long stats line (frag + speed + tracer) is not cut off.
- Stats come from the same cached tarkov.dev catalog as prices (no extra download). While tarkov.dev is down the
  chart keeps the last good data, same as prices. After installing 1.3.0, ammo stats appear on the next price
  refresh (automatic at start).

ALSO
- Includes everything from 1.0.1–1.2.8 (Update now, exit-list fix, update check, outage handling, …).
