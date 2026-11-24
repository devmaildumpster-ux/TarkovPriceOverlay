# Tarkov Price Overlay (Windows x64)

Hover an item in Escape from Tarkov until its name tooltip appears, press the shortcut, and a small
click-through price card appears next to the cursor: flea price, flea net after fee, best trader payout,
buy-back price, value per inventory slot, and what the item is needed for (quests, hideout, crafts, barters).

Data comes from Tarkov.dev. The app only reads screen pixels around the cursor when you press the shortcut.
It never reads game memory, injects, or sends input to the game.

## Download & install

1. Open the **[latest release](https://github.com/devmaildumpster-ux/TarkovPriceOverlay/releases/latest)** and download
   **`TarkovPriceOverlay-win-x64.zip`** (under *Assets*).
2. Unzip it anywhere, for example `C:\Tools`. Inside you get:
   ```
   TarkovPriceOverlay\
     self-contained\TarkovPriceOverlay.exe        <- run this one (no .NET install needed)
     framework-dependent\TarkovPriceOverlay.exe   <- smaller; needs the .NET 8 Desktop Runtime
     README.md, CONTROLS.txt, THIRD-PARTY-NOTICES.md, LICENSE.txt
   ```
   (Windows "Extract All" may add an outer `TarkovPriceOverlay-win-x64\` folder; that's fine.)
3. Run **`TarkovPriceOverlay\self-contained\TarkovPriceOverlay.exe`**. Windows SmartScreen may warn because the exe is
   unsigned: click **More info → Run anyway**.

**Already have it installed?** Download **`TarkovPriceOverlay-update-<version>.zip`** from the same release, close the
app, and copy everything in it (including the `licenses` folder) into your `TarkovPriceOverlay\self-contained` folder,
replacing the files. Settings, tracked items and quest progress are kept. (The update zip is for the
`self-contained` folder; if you use `framework-dependent`, download the full zip instead.)

**Updates:** the app checks GitHub for a newer release when it starts and shows a small "Update available" strip.
Since 1.2.8, **Update now** installs it for you: it downloads the release file from GitHub, checks its SHA-256,
replaces the program files and restarts the overlay (settings, tracked items and quest progress are kept). **Release
page** opens the release in your browser instead. The check and Update now are also in **Settings… › Troubleshooting ›
Updates**. Versions up to 1.2.7 only have the Download button, so install 1.2.8 by hand once.

## Quick start for a new user

1. Install as above and run `TarkovPriceOverlay\self-contained\TarkovPriceOverlay.exe`.
2. In Tarkov, set **Settings › Graphics › Screen mode** to **Borderless** (or Windowed). Price cards can't draw over
   exclusive Fullscreen.
3. Hover an item until Tarkov's name tooltip appears: the price card shows by itself (or press `` ` ``, the backtick).
4. For your position on the map: make sure Tarkov has a **Screenshot** key bound (Settings › Controls; PrtScn by
   default) and press it in raid.
5. If the main window says text reading or Tesseract is unavailable: install the Windows **English OCR language pack**
   and the **Microsoft Visual C++ 2015–2022 Redistributable (x64)** (see Requirements below). Most PCs already have both.
6. The **? Controls** button at the top of the app lists every key and button with your current keys
   (it also opens by itself the first time). The same list is in `CONTROLS.txt` next to the exe.

## Controls & hotkeys

Default keys; every shortcut can be changed or cleared in **Settings…**.

<!-- controls:start -->

**Price cards (in game)**

| Control | What it does |
|---|---|
| Rest the mouse on an item | Auto-read on hover (on by default): when Tarkov's name tooltip appears (after 350 ms), the price card shows by itself. |
| `` ` (backtick) `` | Price shortcut: hover an item until its name tooltip shows, then press it for the price card next to the mouse. |
| Same key again | With a card showing (mouse still): expand / collapse the full list of quests, crafts, barters and hideout needs. |
| `` Ctrl+` / Ctrl+Shift+F8 `` | Used as the price shortcut instead of the backtick if another app already has it (the top bar shows which one you got). |
| Move the mouse | Hides the card (past 6 px). It also hides after 7 s (15 s when expanded). The card never takes focus; clicks go through it. |
| Ctrl+Alt+W | Open the Tarkov Wiki page of the last item looked up. |
| Ctrl+Alt+D | Open the last item looked up on tarkov.dev. |

**Quick search bar (in game)**

| Control | What it does |
|---|---|
| `` Alt+` `` | Open / close the search bar over the game. |
| Type | Filter by item name or short name. |
| ↑ / ↓ | Pick a result. |
| Enter or double-click | Show that item's price card. |
| Esc | Close the bar and give the keyboard back to the game. |
| Click outside the bar | Closes it. |

**Map**

| Control | What it does |
|---|---|
| Ctrl+Alt+M | Switch the map Satellite ↔ Vector from in game (never takes focus; docked or popped out). |
| Satellite / Vector button | Same switch on the map toolbar (greyed out on maps that have only one style). |
| Mouse wheel | Zoom in / out at the mouse. |
| Left-drag | Pan the map. |
| Double-click the map | Fit the whole map (also More ▾ › Fit whole map). |
| Hover a pin | Show the exit / transit / quest spot name and details. |
| Map box | Pick a map. Auto-follow (tick box) switches to the raid's map by itself. |
| Floor box | Pick a floor / underground level. Auto layer switching from screenshots: All floors & underground (Settings… › Map & Screenshots); a floor you pick stays until your next screenshot. |
| Exits / Quests / Me | Show or hide exits, your picked quests and your position dot. |
| Pop out / Dock | Open the map in its own window (remembers monitor and size), or put it back. |
| F11 or Fullscreen | In the popped-out map: borderless fullscreen on that monitor. |
| Esc | Leave fullscreen (popped-out map). |

**Screenshots & position (in raid)**

| Control | What it does |
|---|---|
| PrtScn | Tarkov's own Screenshot key (Tarkov Settings › Controls). Each screenshot puts your dot and facing on the map, and switches the floor. Bind it if nothing happens. |
| More ▾ › Test position from latest screenshot | Re-reads the newest screenshot and says what happened (folder, file, position). |
| O | Experimental exit-list reader (off by default; Settings… › Map & Screenshots): after you press O in raid it reads the exit list and fades and crosses out (⊗) the map's exits that aren't on it. |

**Buttons, menus & windows**

| Control | What it does |
|---|---|
| ? Controls | This list (also More ▾, the tray menu and Settings…). Shows your current keys. |
| Settings… | Settings (also click the PvP/PvE badge): Overlay & Card, Map & Screenshots, Troubleshooting. Changes apply right away. |
| Settings… › Record… / Clear / Default | Change a shortcut: click Record… and press the new keys (Esc cancels), Clear to turn it off, Default to restore it. |
| Refresh prices | Download fresh prices now. |
| More ▾ (map) | Exits for auto / PMC / Scav, Show transits, Browse all quests, Area names, Fit whole map, Quest tracker…, Hideout tracker…, Test position…, Map settings…, Controls & hotkeys. |
| Quests… | Quest tracker. Double-click or Space marks a quest done / not done. |
| Hideout… | Hideout tracker: the station levels you've built (cards skip those needs). |
| Deselect all / Only my active quests | Quick quest-pin choices under the map. |
| Pop out card / Load price history / Wiki / tarkov.dev | Buttons under the item card in the main window. |
| Popped-out card | Drag to move; double-click or Esc closes it. |
| − / + / × | Tracked items: one handed in, one more, stop tracking. |
| Update available: Update now / Release page / Skip this version / × | Shows under the top bar when a newer version is on GitHub. Update now downloads it from GitHub, checks it, installs it and restarts the overlay (settings kept; Cancel stops the download); Release page opens it in your browser instead; Skip hides that version; × hides it until next launch. Also in Settings… › Troubleshooting › Updates. |

**Tray icon (when Minimize to tray is on)**

| Control | What it does |
|---|---|
| Click / double-click | Show the main window. |
| Right-click | Menu: Open, Settings…, Controls & hotkeys, Quit. |

<!-- controls:end -->

## New in 1.2.8

- **Update now: the app updates itself.** The update strip (and Settings… › Troubleshooting › Updates) has an
  **Update now** button. It downloads the update from this repo's latest GitHub release with a progress bar and a
  **Cancel** button, and refuses anything that isn't from GitHub over HTTPS. It checks the file's SHA-256 against
  GitHub's checksum (and `SHA256SUMS.txt` when the release has one), unpacks it to
  `%LOCALAPPDATA%\TarkovPriceOverlay\update`, checks the files inside are the expected version, then closes the overlay.
  A small helper (`TarkovPriceOverlay.Updater.exe`) backs up the files it replaces, copies the new ones in, puts
  everything back if anything fails, and restarts the overlay, which then shows "Updated to X.Y.Z". Settings, caches
  and logs are never touched. Self-contained installs get the small update zip, framework-dependent ones the full zip.
  If the folder needs admin rights (for example under Program Files) you get a clear message instead of a UAC prompt;
  move the folder somewhere you own. Every step is in `overlay.log` (lines starting `update:` and `updater:`).

## New in 1.2.7

- **Exit list fix (O key).** Exits with "Exfil" in their name (Railway Exfil, Saferoom Exfil, …) were crossed out on
  the map even when they were on your in-game list, and a row missed in the first screen read was never re-read.
  Now only the "EXFIL01" row label is ignored, both reads always count, and exits tarkov.dev lists twice (PMC + Scav
  copies) are matched either way. Exits that aren't on your list stay faded with ⊗, now without their zone box.

## New in 1.2.6

- **Update check on launch.** The app asks GitHub once for the latest release (in the background, 5 s timeout, cached
  for 6 h). If there's a newer version, a green "Update available" strip shows under the top bar with **Download**
  (opens the release page; nothing downloads or installs by itself), **Skip this version** and **×**. Silent when
  offline or rate-limited; never takes focus. Settings… › Troubleshooting › Updates turns it off or checks now.
- First public release on GitHub, licensed GPL-3.0-or-later.

## New in 1.2.5

- **tarkov.dev outages are handled quietly.** When tarkov.dev is down (server errors, "server unavailable"
  replies, timeouts, rate limiting) or your internet is out, the app stops sending requests. It checks with one
  small request after 1, 2, 5 and 10 minutes, then every 15 minutes (±20 % so many users don't all check at
  once, and longer if tarkov.dev asks for that). As soon as it's back it refreshes prices once by itself.
- The top bar says e.g. **"tarkov.dev is down — showing prices from 10:25 AM (2 h ago) · next check in 4 min"**.
  Cards and the quick search bar show a small "tarkov.dev down · prices from 2 h ago" line, and **the last
  prices stay on the cards**. "No internet connection" shows instead when your PC is offline. Nothing pops up and
  nothing takes focus.
- **Refresh prices** while it's down does one check right away (at most once every 30 seconds) instead of a full
  download.
- If tarkov.dev answers but its flea-market scanner has stopped (the newest scan of every item is over 2 hours old),
  the top bar says so ("tarkov.dev's flea scans stopped at 2:28 AM"), and cards say "tarkov.dev not updating".
- Your last prices are saved on disk and load at startup even when tarkov.dev is down; their age is shown.
- overlay.log records each change: down (and why), every check and when the next one is due, and recovery.

## New in 1.2.4

- **Fewer "Possible match" cards, and they always show a price.** Fixes the card you get on long names that Tarkov
  wraps onto two lines (e.g. "AR-10 KAC QDC 7.62x51 Flash / Suppressor Kit"):
  - OCR look-alikes are now treated as the same letter when that can only mean one item: l/I/1, O/0/Q, S/5, B/8,
    Z/2, G/6, "rn"/m ("7.62x5l", "ODC", "Klt", "FIash" now verify).
  - Two-line names read in the wrong order still verify; a lost second line gives a possible match.
  - A "Possible match" card now shows the Flea and Trader values, why the read wasn't exact, and up to two other
    items it might be (with prices). It is still marked "Possible match": verify in the main window.
  - If a tooltip has two name lines but only the first was read, and that first line is itself an item
    (e.g. "… scope" vs "… scope (FDE)"), it is shown as a possible match instead of a confident card.
  - overlay.log now records what each OCR pass read for every lookup, so a wrong or "possible" card can be explained.
  - Whole-catalog check (every tarkov.dev item): 0 confident wrong items for every misread class tested.

## New in 1.2.3

- **Controls & hotkeys** window: every hotkey, mouse control and button, grouped (price cards, quick search, map,
  screenshots, menus, tray), with your **current** keys ("(not set)" when cleared). Opens from **? Controls** in the
  top bar, **More ▾ › Controls & hotkeys…**, the tray menu and **Settings… › Controls & hotkeys…**, and once by itself
  on first launch (it doesn't take focus). `CONTROLS.txt` next to the exe has the same list with the default keys.

## New in 1.2.2

- **The map switches floors from your screenshot.** Your height in each screenshot's file name plus tarkov.dev's floor
  outlines pick the floor you're on, exactly like tarkov.dev does for a marker: Reserve Bunkers, Customs Underground,
  Factory Tunnels, Ground Zero Garage, Streets/Shoreline Underground, Labs Technical, Icebreaker lower decks, and the
  upper floors of buildings. Back on the surface it returns to Main (and to the satellite picture). A short
  "Layer: Bunkers" hint shows; nothing ever takes focus. A floor you pick by hand stays until your next screenshot.
  Settings › Map & Screenshots › *Auto-switch map layer on screenshot*: All floors & underground (default) /
  Underground only / Off (manual).

## New in 1.2.1

- **Satellite map by default** wherever tarkov.dev has its rendered top-down tiles (Customs, Woods, Shoreline,
  Interchange, Reserve, Factory, Ground Zero, The Lab, The Labyrinth, Icebreaker); Streets of Tarkov, Lighthouse and
  Terminal stay on the vector drawing (the only one tarkov.dev has). Sharper too: about 4000 px across (zoom 4).
- **Satellite / Vector button** on the map toolbar (docked or popped out), and a global shortcut **Ctrl+Alt+M** that
  switches it from inside the game without alt-tabbing (it never takes focus). A short "Map: Satellite" hint shows on
  the map. Settings › Map & Screenshots: *Default to the satellite map where available* and the shortcut (Record… /
  Clear / Default).
- Area names and exit labels get dark rounded backdrops on the satellite picture so they stay readable.

## New in 1.2.0

- **Quest & hideout progress:** quests marked Done in **Map › Quests…** and hideout levels set in **Map › Hideout…** are left off cards (the card says how many were hidden). FIR counts show in the Quests/Hideout titles; a **KAPPA** badge marks items a Kappa-required quest still needs (tarkov.dev `kappaRequired`: Collector and its chain). Toggles: Settings… › *Filter needs by my quest & hideout progress*, *Show non-FIR needs*, *Kappa badge*.
- **Auto PvP / PvE:** follows Tarkov's log ("Session mode: …") and switches prices, cards and the map; logged in overlay.log as `mode auto-detect`. Toggle: *Auto-detect PvP / PvE from Tarkov's logs*.
- **Wiki / tarkov.dev:** buttons on the detail card and the search card; shortcuts for the last item: **Ctrl+Alt+W** (Wiki), **Ctrl+Alt+D** (tarkov.dev). Not Ctrl+Shift+W/D because browsers use those. Change, clear or turn off in Settings….
- **In-game search bar:** **Alt+`** opens a search bar on Tarkov's monitor; ↑↓ Enter for the full card, Esc to go back to the game.
- **Card options:** pick which rows the card shows, card opacity 40–100 % (default 95 %), optional minimize to tray (right-click the tray icon: Open / Settings… / Quit).
- **Graphics.ini:** Tarkov's display setting is used to place the search bar; Settings… › Troubleshooting shows the monitor it found.

## Requirements

- Windows 10 (1809 or newer) or Windows 11, x64.
- Run Tarkov in **Borderless** (or Windowed) mode. In exclusive **Fullscreen** the lookup can still work (the main window updates) but **the price card cannot be drawn over the game**. No normal overlay window can.
- **English OCR language pack** for Windows OCR, the primary engine. Most English Windows installs already have it.
  - To add it: Settings → Time & language → Language & region → add/choose English (United States) → Language options → install basic typing/handwriting/OCR. Or run in an admin PowerShell:
    `Add-WindowsCapability -Online -Name "Language.OCR~~~en-US~0.0.1.0"`
  - Without it, the bundled Tesseract engine is used on its own (somewhat slower and less accurate).
- **Microsoft Visual C++ 2015–2022 Redistributable (x64)**, needed by the bundled Tesseract. Most gaming PCs already have it.
  - Download: https://aka.ms/vs/17/release/vc_redist.x64.exe
  - The main window says if it is missing.
- **.NET 8 Desktop Runtime (x64)**, needed only by the `framework-dependent` folder. The `self-contained` folder includes it.
  - Download: https://dotnet.microsoft.com/download/dotnet/8.0

## Run

1. Get the release zip as described in [Download & install](#download--install) and unzip it anywhere.
2. Run `TarkovPriceOverlay\self-contained\TarkovPriceOverlay.exe`, or `TarkovPriceOverlay\framework-dependent\TarkovPriceOverlay.exe` if you have the .NET 8 Desktop Runtime.
3. Windows SmartScreen may warn because the exe is unsigned. Click "More info" → "Run anyway".
4. Optional check: open a terminal in that folder and run `TarkovPriceOverlay.exe --smoke-test`. Every line should say PASS.
   - Exit code 0 means everything works.
   - Exit code 2 means Windows OCR is missing, so only the Tesseract fallback is available.
   - Exit code 1 means a bundled component is broken.
5. Optional: `TarkovPriceOverlay.exe --update-check` prints what the update check sees (this version, the latest release).
   `TarkovPriceOverlay.exe --self-update` does the same as Update now from a terminal (download, verify, install, restart).

## Use

- **Main window layout (1.1.4):** a slim header shows the mode badge (PvP / PvE / Season), the shortcut, whether auto-hover is on, and the price status, with **Refresh prices** and **Settings…** on the right. Below: the map (whole left column), search/results/tracked, and the detail card.
- **Settings…** (header button, or click the mode badge) has three tabs. Every change applies and saves immediately:
  - **Overlay & Card:** shortcut on/off, game mode, card size (1×/2×/3×), always show the full list, hide the card when the mouse moves + drift tolerance, auto-read on hover + rest time + tooltip wait, the value-colour legend, and the Borderless note.
  - **Map & Screenshots:** the old Map settings (game folder, log reading, screenshot folder, position screenshot cleanup, auto-switch map, exit-list OCR). **Map › More ▾ › Map settings…** opens this tab.
  - **Troubleshooting:** price status + Refresh, Windows OCR / Tesseract status, **Test card (in 3 s)**, **Open saved image…**, **Open log folder**, version.
- Pick your **mode** in **Settings… › Overlay & Card**: Regular PvP, PvE, or Seasonal PvP. Prices, cards and (outside a raid) the map's data follow it; during a raid the map follows the raid's mode from the game log.
- **Shortcut:** the app tries these in order and shows which one it got:
  1. `` ` `` (backtick)
  2. `Ctrl+` ``
  3. `Ctrl+Shift+F8`
- **While the backtick is registered, it does not type in other apps** (Discord, browser). Untick **Settings… › Shortcut › Enabled** to release it (remembered across restarts), or close the app.
- **Auto-read on hover** (on by default): rest the mouse on an item in Tarkov. When its name tooltip appears, the card shows by itself.
  - The app polls only the cursor position while Tarkov is the foreground window.
  - After the mouse rests (default 350 ms), it checks the screen near the cursor every 50 ms for up to 600 ms for Tarkov's dark tooltip. It runs the full read only if one appears.
  - The same spot won't read again until you move.
  - Non-item tooltips that don't match anything are ignored silently.
  - The rest time and tooltip wait are in **Settings… › Overlay & Card**. Untick auto-read there to use the shortcut only.
- **Shortcut:** with a card showing, press it to expand/collapse the full list. With no card showing, it does a manual lookup: hover an item, wait for Tarkov's name tooltip, then press the shortcut.
  - The card goes away when you move the mouse or after 7 seconds.
  - It never takes focus and clicks pass through it.
- **Expand the card:** the card shows up to 2 rows per section, with "+N more". Press the shortcut **again** without moving the mouse to expand it. The expanded card lists every quest first (with how many are needed and FIR), then crafts and barters, then hideout. Press once more to collapse.
  - This reuses the last lookup, so no new screen reading happens.
  - Very long lists switch to two columns and shrink to fit the screen.
  - Moving the mouse still hides the card. An expanded card otherwise stays up for 15 s.
  - Tick **Always show the full list** in Settings… to get the expanded card on the first press.
- **Card size** (Settings…): 1×, 2× (default) or 3×. The card shrinks automatically if it would not fit on the monitor.
- If the name cannot be read reliably, the card says so and offers possible matches instead of guessing. Pick the right item in the main window's results list.
- **Main window:**
  - Search by name or short name.
  - Full detail card with all quests, hideout levels, crafts and barters.
  - **Track** quest and hideout needs; tracked items are flagged KEEP on the hover card.
  - 7-day flea history, a pop-out floating card, and **Open saved image…** to read an item name from a screenshot.
- Catalog data refreshes every 20 minutes and is cached in `%LOCALAPPDATA%\TarkovPriceOverlay`, so the app also works offline with older prices (clearly labelled).

## Map and quests (1.1)

The map lives in the main window, under the settings in the first column, and grows with the window (drag the
divider to the right of it to make it wider). Nothing is drawn over the game.

- **Which map**: the app reads Tarkov's own log files (`<game folder>\Logs\log_…\*application.log` and
  `*notifications.log`) and switches to the raid's map as soon as Tarkov loads it (**Auto-follow**). It also sees
  raid start/end, PMC vs Scav in PvP raids (PMC raids have a countdown) and PvE vs PvP. The game folder is found automatically
  (BSG launcher settings, registry, Steam, running game); if not, set it in **More ▾ › Map settings…** (= Settings… › Map & Screenshots).
- **Exits**: green = PMC, yellow = Scav, blue = both, purple = co-op, cyan diamond = transit. A white dot means
  there's a requirement: hover the exit for it (paid, item, flare, switch, paracord…). With the side on Auto, every
  exit is shown in menus and in PvE, and only your side's during a PvP raid (or pick PMC/Scav under **More ▾**); the
  status line says "all exits" / "PMC exits" / "Scav exits". Experimental: **Map settings › read the exit list when I
  press O** dims the exits that aren't on your list this raid.
- **Quests**: only the quests you pick are drawn; nothing picked = no quest pins. Quests you start in the game (or
  mark active) are picked automatically and drop off when done. Tick/untick them under **Quests on this map**
  (with **Deselect all** / **Only my active quests**) or in **Quests…** (Map column, **Deselect all**,
  **Select shown** / **Deselect shown** for the filtered list, and a **<map> only** filter). Filled square = the
  spot, hollow = one of several possible spots, ring = not accepted yet. Hover for the objective. To look at every
  unlocked quest on a map, turn on **More ▾ › Browse: also show every available quest** (off by default).
- **Quest progress**: built in, no account needed. While the overlay runs it reads the "quest started / completed /
  failed" messages Tarkov writes to its log, so it keeps itself up to date. First time: open **Quests…**, select the
  quest you're currently on in each trader line and click **I'm on this quest** (marks everything before it done),
  and/or **Import from game logs**. Progress is kept per mode (PvP/PvE). TarkovTracker sync is optional and read-only.
- **Your position**: press **PrtScn** in raid. Tarkov names each screenshot with your position and facing, and the
  app puts a red arrow there with the time it was taken. Screenshots are read from
  `Documents\Escape from Tarkov\Screenshots` (configurable). Your last position is saved
  (`maps\last-position.json`), so the dot survives a restart for 3 h even without the file.
- **Screenshot cleanup** (**Map settings › Position screenshot cleanup**): **Keep newest 5** (default), **Delete right
  after reading**, **Move to Recycle Bin** (keeps the newest 5) or **Off (keep all)**. Runs on each new position
  screenshot and once at start-up. It only ever touches files directly in that folder whose name is a Tarkov
  position screenshot (`2026-11-23[14-05]_x, y, z_…png/.jpg`); other screenshots, other files and sub-folders are
  never touched.
- **No dot?** **More ▾ › Test position from latest screenshot** re-reads the newest file in the screenshots folder
  and says what happened (folder used, file name, position read, drawn or why not). Common causes: Tarkov has no
  screenshot key bound or it's bound as "SysReq" (the status line warns; rebind "Screenshot" in Tarkov's
  Settings › Controls); Windows 11's "Use the Print screen key to open screen capture" setting grabbing PrtScn
  (turn it off under Settings › Accessibility › Keyboard, or bind Tarkov's screenshot to another key); Documents
  moved to OneDrive or another drive (found automatically; since 1.1.2 also `X:\Documents\...` on any fixed drive, e.g. `D:\Documents`; or set the folder in Map settings).
  Every step is logged in `overlay.log` (lines starting `map:`).
- **Pop out**: the map opens in its own normal window (remembers its monitor and size). **F11** or **Fullscreen**
  there = borderless fullscreen on that monitor, **Esc** to leave. **Back to main window** returns it.
- **Satellite / Vector**: toolbar button or **Ctrl+Alt+M** (changeable in Settings › Map & Screenshots). Pins, exits
  and your dot use the same projection in both, so they sit on the same spots. A floor that exists in only one style
  (e.g. Interchange floors: vector only; Customs 4th floor: satellite only) is shown in that style.
- Mouse: drag to pan, wheel to zoom, double-click to fit.
- Map data and drawings come from tarkov.dev and are cached in `%LOCALAPPDATA%\TarkovPriceOverlay\maps`
  (first open of a map downloads 0.3–2 MB for the vector drawing, 5–25 MB of satellite tiles; later opens and
  switching styles are instant). Map © Tarkov.dev / Shebuka (CC BY-NC-SA 4.0), Icebreaker by TarkovBOT.eu. `TarkovPriceOverlay.exe --map-selftest [folder]` renders every map with
  its exits and quest spots to PNGs as a check.

## Reading the card

- **Stacks** (ammo, money and other stackable items): the app also reads the small count in the bottom-right corner of the hovered cell. This happens only after the name is verified, so the count can never change which item is shown.
  - **This stack · N**: what the pile you're hovering is worth. The value is the better of trader total and fresh flea net total, and the ▼●▲★ rating uses it, since a stack fills one cell. The small text shows the trader and flea-net totals and the price each.
  - **Full stack · M**: what a full stack would sell for.
  - The Flea, Trader and Buy rows are then marked "· each".
  - If the count can't be read safely (hidden by the name tooltip, unclear, or more than a full stack), the card keeps the per-unit prices and says "Stack count not read". It never guesses.
  - **Tip:** if the card says the count is hidden by the tooltip, hover nearer the item's **bottom-right corner** (on or just left of the count) so the name tooltip opens outside the cell.
  - With auto-read on, the count is taken from a frame grabbed while the mouse rests, before Tarkov's tooltip appears, so the tooltip can't hide it. This works in the trader screen too, where the tooltip opens right at the pointer and usually covers the count.

- **Trader screen** (selling): the tooltip there is a little lighter, opens at the pointer tip (right, or above-right), and has a second line. That line is a gold offer ("₽ 35 118") or a red "The trader cannot buy this item."
  - Only the first line is used as the item name.
  - When the gold line reads cleanly as a rouble amount, the card's first row is **This trader offers ₽35,118**. It is left out if the read isn't clean or the OCR engines disagree.
  - Price tags and check marks on the items are ignored.

- **Same-name items** (tarkov.dev lists some items several times under one name):
  - **Dogtags** (BEAR, USEC, Marked) get one card. It shows the flea and trader **range** across the tag variants, the **base tag** value, and "value depends on level". The hover tooltip only says "Dogtag BEAR", and the level appears only in Tarkov's inspect window, so the exact variant can't be picked from a hover.
  - Other same-name entries also get one card, with a range, when they're all priced within 10% of each other or none has a price (e.g. "Sealed weapon case").
  - Otherwise the card asks you to pick in the main window, and each line shows what differs: variant #, flea and trader price.
  - Prices come from the catalog for the **game mode** chosen in the main window (PvP by default), so PvE entries never mix into PvP lookups, or the other way round.

- **Flea**: Tarkov.dev's recent average flea price. It is marked stale if the last scan is over 2 hours old, and shows a level requirement when one applies.
- **Flea net**: what you would receive after the estimated listing fee, if you listed at that price.
- **Trader**: best trader cash payout.
- **Buy**: cheapest cash trader offer. "≈" means an approximate flea low.
- **Per slot**: the better of trader payout and fresh flea net, divided by the item's footprint in slots.
  - ▼ Low · ● Decent · ▲ Good · ★ Premium
  - Trader thresholds: 5k / 10k / 15k ₽ per slot
  - Flea net thresholds: 15k / 30k / 75k ₽ per slot
- **Status**: KEEP (tracked need) · FLEA IF ELIGIBLE · TRADER · CHECK.

## Troubleshooting

- **Short item names (Chainlet, Bolts, Awl)**: Tarkov's tooltip for these is small and often sits above-right of the pointer. Since 1.0.7 tooltips down to ~28 px wide (100% scale) are recognised; older versions needed 64 px and showed no card.
- **Closing the app**: closing the main window quits the app completely (there is no tray icon). Since 1.0.6 no `TarkovPriceOverlay` process stays in Task Manager; the shutdown steps are written to `overlay.log` (lines starting `shutdown:`). If any part hangs, the app force-exits 3 s after the window closes.
- **Lookup works (main window updates) but no card in game**: switch Tarkov's screen mode to **Borderless** (Settings › Graphics › Screen mode). Exclusive Fullscreen hides every overlay window.
  - **Test card (in 3 s)** (Settings… › Troubleshooting) shows the selected item's card beside your cursor 3 s after you click it and keeps it up for 5 s, even if you move. Click it, switch to Tarkov, and hold still.
  - **Hide the card when the mouse moves** and **Mouse may drift N px** (Settings…, pixels at 100% scale, default 6) control how easily the card hides. There is also a 250 ms grace period after it appears. Since 1.1.4 the same tolerance applies while an item is still being read (it used to be a fixed 2 px, so slight hand jitter during a slow read could drop the card).
  - The log at `%LOCALAPPDATA%\TarkovPriceOverlay\logs\overlay.log` records each lookup and each card show/hide: bounds in physical pixels and DIPs, monitor and DPI, the foreground window, and why the card hid. Use **Settings… › Troubleshooting › Open log folder** to find it. Since 1.1.4 a hover that finds no tooltip logs why (the closest dark boxes and why each was rejected). It rotates at 1 MB and keeps 3 old files.

- **"Item tooltip not visible"**: wait for the name tooltip to appear first. Keep the cursor still until the card shows.
- **No card at all**: check the shortcut status in the main window. Another app may own the key, in which case a fallback is used.
- **Wrong or unknown item**: open the main window's "Last lookup" panel to see what was read.
  - "Export last capture…" saves the title crop and a text report, but only when you click it. Nothing is saved automatically.
  - `TarkovPriceOverlay.exe --diagnose shot.png --cursor 1200,640 [--mode pve] [--scale 1.25]` runs the same pipeline on a full-monitor screenshot and writes `shot.png.diag.txt`.
- Errors are logged to `%LOCALAPPDATA%\TarkovPriceOverlay\error.log`.
- **Update now failed**: the strip says why, and nothing was changed (a failed install is rolled back). Common causes:
  GitHub rate limits (try later), the install folder needs admin rights (move it out of Program Files), or antivirus
  holding a file. The replaced files of the last update are kept in `%LOCALAPPDATA%\TarkovPriceOverlay\update\backup`.
  You can always download the zip from the release page by hand.

## Building from source

You only need this to change the code; to just use the app, download the release zip above.

- **SDK:** .NET SDK **8.0.428**, pinned in `global.json` (`rollForward: disable`, so install exactly that version:
  https://dotnet.microsoft.com/download/dotnet/8.0). The app targets Windows x64 (WPF); the core libraries and tests
  also build and run on Linux/macOS with `-p:EnableWindowsTargeting=true`.
- **Tests:**
  ```
  dotnet test tests/TarkovOverlay.Tests -c Release
  dotnet test tests/TarkovOverlay.Maps.Tests -c Release
  ```
- **Release build** (the same commands that make the release zip):
  ```
  dotnet publish src/TarkovOverlay.App -c Release -r win-x64 --self-contained true  -o publish/self-contained
  dotnet publish src/TarkovOverlay.App -c Release -r win-x64 --self-contained false -o publish/framework-dependent
  ```
  Publishing also builds `TarkovPriceOverlay.Updater.exe` (one self-contained file used by Update now) next to the
  app. Output lands in `publish/self-contained/` and `publish/framework-dependent/` (each has `TarkovPriceOverlay.exe`,
  `tessdata\`, `licenses\`, `README.md`, `CONTROLS.txt`, `THIRD-PARTY-NOTICES.md`, `LICENSE.txt`). The release zip is
  those two folders inside a `TarkovPriceOverlay\` folder, plus `README.md`, `CONTROLS.txt`,
  `THIRD-PARTY-NOTICES.md` and `LICENSE.txt`. Builds are deterministic and don't embed your local folder paths.
- Releases attach `TarkovPriceOverlay-win-x64.zip`, `TarkovPriceOverlay-update-<version>.zip` and `SHA256SUMS.txt`
  (`sha256sum` of both zips); Update now uses those names.
- **Testing Update now locally:** `python3 tools/fake_github_release.py <folder with release files> v9.9.9` serves a
  fake latest release on 127.0.0.1:8765; start the app with `TPO_UPDATE_URL=http://127.0.0.1:8765/api/latest`.
  That variable is for testing only (never set by default); while it is set, downloads from that local address are
  allowed too, and overlay.log says "TEST MODE".
- `publish/`, `bin/` and `obj/` are ignored by git.

## License

Tarkov Price Overlay is free software: you can redistribute it and/or modify it under the terms of the GNU General
Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option)
any later version (GPL-3.0-or-later). It is distributed WITHOUT ANY WARRANTY. See [LICENSE](LICENSE).

Copyright (c) 2026 devmaildumpster-ux

Third-party components keep their own licenses (Apache-2.0, BSD-style, MIT); see THIRD-PARTY-NOTICES.md and the
`licenses\` folder next to the executable. All of them are compatible with GPL-3.0.
