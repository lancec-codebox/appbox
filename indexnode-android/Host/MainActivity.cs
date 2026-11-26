using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Webkit;
using AndroidX.WebKit;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndexNode.Hosting;

namespace IndexNode.Host;

[Activity(Label = "IndexNode", MainLauncher = true, Exported = true, Theme = "@android:style/Theme.DeviceDefault.NoActionBar", ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize)]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "medusa", DataHost = "node-setup")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "image/*")]   // 0.1.6: a screenshot shared from another app opens the Search tab with it
public class MainActivity : Activity
{
    const string AssetOrigin = "https://appassets.androidplatform.net";
    const string BundledUrl = AssetOrigin + "/assets/web/index.html";
    const string BridgeName = "hostBridge";

    NodeRuntime _rt = null!;
    WebView _web = null!;
    WebViewAssetLoader _assets = null!;
    Bridge _bridge = null!;
    string _origin = AssetOrigin;
    const int PickFile = 71, MaxSharedBytes = 24 * 1024 * 1024;
    IValueCallback? _fileCb;

    sealed class Chrome(MainActivity a) : WebChromeClient
    {
        public override bool OnConsoleMessage(ConsoleMessage? m) { a._rt.Host.Log("console: " + m?.Message()); return true; }
        // 0.1.6: the page's <input type="file"> ("Choose a picture"): Android's own picker, the choice goes back to the page
        public override bool OnShowFileChooser(WebView? webView, IValueCallback? filePathCallback, FileChooserParams? fileChooserParams)
        {
            a._fileCb?.OnReceiveValue(null); a._fileCb = filePathCallback;
            try { a.StartActivityForResult(fileChooserParams!.CreateIntent(), PickFile); return true; }
            catch (Exception e) { a._rt.Host.Log("picture chooser not opened: " + e.Message); a._fileCb = null; filePathCallback?.OnReceiveValue(null); return true; }
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickFile) return;
        var cb = _fileCb; _fileCb = null;
        cb?.OnReceiveValue(WebChromeClient.FileChooserParams.ParseResult((int)resultCode, data));
    }

    // 0.1.6: the page keeps clear of the status bar, the navigation bar and the keyboard (0.1.5 also drew Android's own title bar over the top of the page: the theme has none now)
    sealed class Insets : Java.Lang.Object, AndroidX.Core.View.IOnApplyWindowInsetsListener
    {
        public AndroidX.Core.View.WindowInsetsCompat? OnApplyWindowInsets(View? v, AndroidX.Core.View.WindowInsetsCompat? insets)
        {
            if (v == null || insets == null) return insets;
            var b = insets.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars() | AndroidX.Core.View.WindowInsetsCompat.Type.DisplayCutout() | AndroidX.Core.View.WindowInsetsCompat.Type.Ime());
            if (b != null) v.SetPadding(b.Left, b.Top, b.Right, b.Bottom);
            return insets;
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _rt = NodeRuntime.Get(this);
        _bridge = new Bridge(Handle, () => _origin, _rt.Host.Log);
        _assets = new WebViewAssetLoader.Builder().AddPathHandler("/assets/", new WebViewAssetLoader.AssetsPathHandler(this)!)!.Build()!;
        _web = new WebView(this);
        _web.Settings.JavaScriptEnabled = true;
        _web.Settings.AllowFileAccess = false;
        _web.Settings.AllowContentAccess = false;
        _web.SetWebViewClient(new Client(this));
        _web.SetWebChromeClient(new Chrome(this));
        SetContentView(_web);
        AndroidX.Core.View.ViewCompat.SetOnApplyWindowInsetsListener(_web, new Insets());
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, 1);
        RunWorker.Schedule(this);
        LoadUi();
        _ = StartupUpdateAsync();
        HandleIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent) { base.OnNewIntent(intent); HandleIntent(intent); }

    // the node-setup link opened from the QR (medusa://node-setup?d=...): import it, then show the status page
    void HandleIntent(Intent? i)
    {
        if (i?.Action == Intent.ActionSend && i.Type?.StartsWith("image/") == true) { TakeShared(i); return; }
        var data = i?.DataString;
        if (data == null || !data.StartsWith("medusa://node-setup")) return;
        try { _rt.ImportSetup(data); RunWorker.Schedule(this); } catch (Exception e) { _rt.Host.Log("setup link refused: " + e.Message); }
    }

    // 0.1.6: a picture shared to the app: its bytes are read here (the page cannot read another app's content) and kept until the Search tab takes them
    void TakeShared(Intent i)
    {
        try
        {
            var uri = i.ClipData is { ItemCount: > 0 } cd ? cd.GetItemAt(0)?.Uri : null;
            if (uri == null) { _rt.Host.Log("shared picture: the share carried no picture"); return; }
            using var s = ContentResolver!.OpenInputStream(uri) ?? throw new IOException("the picture could not be opened");
            using var ms = new MemoryStream(); var buf = new byte[81920]; int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > MaxSharedBytes) throw new IOException("the picture is over 24 MB"); }
            _rt.SharedImage = (uri.LastPathSegment ?? "shared picture", ms.ToArray());
            _rt.Host.Log($"shared picture received ({ms.Length / 1024} KB)");
        }
        catch (Exception e) { _rt.Host.Log("shared picture refused: " + e.Message); }
    }

    // on app start: load the plugin and check for an update (a failure only goes to the log)
    async Task StartupUpdateAsync()
    {
        try { var r = await _rt.CheckPluginAsync(); _rt.Host.Log($"plugin on start: {r.Outcome} {r.Version} {r.Detail}"); } catch (Exception e) { _rt.Host.Log("plugin check failed: " + e.Message); }
        await _rt.Updater.CheckAsync(default);   // 0.1.5: the app's own update; a failure is a state the page shows, never an exception
    }

    string? SavedUiUrl => _rt.Prefs.Get("uiUrl");

    void LoadUi(bool bundledOnly = false)
    {
        var url = bundledOnly ? null : SavedUiUrl;
        var origin = string.IsNullOrWhiteSpace(url) ? null : Bridge.OriginOf(url!);
        if (origin == null) { url = BundledUrl; origin = AssetOrigin; }
        if (!WebViewFeature.IsFeatureSupported(WebViewFeature.WebMessageListener)) _rt.Host.Log("WebMessageListener NOT supported by this WebView");
        else
        {
            WebViewCompat.RemoveWebMessageListener(_web, BridgeName);
            WebViewCompat.AddWebMessageListener(_web, BridgeName, new List<string> { origin }, new Listener(this));
        }
        _origin = origin;
        _rt.Host.Log("UI: " + url);
        _web.LoadUrl(url!);
    }

    async Task<JsonNode?> Handle(string m, string arg)
    {
        switch (m)
        {
            case "getState": { var s = _rt.StateJson(); s["medusa"] = await _rt.MedusaStatusAsync(); return s; }
            case "getLog": return new JsonArray(_rt.Host.LogLines().Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
            case "clearLog": _rt.Host.ClearLog(); return true;
            case "getDiagnostics": return _rt.DiagnosticsJson();
            case "clearDiagnostics": _rt.Host.ClearLog(); CrashRecorder.Clear(); return true;
            case "shareLog": RunOnUiThread(ShareLog); return true;
            case "runNow": if (_rt.Running) { _rt.Host.Log("Run now: " + RunGuard.Busy); return true; } RunWorker.RunNow(this); _rt.Host.Log("Run now requested"); return true;
            case "stopRun": _rt.Stop(); return true;
            case "saveSettings": _rt.SaveSettings(JsonNode.Parse(arg)!.AsObject()); RunWorker.Schedule(this); return true;
            case "importSetup": _rt.ImportSetup(arg); RunWorker.Schedule(this); return _rt.StateJson();
            case "pairMedusa": case "pairStatus": return await _rt.PairAsync();
            case "checkPluginUpdate": { var r = await _rt.CheckPluginAsync(); return new JsonObject { ["outcome"] = r.Outcome, ["version"] = r.Version, ["detail"] = r.Detail }; }
            case "checkAppUpdate": return await _rt.Updater.CheckAsync(default);
            case "installAppUpdate": return await _rt.Updater.InstallAsync(default);
            case "service": return await _rt.Service.CallAsync(arg, default);   // 0.1.6: the Search tab; only the operations ServiceCalls knows
            case "takeShared": { var sh = _rt.SharedImage; _rt.SharedImage = null; return sh is { } x ? new JsonObject { ["name"] = x.Name, ["image"] = Convert.ToBase64String(x.Data) } : null; }
            case "reloadUi": RunOnUiThread(() => { _web.ClearCache(true); LoadUi(); }); return true;   // 0.1.5: the page's reload button: the page again from its source (the app, or the UI URL), without the WebView's cache
            case "setUiUrl":
                if (arg != "" && Bridge.OriginOf(arg) == null) throw new ArgumentException("UI URL must be https (or http on loopback)");
                _rt.Prefs.Set("uiUrl", arg == "" ? null : arg); RunOnUiThread(() => LoadUi()); return true;
            default: throw new InvalidOperationException("unknown method " + m);
        }
    }

    void ShareLog()
    {
        var send = new Intent(Intent.ActionSend); send.SetType("text/plain");
        send.PutExtra(Intent.ExtraText, string.Join("\n", _rt.Host.LogLines()));   // the lines are redacted when written
        StartActivity(Intent.CreateChooser(send, "Share log"));
    }

    sealed class Listener(MainActivity a) : Java.Lang.Object, WebViewCompat.IWebMessageListener
    {
        public void OnPostMessage(WebView? view, WebMessageCompat? message, Android.Net.Uri? sourceOrigin, bool isMainFrame, JavaScriptReplyProxy? replyProxy)
        {
            var text = message?.Data; var origin = sourceOrigin?.ToString();
            _ = Task.Run(async () =>
            {
                var reply = await a._bridge.HandleAsync(text, origin, isMainFrame);
                if (reply != null) a.RunOnUiThread(() => replyProxy!.PostMessage(reply));
            });
        }
    }

    sealed class Client(MainActivity a) : WebViewClient
    {
        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request) =>
            request?.Url == null ? null : a._assets.ShouldInterceptRequest(request.Url);
        // 0.1.6: a link that leaves the page (a video on YouTube) opens in the phone's own app or browser, never inside this WebView
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var u = request?.Url?.ToString();
            if (u == null || u.StartsWith(a._origin, StringComparison.OrdinalIgnoreCase)) return false;
            try { a.StartActivity(new Intent(Intent.ActionView, request!.Url)); } catch (Exception e) { a._rt.Host.Log("link not opened: " + e.Message); }
            return true;
        }
        public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            if (request?.IsForMainFrame != true) return;
            a._rt.Host.Log("UI load failed: " + error?.Description + "; falling back to the bundled page");
            if (!request.Url!.ToString()!.StartsWith(AssetOrigin)) a.RunOnUiThread(() => a.LoadUi(bundledOnly: true));
        }
    }

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.Back && _web.CanGoBack()) { _web.GoBack(); return true; }
        return base.OnKeyDown(keyCode, e);
    }
}
