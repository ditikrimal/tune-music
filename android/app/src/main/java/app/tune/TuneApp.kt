package app.tune

import android.app.Application
import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import app.tune.data.Downloads
import app.tune.data.FeedBuilder
import app.tune.data.InnerTube
import app.tune.data.Library
import app.tune.data.LyricsService
import app.tune.data.SmartDownloads
import app.tune.data.StreamCache
import app.tune.data.NewPipeDownloader
import app.tune.data.StreamResolver
import app.tune.data.YtMusic
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import okhttp3.OkHttpClient
import org.schabi.newpipe.extractor.NewPipe
import org.schabi.newpipe.extractor.localization.ContentCountry
import org.schabi.newpipe.extractor.localization.Localization
import java.util.concurrent.TimeUnit

/** Everything the app needs, created once. */
class Graph(context: Context) {
    val http: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS).readTimeout(30, TimeUnit.SECONDS)
        .build()
    val streamUserAgent = NewPipeDownloader.USER_AGENT
    val library = Library(context)
    val yt = YtMusic(InnerTube(http))
    val streams = StreamResolver()
    val lyrics = LyricsService(http, yt) { library.setting("lyrics.synced") != "0" }
    /** Recommendations: YouTube Music's per-song radios, vibe chips and Related, cached, steered by what you listen to. */
    val vibe = app.tune.data.VibeEngine(library, app.tune.data.Signals(library, yt))
    val feed = FeedBuilder(library, yt, vibe)
    val moods = app.tune.data.MoodsCatalog(library, yt)

    private val _online = MutableStateFlow(true)
    val online: StateFlow<Boolean> = _online
    /** Test hook: pretend there's no internet (set by [TestOfflineReceiver] from a root shell only). */
    @Volatile var forceOffline = false
        set(v) { field = v; _online.value = !v && networkValidated }
    @Volatile private var networkValidated = true
    /** On Wi-Fi (or another unmetered network): smart downloads only run then. */
    private val _unmetered = MutableStateFlow(false)
    val unmetered: StateFlow<Boolean> = _unmetered
    val streamCache = StreamCache(context)
    val downloads = Downloads(context, library, streams, http, online, unmetered) { id, target -> streamCache.copyTo(id, target) }
    val smart = SmartDownloads(library, yt, downloads)
    val updater = app.tune.data.Updater(context, http, library)
    /** Tune account (Continue with Google): likes, playlists and history synced. Null if the build has no Firebase project. */
    val account: app.tune.data.AccountSync? = app.tune.data.SyncConfig.of(BuildConfig.FIREBASE_PROJECT, BuildConfig.FIREBASE_API_KEY,
        BuildConfig.FIREBASE_WEB_CLIENT, BuildConfig.SYNC_EMULATOR)?.let { app.tune.data.AccountSync(library, http, it) }
    init { downloads.onNotice = { toast(it) } }

    /** Playable without internet: downloaded, or fully in the streaming cache. */
    fun isAvailableOffline(videoId: String) = downloads.localFile(videoId) != null || streamCache.isFullyCached(videoId)

    /** Songs Tune knows about that can play offline (downloads + cached songs from history and likes). */
    fun offlineTracks(): List<app.tune.data.Track> {
        val cached = streamCache.cachedIds()
        val known = (downloads.downloaded() + library.liked() + library.recent(400)).distinctBy { it.videoId }
        return known.filter { downloads.localFile(it.videoId) != null || it.videoId in cached }
    }

    /** Short messages for the toast at the bottom of the screen. */
    val toasts = MutableSharedFlow<String>(extraBufferCapacity = 8)
    fun toast(message: String) { toasts.tryEmit(message) }

    init {
        NewPipe.init(NewPipeDownloader(http), Localization("en", "US"), ContentCountry("NP"))
        val cm = context.getSystemService(ConnectivityManager::class.java)
        cm.getNetworkCapabilities(cm.activeNetwork).let {
            networkValidated = it?.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) == true
            _online.value = networkValidated && !forceOffline
            _unmetered.value = it?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) == true
        }
        cm.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
                networkValidated = caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
                _online.value = networkValidated && !forceOffline
                _unmetered.value = caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)
            }
            override fun onLost(network: Network) { networkValidated = false; _online.value = false; _unmetered.value = false }
        })
        downloads.start()
    }
}

class TuneApp : Application() {
    lateinit var graph: Graph
        private set

    override fun onCreate() {
        super.onCreate()
        graph = Graph(this)
        SmartRefreshWorker.schedule(this)
        graph.account?.start()
        // likes (from the app, the widget or sync) and the accent colour show on the home-screen widget
        @OptIn(kotlinx.coroutines.FlowPreview::class)
        kotlinx.coroutines.MainScope().launch { graph.library.version.debounce(300).collect { app.tune.widget.TuneWidgets.render(this@TuneApp) } }
    }
}

/**
 * Refreshes smart downloads in the background, like YouTube Music: about twice a day, only on Wi-Fi and when the
 * battery isn't low, even if Tune isn't open. The downloads themselves then run (also on Wi-Fi only).
 */
class SmartRefreshWorker(context: Context, params: androidx.work.WorkerParameters) : androidx.work.CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val graph = (applicationContext as TuneApp).graph
        return runCatching { kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { graph.smart.refresh() }; Result.success() }
            .getOrElse { Result.retry() }
    }

    companion object {
        fun schedule(context: Context) {
            val constraints = androidx.work.Constraints.Builder()
                .setRequiredNetworkType(androidx.work.NetworkType.UNMETERED)
                .setRequiresBatteryNotLow(true)
                .build()
            val req = androidx.work.PeriodicWorkRequestBuilder<SmartRefreshWorker>(12, java.util.concurrent.TimeUnit.HOURS)
                .setConstraints(constraints).build()
            androidx.work.WorkManager.getInstance(context)
                .enqueueUniquePeriodicWork("smart-downloads", androidx.work.ExistingPeriodicWorkPolicy.KEEP, req)
        }

        /** "Update now" in Settings, or right after turning smart downloads on. */
        fun runNow(context: Context) {
            val req = androidx.work.OneTimeWorkRequestBuilder<SmartRefreshWorker>().build()
            androidx.work.WorkManager.getInstance(context).enqueueUniqueWork("smart-downloads-now", androidx.work.ExistingWorkPolicy.REPLACE, req)
        }
    }
}

/**
 * Testing offline mode without cutting the phone's internet: `su -c am broadcast -n app.tune.music/app.tune.TestOfflineReceiver --ez offline true`.
 * Not exported, so only the system/root can send it.
 */
class TestOfflineReceiver : android.content.BroadcastReceiver() {
    override fun onReceive(context: Context, intent: android.content.Intent) {
        (context.applicationContext as TuneApp).graph.forceOffline = intent.getBooleanExtra("offline", false)
    }
}
