using System.Reflection;
using PuppeteerSharp;

// Runs only an isolated headless browser against in-memory HTML. Does not start
// HISAB KITAB, read client settings, contact AdventPOS, or connect to a database.
var chrome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
    "Google", "Chrome", "Application", "chrome.exe");
if (!File.Exists(chrome))
    throw new InvalidOperationException("Install Google Chrome to run the portal browser regression check.");
var assembly = Assembly.Load("HISAB KITAB");
var service = assembly.GetType("ManagerPaperworkSystem.WinForms.PortalSyncService")!;
var select = service.GetMethod("SelectPortalStoreAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
const string expected = "GALAXY SMOKE SHOP (ELGIN, IL - 60123)";
await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions { Headless = true, ExecutablePath = chrome });
await using var context = await browser.CreateBrowserContextAsync();
var page = await context.NewPageAsync();

foreach (var sharedValue in new[] { "0", "owner-account-42", "unique-galaxy" })
foreach (var usePortalFunctions in new[] { true, false })
{
    var wrongValue = sharedValue == "unique-galaxy" ? "unique-wrong" : sharedValue;
    var otherCityValue = sharedValue == "unique-galaxy" ? "unique-other-city" : sharedValue;
    await page.SetContentAsync($$"""
    <select id="cbxSelectStore">
      <option value="-1">-- Select Store --</option>
      <option value="{{wrongValue}}" data-StoreDBID="wrong-db">ELGIN SMOKE SHOP (ELGIN, IL - 60120)</option>
      <option value="{{otherCityValue}}" data-StoreDBID="other-city-db">Galaxy Smoke Shop (Carpentersville, IL - 60110)</option>
      <option value="{{sharedValue}}" data-StoreDBID="galaxy-elgin-db">GALAXY SMOKE SHOP (ELGIN, IL - 60123)</option>
    </select>
    <button id="login">Login</button>
    <script>
      function recordSelection() {
        window.loadedDatabase = document.querySelector('#cbxSelectStore').selectedOptions[0].getAttribute('data-StoreDBID');
      }
      document.querySelector('#cbxSelectStore').addEventListener('change', recordSelection);
      document.querySelector('#login').onclick = recordSelection;
    </script>
    """);
    if (usePortalFunctions)
        await page.EvaluateExpressionAsync("window.LoadStoreUsers = recordSelection; window.UserAnotherAccount_Clicked = () => document.querySelector('#cbxSelectStore').disabled = true;");
    else
        await page.EvaluateExpressionAsync("delete window.LoadStoreUsers; delete window.UserAnotherAccount_Clicked;");

    await (Task)select.Invoke(null, [page, expected])!;
    if (await page.EvaluateExpressionAsync<string>("window.loadedDatabase") != "galaxy-elgin-db")
        throw new Exception("Store-user controls loaded against the wrong database.");
    await page.EvaluateExpressionAsync("document.querySelector('#login').click()");
    if (await page.EvaluateExpressionAsync<string>("window.loadedDatabase") != "galaxy-elgin-db")
        throw new Exception("Final login would use the wrong database.");
}
Console.WriteLine("PASS: unique and duplicate dropdown values (zero and owner ID), portal callbacks and change-event fallback select Galaxy Elgin's database.");

// Even a portal callback that changes the selection must not be accepted merely
// because the first and desired options have the same value.
await page.EvaluateExpressionAsync("Array.from(document.querySelector('#cbxSelectStore').options).forEach(option => option.value = 'shared-owner'); window.LoadStoreUsers = () => document.querySelector('#cbxSelectStore').selectedIndex = 1;");
await ExpectRejected(expected);
await ExpectRejected("GALAXY SMOKE SHOP");
await page.EvaluateExpressionAsync("document.querySelector('#cbxSelectStore').add(new Option('GALAXY SMOKE SHOP (ELGIN, IL - 60123)', 'duplicate'))");
await ExpectRejected(expected);
Console.WriteLine("PASS: portal selection reset, partial name and duplicate exact names rejected.");

// Keep an independent setup-like browser open throughout automatic sync.
// Use only a freshly created temporary fixture, never a client Chrome profile.
var createLaunch = service.GetMethod("CreateSyncBrowserLaunchOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
var scratch = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "HK-PortalBrowserCheck-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(scratch);
try
{
    var setupProfile = Path.Combine(scratch, "setup-profile");
    await using (var setupBrowser = await Puppeteer.LaunchAsync(new LaunchOptions
        { Headless = true, ExecutablePath = chrome, UserDataDir = setupProfile }))
    {
        var setupPage = await setupBrowser.NewPageAsync();
        await setupPage.SetContentAsync("<div id='marker'>Setup must stay open</div>");
        await setupPage.SetCookieAsync(new CookieParam { Name = "store", Value = "other-store", Url = "http://profile.test" });
        var options = (LaunchOptions)createLaunch.Invoke(null, [chrome, scratch, false])!;
        if (!options.Headless || !string.IsNullOrEmpty(options.UserDataDir) || options.Args.Any(arg => arg.StartsWith("--user-data-dir")))
            throw new Exception("Background sync must use an independently owned temporary browser profile.");
        await using (var firstSync = await Puppeteer.LaunchAsync(options))
        await using (var secondSync = await Puppeteer.LaunchAsync((LaunchOptions)createLaunch.Invoke(null, [chrome, scratch, false])!))
        {
            var firstPage = await firstSync.NewPageAsync();
            var secondPage = await secondSync.NewPageAsync();
            if ((await firstPage.GetCookiesAsync("http://profile.test")).Length != 0)
                throw new Exception("Setup cookies leaked into sync.");
            await firstPage.SetCookieAsync(new CookieParam { Name = "store", Value = "first-sync", Url = "http://profile.test" });
            if ((await secondPage.GetCookiesAsync("http://profile.test")).Length != 0)
                throw new Exception("Concurrent sync browsers share cookies.");
            if (await setupPage.EvaluateExpressionAsync<string>("document.querySelector('#marker').textContent") != "Setup must stay open")
                throw new Exception("Sync changed or closed the setup browser.");
        }
        if (await setupPage.EvaluateExpressionAsync<string>("document.querySelector('#marker').textContent") != "Setup must stay open")
            throw new Exception("Sync disposal affected the setup browser.");
    }
}
finally
{
    var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
    if (Path.GetDirectoryName(scratch)?.TrimEnd(Path.DirectorySeparatorChar) != expectedParent ||
        !Path.GetFileName(scratch).StartsWith("HK-PortalBrowserCheck-", StringComparison.Ordinal))
        throw new Exception("Refusing to clean an unexpected fixture path.");
    Directory.Delete(scratch, recursive: true);
}
Console.WriteLine("PASS: occupied setup profile stays open; simultaneous automatic browsers launch with isolated cookies and independent disposal.");

async Task ExpectRejected(string name)
{
    try { await (Task)select.Invoke(null, [page, name])!; }
    catch (InvalidOperationException) { return; }
    throw new Exception("Unsafe portal selection was accepted.");
}
