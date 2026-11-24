Tarkov Price Overlay 1.2.7 (applies over 1.0.0 – 1.2.6, self-contained build)

INSTALL (update)
1. Close Tarkov Price Overlay (if "Minimize to tray" is on: right-click the tray icon › Quit).
2. Copy every file and folder in TarkovPriceOverlay-update-1.2.7.zip (including "licenses") into
   <your install folder>\TarkovPriceOverlay\self-contained
   and choose "Replace the files in the destination". Copy everything, not just the exe.
3. Start TarkovPriceOverlay.exe (overlay.log / smoke test says 1.2.7.0).
   Settings, tracked items, quest progress and map settings are kept.
New users: download TarkovPriceOverlay-win-x64.zip and run TarkovPriceOverlay\self-contained\TarkovPriceOverlay.exe.

FIX: EXIT LIST (O KEY) CROSSED OUT EXITS THAT WERE ON YOUR LIST
Report (Interchange, PvE, PMC). Your list: Scav Camp (Co-Op), Saferoom Exfil, Railway Exfil, Power Station V-Ex,
Path to River (Flare), plus the two transits. The map crossed out Railway Exfil, Saferoom Exfil and Scav Camp (Co-Op)
as "not on your list"; only Power Station V-Ex and Path to River were marked as listed.
- Cause 1: to drop the row label ("EXFIL03") the reader removed every word starting with "exfil", so
  "Railway Exfil" became "Railway" and "Saferoom Exfil" became "Saferoom", which were too different to match.
  Now only the row label is removed (EXFIL01…, TRANSIT01…, and OCR misreads like EXF1L03, EXFIL@3, EXFILO3, also when
  glued to the name like "EXFIL01Scav"). Any exit with "Exfil" in its name was affected (Interchange, Woods, …).
- Cause 2: the reader stopped after the first of its two screen reads that matched anything. The list fades in,
  so a row missed in the first read (likely what happened to Scav Camp (Co-Op)) never got a second chance.
  Both reads now always run and their matches add up.
- Cause 3 (same root, made sturdier): tarkov.dev lists some exits twice (a PMC and a Scav "Railway Exfil",
  "Emercom Checkpoint"); the map draws one "PMC + Scav" marker. A matched name now marks every copy, so the marker
  is listed whichever copy it uses.
- Rows cut off at the edge ("Power Station V-", "Scav Camp (Co-0") still match. Timers like ??:??:?? are ignored.
- Hole in the Fence and Emercom Checkpoint were NOT on your list, so they are still crossed out (⊗, faded). That's
  intended: the map keeps every exit of the map and fades the ones not on this raid's list. Their zone boxes are
  no longer drawn, so they stand out less.
- The pink rectangle with a diamond near Path to River is a quest objective zone, not an exit.
- overlay.log now records what the exit-list reader read ("map: exit list OCR (…): EXFIL01 … → 5 matched") so a
  wrong match can be checked.

ALSO
- Includes everything from 1.0.1-1.2.6 (update check on launch, outage handling, controls window, …).
