using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace IndexNode;

/// <summary>One unit of an upload: a face cluster (after the recursive split) with its mbf centre and 3-4 representative vectors (Vec[0] = the centre, then the representatives), and the actor it belongs to.</summary>
public sealed class UnitRec
{
    public string Key = "", Actor = ""; public int N; public double First, Last, Child; public int[] Faces = [], Rep = [], Pick = []; public float[][] Vec = [];
}

/// <summary>index/actors/units/&lt;id&gt;.bin: everything the actors stage found for ONE upload (written only by that upload's stage run). Header JSON + float16 vector rows.</summary>
public sealed class UnitDoc
{
    public string Id = "", Prod = "", Kind = "new", At = ""; public bool Manual; public int Dim, FacesUsed, ChildUnits, Gen; public string Grp = ""; public List<UnitRec> Units = []; public JsonArray Links = [];
    /// <summary>0.13.32: Gen 2 = the repaired cast state: the units of this file came from the stored faces under the current band limits, the upload's own r50 file holds a vector for each of them, and nothing later needs cast.zip or frames.zip. Grp = hash of the grouping record the units were built from.</summary>
    public const int CurrentGen = 2;
    public static string GrpOf(JsonObject? rec) => rec == null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(rec.ToJsonString())))[..16];

    public byte[] ToBytes()
    {
        var units = new JsonArray(); var rows = new List<float[]>();
        foreach (var u in Units)
        {
            units.Add(new JsonObject { ["key"] = u.Key, ["n"] = u.N, ["first"] = Math.Round(u.First, 2), ["last"] = Math.Round(u.Last, 2), ["child"] = Math.Round(u.Child, 4), ["actor"] = u.Actor, ["rows"] = u.Vec.Length,
                ["faces"] = new JsonArray(u.Faces.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()), ["rep"] = new JsonArray(u.Rep.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()), ["pick"] = new JsonArray(u.Pick.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()) });
            rows.AddRange(u.Vec);
        }
        var blob = new byte[rows.Count * Dim * 2]; for (int r = 0; r < rows.Count; r++) for (int d = 0; d < Dim; d++) BitConverter.TryWriteBytes(blob.AsSpan((r * Dim + d) * 2, 2), (Half)rows[r][d]);
        return ActorsData.Pack(new JsonObject { ["v"] = 1, ["id"] = Id, ["prod"] = Prod, ["kind"] = Kind, ["at"] = At, ["manual"] = Manual, ["dim"] = Dim, ["facesUsed"] = FacesUsed, ["childUnits"] = ChildUnits, ["gen"] = Gen, ["grp"] = Grp, ["units"] = units, ["links"] = Links.DeepClone() }, blob);
    }

    public static UnitDoc FromBytes(byte[] b, string what)
    {
        var (h, blob) = ActorsData.Unpack(b, what); var d = new UnitDoc { Id = (string?)h["id"] ?? "", Prod = (string?)h["prod"] ?? "", Kind = (string?)h["kind"] ?? "new", At = (string?)h["at"] ?? "", Manual = (bool?)h["manual"] ?? false, Dim = (int?)h["dim"] ?? 0, FacesUsed = (int?)h["facesUsed"] ?? 0, ChildUnits = (int?)h["childUnits"] ?? 0, Gen = (int?)h["gen"] ?? 0, Grp = (string?)h["grp"] ?? "", Links = (h["links"] as JsonArray)?.DeepClone() as JsonArray ?? [] };
        int row = 0;
        foreach (var o in (h["units"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            int n = (int?)o["rows"] ?? 0; if ((row + n) * d.Dim * 2 > blob.Length) throw new InvalidDataException($"{what}: vector rows run past the file");
            var vec = new float[n][]; for (int r = 0; r < n; r++, row++) { vec[r] = new float[d.Dim]; for (int k = 0; k < d.Dim; k++) vec[r][k] = (float)BitConverter.ToHalf(blob, (row * d.Dim + k) * 2); }
            d.Units.Add(new UnitRec { Key = (string?)o["key"] ?? "", N = (int?)o["n"] ?? 0, First = (double?)o["first"] ?? 0, Last = (double?)o["last"] ?? 0, Child = (double?)o["child"] ?? 0, Actor = (string?)o["actor"] ?? "",
                Faces = (o["faces"] as JsonArray)?.Select(x => (int)x!).ToArray() ?? [], Rep = (o["rep"] as JsonArray)?.Select(x => (int)x!).ToArray() ?? [], Pick = (o["pick"] as JsonArray)?.Select(x => (int)x!).ToArray() ?? [], Vec = vec });
        }
        return d;
    }
}

/// <summary>index/actors/state.json: the actor id counter and the redirects of merged actors (from -> to, by which unit, when). Written with If-Match; ids only grow, a merge keeps the older (smaller) id.</summary>
public sealed class ActorState
{
    public int Next = 1; public Dictionary<string, (string To, string By, string At)> Redirects = new(StringComparer.Ordinal);
    public string Alloc() => $"A{Next++:D6}";
    public string Resolve(string id) { for (int i = 0; i < 10000 && Redirects.TryGetValue(id, out var r); i++) id = r.To; return id; }
    /// <summary>Folds actor <paramref name="from"/> into <paramref name="to"/> (both resolved roots; the caller passes the older as to).</summary>
    public void Merge(string from, string to, string by, string at) { if (from != to) Redirects[from] = (to, by, at); }

    public byte[] ToBytes()
    {
        var r = new JsonObject(); foreach (var kv in Redirects.OrderBy(k => k.Key, StringComparer.Ordinal)) r[kv.Key] = new JsonObject { ["to"] = kv.Value.To, ["by"] = kv.Value.By, ["at"] = kv.Value.At };
        return Encoding.UTF8.GetBytes(new JsonObject { ["v"] = 1, ["next"] = Next, ["redirects"] = r }.ToJsonString());
    }
    public static ActorState FromBytes(byte[] b)
    {
        var o = JsonNode.Parse(Encoding.UTF8.GetString(b)) as JsonObject ?? throw new InvalidDataException("actors/state.json: not an object"); var s = new ActorState { Next = (int?)o["next"] ?? 1 };
        foreach (var kv in (o["redirects"] as JsonObject) ?? []) s.Redirects[kv.Key] = ((string?)kv.Value?["to"] ?? "", (string?)kv.Value?["by"] ?? "", (string?)kv.Value?["at"] ?? "");
        return s;
    }
}

/// <summary>index/actors/r50/&lt;id&gt;.bin: the w600k_r50 fp32 centre of up to 30 faces of each unit that has landed in a 0.70-0.75 pair, computed once (CAS-merged into the upload's file).</summary>
public sealed class R50Doc
{
    public int Dim; public Dictionary<string, float[]> Units = new(StringComparer.Ordinal);
    public byte[] ToBytes()
    {
        var keys = Units.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(); var blob = new byte[keys.Count * Dim * 4];
        for (int r = 0; r < keys.Count; r++) for (int d = 0; d < Dim; d++) BitConverter.TryWriteBytes(blob.AsSpan((r * Dim + d) * 4, 4), Units[keys[r]][d]);
        return ActorsData.Pack(new JsonObject { ["v"] = 1, ["dim"] = Dim, ["units"] = new JsonArray(keys.Select(k => (JsonNode)JsonValue.Create(k)).ToArray()) }, blob);
    }
    public static R50Doc FromBytes(byte[] b, string what)
    {
        var (h, blob) = ActorsData.Unpack(b, what); var d = new R50Doc { Dim = (int?)h["dim"] ?? 0 }; var keys = (h["units"] as JsonArray)?.Select(x => (string)x!).ToList() ?? [];
        if (keys.Count * d.Dim * 4 > blob.Length) throw new InvalidDataException($"{what}: vectors run past the file");
        for (int r = 0; r < keys.Count; r++) { var v = new float[d.Dim]; for (int k = 0; k < d.Dim; k++) v[k] = BitConverter.ToSingle(blob, (r * d.Dim + k) * 4); d.Units[keys[r]] = v; }
        return d;
    }
}

public static class ActorsData
{
    public static string UnitsDir(Paths p) => $"{p.Prefix}actors/units/";
    public static string UnitFile(Paths p, string id) => $"{p.Prefix}actors/units/{id}.bin";
    public static string R50File(Paths p, string id) => $"{p.Prefix}actors/r50/{id}.bin";
    public static string R50Dir(Paths p) => $"{p.Prefix}actors/r50/";
    public static string StateFile(Paths p) => $"{p.Prefix}actors/state.json";
    public static string HeadFile(Paths p) => $"{p.Prefix}actors/head.json";
    public static string VersionDir(Paths p, int v) => $"{p.Prefix}actors/v{v}/";

    /// <summary>"AUF1", u32 length of the header JSON, the header JSON, then the raw rows.</summary>
    public static byte[] Pack(JsonObject head, byte[] blob)
    {
        var j = Encoding.UTF8.GetBytes(head.ToJsonString()); var o = new byte[8 + j.Length + blob.Length];
        o[0] = (byte)'A'; o[1] = (byte)'U'; o[2] = (byte)'F'; o[3] = (byte)'1'; BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(4), j.Length); j.CopyTo(o, 8); blob.CopyTo(o, 8 + j.Length); return o;
    }
    public static (JsonObject Head, byte[] Blob) Unpack(byte[] b, string what)
    {
        if (b.Length < 8 || b[0] != 'A' || b[1] != 'U' || b[2] != 'F' || b[3] != '1') throw new InvalidDataException($"{what}: not an actors file");
        int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4)); if (n < 2 || 8 + n > b.Length) throw new InvalidDataException($"{what}: bad header length");
        return (JsonNode.Parse(Encoding.UTF8.GetString(b, 8, n)) as JsonObject ?? throw new InvalidDataException($"{what}: header is not an object"), b.AsSpan(8 + n).ToArray());
    }

    public static async Task<(ActorState State, string? ETag)> LoadStateAsync(ActorsIO io, CancellationToken ct)
        => await io.GetAsync(StateFile(io.Paths), ct) is { } c ? (ActorState.FromBytes(c.Data), c.ETag) : (new ActorState(), null);
    public static Task<bool> SaveStateAsync(ActorsIO io, ActorState s, string? etag, CancellationToken ct) => io.PutAsync(StateFile(io.Paths), s.ToBytes(), etag, etag == null, false, ct);
}
