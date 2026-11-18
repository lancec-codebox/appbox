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

    public bool Periodic => Kind is "every" or "daily";

    public static Schedule Parse(string? s)
    {
        s = s?.Trim() ?? "";
        if (s.Length == 0 || s.Equals("once", StringComparison.OrdinalIgnoreCase)) return Once;
        if (s.Equals("off", StringComparison.OrdinalIgnoreCase)) return Off;
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

    /// <summary>The next time after <paramref name="now"/>; null for once and off.</summary>
    public DateTimeOffset? Next(DateTimeOffset now)
    {
        if (Kind == "every") return now + Interval;
        if (Kind != "daily") return null;
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero) + At;
        return today > now ? today : today.AddDays(1);
    }

    public string Describe() => Kind switch { "every" => "every " + Text, "daily" => "daily " + Text[6..] + " UTC", _ => Text };
}
