using Tune.App.Platform;
using Tune.Core.Feed;
using Tune.Core.Library;
using Tune.Core.Playback;
using Tune.Core.Streams;

namespace Tune.App;

/// <summary>Everything the UI needs, created once in <see cref="App.OnStartup"/>.</summary>
public sealed record AppServices(
    LibraryStore Library,
    IYtMusic Api,
    PlaybackService Playback,
    FeedBuilder Feed,
    ToolManager Tools,
    ArtCache Art,
    IClock Clock,
    string DataDir,
    StreamPrefetcher Prefetcher,
    Tune.Core.Lyrics.LyricsService Lyrics,
    Tune.Core.Offline.DownloadManager Downloads,
    Tune.Core.Offline.IConnectivity Network,
    Tune.Core.Offline.AudioCache Cache,
    Tune.Core.Offline.OfflineLibrary Offline,
    Tune.Core.Offline.SmartDownloads Smart,
    AppUpdater Updater);
