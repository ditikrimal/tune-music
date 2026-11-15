# Tune — memory & CPU report

Measured 2026-11-15 on the user's ROG Strix G513QC (Ryzen 7 5800H, 16 GB, RTX 3050 + Radeon iGPU),
Release build published to `D:\Apps\Tune` (framework-dependent, ReadyToRun), driven by UI automation.
Song: "Dhairya" (Sajjan Raj Vaidya), opus 251 stream.

"Task Manager" = the *Memory* column (private working set). "Private" = private bytes (committed, incl. paged out).
"WS" = full working set, which also counts shared read-only DLL pages (mostly the 124 MB libmpv image) that
Windows can drop under memory pressure.

| Scenario | Task Manager | Private | WS | CPU (whole machine) |
|---|---:|---:|---:|---:|
| Idle, Home shown 1280×800 | 75 MB | 119 MB | 195 MB | — |
| (a) Playing, Home maximized | 105 MB | 149 MB | 249 MB | 0.08 % (30 s) |
| (b) Playing, main window closed, mini-player only | 120 MB | 165 MB | 266 MB | 0.12 % (30 s) |
| (c) Paused, mini-player only, after 5 min | 103 MB | 148 MB | 249 MB | 0.00 % (120 s) |

Short-lived helpers per song start (one resolve on click, one look-ahead for the next song):

| Process | Peak | Lifetime |
|---|---:|---|
| yt-dlp.exe | 94 MB | ~5 s |
| deno.exe (JS runtime yt-dlp needs for YouTube) | 277 MB | ~3 s |

Click → audio: **~10 s** for the first song (≈5 s yt-dlp resolve + YouTube's `available_at` lock, see below).
Following songs start immediately because the look-ahead resolved them 10 s into the previous one.

## Target

Spec target: ≤ 120 MB for the app process while playing (soft). Met as Task Manager shows it (105–120 MB);
private bytes are 150–165 MB.

## What was found and changed

Before the fix the same scenarios measured **~260 MB private / ~310 MB WS**. Breakdown by experiment:

| Component | Private | How measured |
|---|---:|---|
| .NET managed heap | 15–30 MB | in-app `GC.GetTotalMemory` / `TotalCommittedBytes` log |
| libmpv streaming audio | ~35 MB | bare console: load DLL → init → stream the real URL |
| Empty WPF window, **hardware** rendering | ~101 MB | bare WPF app |
| Empty WPF window, **software** rendering | ~51 MB | same app, `RenderMode.SoftwareOnly` |
| 40 cards with DropShadowEffect, hardware | +17–34 MB | same app |
| 40 cards with DropShadowEffect, software | ≈ 0 | same app |

The Direct3D device WPF opens for hardware rendering costs ~50 MB of graphics-driver memory on this
hybrid-GPU laptop. Tune now sets `RenderOptions.ProcessRenderMode = SoftwareOnly` (App.OnStartup):
private memory −100 MB, idle CPU unchanged (0 %), and the dedicated GPU is never woken for the UI.

Already in place: art decoded at display size and held only through weak references; mpv demuxer cache
capped at 8 MiB + 2 MiB back-buffer; workstation non-concurrent GC; position updates stop while no window
shows them.

## Not done (possible later)

- deno's 277 MB peak is yt-dlp's requirement for YouTube's JS challenges; it lives ~3 s per resolve.
- Trimming the working set when hidden (`EmptyWorkingSet`) would only move pages to the standby list — cosmetic.
