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
import app.tune.data.NewPipeDownloader
import app.tune.data.StreamResolver
import app.tune.data.YtMusic
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
    val feed = FeedBuilder(library, yt)

    private val _online = MutableStateFlow(true)
    val online: StateFlow<Boolean> = _online
    val downloads = Downloads(context, library, streams, http, online)

    /** Short messages for the toast at the bottom of the screen. */
    val toasts = MutableSharedFlow<String>(extraBufferCapacity = 8)
    fun toast(message: String) { toasts.tryEmit(message) }

    init {
        NewPipe.init(NewPipeDownloader(http), Localization("en", "US"), ContentCountry("NP"))
        val cm = context.getSystemService(ConnectivityManager::class.java)
        _online.value = cm.getNetworkCapabilities(cm.activeNetwork)?.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) == true
        cm.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
                _online.value = caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
            }
            override fun onLost(network: Network) { _online.value = false }
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
    }
}
