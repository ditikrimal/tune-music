package app.tune.ui

import android.content.Context
import android.util.Base64
import androidx.credentials.CredentialManager
import androidx.credentials.CustomCredential
import androidx.credentials.GetCredentialRequest
import androidx.credentials.exceptions.GetCredentialCancellationException
import androidx.credentials.exceptions.NoCredentialException
import app.tune.data.SyncConfig
import com.google.android.libraries.identity.googleid.GetSignInWithGoogleOption
import com.google.android.libraries.identity.googleid.GoogleIdTokenCredential

/** "Continue with Google" through Android's Credential Manager: the system account sheet, then a Google ID token. */
object GoogleSignIn {
    suspend fun idToken(context: Context, config: SyncConfig): String {
        if (config.emulatorHost != null) return emulatorToken() // testing against the local Firebase emulators
        val clientId = config.webClientId ?: throw IllegalStateException("This build of Tune can't sign in (no Google client id).")
        val request = GetCredentialRequest.Builder()
            .addCredentialOption(GetSignInWithGoogleOption.Builder(clientId).build())
            .build()
        val credential = CredentialManager.create(context).getCredential(context, request).credential
        if (credential is CustomCredential && credential.type == GoogleIdTokenCredential.TYPE_GOOGLE_ID_TOKEN_CREDENTIAL)
            return GoogleIdTokenCredential.createFrom(credential.data).idToken
        throw IllegalStateException("Google didn't return an account")
    }

    fun describe(e: Exception): String = when (e) {
        is GetCredentialCancellationException -> "Sign-in cancelled"
        is NoCredentialException -> "No Google account on this phone. Add one in Android settings first."
        is java.io.IOException -> "No connection. Try again when you're online."
        else -> "Couldn't sign in: ${e.message ?: e.javaClass.simpleName}"
    }

    private fun emulatorToken(): String {
        fun b64(s: String) = Base64.encodeToString(s.toByteArray(), Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
        return b64("{\"alg\":\"none\"}") + "." + b64("{\"sub\":\"ditik\",\"email\":\"ditik@example.com\",\"email_verified\":true,\"name\":\"Test ditik\"}") + "."
    }
}
