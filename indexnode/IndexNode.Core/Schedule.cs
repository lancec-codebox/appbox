using System.Globalization;

namespace IndexNode;

/// <summary>
/// When a source is refreshed after its lookup (0.10.3): "once"/"off" (never again unless triggered by hand), "every N s/m/h/d" (written 6h, 1d), or a daily time (daily@03:00, UTC).
/// A job written before 0.10.3 has only <see cref="JobDescriptor.Every"/>, which stays its schedule.
/// </summary>
public sealed record Schedule(string Kind, TimeSpan Interval, TimeSpan At, string Text)
{
    public static readonly Schedule Once = new("once", TimeSpan.Zero, TimeSpan.Zero, "once");
    public static readonly Schedule Off = new("off", TimeSpan.Zero, TimeSpan.Zero, "off");

    /// <summary>0.13.0: no explicit schedule. The next check comes from how old the source's newest upload is (see <see cref="DynamicSchedule"/>).</summary>
    public static readonly Schedule Dynamic = new("dynamic", TimeSpan.Zero, TimeSpan.Zero, "dynamic");

    public bool Periodic => Kind is "every" or "daily" or "dynamic";
    public bool IsDynamic => Kind == "dynamic";

    public static Schedule Parse(string? s)
    {
        s = s?.Trim() ?? "";
        if (s.Length == 0 || s.Equals("once", StringComparison.OrdinalIgnoreCase)) return Once;
        if (s.Equals("off", StringComparison.OrdinalIgnoreCase)) return Off;
        if (s.Equals("dynamic", StringComparison.OrdinalIgnoreCase)) return Dynamic;
        if (s.StartsWith("daily@", StringComparison.OrdinalIgnoreCase))
        {
            if (!TimeSpan.TryParseExact(s[6..], @"h\:mm", CultureInfo.InvariantCulture, out var at) && !TimeSpan.TryParseExact(s[6..], @"hh\:mm", CultureInfo.InvariantCulture, out at))
                throw new ArgumentException($"bad schedule '{s}' (daily@03:00 needs a time of day, UTC)");
            return new("daily", TimeSpan.Zero, at, "daily@" + at.ToString(@"hh\:mm"));
        }
        var every = RefreshStage.ParseEvery(s.StartsWith("every ", StringComparison.OrdinalIgnoreCase) ? s[6..] : s); // throws a clear error for anything else
        return new("every", every, TimeSpan.Zero, s.StartsWith("every ", StringComparison.OrdinalIgnoreCase) ? s[6..].Trim() : s);
    }

    /// <summary>The schedule of a source job: its schedule, else the interval it was added with before 0.10.3, else once.</summary>
    public static Schedule Of(JobDescriptor j) => Parse(j.Schedule ?? j.Every);

    /// <summary>True for a source whose schedule was set on purpose (every 6h, daily@03:00, off, once); a dynamic one is not explicit.</summary>
    public bool Explicit => Kind != "dynamic";

    /// <summary>The next time after <paramref name="now"/>; null for once and off.</summary>
    public DateTimeOffset? Next(DateTimeOffset now)
    {
        if (Kind == "every") return now + Interval;
        if (Kind != "daily") return null;
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero) + At;
        return today > now ? today : today.AddDays(1);
    }

    public string Describe() => Kind switch { "dynamic" => "dynamic", "every" => "every " + Text, "daily" => "daily " + Text[6..] + " UTC", _ => Text };
}

/// <summary>
/// 0.13.0 dynamic refresh schedule. Tier: the smallest of 1, 2, 4, 8, 16, 32 days that is at least the time since the source was last modified (older than 32 days, or never known, is 32).
/// Next check: a uniformly random whole number of hours in [tier/2, tier] days, counted from this check (1 d: 12-24 h, 2 d: 24-48 h ... 32 d: 16-32 d). A source never polled is due now.
/// </summary>
public static class DynamicSchedule
{
    public static readonly int[] Tiers = [1, 2, 4, 8, 16, 32];

    /// <summary>The tier in days for a newest upload at <paramref name="newestUpload"/> (null: none known), as seen at <paramref name="now"/>.</summary>
    public static int Tier(DateTimeOffset now, DateTimeOffset? newestUpload)
    {
        if (newestUpload is not { } u) return 32;
        var age = (now - u).TotalDays;
        foreach (var t in Tiers) if (age <= t) return t;   // a future or same-day date (age <= 1) is tier 1
        return 32;
    }

    /// <summary>Owner rule: a dynamic source last modified more than a year (365 days) ago is inactive: nothing is scheduled until a manual refresh or a new schedule. Never known is not inactive (tier 32).</summary>
    public static bool IsInactive(DateTimeOffset now, DateTimeOffset? newestUpload) => newestUpload is { } u && (now - u).TotalDays > 365;

    /// <summary>The delay to the next check: <paramref name="nextInt"/>(minInclusive, maxExclusive) picks the whole number of hours in [tier*12, tier*24].</summary>
    public static TimeSpan Delay(int tierDays, Func<int, int, int>? nextInt = null)
    {
        int lo = tierDays * 12, hi = tierDays * 24;
        int h = (nextInt ?? Random.Shared.Next)(lo, hi + 1);
        return TimeSpan.FromHours(Math.Clamp(h, lo, hi));
    }

    /// <summary>yt-dlp's upload_date (YYYYMMDD) as a UTC date; null when missing ("NA") or not a date.</summary>
    public static DateTimeOffset? ParseUploadDate(string? s) =>
        s is { Length: 8 } && DateTimeOffset.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    /// <summary>"4d" for the status lines.</summary>
    public static string TierText(int days) => days + "d";
}
