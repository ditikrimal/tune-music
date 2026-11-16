using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Tune.Core.Sync;

/// <summary>
/// The Firebase project Tune signs in to (filled from tune-firebase.properties at build time). With
/// <see cref="EmulatorHost"/> set, everything talks to the local Firebase emulators instead (tests).
/// </summary>
public sealed record SyncConfig(string ProjectId, string ApiKey, string? WebClientId, string? DesktopClientId,
    string? DesktopClientSecret, string? EmulatorHost = null)
{
    /// <summary>key=value lines; null when the required keys are missing.</summary>
    public static SyncConfig? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq > 0) d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        string? Get(string k) => d.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        return Get("projectId") is { } p && Get("apiKey") is { } k
            ? new SyncConfig(p, k, Get("webClientId"), Get("desktopClientId"), Get("desktopClientSecret"), Get("emulator"))
            : null;
    }

    internal string AuthBase => EmulatorHost is { } h ? $"http://{h}:9099/identitytoolkit.googleapis.com" : "https://identitytoolkit.googleapis.com";
    internal string TokenBase => EmulatorHost is { } h ? $"http://{h}:9099/securetoken.googleapis.com" : "https://securetoken.googleapis.com";
    internal string FirestoreBase => EmulatorHost is { } h ? $"http://{h}:8080/v1" : "https://firestore.googleapis.com/v1";
    internal string DocumentsRoot => $"projects/{ProjectId}/databases/(default)/documents";
}

public sealed record AuthSession(string Uid, string? Email, string? Name, string? PhotoUrl, string IdToken, string RefreshToken, DateTimeOffset ExpiresAt);

public sealed class SyncException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Firebase Auth over its REST API: Google sign-in (with a Google ID token) and token refresh.</summary>
public sealed class FirebaseAuthClient(HttpClient http, SyncConfig config)
{
    public async Task<AuthSession> SignInWithGoogleAsync(string googleIdToken, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["postBody"] = $"id_token={Uri.EscapeDataString(googleIdToken)}&providerId=google.com",
            ["requestUri"] = "http://localhost",
            ["returnIdpCredential"] = true,
            ["returnSecureToken"] = true,
        };
        using var r = await http.PostAsync($"{config.AuthBase}/v1/accounts:signInWithIdp?key={config.ApiKey}",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        var o = await ReadAsync(r, ct).ConfigureAwait(false);
        return new AuthSession(Str(o, "localId") ?? throw new SyncException("Sign-in returned no user"),
            Str(o, "email"), Str(o, "displayName") ?? Str(o, "fullName"), Str(o, "photoUrl"),
            Str(o, "idToken")!, Str(o, "refreshToken")!, Expiry(Str(o, "expiresIn")));
    }

    public async Task<AuthSession> RefreshAsync(AuthSession old, CancellationToken ct = default)
    {
        using var r = await http.PostAsync($"{config.TokenBase}/v1/token?key={config.ApiKey}", new FormUrlEncodedContent(
        [
            new("grant_type", "refresh_token"),
            new("refresh_token", old.RefreshToken),
        ]), ct).ConfigureAwait(false);
        var o = await ReadAsync(r, ct).ConfigureAwait(false);
        return old with
        {
            IdToken = Str(o, "id_token") ?? throw new SyncException("Refresh returned no token"),
            RefreshToken = Str(o, "refresh_token") ?? old.RefreshToken,
            ExpiresAt = Expiry(Str(o, "expires_in")),
        };
    }

    private static DateTimeOffset Expiry(string? seconds) =>
        DateTimeOffset.UtcNow.AddSeconds(int.TryParse(seconds, CultureInfo.InvariantCulture, out var s) ? s : 3600);

    internal static async Task<JsonObject> ReadAsync(HttpResponseMessage r, CancellationToken ct)
    {
        var text = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode)
        {
            var msg = (JsonNode.Parse(string.IsNullOrEmpty(text) ? "{}" : text) as JsonObject)?["error"]?["message"]?.ToString();
            throw new SyncException(msg ?? $"HTTP {(int)r.StatusCode}", r.StatusCode);
        }
        return JsonNode.Parse(text) as JsonObject ?? throw new SyncException("Unexpected reply");
    }

    private static string? Str(JsonObject o, string k) => o[k]?.GetValue<string>();
}

/// <summary>A document in the user's part of the account: users/{uid}/{collection}/{id}. Field values: string, long or bool.</summary>
public sealed record RemoteDoc(string Collection, string Id, IReadOnlyDictionary<string, object?> Fields)
{
    public string? Str(string k) => Fields.TryGetValue(k, out var v) ? v as string : null;
    public long Long(string k) => Fields.TryGetValue(k, out var v) && v is long l ? l : 0;
    public bool Bool(string k) => Fields.TryGetValue(k, out var v) && v is true;
}

public sealed record RemotePage(IReadOnlyList<RemoteDoc> Docs, string? Cursor);

/// <summary>Where synced documents live. Firestore in the app; an in-memory fake in tests.</summary>
public interface ISyncRemote
{
    /// <summary>Writes whole documents (each replaces what was there).</summary>
    Task PushAsync(IReadOnlyList<RemoteDoc> docs, CancellationToken ct);

    /// <summary>Documents of a collection changed after <paramref name="cursor"/> (null = all), oldest change first.</summary>
    Task<RemotePage> PullAsync(string collection, string? cursor, int limit, CancellationToken ct);
}

/// <summary>
/// Firestore over REST. Every write also stamps serverAt with the server's clock, and pulls page through
/// documents ordered by (serverAt, name) — so a device with a wrong clock can't make others miss changes.
/// </summary>
public sealed class FirestoreRemote(HttpClient http, SyncConfig config, string uid, Func<bool, CancellationToken, Task<string>> token) : ISyncRemote
{
    private string UserPath => $"{config.DocumentsRoot}/users/{uid}";

    public async Task PushAsync(IReadOnlyList<RemoteDoc> docs, CancellationToken ct)
    {
        foreach (var chunk in docs.Chunk(400))
        {
            var writes = new JsonArray();
            foreach (var d in chunk)
                writes.Add(new JsonObject
                {
                    ["update"] = new JsonObject { ["name"] = $"{UserPath}/{d.Collection}/{d.Id}", ["fields"] = Encode(d.Fields) },
                    ["updateTransforms"] = new JsonArray(new JsonObject { ["fieldPath"] = "serverAt", ["setToServerValue"] = "REQUEST_TIME" }),
                });
            await SendAsync($"{config.FirestoreBase}/{config.DocumentsRoot}:commit", new JsonObject { ["writes"] = writes }, ct).ConfigureAwait(false);
        }
    }

    public async Task<RemotePage> PullAsync(string collection, string? cursor, int limit, CancellationToken ct)
    {
        var query = new JsonObject
        {
            ["from"] = new JsonArray(new JsonObject { ["collectionId"] = collection }),
            ["orderBy"] = new JsonArray(
                new JsonObject { ["field"] = new JsonObject { ["fieldPath"] = "serverAt" }, ["direction"] = "ASCENDING" },
                new JsonObject { ["field"] = new JsonObject { ["fieldPath"] = "__name__" }, ["direction"] = "ASCENDING" }),
            ["limit"] = limit,
        };
        if (cursor?.Split('|', 2) is [var ts, var name])
            query["startAt"] = new JsonObject
            {
                ["values"] = new JsonArray(new JsonObject { ["timestampValue"] = ts }, new JsonObject { ["referenceValue"] = name }),
                ["before"] = false,
            };
        var reply = await SendAsync($"{config.FirestoreBase}/{UserPath}:runQuery", new JsonObject { ["structuredQuery"] = query }, ct).ConfigureAwait(false);
        var docs = new List<RemoteDoc>();
        var next = cursor;
        foreach (var item in reply.AsArray().OfType<JsonObject>())
        {
            if (item["document"] is not JsonObject doc) continue;
            var fullName = doc["name"]!.GetValue<string>();
            var fields = doc["fields"] as JsonObject ?? [];
            docs.Add(new RemoteDoc(collection, fullName[(fullName.LastIndexOf('/') + 1)..], Decode(fields)));
            if (fields["serverAt"]?["timestampValue"]?.GetValue<string>() is { } at) next = $"{at}|{fullName}";
        }
        return new RemotePage(docs, next);
    }

    private async Task<JsonNode> SendAsync(string url, JsonObject body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new("Bearer", await token(attempt > 0, ct).ConfigureAwait(false));
            using var r = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (r.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue; // token expired early: refresh once
            var text = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode)
            {
                var err = JsonNode.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
                var msg = (err as JsonObject)?["error"]?["message"]?.ToString()
                    ?? (err as JsonArray)?.FirstOrDefault()?["error"]?["message"]?.ToString();
                throw new SyncException(msg ?? $"HTTP {(int)r.StatusCode}", r.StatusCode);
            }
            return JsonNode.Parse(text) ?? new JsonObject();
        }
    }

    private static JsonObject Encode(IReadOnlyDictionary<string, object?> fields)
    {
        var o = new JsonObject();
        foreach (var (k, v) in fields)
            o[k] = v switch
            {
                string s => new JsonObject { ["stringValue"] = s },
                long l => new JsonObject { ["integerValue"] = l.ToString(CultureInfo.InvariantCulture) },
                int i => new JsonObject { ["integerValue"] = i.ToString(CultureInfo.InvariantCulture) },
                bool b => new JsonObject { ["booleanValue"] = b },
                _ => new JsonObject { ["nullValue"] = null },
            };
        return o;
    }

    private static Dictionary<string, object?> Decode(JsonObject fields)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in fields)
        {
            if (v is not JsonObject val) continue;
            d[k] = val switch
            {
                _ when val["stringValue"] is { } s => s.GetValue<string>(),
                _ when val["integerValue"] is { } i => long.Parse(i.ToString(), CultureInfo.InvariantCulture),
                _ when val["booleanValue"] is { } b => b.GetValue<bool>(),
                _ when val["timestampValue"] is { } t => t.GetValue<string>(),
                _ => null,
            };
        }
        return d;
    }
}
