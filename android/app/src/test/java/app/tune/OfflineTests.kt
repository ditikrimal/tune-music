package app.tune

import app.tune.data.ArtistRef
import app.tune.data.OfflineMix
import app.tune.data.SmartPicker
import app.tune.data.Track
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.random.Random

class OfflineTests {
    private fun t(id: String, artist: String = "A$id") = Track(id, "Song $id", listOf(ArtistRef(artist, "UC$artist")), null, 200, null)
    private val day = 86_400_000L
    private val now = 1_000 * day

    @Test fun recent_favourites_beat_old_ones_and_likes_count() {
        val stats = listOf(
            Triple(t("old"), 10, now - 80 * day),   // played a lot, long ago
            Triple(t("new"), 6, now - 1 * day),     // played a fair bit this week
            Triple(t("once"), 1, now - 2 * day),
        )
        val ranked = SmartPicker.rank(stats, liked = listOf(t("liked")), now = now).map { it.videoId }
        assertEquals("new", ranked.first())
        assertTrue(ranked.indexOf("liked") < ranked.indexOf("once")) // a like outweighs a single play
        assertEquals(4, ranked.size)
    }

    @Test fun offline_mix_prefers_radio_then_same_artist_then_favourites() {
        val seed = t("seed", artist = "Hozier")
        val available = listOf(t("r2"), t("x"), t("h1", "Hozier"), t("fav"), t("r1"), t("queued"))
        val mix = OfflineMix.build(seed, available, radioIds = listOf("r1", "missing", "r2"), playCounts = mapOf("fav" to 9),
            exclude = setOf("queued"), random = Random(1)).map { it.videoId }
        assertEquals(listOf("r1", "r2", "h1"), mix.take(3))   // radio order kept, then the same artist
        assertEquals("fav", mix[3])                            // then what you play most
        assertFalse("queued" in mix)                           // nothing already in the queue
        assertFalse("seed" in mix)
        assertEquals(mix.toSet().size, mix.size)
    }

    @Test fun offline_stuck_rules() {
        val s = app.tune.data.OfflineStall
        // streaming song still playing from its buffer: leave it alone
        assertFalse(s.isStuck(state = 3, failed = false, currentPlayable = false, justArrived = false))
        // buffer ran dry on a song that isn't saved: move on (don't wait ~30 s for ExoPlayer to give up)
        assertTrue(s.isStuck(state = 2, failed = false, currentPlayable = false, justArrived = false))
        // a saved song that's briefly buffering is fine
        assertFalse(s.isStuck(state = 2, failed = false, currentPlayable = true, justArrived = false))
        // the queue ran out offline, or the song failed: move on
        assertTrue(s.isStuck(state = 4, failed = false, currentPlayable = true, justArrived = false))
        assertTrue(s.isStuck(state = 1, failed = true, currentPlayable = false, justArrived = false))
        // just skipped onto a song that can't play offline: skip it straight away
        assertTrue(s.isStuck(state = 3, failed = false, currentPlayable = false, justArrived = true))
        // a second, late request after we already moved to a saved song must not skip that one too
        assertFalse(s.isStuck(state = 2, failed = false, currentPlayable = true, justArrived = true))
    }

    @Test fun offline_mix_without_seed_still_plays_something() {
        val mix = OfflineMix.build(null, listOf(t("a"), t("b")), null, emptyMap(), emptySet())
        assertEquals(setOf("a", "b"), mix.map { it.videoId }.toSet())
    }
}
