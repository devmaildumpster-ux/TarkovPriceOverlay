Tarkov Price Overlay 1.2.6 (applies over 1.0.0 – 1.2.5, self-contained build)

The first public release on GitHub: https://github.com/devmaildumpster-ux/TarkovPriceOverlay/releases/latest

INSTALL (new users)
1. Download TarkovPriceOverlay-win-x64.zip from the Releases page and unzip it anywhere.
2. Run TarkovPriceOverlay\self-contained\TarkovPriceOverlay.exe (no .NET install needed).
   Windows SmartScreen may warn because the exe is unsigned: More info › Run anyway.

UPDATE (existing users, 1.0.0 – 1.2.5)
1. Close Tarkov Price Overlay (if "Minimize to tray" is on: right-click the tray icon › Quit).
2. Download TarkovPriceOverlay-update-1.2.6.zip and copy every file and folder in it (including "licenses") into
   <your install folder>\TarkovPriceOverlay\self-contained
   and choose "Replace the files in the destination". Copy everything, not just the exe.
3. Start TarkovPriceOverlay.exe (overlay.log / smoke test says 1.2.6.0).
   Settings, tracked items, quest progress and map settings are kept.
   Using the framework-dependent folder instead? Download the full zip; the update zip is for self-contained.

NEW: UPDATE CHECK ON LAUNCH
- A few seconds after the app starts it asks GitHub once for the latest release (in the background, 5 s timeout).
  If a newer version exists, a green strip appears under the top bar:
    "Update available: 1.2.7 (you have 1.2.6)   [Download] [Skip this version] [×]"
  - Download opens the release page in your browser. Nothing is downloaded or installed by itself.
  - Skip this version hides that version for good; a later version shows again.
  - × hides the strip until the next launch.
- Gentle on GitHub: at most one check per launch, and the answer is cached for 6 hours (update-check.json in
  %LOCALAPPDATA%\TarkovPriceOverlay), so restarting the app doesn't ask again.
- Silent when it can't check: offline, timeout, GitHub rate limit (403/429; waits 6 h before asking again),
  or anything unexpected. It only writes a line to overlay.log. Drafts and pre-releases are ignored.
- Never takes focus from the game; the strip only appears in the main window.
- Settings… › Troubleshooting › Updates: "Check for updates on launch" (on by default), Check now,
  Open releases page, Show skipped version again, and what the last check said.
- No account, token or personal data is sent: one plain request to api.github.com with the app's version in
  the User-Agent.

OVERLAY.LOG
  update check: UpToDate · this 1.2.6 · latest 1.2.6 · latest release v1.2.6
  update check: Available · this 1.2.6 · latest 1.2.7 · latest release v1.2.7
  update check: Failed · this 1.2.6 · latest ? · offline or unreachable (HttpRequestException: …)

ALSO
- Licensed GPL-3.0-or-later (LICENSE.txt next to the exe); third-party components keep their own licenses
  (THIRD-PARTY-NOTICES.md, licenses\).
- Builds no longer contain the build machine's folder paths (deterministic builds).
- README: install from the Releases page; the repository is source code only. New "Building from source" section.
- New command-line check: TarkovPriceOverlay.exe --update-check prints what the update check sees.
- Includes everything from 1.0.1-1.2.5 (outage handling, fewer "possible match" cards, controls window,
  auto floor switching, satellite map, …).
