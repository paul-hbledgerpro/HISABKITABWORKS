using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace ManagerPaperworkSystem.UI.Views;

/// <summary>
/// Hosts Plaid Link in WebView2 using a local HTTP server.
/// Plaid Link requires a real HTTP origin (not NavigateToString/data URI)
/// for its iframes to work. We serve the page from localhost.
/// </summary>
public partial class PlaidLinkWindow : Window
{
    private readonly string _linkToken;
    private HttpListener? _server;
    private int _port = 17834;
    private bool _resultHandled;

    public bool Success { get; private set; }
    public string? PublicToken { get; private set; }
    public string? InstitutionId { get; private set; }
    public string? InstitutionName { get; private set; }
    public string? AccountId { get; private set; }
    public string? AccountName { get; private set; }
    public string? AccountMask { get; private set; }
    public string? ErrorMessage { get; private set; }

    public PlaidLinkWindow(string linkToken)
    {
        InitializeComponent();
        _linkToken = linkToken;
        Loaded += PlaidLinkWindow_Loaded;
        Closing += (s, e) => StopServer();
    }

    private async void PlaidLinkWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            StartServer();

            var userDataFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HBStoreLedgerPro", "WebView2");
            System.IO.Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await plaidWebView.EnsureCoreWebView2Async(env);

            plaidWebView.CoreWebView2.Settings.IsScriptEnabled = true;
            plaidWebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            plaidWebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
            plaidWebView.CoreWebView2.Settings.AreHostObjectsAllowed = true;
            plaidWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            plaidWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // CRITICAL: Allow third-party cookies for Plaid iframes
            plaidWebView.CoreWebView2.Profile.PreferredTrackingPreventionLevel = 
                CoreWebView2TrackingPreventionLevel.None;

            // Handle popups by navigating in place
            plaidWebView.CoreWebView2.NewWindowRequested += (s2, args) =>
            {
                args.Handled = true;
                plaidWebView.CoreWebView2.Navigate(args.Uri);
            };

            // Monitor navigation for callback
            plaidWebView.CoreWebView2.NavigationStarting += (s2, args) =>
            {
                if (args.Uri.Contains("/plaid-callback") && !_resultHandled)
                {
                    args.Cancel = true;
                    HandleCallbackUri(args.Uri);
                }
            };

            plaidWebView.CoreWebView2.WebMessageReceived += WebView_MessageReceived;

            plaidWebView.CoreWebView2.NavigationCompleted += (s2, args) =>
            {
                loadingOverlay.Visibility = Visibility.Collapsed;
            };

            // Navigate to local server
            plaidWebView.CoreWebView2.Navigate($"http://127.0.0.1:{_port}/");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            System.Windows.MessageBox.Show($"Failed to initialize: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
            Close();
        }
    }

    private void StartServer()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                _server = new HttpListener();
                _server.Prefixes.Add($"http://127.0.0.1:{_port + attempt}/");
                _server.Start();
                _port = _port + attempt;
                _ = ServeAsync();
                return;
            }
            catch { _server?.Close(); }
        }
        throw new Exception("Could not start local server on any port.");
    }

    private void StopServer()
    {
        try { _server?.Stop(); _server?.Close(); } catch { }
    }

    private async Task ServeAsync()
    {
        try
        {
            while (_server != null && _server.IsListening)
            {
                var ctx = await _server.GetContextAsync();
                var path = ctx.Request.Url?.AbsolutePath ?? "/";

                string html;
                if (path.Contains("plaid-callback"))
                {
                    html = "<html><body style='background:#0B0B0F;color:#22c55e;font-family:sans-serif;display:flex;align-items:center;justify-content:center;height:100vh'><h2>✅ Connected! Closing...</h2></body></html>";
                    Dispatcher.Invoke(() => HandleCallbackUri(ctx.Request.Url?.ToString() ?? ""));
                }
                else
                {
                    html = GetPlaidHtml();
                }

                var buf = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = buf.Length;
                ctx.Response.Headers.Add("Cache-Control", "no-store");
                await ctx.Response.OutputStream.WriteAsync(buf, 0, buf.Length);
                ctx.Response.Close();
            }
        }
        catch (ObjectDisposedException) { }
        catch (HttpListenerException) { }
    }

    private void HandleCallbackUri(string uri)
    {
        if (_resultHandled) return;
        _resultHandled = true;

        try
        {
            var u = new Uri(uri);
            var q = System.Web.HttpUtility.ParseQueryString(u.Query);

            if (q["type"] == "success" && !string.IsNullOrEmpty(q["public_token"]))
            {
                Success = true;
                PublicToken = q["public_token"];
                InstitutionId = q["institution_id"];
                InstitutionName = q["institution_name"];
                AccountId = q["account_id"];
                AccountName = q["account_name"];
                AccountMask = q["account_mask"];
                Dispatcher.Invoke(() => { DialogResult = true; Close(); });
            }
            else
            {
                Success = false;
                ErrorMessage = q["error"];
                Dispatcher.Invoke(() => { DialogResult = false; Close(); });
            }
        }
        catch { }
    }

    private void WebView_MessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_resultHandled) return;
        try
        {
            var json = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "success")
            {
                _resultHandled = true;
                Success = true;
                PublicToken = root.TryGetProperty("publicToken", out var pt) ? pt.GetString() : null;
                InstitutionId = root.TryGetProperty("institutionId", out var v1) ? v1.GetString() : null;
                InstitutionName = root.TryGetProperty("institutionName", out var v2) ? v2.GetString() : null;
                AccountId = root.TryGetProperty("accountId", out var v3) ? v3.GetString() : null;
                AccountName = root.TryGetProperty("accountName", out var v4) ? v4.GetString() : null;
                AccountMask = root.TryGetProperty("accountMask", out var v5) ? v5.GetString() : null;
                Dispatcher.Invoke(() => { DialogResult = true; Close(); });
            }
            else if (type == "exit")
            {
                _resultHandled = true;
                Success = false;
                ErrorMessage = root.TryGetProperty("error", out var err) ? err.GetString() : null;
                Dispatcher.Invoke(() => { DialogResult = false; Close(); });
            }
        }
        catch { }
    }

    private string GetPlaidHtml()
    {
        var safeToken = _linkToken.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"");
        var cb = $"http://127.0.0.1:{_port}/plaid-callback";

        return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""UTF-8"">
<style>
  * {{ margin:0; padding:0; box-sizing:border-box; }}
  body {{ background:#0B0B0F; color:#f0ece0; font-family:'Segoe UI',sans-serif;
         display:flex; align-items:center; justify-content:center; height:100vh; }}
  .msg {{ text-align:center; }}
  .msg .icon {{ font-size:48px; margin-bottom:16px; }}
  .msg .text {{ font-size:14px; color:#D4AF37; }}
  .msg .sub {{ font-size:12px; color:#8a8a9a; margin-top:8px; }}
  #errDiv {{ display:none; color:#ef4444; text-align:center; padding:20px; }}
</style>
</head>
<body>
<div class=""msg"" id=""msgDiv"">
  <div class=""icon"">🏦</div>
  <div class=""text"" id=""statusText"">Initializing secure connection...</div>
  <div class=""sub"">This may take a few seconds</div>
</div>
<div id=""errDiv""></div>

<script src=""https://cdn.plaid.com/link/v2/stable/link-initialize.js""></script>
<script>
(function() {{
  var statusEl = document.getElementById('statusText');
  var errEl = document.getElementById('errDiv');
  var msgEl = document.getElementById('msgDiv');

  function finish(type, publicToken, metadata) {{
    // Try WebView2 message first
    try {{
      if (type === 'success') {{
        var inst = metadata.institution || {{}};
        var acct = (metadata.accounts && metadata.accounts[0]) || {{}};
        window.chrome.webview.postMessage(JSON.stringify({{
          type:'success', publicToken:publicToken,
          institutionId:inst.institution_id||'', institutionName:inst.name||'',
          accountId:acct.id||'', accountName:acct.name||'', accountMask:acct.mask||''
        }}));
      }} else {{
        window.chrome.webview.postMessage(JSON.stringify({{
          type:'exit', error:metadata||null
        }}));
      }}
    }} catch(e) {{}}

    // Also redirect as backup
    if (type === 'success') {{
      var inst = metadata.institution || {{}};
      var acct = (metadata.accounts && metadata.accounts[0]) || {{}};
      var p = new URLSearchParams({{
        type:'success', public_token:publicToken,
        institution_id:inst.institution_id||'', institution_name:inst.name||'',
        account_id:acct.id||'', account_name:acct.name||'', account_mask:acct.mask||''
      }});
      window.location.href = '{cb}?' + p.toString();
    }} else {{
      window.location.href = '{cb}?type=exit&error=' + encodeURIComponent(metadata||'');
    }}
  }}

  statusEl.textContent = 'Loading Plaid...';
  
  // Wait for Plaid SDK to be ready
  var checkReady = setInterval(function() {{
    if (typeof Plaid !== 'undefined') {{
      clearInterval(checkReady);
      statusEl.textContent = 'Opening bank selection...';

      try {{
        var handler = Plaid.create({{
          token: '{safeToken}',
          onSuccess: function(pub, meta) {{ finish('success', pub, meta); }},
          onExit: function(err, meta) {{
            var errMsg = err ? (err.display_message || err.error_message || 'Cancelled') : 'Cancelled';
            finish('exit', null, errMsg);
          }},
          onLoad: function() {{ statusEl.textContent = 'Bank login loaded — select your bank above.'; }},
          onEvent: function(eventName, metadata) {{
            // Log events for debugging
            console.log('Plaid event:', eventName, metadata);
            if (eventName === 'OPEN') {{ msgEl.style.display = 'none'; }}
            if (eventName === 'HANDOFF') {{ statusEl.textContent = 'Completing...'; }}
          }}
        }});
        handler.open();
      }} catch(e) {{
        msgEl.style.display = 'none';
        errEl.style.display = 'block';
        errEl.textContent = 'Error: ' + e.message;
      }}
    }}
  }}, 200);

  // Timeout after 30 seconds
  setTimeout(function() {{
    clearInterval(checkReady);
    if (typeof Plaid === 'undefined') {{
      msgEl.style.display = 'none';
      errEl.style.display = 'block';
      errEl.textContent = 'Plaid SDK failed to load. Check your internet connection.';
    }}
  }}, 30000);
}})();
</script>
</body>
</html>";
    }
}
