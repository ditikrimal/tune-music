package app.tune.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import app.tune.data.AccountSync
import coil3.compose.AsyncImage
import kotlinx.coroutines.launch

/** Round avatar: the Google photo, or the first letter, or a person icon when signed out. */
@Composable
fun Avatar(state: AccountSync.State?, size: Dp) {
    val c = LocalTune.current
    val signedIn = state?.signedIn == true
    Box(Modifier.size(size).clip(CircleShape).background(if (signedIn) c.accent else c.card), contentAlignment = Alignment.Center) {
        if (signedIn) {
            Text((state!!.name ?: state.email ?: "?").take(1).uppercase(), style = if (size > 40.dp) Type.title2 else Type.bodyStrong, color = c.onAccent)
            state.photo?.let { AsyncImage(it, "Your account", Modifier.fillMaxSize().clip(CircleShape)) }
        } else Icon(Ico.Person, "Account", tint = c.subtle, modifier = Modifier.size(size * 0.6f))
    }
}

/** Top-right of Home and Library, like YouTube / Google apps: opens the account card (sign in, sync, settings). */
@Composable
fun ProfileButton() {
    val app = LocalApp.current
    val state = app.graph.account?.state?.collectAsState()?.value
    var open by remember { mutableStateOf(false) }
    Box(Modifier.size(44.dp).clip(CircleShape).clickable { open = true }, contentAlignment = Alignment.Center) {
        Avatar(state, 32.dp)
    }
    if (open) ProfileCard { open = false }
}

@Composable
private fun ProfileCard(onDismiss: () -> Unit) {
    val app = LocalApp.current
    val c = LocalTune.current
    val account = app.graph.account
    val state = account?.state?.collectAsState()?.value
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    var busy by remember { mutableStateOf(false) }

    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        // full-screen layer: tap outside the card closes it; the card sits near the top like Google's account menu
        Box(Modifier.fillMaxSize().clickable(interactionSource = null, indication = null, onClick = onDismiss).statusBarsPadding().padding(12.dp),
            contentAlignment = Alignment.TopCenter) {
            Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(28.dp)).background(c.card)
                .clickable(interactionSource = null, indication = null) { }.padding(vertical = 8.dp)) {
                Row(Modifier.fillMaxWidth().padding(start = 8.dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    IconBtn(Ico.Close, "Close", tint = c.text, onClick = onDismiss)
                    Text("Tune", style = Type.bodyStrong, color = c.text, textAlign = TextAlign.Center, modifier = Modifier.weight(1f))
                    Spacer(Modifier.width(44.dp))
                }
                Column(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp).clip(RoundedCornerShape(20.dp)).background(c.base).padding(18.dp),
                    horizontalAlignment = Alignment.CenterHorizontally) {
                    when {
                        account == null -> {
                            Avatar(null, 64.dp)
                            Text("Sign-in isn't available in this version.", style = Type.caption, color = c.subtle, textAlign = TextAlign.Center,
                                modifier = Modifier.padding(top = 12.dp))
                        }
                        state?.signedIn != true -> {
                            Avatar(null, 64.dp)
                            Text("Sign in to sync", style = Type.title2, color = c.text, modifier = Modifier.padding(top = 12.dp))
                            Text("Likes, playlists and history on all your devices.",
                                style = Type.caption, color = c.subtle, textAlign = TextAlign.Center, modifier = Modifier.padding(top = 6.dp))
                            Text(if (busy) "Signing in…" else "Continue with Google", style = Type.bodyStrong, color = c.onAccent,
                                modifier = Modifier.padding(top = 16.dp).clip(RoundedCornerShape(24.dp)).background(c.accent)
                                    .clickable(enabled = !busy) {
                                        busy = true
                                        scope.launch {
                                            try {
                                                account.signInWithGoogle(GoogleSignIn.idToken(context, account.config))
                                                app.graph.toast("Signed in as ${account.state.value.email ?: account.state.value.name}")
                                            } catch (e: kotlinx.coroutines.CancellationException) {
                                                throw e
                                            } catch (e: Exception) {
                                                android.util.Log.w("Tune", "Sign-in failed: $e")
                                                app.graph.toast(GoogleSignIn.describe(e))
                                            } finally { busy = false }
                                        }
                                    }.padding(horizontal = 24.dp, vertical = 12.dp))
                        }
                        else -> {
                            Avatar(state, 64.dp)
                            Text("Hi, ${(state.name ?: state.email ?: "there").substringBefore(' ')}!", style = Type.title2, color = c.text,
                                modifier = Modifier.padding(top = 12.dp))
                            state.email?.let { Text(it, style = Type.caption, color = c.subtle, maxLines = 1, overflow = TextOverflow.Ellipsis) }
                            val status = when {
                                state.syncing -> "Syncing…"
                                state.error != null -> "Couldn't sync: ${state.error}"
                                state.lastSynced > 0 -> "Synced " +
                                    if (System.currentTimeMillis() - state.lastSynced < 60_000) "just now"
                                    else android.text.format.DateUtils.getRelativeTimeSpanString(state.lastSynced).toString().lowercase()
                                else -> "Waiting to sync"
                            }
                            Text(status, style = Type.caption, color = c.accentText, textAlign = TextAlign.Center, modifier = Modifier.padding(top = 10.dp))
                        }
                    }
                }
                Spacer(Modifier.height(4.dp))
                if (state?.signedIn == true)
                    MenuRow(Ico.Sync, "Sync now") { scope.launch { account?.syncNow() } }
                MenuRow(Ico.Settings, "Settings") { onDismiss(); app.nav.navigate("settings") }
                MenuRow(Ico.Download, "Downloads") { onDismiss(); app.nav.navigate("downloads") }
                if (state?.signedIn == true)
                    MenuRow(Ico.Logout, "Sign out") {
                        account?.signOut(); app.graph.toast("Signed out. Everything stays on this phone.")
                    }
                Spacer(Modifier.height(6.dp))
            }
        }
    }
}

@Composable
private fun MenuRow(icon: ImageVector, label: String, onClick: () -> Unit) {
    val c = LocalTune.current
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 26.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(18.dp)) {
        Icon(icon, null, tint = c.text, modifier = Modifier.size(22.dp))
        Text(label, style = Type.body, color = c.text)
    }
}
