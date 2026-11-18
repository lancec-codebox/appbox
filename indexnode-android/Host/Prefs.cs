using Android.Content;

namespace IndexNode.Host;

/// <summary>Settings and secrets in the app's private SharedPreferences (not backed up: allowBackup is off). The SAS and the Medusa token live here and nowhere else.</summary>
public sealed class Prefs(Context ctx)
{
    private readonly ISharedPreferences p = ctx.GetSharedPreferences("indexnode", FileCreationMode.Private)!;
    public string? Get(string k) => p.GetString(k, null);
    public void Set(string k, string? v) { var e = p.Edit()!; if (v == null) e.Remove(k); else e.PutString(k, v); e.Apply(); }
    public int Int(string k, int d) => int.TryParse(Get(k), out var v) ? v : d;
    public bool Bool(string k, bool d) => Get(k) is { } s ? s == "true" : d;

    public string? Sas { get => Get("sas"); set => Set("sas", value); }
    public string? MedusaToken { get => Get("medusaToken"); set => Set("medusaToken", value); }

    /// <summary>Jobs started today (local date): the daily cap counts these.</summary>
    public int DoneToday { get => Get("day") == DateTime.Now.ToString("yyyy-MM-dd") ? Int("done", 0) : 0; }
    public void AddDone(int n) { var today = DateTime.Now.ToString("yyyy-MM-dd"); Set("day", today); Set("done", (DoneToday + n).ToString()); }
}
