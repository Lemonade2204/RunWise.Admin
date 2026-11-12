using RunWise.Admin.Components;
using RunWise.Admin.Services;

var builder = WebApplication.CreateBuilder(args);

// Supabase settings. The secret key comes from user-secrets (local) or an environment variable (hosted).
var supabase = builder.Configuration.GetSection("Supabase").Get<SupabaseOptions>() ?? new SupabaseOptions();
if (string.IsNullOrWhiteSpace(supabase.Url) || string.IsNullOrWhiteSpace(supabase.PublishableKey))
    throw new InvalidOperationException("Supabase:Url and Supabase:PublishableKey are missing from appsettings.json.");
if (string.IsNullOrWhiteSpace(supabase.SecretKey))
    throw new InvalidOperationException(
        "Supabase:SecretKey is missing. In the RunWise.Admin project folder run:\n" +
        "  dotnet user-secrets set \"Supabase:SecretKey\" \"sb_secret_...\"\n" +
        "(Supabase dashboard → Project Settings → API Keys → Secret keys). Never put it in appsettings.json.");

builder.Services.AddSingleton(supabase);
builder.Services.AddHttpClient<SupabaseAdminClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<AdminSession>();
builder.Services.AddSingleton<AdminClock>();   // dates shown in Philippine time (Display:TimeZone)

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
