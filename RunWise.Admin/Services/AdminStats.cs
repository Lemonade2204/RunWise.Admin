namespace RunWise.Admin.Services;

public sealed record WeekTotal(DateTime WeekStart, int Runs, double Km);

public sealed record RetentionResult(int Days, int Eligible, int Retained)
{
    public double Share => Eligible > 0 ? (double)Retained / Eligible : 0;
}

// Totals across all users for the dashboard. Pure functions over the run and user lists.
public static class AdminStats
{
    public static DateTime WeekStart(DateTime localDate) => localDate.Date.AddDays(-(((int)localDate.DayOfWeek + 6) % 7)); // Monday

    // Runs and km per Monday-to-Sunday week, oldest first, ending with the current week
    public static List<WeekTotal> Weekly(IEnumerable<RunRow> runs, AdminClock clock, int weeks)
    {
        var thisWeek = WeekStart(clock.Today);
        var byWeek = runs.GroupBy(r => WeekStart(clock.ToLocal(r.StartedAt)))
                         .ToDictionary(g => g.Key, g => (Runs: g.Count(), Km: g.Sum(r => r.DistanceKm)));
        return Enumerable.Range(0, weeks)
            .Select(i => thisWeek.AddDays(-7 * (weeks - 1 - i)))
            .Select(w => byWeek.TryGetValue(w, out var t) ? new WeekTotal(w, t.Runs, t.Km) : new WeekTotal(w, 0, 0))
            .ToList();
    }

    // Of the users who signed up at least `days` days ago, how many recorded a run `days` or more days after signing up
    public static RetentionResult Retention(IEnumerable<UserRow> users, IEnumerable<RunRow> runs, int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var eligible = users.Where(u => u.CreatedAt > DateTime.MinValue && u.CreatedAt <= cutoff).ToList();
        var runsByUser = runs.ToLookup(r => r.UserId);
        var retained = eligible.Count(u => runsByUser[u.Id].Any(r => r.StartedAt >= u.CreatedAt.AddDays(days)));
        return new RetentionResult(days, eligible.Count, retained);
    }

    // Runs per [weekday (Mon=0), hour] in local time
    public static int[,] PeakGrid(IEnumerable<RunRow> runs, AdminClock clock)
    {
        var grid = new int[7, 24];
        foreach (var r in runs)
        {
            var t = clock.ToLocal(r.StartedAt);
            grid[((int)t.DayOfWeek + 6) % 7, t.Hour]++;
        }
        return grid;
    }

    // Busiest weekday and hour, e.g. ("Saturday", "6 AM")
    public static (string Day, string Hour)? Busiest(int[,] grid)
    {
        int bestDay = -1, bestHour = -1, best = 0;
        var dayTotals = new int[7];
        var hourTotals = new int[24];
        for (int d = 0; d < 7; d++)
            for (int h = 0; h < 24; h++)
            {
                dayTotals[d] += grid[d, h];
                hourTotals[h] += grid[d, h];
            }
        for (int d = 0; d < 7; d++) if (dayTotals[d] > best) { best = dayTotals[d]; bestDay = d; }
        best = 0;
        for (int h = 0; h < 24; h++) if (hourTotals[h] > best) { best = hourTotals[h]; bestHour = h; }
        if (bestDay < 0 || bestHour < 0) return null;
        return (DayName(bestDay), HourName(bestHour));
    }

    public static string DayName(int mondayBased) =>
        System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)((mondayBased + 1) % 7));

    public static string HourName(int hour) => hour switch
    {
        0 => "12 AM",
        < 12 => $"{hour} AM",
        12 => "12 PM",
        _ => $"{hour - 12} PM",
    };

    // Share of runs with heart rate recorded (Health Connect / a watch)
    public static (int WithHeartRate, int Total) HeartRateShare(IEnumerable<RunRow> runs)
    {
        var list = runs as ICollection<RunRow> ?? runs.ToList();
        return (list.Count(r => r.HasHeartRate), list.Count);
    }

    public static string Pace(double km, double seconds)
    {
        if (km < 0.01 || seconds <= 0) return "–";
        var p = TimeSpan.FromSeconds(seconds / km);
        return p.TotalMinutes >= 60 ? "–" : $"{(int)p.TotalMinutes}:{p.Seconds:00} /km";
    }

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
    }
}
