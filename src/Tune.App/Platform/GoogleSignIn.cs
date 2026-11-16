using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Tune.App.Platform;

/// <summary>
/// "Continue with Google" for a desktop app: opens the Google sign-in page in the browser and catches the answer
/// on a one-time local port (Google's loopback flow with PKCE), then trades the code for a Google ID token.
/// </summary>
public static class GoogleSignIn
{
    public static async Task<string> GetIdTokenAsync(HttpClient http, string clientId, string clientSecret, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            var verifier = B64(RandomNumberGenerator.GetBytes(32));
            var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = B64(RandomNumberGenerator.GetBytes(16));
            var url = "https://accounts.google.com/o/oauth2/v2/auth"
                      + $"?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirect)}"
                      + "&response_type=code&scope=openid%20email%20profile&prompt=select_account"
                      + $"&code_challenge={challenge}&code_challenge_method=S256&state={state}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                await using var stream = client.GetStream();
                var query = await ReadRequestQueryAsync(stream, timeout.Token).ConfigureAwait(false);
                if (query is null) { await RespondAsync(stream, 404, "").ConfigureAwait(false); continue; } // e.g. /favicon.ico
                var args = System.Web.HttpUtility.ParseQueryString(query);
                if (args["state"] != state) { await RespondAsync(stream, 400, Page("Something went wrong. Try again from Tune.")).ConfigureAwait(false); continue; }
                if (args["error"] is { } error)
                {
                    await RespondAsync(stream, 200, Page("Sign-in was cancelled. You can close this tab.")).ConfigureAwait(false);
                    throw new OperationCanceledException(error);
                }
                await RespondAsync(stream, 200, Page("You're signed in to Tune. You can close this tab and go back to the app.")).ConfigureAwait(false);
                return await ExchangeAsync(http, clientId, clientSecret, args["code"]!, verifier, redirect, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> ExchangeAsync(HttpClient http, string clientId, string secret, string code, string verifier, string redirect, CancellationToken ct)
    {
        using var r = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(
        [
            new("code", code), new("client_id", clientId), new("client_secret", secret), new("redirect_uri", redirect),
            new("grant_type", "authorization_code"), new("code_verifier", verifier),
        ]), ct).ConfigureAwait(false);
        var o = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
        return o?["id_token"]?.GetValue<string>()
               ?? throw new InvalidOperationException(o?["error_description"]?.ToString() ?? o?["error"]?.ToString() ?? "Google sign-in failed");
    }

    /// <summary>The query string of "GET /?… HTTP/1.1", or null for any other path.</summary>
    private static async Task<string?> ReadRequestQueryAsync(Stream s, CancellationToken ct)
    {
        var buf = new byte[8192];
        var n = 0;
        while (n < buf.Length)
        {
            var read = await s.ReadAsync(buf.AsMemory(n), ct).ConfigureAwait(false);
            if (read == 0) break;
            n += read;
            if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0].Split(' ');
        if (line.Length < 2 || !line[1].StartsWith("/?", StringComparison.Ordinal)) return null;
        return line[1][2..];
    }

    private static async Task RespondAsync(Stream s, int status, string html)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head).ConfigureAwait(false);
        await s.WriteAsync(body).ConfigureAwait(false);
    }

    private static string Page(string message) =>
        "<!doctype html><meta charset=utf-8><title>Tune</title><body style=\"font-family:Segoe UI,sans-serif;background:#121212;color:#eee;"
        + "display:flex;align-items:center;justify-content:center;height:90vh;font-size:20px\">" + WebUtility.HtmlEncode(message);

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
