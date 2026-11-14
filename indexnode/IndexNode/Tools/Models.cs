namespace IndexNode;

/// <summary>One downloadable model file: pinned URL and sha256, stored in the tools folder.</summary>
public sealed record ModelFile(string Id, string File, string Url, string Sha256, long Size, string License);

/// <summary>The model files the process steps need. Downloaded on first use into the tools folder, verified against the pinned sha256.</summary>
public static class Models
{
    private const string Siglip = "https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX/resolve/main/";
    private const string Rapid = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/";
    private const string SenseVoice = "https://huggingface.co/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/";

    public const string ClipModelId = "onnx-community/siglip2-base-patch16-224-ONNX";

    public static readonly ModelFile ClipVision = new("clip-vision", "siglip2-base-patch16-224.vision_model.onnx", Siglip + "onnx/vision_model.onnx", "c0573e3f4140c3a7c4e9cc5912bd6b26a033b46a6a8e8af26cbea262b163bcad", 371_807_752, "Apache-2.0");
    public static readonly ModelFile ClipText = new("clip-text", "siglip2-base-patch16-224.text_model_int8.onnx", Siglip + "onnx/text_model_int8.onnx", "3a0603d3a00c05a80a6ded4743c16aaac7b1e62cdcc7e362e7ce418659b96400", 283_438_275, "Apache-2.0");
    public static readonly ModelFile ClipTokenizer = new("clip-tokenizer", "siglip2-base-patch16-224.tokenizer.model", Siglip + "tokenizer.model", "61a7b147390c64585d6c3543dd6fc636906c9af3865a5548f27f31aee1d4c8e2", 4_241_003, "Apache-2.0");

    public static readonly ModelFile OcrDet = new("ocr-det", "ch_PP-OCRv5_det_mobile.onnx", Rapid + "onnx/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx", "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae", 4_819_576, "Apache-2.0");
    public static readonly ModelFile OcrRec = new("ocr-rec", "ch_PP-OCRv5_rec_mobile.onnx", Rapid + "onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx", "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5", 16_631_306, "Apache-2.0");
    public static readonly ModelFile OcrDict = new("ocr-dict", "ppocrv5_dict.txt", Rapid + "paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt", "d1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b", 74_012, "Apache-2.0");

    public static readonly ModelFile AsrModel = new("asr-model", "sensevoice-small.int8.onnx", SenseVoice + "model.int8.onnx", "c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51", 239_233_841, "FunASR Model Open Source License 1.1");
    public static readonly ModelFile AsrTokens = new("asr-tokens", "sensevoice-small.tokens.txt", SenseVoice + "tokens.txt", "f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc", 315_894, "FunASR Model Open Source License 1.1");
    public static readonly ModelFile Vad = new("silero-vad", "silero_vad.onnx", "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx", "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6", 643_854, "MIT");

    public static readonly ModelFile[] Clip = [ClipVision, ClipText, ClipTokenizer];
    public static readonly ModelFile[] Ocr = [OcrDet, OcrRec, OcrDict];
    public static readonly ModelFile[] Asr = [AsrModel, AsrTokens, Vad];
    public static readonly ModelFile[] All = [.. Clip, .. Ocr, .. Asr];

    /// <summary>The steps a node can run with the model files it has: a node never claims a job that needs a step it cannot run.</summary>
    public static readonly Dictionary<string, ModelFile[]> ByStep = new() { ["clip"] = Clip, ["ocrsubs"] = Ocr, ["transcribe"] = Asr };

    public static string PathOf(ModelFile m, string? dir = null) => Path.Combine(dir ?? NodeConfig.ToolsDir(), "models", m.File);

    public static bool Has(ModelFile m, string? dir = null) => File.Exists(PathOf(m, dir));

    public static bool HasStep(string step, string? dir = null) => !ByStep.TryGetValue(step, out var files) || files.All(f => Has(f, dir));

    /// <summary>Downloads the model (when absent) and returns its path. A file that fails its sha256 is deleted and reported.</summary>
    public static async Task<string> EnsureAsync(ModelFile m, HttpClient http, CancellationToken ct, string? dir = null)
    {
        var path = PathOf(m, dir);
        if (File.Exists(path)) return path;
        Log.Info($"models: downloading {m.File} ({m.Size / 1_000_000} MB)");
        await Fetch.VerifiedAsync(http, m.Url, path, m.Sha256, ct);
        return path;
    }

    public static async Task EnsureAllAsync(IEnumerable<ModelFile> files, HttpClient http, CancellationToken ct, string? dir = null)
    { foreach (var f in files) await EnsureAsync(f, http, ct, dir); }

    public static IReadOnlyList<string> Status()
    {
        var lines = new List<string>();
        foreach (var (step, files) in ByStep)
            lines.Add($"model {step,-10} " + (files.All(f => Has(f)) ? "installed" : "missing: " + string.Join(", ", files.Where(f => !Has(f)).Select(f => f.File)) + "  (indexnode tools install --models, or downloaded on first use)")
                      + $"  [{files.Sum(f => f.Size) / 1_000_000} MB]");
        return lines;
    }
}
