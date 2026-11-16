package app.tune

import app.tune.data.ReleaseFeed
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class UpdateTests {
    private val json = """
        {"tag_name":"v1.3.0","name":"Tune 1.3.0","html_url":"https://github.com/x/y","draft":false,"prerelease":false,
         "assets":[{"name":"Tune.exe","browser_download_url":"https://e/Tune.exe","size":1},
                   {"name":"Tune.apk","browser_download_url":"https://e/Tune.apk","size":7000000}]}
    """

    @Test fun finds_the_apk_and_version() {
        val r = ReleaseFeed.parse(json)!!
        assertEquals(listOf(1, 3, 0), r.version)
        assertEquals("1.3.0", r.versionName)
        assertEquals("https://e/Tune.apk", r.apkUrl)
        assertEquals(7000000L, r.apkBytes)
    }

    @Test fun ignores_prereleases_and_junk() {
        assertNull(ReleaseFeed.parse(json.replace("\"prerelease\":false", "\"prerelease\":true")))
        assertNull(ReleaseFeed.parse("""{"message":"API rate limit exceeded"}"""))
        assertNull(ReleaseFeed.parse("nope"))
    }

    @Test fun compares_versions_as_numbers() {
        val v = { s: String -> ReleaseFeed.parseVersion(s)!! }
        assertTrue(ReleaseFeed.isNewer(v("v1.3.0"), v("1.2.0")))
        assertFalse(ReleaseFeed.isNewer(v("v1.2.0"), v("1.2.0")))
        assertTrue(ReleaseFeed.isNewer(v("1.10.0"), v("1.9.0")))
        assertFalse(ReleaseFeed.isNewer(v("v1.1.0"), v("1.2.0")))
        assertTrue(ReleaseFeed.isNewer(v("v2"), v("1.9.9")))
        assertEquals(listOf(1, 3, 0), v("1.3.0-beta"))
    }
}
