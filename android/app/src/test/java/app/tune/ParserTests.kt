package app.tune

import app.tune.data.AlbumRef
import app.tune.data.CardKind
import app.tune.data.Lrc
import app.tune.data.Parsers
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The Kotlin port of the InnerTube parsers, checked against the same recorded responses as the Windows tests. */
class ParserTests {
    private fun load(name: String) = JSONObject(javaClass.classLoader!!.getResource("fixtures/$name")!!.readText())

    private val arijit by lazy { Parsers.search(load("search_arijit.json")) }

    @Test fun search_songs_have_ids_titles_artists_and_durations() {
        assertTrue(arijit.songs.size >= 3)
        arijit.songs.forEach { assertEquals(11, it.videoId.length); assertTrue(it.title.isNotBlank()); assertTrue(it.artists.isNotEmpty()) }
        assertTrue(arijit.songs.any { it.durationSec != null })
        val first = arijit.songs.first { it.videoId == "oyr9_tu_F2U" }
        assertEquals(248, first.durationSec)
        assertEquals("Arijit Singh", first.artistLine)
    }

    @Test fun search_top_result_albums_artists_playlists() {
        assertEquals(CardKind.Artist, arijit.top?.kind)
        assertEquals("Arijit Singh", arijit.top?.title)
        assertTrue(arijit.artists.any { it.browseId!!.startsWith("UC") })
        assertTrue(arijit.albums.any { it.browseId!!.startsWith("MPRE") && it.subtitle!!.contains("Arijit Singh") })
        val pl = arijit.playlists.single { it.title == "Spotlight: Arijit Singh" }
        assertFalse(pl.playlistId!!.startsWith("VL"))
        assertEquals("VL" + pl.playlistId, pl.browseId)
    }

    @Test fun home_shelves_and_continuation() {
        val page = Parsers.home(load("home.json"))
        assertTrue(page.shelves.size >= 2)
        page.shelves.forEach { assertTrue(it.title.isNotBlank()); assertTrue(it.items.isNotEmpty()) }
        assertNotNull(page.continuation)
        assertTrue(Parsers.home(load("home_continuation.json")).shelves.isNotEmpty())
        val pl = page.shelves.flatMap { it.items }.first { it.kind == CardKind.Playlist }
        assertEquals("VL" + pl.playlistId, pl.browseId)
    }

    @Test fun album_tracks_inherit_album_info() {
        val album = Parsers.album(load("album.json"), "MPREb_test")
        assertTrue(album.title.startsWith("Tu Hain Toh Main Hoon"))
        assertEquals("Single • 2025", album.subtitle)
        val t = album.tracks.single()
        assertEquals("CKJA9blyMUg", t.videoId)
        assertEquals(248, t.durationSec)
        assertEquals(listOf("Tanishk Bagchi", "Arijit Singh", "Afsana Khan"), t.artists.map { it.name })
        assertEquals(album.thumbnail, t.thumbnail)
        assertEquals(AlbumRef(album.title, "MPREb_test"), t.album)
    }

    @Test fun playlist_page() {
        val (c, cont) = Parsers.playlist(load("playlist.json"))
        assertEquals("Mellow Pop Classics", c.title)
        assertEquals(49, c.tracks.size)
        assertEquals("Thinking out Loud", c.tracks[0].title)
        assertEquals("Ed Sheeran", c.tracks[0].artistLine)
        assertEquals(290, c.tracks[0].durationSec)
        assertNull(cont)
    }

    @Test fun artist_page() {
        val a = Parsers.artist(load("artist.json"), "UC")
        assertEquals("Arijit Singh", a.name)
        assertNotNull(a.thumbnail)
        assertEquals(5, a.topSongs.size)
        assertTrue(a.shelves.any { s -> s.title == "Albums" && s.items.all { it.kind == CardKind.Album } })
    }

    @Test fun radio_queue() {
        val tracks = Parsers.radio(load("next_radio.json"))
        assertTrue(tracks.size >= 10)
        assertEquals("oyr9_tu_F2U", tracks[0].videoId)
        assertEquals(248, tracks[0].durationSec)
        assertTrue(tracks[0].album!!.browseId!!.startsWith("MPRE"))
    }

    @Test fun lrc_parsing_and_lookup() {
        val lines = Lrc.parse("[00:01.50]one\n[00:03.2]two\n[01:00.123][01:05]three")
        assertEquals(listOf(1500L, 3200L, 60123L, 65000L), lines.map { it.atMs })
        assertEquals(-1, Lrc.indexAt(lines, 1000))
        assertEquals(1, Lrc.indexAt(lines, 3300))
        assertEquals(3, Lrc.indexAt(lines, 99_000))
    }

    @Test fun playlist_links() {
        assertEquals("PLJ968k8uWUmXbTohbJAfR2pYiDHo771el",
            app.tune.data.PlaylistLinks.parse("https://music.youtube.com/playlist?list=PLJ968k8uWUmXbTohbJAfR2pYiDHo771el&si=SjJYibdrW5YjSuzc&drli=1"))
        assertEquals("PLJ968k8uWUmXbTohbJAfR2pYiDHo771el", app.tune.data.PlaylistLinks.parse(" VLPLJ968k8uWUmXbTohbJAfR2pYiDHo771el "))
        assertNull(app.tune.data.PlaylistLinks.parse("https://music.youtube.com/watch?v=abcdefghijk"))
        assertNull(app.tune.data.PlaylistLinks.parse("hello"))
    }
}
