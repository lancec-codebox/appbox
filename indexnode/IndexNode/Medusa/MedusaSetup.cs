using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QRCoder;

namespace IndexNode;

/// <summary>Settings for a Medusa phone node, as a medusa://node-setup link (QR) or a .medusa-node.json file. The SAS is a secret: it only ever goes into the QR, the PNG or the file.</summary>
public static class MedusaSetup
{
    public const string Kind = "medusa-node-settings", Scheme = "medusa://node-setup?d=";
    public const string Warning = "contains your storage key; delete after use";

    public sealed record Options(string? Sas, string? NodeName, string Conditions = "wifi+charging", int DailyCap = 20, int? Budget = null, string? Schedule = null, string? Window = null, int MaxHeight = 480);

    public static JsonObject Build(Options o)
    {
        if (o.Conditions is not ("wifi+charging" or "wifi")) throw new ArgumentException("--conditions: wifi+charging or wifi");
        if (o.DailyCap < 0) throw new ArgumentException("--daily-cap must be 0 or more");
        if (o.Schedule != null) CronExpr.Parse(o.Schedule);
        if (o.Window != null) WatchStage.ParseEvery(o.Window);
        return new JsonObject
        {
            ["v"] = 1, ["kind"] = Kind, ["sas"] = o.Sas, ["nodeName"] = o.NodeName, ["conditions"] = o.Conditions, ["dailyCap"] = o.DailyCap,
            ["budget"] = o.Budget, ["schedule"] = o.Schedule, ["window"] = o.Window, ["maxHeight"] = o.MaxHeight,
        };
    }

    public const string WebBase = "https://lancec-codebox.github.io/appbox/setup/#d=";

    /// <summary>The https hand-off page link (Medusa 1.7.4+): it opens medusa:// itself, so the QR works before Medusa is installed.</summary>
    public static string WebLink(JsonObject settings) => WebBase + B64(Encoding.UTF8.GetBytes(settings.ToJsonString()));

    public static string Link(JsonObject settings) => Scheme + B64(Encoding.UTF8.GetBytes(settings.ToJsonString()));

    public static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/'); s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    /// <summary>The round trip: link to settings JSON.</summary>
    public static JsonObject Parse(string link)
    {
        string payload;
        if (link.StartsWith(Scheme)) payload = link[Scheme.Length..];
        else if (link.StartsWith(WebBase)) payload = link[WebBase.Length..];
        else throw new FormatException("not a medusa://node-setup or setup page link");
        var j = JsonNode.Parse(Encoding.UTF8.GetString(FromB64(payload)))!.AsObject();
        if (j["kind"]?.GetValue<string>() != Kind) throw new FormatException("not medusa node settings");
        return j;
    }

    public static byte[] QrPng(string link)
    {
        using var gen = new QRCodeGenerator(); using var data = gen.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(8);
    }

    /// <summary>Terminal drawing: two QR rows per text line with half blocks, quiet zone included.</summary>
    public static string QrTerminal(string link)
    {
        using var gen = new QRCodeGenerator(); using var data = gen.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
        var m = data.ModuleMatrix; int n = m.Count; var sb = new StringBuilder();
        bool Dark(int r, int c) => r >= 0 && r < n && c >= 0 && c < n && m[r][c];
        for (int r = -2; r < n + 2; r += 2)
        {
            for (int c = -2; c < n + 2; c++) { bool t = Dark(r, c), b = Dark(r + 1, c); sb.Append(t && b ? '█' : t ? '▀' : b ? '▄' : ' '); }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public static string DefaultDir() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "indexnode", "setup")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "indexnode", "setup");

    /// <summary>User-only folder and file (mode 700/600 on Linux).</summary>
    public static void WritePrivate(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!; Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) { try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { } }
        File.WriteAllBytes(path, []);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllBytes(path, data);
    }

    /// <summary>The command. Returns what goes to the terminal: never the SAS in text, only the QR drawing and file paths.</summary>
    public static string Run(Options o, bool qr, string? file, string? dir, bool noSas)
    {
        if (!noSas && string.IsNullOrWhiteSpace(o.Sas)) throw new InvalidOperationException("no SAS in this node's config: run 'indexnode config set-sas <url>' or use --no-sas");
        if (!noSas && string.IsNullOrWhiteSpace(o.NodeName)) throw new ArgumentException("--name is required when the SAS is included: each helper's node needs its own name");
        var settings = Build(noSas ? o with { Sas = null } : o);
        var link = WebLink(settings); var sb = new StringBuilder(); var folder = dir ?? DefaultDir(); bool secret = !noSas;
        if (!qr && file == null) qr = true;
        if (qr)
        {
            var png = Path.Combine(folder, "medusa-node-setup.png"); WritePrivate(png, QrPng(link));
            sb.Append(QrTerminal(link)).Append($"QR image: {png}\n");
        }
        // the https link carries the SAS inside the payload, so it is only ever written to the QR/PNG, never printed when a SAS is included
        if (!secret) sb.Append($"link: {link}\n");
        if (file != null) { WritePrivate(file, Encoding.UTF8.GetBytes(settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))); sb.Append($"settings file: {file}\n"); }
        sb.Append("Scan the QR with the phone camera (it opens Medusa), or open the file in Medusa.\n");
        if (secret) sb.Append("NOTE: " + Warning + ".\n");
        return sb.ToString();
    }
}
