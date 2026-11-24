Tarkov Price Overlay 1.2.8 (applies over 1.0.0 – 1.2.7, self-contained build)

INSTALL (update) - this one time by hand
Versions up to 1.2.7 only have the old Download button, so 1.2.8 has to be copied in by hand once.
From 1.2.8 on, the Update now button installs new versions for you.
1. Close Tarkov Price Overlay (if "Minimize to tray" is on: right-click the tray icon › Quit).
2. Copy every file and folder in TarkovPriceOverlay-update-1.2.8.zip (including "licenses" and the new
   TarkovPriceOverlay.Updater.exe) into
   <your install folder>\TarkovPriceOverlay\self-contained
   and choose "Replace the files in the destination". Copy everything, not just the exe.
3. Start TarkovPriceOverlay.exe (overlay.log / smoke test says 1.2.8.0).
   Settings, tracked items, quest progress and map settings are kept.
New users: download TarkovPriceOverlay-win-x64.zip and run TarkovPriceOverlay\self-contained\TarkovPriceOverlay.exe.
Framework-dependent users: download the full zip and replace your framework-dependent folder.

NEW: UPDATE NOW (THE APP UPDATES ITSELF)
- The "Update available" strip has Update now, Release page (the old Download: opens the release in your
  browser), Skip this version and ×. Settings… › Troubleshooting › Updates has Update now and Cancel too.
- Update now:
  1. asks GitHub for this repo's latest release and picks the file: the small update zip for self-contained
     installs, the full TarkovPriceOverlay-win-x64.zip for framework-dependent ones (or if there's no update zip);
  2. downloads it with a progress bar; Cancel stops it and nothing changes. Downloads are only accepted over HTTPS
     from github.com (this repo's release files) and GitHub's download servers;
  3. checks the size and SHA-256 against GitHub's checksum for the file, and against SHA256SUMS.txt when the
     release has one. A file without any checksum, or one that doesn't match, is deleted and not installed;
  4. unpacks it to %LOCALAPPDATA%\TarkovPriceOverlay\update\staging, refusing zips with unexpected layout or
     file names that would escape the folder, and checks the files inside are the release's version;
  5. closes the overlay and starts TarkovPriceOverlay.Updater.exe (copied to the update folder so nothing in your
     install is locked). It waits for the overlay to exit, backs up every file it replaces to
     %LOCALAPPDATA%\TarkovPriceOverlay\update\backup, copies the new files in, and if anything fails puts all of
     them back, then restarts the overlay;
  6. after the restart the strip says "Updated to X.Y.Z" (or why it didn't install; you keep the old version).
- Never touches settings, caches, logs or quest progress (they live in %LOCALAPPDATA%, not the install folder).
- Never asks for admin rights. If the install folder needs them (for example under Program Files) you get a
  clear message: move the TarkovPriceOverlay folder somewhere you own, or update by hand.
- Every step is written to overlay.log (lines starting "update:" and "updater:").
- TarkovPriceOverlay.exe --self-update does the same from a terminal.
- Releases now also attach SHA256SUMS.txt.

ALSO
- Includes everything from 1.0.1-1.2.7 (exit list fix, update check on launch, outage handling, …).
