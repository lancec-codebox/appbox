namespace IndexNode;

/// <summary>0.13.37 "media import [--out-local DIR | --from-local DIR]" and "seq renumber [--out-local DIR | --from-local DIR]".</summary>
public static class MediaCommand
{
    public static async Task<int> RunAsync(Args a)
    {
        if (a.Positional.FirstOrDefault() == "selftest") return await SelfTestAsync(a);
        if (a.Positional.FirstOrDefault() == "verify") return await VerifyAsync(a);
        if (a.Positional.FirstOrDefault() != "import") throw new ArgumentException("media import [--out-local <dir> | --from-local <dir>] | media selftest | media verify [--limit N]");
        var (_, store, paths, http) = Commands.Open(a); using var _h = http;
        var table = MediaTables.For(store);
        var from = a.Get("from-local"); var outDir = a.Get("out-local");
        List<(string Id, System.Text.Json.Nodes.JsonObject Props)> rows;
        if (from != null) rows = MediaImport.ReadLocal(from);
        else
        {
            var (r, rep) = await MediaImport.FromAggregateAsync(store, paths, default); rows = r;
            Console.WriteLine(MediaImport.ReportText(rep));
            if (outDir != null) { MediaImport.WriteLocal(rows, rep, outDir); Console.WriteLine($"rows written to {outDir} (nothing was written to the store or the table)"); return 0; }
        }
        if (table == null) throw new InvalidOperationException("no table SAS: 'indexnode config set-table-sas <url>' (or --out-local to plan only)");
        Console.WriteLine($"applied {await MediaImport.ApplyAsync(table, rows, default)} batch(es) for {rows.Count} row(s)");
        return 0;
    }

    /// <summary>media selftest: the table calls against a scratch partition of the configured table (the real REST client, never an overlay); prints pass/fail per call, deletes its rows; exit 1 on any failure.</summary>
    static async Task<int> SelfTestAsync(Args a)
    {
        var cfg = NodeConfig.Load();
        if (string.IsNullOrWhiteSpace(cfg.MediaTableSas)) throw new InvalidOperationException("no table SAS: 'indexnode config set-media-table-sas <url>' (or INDEXNODE_MEDIA_TABLE_SAS_URL)");
        using var http = Commands.NewHttp();
        var table = new RestMediaTable(http, cfg.MediaTableSas);
        if (RestMediaTable.Expiry(cfg.MediaTableSas) is { } exp) Console.WriteLine($"table SAS expires {exp:u}");
        var res = await MediaSelfTest.RunAsync(table, default);
        foreach (var r in res) Console.WriteLine($"{(r.Ok ? "PASS" : "FAIL")}  {r.Call}: {r.Detail}");
        Console.WriteLine(res.All(r => r.Ok) ? "selftest: all calls passed" : $"selftest: {res.Count(r => !r.Ok)} call(s) FAILED");
        return res.All(r => r.Ok) ? 0 : 1;
    }

    /// <summary>media verify [--limit N]: read-only, the row against video.json section by section.</summary>
    static async Task<int> VerifyAsync(Args a)
    {
        var (_, store, paths, http) = Commands.Open(a); using var _h = http;
        var table = MediaTables.For(store) ?? throw new InvalidOperationException("media verify needs the media table");
        int? limit = a.Get("limit") is { } l ? int.Parse(l) : null;
        var rep = await MediaVerify.RunAsync(table, store, paths, limit, default);
        foreach (var d in rep.Diffs) Console.WriteLine($"{d.Id}  {d.Section}  {d.Kind}: {d.Detail}");
        Console.WriteLine($"verify: {rep.Checked} upload(s) checked, {rep.Clean} identical, {rep.Diffs.Count} difference(s)");
        return rep.Diffs.Count == 0 ? 0 : 1;
    }

    public static async Task<int> RenumberAsync(Args a)
    {
        var (_, store, paths, http) = Commands.Open(a); using var _h = http;
        var table = MediaTables.For(store) ?? throw new InvalidOperationException("seq renumber needs the media table");
        var from = a.Get("from-local"); var outDir = a.Get("out-local");
        if (from != null) { var plan = SeqBlobs.ReadLocal(from); await SeqBlobs.ApplyAsync(store, paths, table, plan, default); Console.WriteLine($"renumbered {plan.Map.Count}, dropped {plan.Dropped.Count}"); return 0; }
        var p = await SeqBlobs.PlanFromTableAsync(table, default);
        Console.WriteLine(SeqBlobs.ReportText(p).Split('\n')[0]);
        if (outDir == null) throw new ArgumentException("seq renumber --out-local <dir> (plan) then --from-local <dir> (apply, with the queue drained and no live leases)");
        SeqBlobs.WriteLocal(p, outDir); Console.WriteLine($"plan written to {outDir}"); return 0;
    }
}
