using Android.App;
using Android.OS;
using Android.Views;
using Android.Webkit;
using AndroidX.WebKit;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Host;

[Activity(Label = "IndexNode Spike", MainLauncher = true, Exported = true, ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize)]
public class MainActivity : Activity
{
    const string AssetOrigin = "https://appassets.androidplatform.net";
    const string BundledUrl = AssetOrigin + "/assets/web/index.html";
    const string BridgeName = "hostBridge";

    SpikeHost _host = null!;
    WebView _web = null!;
    WebViewAssetLoader _assets = null!;
    string _origin = AssetOrigin;   // origin of the page now allowed to use the bridge
    bool _firstShown;
    string? _autoJs;

    sealed class Chrome(MainActivity a) : WebChromeClient
    {
        public override bool OnConsoleMessage(ConsoleMessage? m) { a._host.Log("console: " + m?.Message()); return true; }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _host = new SpikeHost(CacheDir!.AbsolutePath);
        _assets = new WebViewAssetLoader.Builder().AddPathHandler("/assets/", new WebViewAssetLoader.AssetsPathHandler(this)!)!.Build()!;
        _web = new WebView(this);
        _web.Settings.JavaScriptEnabled = true;
        _web.Settings.AllowFileAccess = false;
        _web.Settings.AllowContentAccess = false;
        _web.SetWebViewClient(new Client(this));
        _web.SetWebChromeClient(new Chrome(this));
        _autoJs = Intent?.GetStringExtra("js");   // test hook: adb shell am start ... --es js "<javascript>"
        SetContentView(_web);
        LoadUi();
    }

    string? SavedUiUrl { get => GetPreferences(Android.Content.FileCreationMode.Private)!.GetString("uiUrl", null); set => GetPreferences(Android.Content.FileCreationMode.Private)!.Edit()!.PutString("uiUrl", value)!.Apply(); }

    static string? OriginOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        bool loopback = u.Host is "127.0.0.1" or "localhost";
        if (u.Scheme != "https" && !(u.Scheme == "http" && loopback)) return null;
        return u.GetLeftPart(UriPartial.Authority);
    }

    void LoadUi(bool bundledOnly = false)
    {
        var url = bundledOnly ? null : SavedUiUrl;
        var origin = string.IsNullOrWhiteSpace(url) ? null : OriginOf(url!);
        if (origin == null) { url = BundledUrl; origin = AssetOrigin; }
        // The bridge exists only for the origins listed here, in every frame the listener sees. Re-registered on each UI change.
        if (!WebViewFeature.IsFeatureSupported(WebViewFeature.WebMessageListener)) { _host.Log("WebMessageListener NOT supported by this WebView"); }
        else
        {
            WebViewCompat.RemoveWebMessageListener(_web, BridgeName);
            var rules = new List<string> { origin };
            WebViewCompat.AddWebMessageListener(_web, BridgeName, rules, new Listener(this));
        }
        _origin = origin;
        _host.Log("UI: " + url + " (bridge origin " + origin + ")");
        _web.LoadUrl(url!);
    }

    sealed class Listener(MainActivity a) : Java.Lang.Object, WebViewCompat.IWebMessageListener
    {
        public void OnPostMessage(WebView? view, WebMessageCompat? message, Android.Net.Uri? sourceOrigin, bool isMainFrame, JavaScriptReplyProxy? replyProxy)
        {
            // Second check on top of the origin rules: only the top frame, only the expected origin.
            var o = sourceOrigin?.ToString()?.TrimEnd('/');
            if (!isMainFrame || !string.Equals(o, a._origin, StringComparison.OrdinalIgnoreCase)) { a._host.Log("bridge: rejected message from " + o); return; }
            var text = message?.Data;
            _ = Task.Run(async () =>
            {
                int id = 0; string reply;
                try
                {
                    using var doc = JsonDocument.Parse(text!);
                    id = doc.RootElement.GetProperty("id").GetInt32();
                    var m = doc.RootElement.GetProperty("m").GetString()!;
                    var arg = doc.RootElement.TryGetProperty("a", out var av) ? av.GetString() ?? "" : "";
                    if (m == "setUiUrl") { a.SetUiUrl(arg); reply = new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = "ui url " + (arg == "" ? "reset" : "saved") }.ToJsonString(); }
                    else reply = new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = await a._host.HandleAsync(m, arg) }.ToJsonString();
                }
                catch (Exception e)
                {
                    a._host.Log("bridge error: " + e.GetType().Name + ": " + e.Message);
                    reply = new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = e.GetType().Name + ": " + e.Message }.ToJsonString();
                }
                a.RunOnUiThread(() => replyProxy!.PostMessage(reply));
            });
        }
    }

    void SetUiUrl(string url)
    {
        if (url != "" && OriginOf(url) == null) throw new ArgumentException("UI URL must be https (or http on loopback)");
        SavedUiUrl = url;
        RunOnUiThread(() => LoadUi());
    }

    sealed class Client(MainActivity a) : WebViewClient
    {
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request) =>
            request?.Url == null ? null : a._assets.ShouldInterceptRequest(request.Url);
        public override void OnPageFinished(WebView? view, string? url)
        {
            if (a._autoJs != null && url!.StartsWith(AssetOrigin)) { var js = a._autoJs; a._autoJs = null; view!.EvaluateJavascript(js!, null); }
            if (a._firstShown) return;
            a._firstShown = true;
            a._host.FirstScreenMs = SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime;
            a._host.Log($"first screen {a._host.FirstScreenMs} ms after process start");
        }
        public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            if (request?.IsForMainFrame != true) return;
            a._host.Log("UI load failed: " + error?.Description + "; falling back to the bundled page");
            if (!request.Url!.ToString()!.StartsWith(AssetOrigin)) a.RunOnUiThread(() => a.LoadUi(bundledOnly: true));
        }
    }

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.Back && _web.CanGoBack()) { _web.GoBack(); return true; }
        return base.OnKeyDown(keyCode, e);
    }
}
