namespace RunWise.Admin.Services;

// Shows every date in one time zone (config "Display:TimeZone", default Asia/Manila), so "today",
// weekdays and peak hours are right no matter which time zone the server is hosted in.
public sealed class AdminClock
{
    public TimeZoneInfo Zone { get; }

    public AdminClock(IConfiguration config)
    {
        var id = config["Display:TimeZone"] is { Length: > 0 } configured ? configured : "Asia/Manila";
        Zone = Find(id) ?? Find("Singapore Standard Time") ?? TimeZoneInfo.Local;   // Windows name for UTC+8
    }

    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
    public DateTime Today => Now.Date;

    // Stored times are UTC
    public DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    // Start of a local day, as UTC for comparing with stored times
    public DateTime ToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);

    public string ZoneName => Zone.Id == "Asia/Manila" || Zone.Id == "Singapore Standard Time" ? "Philippine time" : Zone.DisplayName;

    private static TimeZoneInfo? Find(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return null; }
    }
}
