# Tune

An ad-free YouTube Music player for **Windows** and **Android**: no account, no ads, light on battery.

- Home feed from what you play, search, albums, artists, playlists, radio
- Liked songs and your own playlists, plus **import any public YouTube Music playlist** (Settings → Import)
- **Downloads and offline mode**: when the internet drops, Home switches to your downloads
- Synced lyrics, queue with drag-to-reorder, sleep timer, your own accent colour
- Windows: floating mini-player, media keys, tray. Android: background playback, lock-screen and headset controls

## Download

Get the latest files from the **[Releases](../../releases/latest)** page.

**Windows** (10/11, 64-bit): download `Tune.exe` and run it. Nothing else to install.
The first start downloads two small helpers (yt-dlp and deno) into `%LOCALAPPDATA%\Tune`.
Windows SmartScreen may warn because the app isn't signed: choose *More info → Run anyway*.

**Android** (9 or newer): download `Tune.apk` on your phone and open it. Allow *Install unknown apps* for your
browser or file manager when asked.

Your likes, playlists and downloads stay on your device. Tune uses YouTube Music's public pages anonymously.

## Build

- Windows: .NET 10 SDK, `tools\fetch-libmpv.ps1`, then `tools\publish.ps1` (local install) or `tools\release.ps1` (single exe).
- Android (`android/`): JDK 21 and the Android SDK, then `./gradlew assembleRelease`.
