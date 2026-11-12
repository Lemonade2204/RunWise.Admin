namespace RunWise.Admin.Services;

// From configuration section "Supabase".
// Url and PublishableKey are public (they're in the app too) and live in appsettings.json.
// SecretKey bypasses all security rules: it stays on the server and is never committed.
// Locally:   dotnet user-secrets set "Supabase:SecretKey" "sb_secret_..."
// Hosted:    environment variable Supabase__SecretKey
public sealed class SupabaseOptions
{
    public string Url { get; set; } = "";
    public string PublishableKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
}
