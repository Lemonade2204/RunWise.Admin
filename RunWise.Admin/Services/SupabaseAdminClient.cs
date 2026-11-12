using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RunWise.Admin.Services;

public sealed record AdminUser(string Id, string Email);

// An account from Supabase Auth, joined with its profile and run totals for the Users page
public sealed record UserRow(
    string Id,
    string Email,
    DateTime CreatedAt,
    DateTime? LastSignInAt,
    bool EmailConfirmed,
    bool Disabled)
{
    public string? Username { get; init; }
    public int Runs { get; init; }
    public double TotalKm { get; init; }
    public DateTime? LastRunAt { get; init; }
}

// Only what the dashboard needs from a run: no routes or health values. HasHeartRate only says
// whether heart rate was recorded (to measure Health Connect use), never the bpm itself.
public sealed record RunRow(string UserId, double DistanceKm, int DurationSeconds, DateTime StartedAt, bool HasHeartRate = false);

// Talks to Supabase from the server. Requests made with the secret key bypass Row Level Security,
// so every caller must already be a signed-in admin (the layout only renders pages for admins).
public sealed class SupabaseAdminClient(HttpClient http, SupabaseOptions options, ILogger<SupabaseAdminClient> log)
{
    private const int PageSize = 1000;   // Supabase returns at most 1000 rows per request by default

    // ---------- Sign-in ----------

    // Checks the password with Supabase Auth, then that the account is in the admins table
    public async Task<(AdminUser? Admin, string? Error)> SignInAsync(string email, string password)
    {
        using var req = Request(HttpMethod.Post, "/auth/v1/token?grant_type=password", secret: false,
            new { email = email.Trim(), password });
        using var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            return (null, "Wrong email or password.");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("user", out var user) || Str(user, "id") is not string id)
            return (null, "Sign-in failed. Please try again.");

        var (isAdmin, checkError) = await IsAdminAsync(id);
        if (checkError != null)
            return (null, checkError);
        if (!isAdmin)
        {
            log.LogWarning("Non-admin sign-in attempt for user {UserId}", id);
            return (null, "This account isn't on the admin list. Run the insert from admins.sql with this email.");
        }
        return (new AdminUser(id, Str(user, "email") ?? email), null);
    }

    // Error is set when the check itself failed (bad secret key, missing table), so that
    // isn't confused with "this account isn't an admin"
    public async Task<(bool IsAdmin, string? Error)> IsAdminAsync(string userId)
    {
        if (!Guid.TryParse(userId, out _)) return (false, null);
        using var req = Request(HttpMethod.Get, $"/rest/v1/admins?select=user_id&user_id=eq.{userId}", secret: true);
        using var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            log.LogError("Admin check failed: {Status} {Body}", resp.StatusCode, body);
            return (false, (int)resp.StatusCode switch
            {
                401 or 403 => "Supabase rejected the secret key. Set it again with dotnet user-secrets (it starts with sb_secret_).",
                404 => "The admins table wasn't found. Run admins.sql in the Supabase SQL Editor.",
                _ => $"Couldn't check admin access (Supabase {(int)resp.StatusCode}): {Short(body)}"
            });
        }
        using var doc = JsonDocument.Parse(body);
        return (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0, null);
    }

    private static string Short(string text) => text.Length <= 160 ? text : text[..160] + "…";

    // ---------- Users ----------

    public async Task<List<UserRow>> GetUsersAsync()
    {
        var accounts = await ListAuthUsersAsync();
        var usernames = await GetUsernamesAsync();
        var runsByUser = (await GetRunsAsync(since: null)).ToLookup(r => r.UserId);

        return accounts.Select(u =>
        {
            var runs = runsByUser[u.Id].ToList();
            return u with
            {
                Username = usernames.GetValueOrDefault(u.Id),
                Runs = runs.Count,
                TotalKm = runs.Sum(r => r.DistanceKm),
                LastRunAt = runs.Count > 0 ? runs.Max(r => r.StartedAt) : null
            };
        }).OrderByDescending(u => u.CreatedAt).ToList();
    }

    private async Task<List<UserRow>> ListAuthUsersAsync()
    {
        var users = new List<UserRow>();
        for (int page = 1; ; page++)
        {
            using var req = Request(HttpMethod.Get, $"/auth/v1/admin/users?page={page}&per_page=200", secret: true);
            using var resp = await http.SendAsync(req);
            await EnsureOk(resp, "list users");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("users", out var list) || list.GetArrayLength() == 0) break;

            foreach (var u in list.EnumerateArray())
            {
                users.Add(new UserRow(
                    Str(u, "id") ?? "",
                    Str(u, "email") ?? "(no email)",
                    Date(u, "created_at") ?? DateTime.MinValue,
                    Date(u, "last_sign_in_at"),
                    Date(u, "email_confirmed_at") != null,
                    Date(u, "banned_until") > DateTime.UtcNow));
            }
            if (list.GetArrayLength() < 200) break;
        }
        return users;
    }

    private async Task<Dictionary<string, string>> GetUsernamesAsync()
    {
        var names = new Dictionary<string, string>();
        await foreach (var p in PagedAsync("/rest/v1/profiles?select=id,username&order=id"))
            if (Str(p, "id") is string id && Str(p, "username") is string name && name.Length > 0)
                names[id] = name;
        return names;
    }

    // Disabling bans the account in Supabase Auth: it can't sign in, and existing sessions stop refreshing
    public async Task<string?> SetDisabledAsync(string userId, bool disabled)
    {
        if (!Guid.TryParse(userId, out _)) return "Unknown user.";
        using var req = Request(HttpMethod.Put, $"/auth/v1/admin/users/{userId}", secret: true,
            new { ban_duration = disabled ? "876000h" : "none" });   // ~100 years, or lift the ban
        using var resp = await http.SendAsync(req);
        return resp.IsSuccessStatusCode ? null : $"Couldn't update the account ({(int)resp.StatusCode}).";
    }

    // Same email the app's "Forgot password" sends
    public async Task<string?> SendPasswordResetAsync(string email)
    {
        using var req = Request(HttpMethod.Post, "/auth/v1/recover", secret: false, new { email });
        using var resp = await http.SendAsync(req);
        return resp.IsSuccessStatusCode ? null : $"Couldn't send the email ({(int)resp.StatusCode}).";
    }

    // ---------- Runs ----------

    public async Task<List<RunRow>> GetRunsAsync(DateTime? since, string? userId = null)
    {
        var filter = since is DateTime s ? $"&datecreated=gte.{Uri.EscapeDataString(s.ToUniversalTime().ToString("o"))}" : "";
        if (userId != null)
        {
            if (!Guid.TryParse(userId, out _)) return new();
            filter += $"&user_id=eq.{userId}";
        }

        var runs = new List<RunRow>();
        await foreach (var r in PagedAsync($"/rest/v1/runs?select=id,user_id,distance_km,duration_seconds,datecreated,avg_heart_rate{filter}&order=datecreated.desc"))
        {
            if (Str(r, "user_id") is not string owner || Date(r, "datecreated") is not DateTime started) continue;
            var hasHeartRate = r.TryGetProperty("avg_heart_rate", out var hr) && hr.ValueKind == JsonValueKind.Number;
            runs.Add(new RunRow(owner, Num(r, "distance_km"), (int)Num(r, "duration_seconds"), started, hasHeartRate));
        }
        return runs;
    }

    // One account for the user detail page, with its username
    public async Task<UserRow?> GetUserAsync(string userId)
    {
        if (!Guid.TryParse(userId, out _)) return null;
        using var req = Request(HttpMethod.Get, $"/auth/v1/admin/users/{userId}", secret: true);
        using var resp = await http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureOk(resp, "get user");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var u = doc.RootElement;
        var row = new UserRow(
            Str(u, "id") ?? userId,
            Str(u, "email") ?? "(no email)",
            Date(u, "created_at") ?? DateTime.MinValue,
            Date(u, "last_sign_in_at"),
            Date(u, "email_confirmed_at") != null,
            Date(u, "banned_until") > DateTime.UtcNow);

        string? username = null;
        await foreach (var p in PagedAsync($"/rest/v1/profiles?select=username&id=eq.{userId}"))
            username = Str(p, "username");
        return row with { Username = string.IsNullOrEmpty(username) ? null : username };
    }

    // ---------- Plumbing ----------

    private async IAsyncEnumerable<JsonElement> PagedAsync(string path)
    {
        for (int offset = 0; ; offset += PageSize)
        {
            using var req = Request(HttpMethod.Get, $"{path}&limit={PageSize}&offset={offset}", secret: true);
            using var resp = await http.SendAsync(req);
            await EnsureOk(resp, path.Split('?')[0]);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var count = 0;
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                count++;
                yield return row.Clone();
            }
            if (count < PageSize) yield break;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, bool secret, object? body = null)
    {
        var key = secret ? options.SecretKey : options.PublishableKey;
        var req = new HttpRequestMessage(method, options.Url.TrimEnd('/') + path);
        req.Headers.Add("apikey", key);
        // New sb_secret_ keys go only in "apikey" (Supabase turns them into the service role).
        // Older service_role keys are JWTs ("eyJ…") and also go in Authorization.
        if (secret && key.StartsWith("eyJ", StringComparison.Ordinal))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body != null) req.Content = JsonContent.Create(body);
        return req;
    }

    private async Task EnsureOk(HttpResponseMessage resp, string what)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        log.LogError("Supabase {What} failed: {Status} {Body}", what, resp.StatusCode, body);
        throw new InvalidOperationException($"Supabase request failed ({what}, {(int)resp.StatusCode}).");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static DateTime? Date(JsonElement e, string name) =>
        Str(e, name) is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
