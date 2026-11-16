package app.tune.data

import android.util.Log
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.schabi.newpipe.extractor.ServiceList
import org.schabi.newpipe.extractor.downloader.Downloader
import org.schabi.newpipe.extractor.downloader.Response
import org.schabi.newpipe.extractor.exceptions.ReCaptchaException
import org.schabi.newpipe.extractor.services.youtube.YoutubeParsingHelper
import org.schabi.newpipe.extractor.stream.DeliveryMethod
import org.schabi.newpipe.extractor.stream.StreamInfo
import java.util.concurrent.ConcurrentHashMap

/** NewPipeExtractor's network hook, backed by our OkHttp client. */
class NewPipeDownloader(private val http: OkHttpClient) : Downloader() {
    override fun execute(request: org.schabi.newpipe.extractor.downloader.Request): Response {
        val body = request.dataToSend()?.toRequestBody()
        val b = Request.Builder().url(request.url()).method(request.httpMethod(), body)
            .header("User-Agent", USER_AGENT)
        for ((name, values) in request.headers()) {
            b.removeHeader(name)
            values.forEach { b.addHeader(name, it) }
        }
        http.newCall(b.build()).execute().use { r ->
            if (r.code == 429) throw ReCaptchaException("reCaptcha challenge requested", request.url())
            return Response(r.code, r.message, r.headers.toMultimap(), r.body.string(), r.request.url.toString())
        }
    }

    companion object {
        const val USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0"
    }
}

/** A playable audio link plus how it must be requested. */
data class ResolvedStream(val url: String, val userAgent: String, val webClient: Boolean, val expiresAtMs: Long, val durationSec: Int?)

/**
 * Finds the best audio-only stream for a song with NewPipeExtractor (handles YouTube's signature and
 * throttling ciphers). Links stay valid for hours and are cached; [prepare] warms the cache ahead of time.
 */
class StreamResolver {
    private val cache = ConcurrentHashMap<String, ResolvedStream>()
    private val inFlight = ConcurrentHashMap<String, Any>()

    fun cached(videoId: String): ResolvedStream? =
        cache[videoId]?.takeIf { it.expiresAtMs - System.currentTimeMillis() > 10 * 60_000 }

    fun invalidate(videoId: String) { cache.remove(videoId) }

    /** Blocking; call off the main thread. */
    fun resolve(videoId: String): ResolvedStream {
        cached(videoId)?.let { return it }
        val lock = inFlight.computeIfAbsent(videoId) { Any() }
        synchronized(lock) {
            cached(videoId)?.let { return it }
            val started = System.currentTimeMillis()
            val info = StreamInfo.getInfo(ServiceList.YouTube, "https://www.youtube.com/watch?v=$videoId")
            val audio = info.audioStreams
                .filter { it.deliveryMethod == DeliveryMethod.PROGRESSIVE_HTTP && it.isUrl }
                .maxByOrNull { it.averageBitrate }
                ?: throw java.io.IOException("No audio stream for $videoId")
            val url = audio.content
            val web = YoutubeParsingHelper.isWebStreamingUrl(url)
            val ua = if (YoutubeParsingHelper.isVisionOsStreamingUrl(url)) YoutubeParsingHelper.getVisionOsUserAgent(null) else NewPipeDownloader.USER_AGENT
            val expire = Regex("[?&]expire=(\\d+)").find(url)?.groupValues?.get(1)?.toLongOrNull()?.times(1000)
                ?: (System.currentTimeMillis() + 5 * 3_600_000)
            val s = ResolvedStream(url, ua, web, expire, info.duration.toInt().takeIf { it > 0 })
            cache[videoId] = s
            inFlight.remove(videoId)
            Log.i("Tune", "Resolved $videoId in ${System.currentTimeMillis() - started} ms (itag ${audio.itag}, ${audio.averageBitrate} kbps, web=$web)")
            return s
        }
    }
}
