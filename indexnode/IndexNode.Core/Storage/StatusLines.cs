namespace IndexNode;

public static class StatusLines
{
    /// <summary>The status line of the media table SAS: whether it is configured and when it expires.</summary>
    public static string MediaTable(string? sas, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(sas)) return "media table: not configured (per-upload facts stay in video.json and the aggregate; config set-media-table-sas <url>)";
        var e = RestMediaTable.Expiry(sas);
        if (e == null) return "media table: configured (the SAS has no expiry field)";
        var left = e.Value - now;
        return $"media table: SAS expires {e.Value:yyyy-MM-dd HH:mm}Z ({(left <= TimeSpan.Zero ? "EXPIRED" : left.TotalDays >= 1 ? $"in {left.TotalDays:0} days" : $"in {left.TotalHours:0} hours")})";
    }
}
