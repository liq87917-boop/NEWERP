using OpenQA.Selenium;
using OpenQA.Selenium.Edge;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// 真实浏览器端到端测试 Fixture：
/// - 启动 dotnet 跑 ERP.Api (http://localhost:5059)
/// - 启动 Microsoft Edge headless（Selenium 4 自带 Selenium Manager 自动下载匹配版本 msedgedriver）
/// - 提供给所有 [Collection("UiTests")] 用例共享
/// </summary>
public class UiTestFixture : IAsyncLifetime
{
    public IWebDriver Driver { get; private set; } = null!;
    public const string BaseUrl = "http://localhost:5059";

    private Process? _apiProcess;
    private int _evidenceSequence;

    public Task InitializeAsync()
    {
        _apiProcess = StartApi();
        WaitForApiReady();
        InitEdgeDriver();
        Driver.Navigate().GoToUrl(BaseUrl);
        WriteBrowserMetadata();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Driver?.Quit(); } catch { }
        try { Driver?.Dispose(); } catch { }
        try { if (_apiProcess is not null && !_apiProcess.HasExited) { _apiProcess.Kill(entireProcessTree: true); _apiProcess.Dispose(); } } catch { }
        return Task.CompletedTask;
    }

    // ============ 私有助手 ============

    public void CaptureEvidence(string scenario)
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("ERP_AI_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory)) return;
        Directory.CreateDirectory(evidenceDirectory);
        var safeName = string.Concat(scenario.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        var sequence = Interlocked.Increment(ref _evidenceSequence);
        var path = Path.Combine(evidenceDirectory, $"{sequence:00}-{safeName}.png");
        var screenshot = ((ITakesScreenshot)Driver).GetScreenshot();
        screenshot.SaveAsFile(path);
    }

    private void WriteBrowserMetadata()
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("ERP_AI_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory)) return;
        Directory.CreateDirectory(evidenceDirectory);
        var capabilities = ((EdgeDriver)Driver).Capabilities;
        var metadata = new
        {
            browser = capabilities.GetCapability("browserName")?.ToString() ?? "MicrosoftEdge",
            version = capabilities.GetCapability("browserVersion")?.ToString() ?? "unknown",
            realBrowser = true,
            headless = true,
            capturedAt = DateTimeOffset.UtcNow
        };
        File.WriteAllText(Path.Combine(evidenceDirectory, "browser-session.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Process StartApi()
    {
        var binDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ERP.Api", "bin", "Debug", "net8.0"));
        var dllPath = Path.Combine(binDir, "ERP.Api.dll");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\" --urls {BaseUrl} --contentRoot \"{binDir}\"",
            WorkingDirectory = binDir,                                     // 让 ASP.NET Core 从 bin 目录找 appsettings.Development.json
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
        return Process.Start(psi)!;
    }

    private static void WaitForApiReady()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = client.GetAsync(BaseUrl + "/swagger/v1/swagger.json").GetAwaiter().GetResult();
                if ((int)resp.StatusCode == 200) return;
            }
            catch { }
            Thread.Sleep(500);
        }
        throw new InvalidOperationException("ERP.Api 在 60 秒内未就绪");
    }

    private void InitEdgeDriver()
    {
        var options = new EdgeOptions();
        options.AddArgument("--headless=new");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--window-size=1920,1080");
        options.BinaryLocation = Environment.GetEnvironmentVariable("ERP_AI_EDGE_BINARY")
            ?? @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

        Driver = new EdgeDriver(options);
        Driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(10);
    }
}

[CollectionDefinition("UiTests")]
public class UiTestCollection : ICollectionFixture<UiTestFixture> { }
