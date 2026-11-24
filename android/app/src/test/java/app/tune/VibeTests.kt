package app.tune

import app.tune.data.ArtistRef
import app.tune.data.Parsers
import app.tune.data.Session
import app.tune.data.SessionSong
import app.tune.data.Track
import app.tune.data.UpNext
import app.tune.data.VibeChip
import app.tune.data.VibeKind
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The Kotlin port of the vibe engine, checked like the Windows VibeTests. */
class VibeTests {
    private fun load(name: String) = JSONObject(javaClass.classLoader!!.getResource("fixtures/$name")!!.readText())
    private fun t(id: String, artist: String = "Artist") = Track(id.padEnd(11, '_'), "Title $id", listOf(ArtistRef(artist, null)), null, 180, null)
    private fun id(x: String) = t(x).videoId
    private fun chip(name: String) = VibeChip(name, "RDAT$name", "p", VibeChip.kindOf(name))
    private val now = 1_800_000_000_000L

    @Test fun radio_page_has_chips_continuation_and_related() {
        val page = Parsers.radioPage(load("next_radio_chips.json"))
        assertTrue("tracks ${page.tracks.size}", page.tracks.size >= 40)
        assertEquals("kJQP7kiw5Fk", page.tracks[0].videoId)
        assertNotNull(page.continuation)
        assertTrue(page.relatedId!!.startsWith("MPTRt"))
        assertFalse(page.chips.any { it.name == "All" })
        assertTrue(page.chips.any { it.name == "Popular" && it.kind == VibeKind.Discovery })
        assertTrue(page.chips.any { it.name == "Romance" && it.kind == VibeKind.Mood })
        assertTrue(page.chips.any { it.name == "2010s" && it.kind == VibeKind.Era })
        assertTrue(page.chips.any { it.name == "Reggaeton" && it.kind == VibeKind.Genre })
    }

    @Test fun radio_continuation_and_related() {
        val more = Parsers.radioMore(load("next_radio_continuation.json"))
        assertTrue(more.tracks.size >= 40)
        assertNotNull(more.continuation)
        val r = Parsers.related(load("related.json"))
        assertTrue("songs ${r.songs.size}", r.songs.size >= 10)
        assertTrue(r.artists.size >= 5)
        assertTrue(r.playlists.size >= 5)
    }

    @Test fun session_is_the_run_without_a_long_break() {
        val plays = listOf(t("a") to now - 5 * 60_000, t("b") to now - 9 * 60_000, t("old") to now - 2 * 3_600_000)
        val skips = listOf(id("s") to now - 7 * 60_000)
        val s = Session.build(plays, skips, { t(it.trimEnd('_')) }, now, current = t("now"))
        assertEquals(listOf(id("now"), id("a"), id("s"), id("b")), s.songs.map { it.track.videoId })
        assertTrue(s.songs[2].skipped)
        assertTrue(Session.build(listOf(t("a") to now - 3 * 3_600_000, t("b") to now - 3 * 3_600_000 - 60_000), emptyList(), { null }, now).isEmpty)
    }

    @Test fun mood_is_the_specific_chip_most_songs_share() {
        val s = Session(listOf(SessionSong(t("a"), 1.0, now), SessionSong(t("b"), 0.65, now), SessionSong(t("c"), 0.4, now)))
        val chips = mapOf(id("a") to listOf(chip("Downbeat"), chip("Romance")), id("b") to listOf(chip("Downbeat"), chip("Romance")), id("c") to listOf(chip("Party")))
        assertEquals("Downbeat", s.mood({ chips[it] }))
        assertNull(s.mood({ chips[it] }, VibeKind.Genre))
    }

    @Test fun agreement_beats_one_radio_and_skipped_neighbours_sink() {
        val ra = listOf(t("a")) + (0 until 12).map { t("p$it", "P$it") } + t("both", "B")
        val rb = listOf(t("b")) + (0 until 12).map { t("q$it", "Q$it") } + t("both", "B")
        val ranked = UpNext.rank(listOf(UpNext.Source(ra, 1.0), UpNext.Source(rb, 1.0), UpNext.Source(listOf(t("p0", "P0")), -1.0)), setOf(id("a"), id("b")), 30)
        assertEquals(id("both"), ranked[0].videoId)
        assertFalse(ranked.any { it.videoId == id("p0") })
    }

    @Test fun one_version_of_a_song_and_artists_spaced() {
        assertEquals(UpNext.titleKey("Believer"), UpNext.titleKey("Believer (Live at Pinkpop) - Remastered"))
        val spaced = UpNext.spaced(listOf(t("x1", "X"), t("x2", "X"), t("x3", "X"), t("y1", "Y"), t("z1", "Z"), t("w1", "W")), 6)
        assertEquals(listOf("x1", "y1", "z1", "w1", "x2"), spaced.take(5).map { it.videoId.trimEnd('_') })
    }
}
