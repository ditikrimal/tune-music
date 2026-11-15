# Tune — native YouTube Music player for Windows (design)

Date: 2026-11-15 · Status: draft for approval · Working name "Tune" (rename freely)

## Intent and the why

The user listens to music on their laptop by opening YouTube Music in Edge, which costs 1 GB+ of RAM
and noticeable battery. The laptop (ROG Strix G513QC, 16 GB single-channel, also hosts a website
24/7) is regularly near its memory limit. They want the desktop equivalent of ReVanced YouTube on
their phone: ad-free, plays in the background, looks beautiful (Spotify-like), but **native and
light** — they explicitly rejected Electron apps (th-ch YouTube Music etc.) because of power/RAM.

How they listen: mostly click whatever is on the front feed, search for a few songs, and play from
playlists. No Google login wanted. Downloads not needed.

## Goals

1. **Home feed without login**, personalised locally:
   - Rows built from local play history: "Because you played <artist/song>" (YT Music radio /
     related for songs they played), "Your top artists" mixes, "Recently played".
   - Below those: YT Music's anonymous discovery rows (trending / charts / new releases for the
     user's region — Nepal).
   - Day one (empty history): only the discovery rows; becomes personal after some listening.
2. **Search** songs, artists, albums, playlists (YT Music search, no login). Artist/album/playlist
   pages playable.
3. **Queue & autoplay** (user confirmed): a song started from a list (local playlist, Liked,
   album, artist top songs, YT playlist) queues the rest of that list from that song on; when the
   list ends, YT Music radio for the last song continues. A song started from Home cards or Search
   starts its radio immediately. **Shuffle** (shuffles the list part of the queue) and **Repeat**
   (off / all / one) buttons in the player bar. Queue is visible and editable (play next / remove /
   reorder).
4. **Local playlists** created in the app (create, rename, delete, add/remove/reorder songs) and
   **Liked songs** (♡). Stored only on the laptop. No import of YT account playlists.
5. **Main window — layout A "Classic Spotify"** (approved mockup): dark theme; left sidebar
   (logo, Home, Search, Liked, "Your playlists" list); main area with greeting and horizontal rows
   of square art cards; bottom player bar (art, title/artist, ♡, shuffle, prev/play/next, repeat,
   seek bar with times, queue button, volume). Must look good maximized (rows show more cards, layout reflows).
6. **Floating mini-player — "Slim bar"** (approved mockup): ~300×64 rounded, translucent dark
   strip; album art, title, artist, progress bar, prev/play/next; ⤢ opens main window, ✕ hides the
   bar. Always on top, draggable anywhere, remembers its position across restarts.
7. **Background / tray**: closing the main window keeps music playing; app lives in the system
   tray (menu: Show, Mini-player on/off, Play/Pause, Next, Quit).
8. **Windows integration**: hardware media keys and the Windows media flyout (SMTC) show the song
   and control playback.
9. **Light**: soft target ≤ 120 MB working set for the app process while playing (measured both
   with the main window maximised on Home and with only the mini-player showing), near-zero CPU
   when idle/minimised. Not a hard pass/fail — measure and report actual numbers, including the
   short-lived yt-dlp/JS-runtime processes separately. *(Builder-check answer, my call.)* Keep
   those helper processes rare: resolve on play + one look-ahead for the next song, never more.
11. **SponsorBlock** (user confirmed, like ReVanced on their phone): auto-skip the
    `music_offtopic` category (non-music intros/skits/outros). On by default with an off switch in
    Settings. This sends the video ID of each played song to sponsor.ajay.app — the one accepted
    exception to the privacy anti-goal. Fail soft: if the API is slow/down, just play normally.
10. **Self-healing playback**: bundled yt-dlp is auto-updated (on startup at most once/day, and
    once on a playback failure before showing an error).

## Non-goals

- Google account login, syncing with the user's YT Music library, importing playlists.
- Downloads / offline.
- Video playback (audio only). Lyrics, podcasts, equalizer — not now.
- Cross-platform; Windows 10/11 x64 only.
- Distribution to others / installer polish (personal app; a portable folder + Start-menu
  shortcut is enough).

## Anti-goals (failure even if it "works")

- Using a browser engine anywhere (WebView2/CEF/Electron) — defeats the purpose.
- Ads reaching the user (audio streams via yt-dlp carry none; never fall back to an ad-bearing
  path).
- Sending anything about the user (history, playlists) anywhere other than the YouTube requests
  needed to play/search, plus SponsorBlock video-ID lookups (goal 11, user-approved, can be off).
- Sitting at hundreds of MB or burning CPU while minimised.
- Ugly / default-looking UI — the user specifically asked for beautiful.

## Constraints

- Laptop is RAM-constrained and always on; app must not destabilise the site it hosts.
- No .NET SDK installed yet → install .NET SDK via winget (`Microsoft.DotNet.SDK.10`).
  ffmpeg and git are present; a user-level yt-dlp (2026.07.04, Python) exists but the app ships its
  own `yt-dlp.exe` so it doesn't depend on the user's Python.
- YouTube changes its internals periodically; parsing code must fail soft (skip a broken row,
  not crash the app).
- User is in Nepal (region/hl params for discovery rows; Nepali + English titles must render).

## Approaches considered

| | Approach | Trade-off |
|---|---|---|
| **A (chosen)** | **C# / .NET 10 / WPF**, audio via **libmpv**, stream resolution via bundled **yt-dlp.exe**, YT Music data via own InnerTube client | Native, full control of a custom dark UI, ~60–120 MB. We own the YT Music data-parsing code. |
| B | Rust + Slint/egui | Leanest (~30–60 MB) but polished UI is much harder and slower to build. |
| C | Python + Qt (PySide6) + ytmusicapi | Fastest to build, reuses ytmusicapi, but ~150–250 MB with Python resident. Works against the goal. |

User picked **A**.

## Decided parts of the how

- **UI**: WPF on .NET 10 with a hand-built dark theme (Spotify-like: near-black #121212 base,
  #000 sidebar, green accent, Segoe UI Variable). No third-party UI framework required.
- **Audio**: libmpv (`libmpv-2.dll`) hosted in-process, audio only (`vid=no`), fed a direct audio
  stream URL. mpv chosen because it handles YouTube's opus/m4a streams robustly and is light.
- **JS runtime**: current yt-dlp needs an external JavaScript runtime to resolve YouTube streams;
  bundle a pinned `deno.exe` next to `yt-dlp.exe` and point yt-dlp at it.
- **Stream URLs**: bundled `yt-dlp.exe` resolves video ID → best audio URL (`-f bestaudio -g`).
  URLs expire (~6 h); resolve at play time, cache per session, pre-resolve the next queue item.
- **Catalogue data**: own small client for YouTube Music's InnerTube endpoints (`browse` for
  home/charts/artist/album/playlist, `search`, `next` for radio/watch-next), modelled on ytmusicapi's
  parsing. Anonymous (no cookies).
- **Local data**: SQLite file in `%LOCALAPPDATA%\Tune\` (history, likes, playlists, settings,
  window/mini-player positions). Album art cached on disk (size-capped).
- **Window chrome**: custom title bar to match the dark theme; mini-player is a separate borderless
  topmost window.

## Left to the builder

- Project structure, MVVM approach, libraries (e.g. CommunityToolkit.Mvvm, Microsoft.Data.Sqlite,
  H.NotifyIcon or WinForms NotifyIcon, mpv binding style).
- Exact personalised-feed recipe (how many rows, ranking, de-duplication), within the goals above.
- How libmpv / yt-dlp binaries are obtained and bundled (pinned versions, checksums).
- Animations, icons, exact spacing — keep consistent with the approved mockups.
- Test strategy (InnerTube parsing should be tested against saved sample responses).
