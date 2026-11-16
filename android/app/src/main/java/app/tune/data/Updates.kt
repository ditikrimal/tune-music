package app.tune.data

import android.content.Context
import android.content.Intent
import android.os.Build
import android.provider.Settings
import android.util.Log
import androidx.core.content.FileProvider
import androidx.core.net.toUri
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONObject
import java.io.File

/** A published Tune release and the APK in it. */
data class Release(val version: List<Int>, val tag: String, val title: String, val pageUrl: String, val apkUrl: String?, val apkBytes: Long) {
    val versionName: String get() = version.joinToString(".")
}

/** Reads GitHub's "latest release" (public, no account) and compares versions. Pure, so it can be tested. */
object ReleaseFeed {
    const val LATEST_URL = "https://api.github.com/repos/ditikrimal/tune-music/releases/latest"
    const val ASSET = "Tune.apk"

    fun parse(json: String): Release? = runCatching {
        val o = JSONObject(json)
        if (o.optBoolean("draft") || o.optBoolean("prerelease")) return null
        val tag = o.getString("tag_name")
        val version = parseVersion(tag) ?: return null
        var url: String? = null
        var bytes = 0L
        val assets = o.optJSONArray("assets")
        for (i in 0 until (assets?.length() ?: 0)) {
            val a = assets!!.getJSONObject(i)
            if (a.optString("name").equals(ASSET, ignoreCase = true)) { url = a.optString("browser_download_url"); bytes = a.optLong("size") }
        }
        Release(version, tag, o.optString("name", tag), o.optString("html_url"), url, bytes)
    }.getOrNull()

    /** "v1.3.0" / "1.3" / "1.3.0-beta" -> [1, 3, 0]. */
    fun parseVersion(tag: String): List<Int>? {
        val core = tag.trim().trimStart('v', 'V').split('-', '+', ' ').first()
        val parts = core.split('.').map { it.toIntOrNull() ?: return null }
        if (parts.isEmpty()) return null
        return (parts + listOf(0, 0, 0)).take(3)
    }

    fun isNewer(release: List<Int>, current: List<Int>): Boolean {
        for (i in 0 until 3) {
            val a = release.getOrElse(i) { 0 }; val b = current.getOrElse(i) { 0 }
            if (a != b) return a > b
        }
        return false
    }
}

/**
 * Keeps Tune current for everyone who installed the APK: checks GitHub every few hours (and when asked), shows
 * "Update" when there's a newer release, downloads the APK and opens Android's installer. Same signing key, so it
 * updates in place and keeps the library.
 */
class Updater(private val context: Context, private val http: OkHttpClient, private val lib: Library) {
    private val _available = MutableStateFlow<Release?>(null)
    val available: StateFlow<Release?> = _available
    /** 0..1 while downloading, null otherwise. */
    private val _progress = MutableStateFlow<Float?>(null)
    val progress: StateFlow<Float?> = _progress

    val currentVersion: String = runCatching {
        context.packageManager.getPackageInfo(context.packageName, 0).versionName
    }.getOrNull() ?: "0"

    /** Asks GitHub unless it did recently. Returns the newer release, or null. Never throws. */
    suspend fun check(force: Boolean = false): Release? = withContext(Dispatchers.IO) {
        val last = lib.setting("update.lastCheck")?.toLongOrNull() ?: 0
        if (!force && System.currentTimeMillis() - last < 6 * 3_600_000L) return@withContext _available.value
        val found = runCatching {
            http.newCall(Request.Builder().url(ReleaseFeed.LATEST_URL)
                .header("User-Agent", "Tune-Updater").header("Accept", "application/vnd.github+json").build()).execute().use { r ->
                if (!r.isSuccessful) null else ReleaseFeed.parse(r.body.string())
            }
        }.onFailure { Log.w("Tune", "Update check failed: $it") }.getOrNull()
        lib.setSetting("update.lastCheck", System.currentTimeMillis().toString())
        val current = ReleaseFeed.parseVersion(currentVersion) ?: listOf(0, 0, 0)
        val newer = found?.takeIf { it.apkUrl != null && ReleaseFeed.isNewer(it.version, current) }
        _available.value = newer
        newer
    }

    /** Android needs a one-time "Allow from this source" for Tune before it can install an update. */
    fun canInstall(): Boolean = Build.VERSION.SDK_INT < 26 || context.packageManager.canRequestPackageInstalls()

    fun openInstallPermission(): Intent =
        Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, "package:${context.packageName}".toUri()).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

    /** Downloads the APK (with progress) and returns the installer screen to open. Throws on failure. */
    suspend fun download(release: Release): Intent = withContext(Dispatchers.IO) {
        val dir = File(context.cacheDir, "updates").apply { mkdirs(); listFiles()?.forEach { it.delete() } }
        val target = File(dir, "Tune-${release.versionName}.apk")
        val part = File(dir, target.name + ".part")
        _progress.value = 0f
        try {
            http.newCall(Request.Builder().url(release.apkUrl!!).header("User-Agent", "Tune-Updater").build()).execute().use { r ->
                if (!r.isSuccessful) throw java.io.IOException("HTTP ${r.code}")
                val total = r.body.contentLength().takeIf { it > 0 } ?: release.apkBytes
                var done = 0L
                var lastTick = 0L
                r.body.byteStream().use { input ->
                    part.outputStream().use { out ->
                        val buf = ByteArray(1 shl 16)
                        while (true) {
                            val n = input.read(buf); if (n < 0) break
                            out.write(buf, 0, n); done += n
                            if (total > 0 && System.currentTimeMillis() - lastTick > 200) {
                                lastTick = System.currentTimeMillis(); _progress.value = (done.toFloat() / total).coerceIn(0f, 1f)
                            }
                        }
                    }
                }
                if (total > 0 && done != total) throw java.io.IOException("Download stopped at $done of $total bytes")
            }
            // an APK is a zip: don't hand an error page to the installer
            part.inputStream().use { if (it.read() != 'P'.code || it.read() != 'K'.code) throw java.io.IOException("Not an APK") }
            part.renameTo(target)
            val uri = FileProvider.getUriForFile(context, "${context.packageName}.updates", target)
            Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        } finally {
            part.delete()
            _progress.value = null
        }
    }
}
