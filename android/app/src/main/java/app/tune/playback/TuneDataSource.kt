package app.tune.playback

import android.net.Uri
import androidx.annotation.OptIn
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DataSource
import androidx.media3.datasource.DataSpec
import androidx.media3.datasource.TransferListener
import java.io.File

/**
 * Where a song's bytes come from: a downloaded file, else the streaming cache (which fetches a fresh link and
 * keeps what it reads), else plain network for anything that isn't a song (e.g. artwork).
 */
@OptIn(UnstableApi::class)
class TuneDataSource(
    private val localFile: (String) -> File?,
    private val file: DataSource,
    private val cached: DataSource,
    private val other: DataSource,
) : DataSource {
    private var current: DataSource? = null

    override fun addTransferListener(transferListener: TransferListener) {
        file.addTransferListener(transferListener)
        cached.addTransferListener(transferListener)
        other.addTransferListener(transferListener)
    }

    override fun open(dataSpec: DataSpec): Long {
        if (dataSpec.uri.scheme == TuneUri.SCHEME) {
            val id = dataSpec.uri.host ?: throw java.io.IOException("Bad song uri ${dataSpec.uri}")
            localFile(id)?.let {
                current = file
                return file.open(dataSpec.withUri(Uri.fromFile(it)))
            }
            current = cached
            return cached.open(dataSpec.buildUpon().setKey(id).build()) // cache key = video id
        }
        current = other
        return other.open(dataSpec)
    }

    override fun read(buffer: ByteArray, offset: Int, length: Int): Int = current!!.read(buffer, offset, length)

    override fun getUri(): Uri? = current?.uri

    override fun getResponseHeaders(): Map<String, List<String>> = current?.responseHeaders ?: emptyMap()

    override fun close() {
        try { current?.close() } finally { current = null }
    }

    class Factory(
        private val localFile: (String) -> File?,
        private val file: DataSource.Factory,
        private val cached: DataSource.Factory,
        private val other: DataSource.Factory,
    ) : DataSource.Factory {
        override fun createDataSource(): DataSource =
            TuneDataSource(localFile, file.createDataSource(), cached.createDataSource(), other.createDataSource())
    }
}
