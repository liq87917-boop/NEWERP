using OpenQA.Selenium;
using OpenQA.Selenium.Edge;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// 真实浏览器端到端测试 Fixture：
/// - 启动 dotnet 跑 ERP.Api (http://localhost:5059)，异步消费其控制台输出（否则启动 SQL 输出会写阻塞管道）
/// - 启动 Microsoft Edge headless：优先使用本机预置的 msedgedriver（ERP_AI_EDGE_DRIVER / %LOCALAPPDATA%\erp-ai\webdrivers），
///   未预置时回退 Selenium Manager 自动下载匹配版本驱动
/// - 提供给所有 [Collection("UiTests")] 用例共享
/// </summary>
public class UiTestFixture : IAsyncLifetime
{
    public IWebDriver Driver { get; private set; } = null!;

    /// <summary>
    /// 验收基址：默认 <c>http://localhost:5059</c>；可用环境变量 <c>ERP_AI_UI_PORT</c> 覆盖端口，
    /// 便于与「上一次验收残留的 ERP.Api」或并行验收的实例隔离（否则会静默连上外部实例）。
    /// </summary>
    public static string BaseUrl { get; } =
        $"http://localhost:{Environment.GetEnvironmentVariable("ERP_AI_UI_PORT") ?? "5059"}";

    private Process? _apiProcess;
    private int _evidenceSequence;
    private readonly StringBuilder _apiConsole = new();
    private readonly object _apiConsoleLock = new();

    public Task InitializeAsync()
    {
        // 安全护栏：目标库若与部署配置同一实例 + 库，必须显式获批，否则在启动 API 之前中止（不写任何数据）
        TestDatabaseSafetyGuard.EnsureApprovedTarget(
            Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default"), "真实浏览器 UI 验收");
        EnsurePortIsFree();
        _apiProcess = StartApi();
        try
        {
            WaitForApiReady();
        }
        catch
        {
            DumpApiConsole();
            throw;
        }
        DumpApiConsole();
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

    /// <summary>
    /// 重置浏览器登录态：清空 localStorage/sessionStorage 中的 JWT 后回到首页，
    /// 保证每个用例都从登录页开始（同一 Fixture 共享一个浏览器会话，前一用例留下的登录态会让登录页元素存在但不可交互）。
    /// </summary>
    public void ResetBrowserSession()
    {
        Driver.Navigate().GoToUrl(BaseUrl);
        ((IJavaScriptExecutor)Driver).ExecuteScript(
            "try { localStorage.clear(); sessionStorage.clear(); } catch (e) { } return 'ok';");
        Driver.Navigate().Refresh();
    }

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

    /// <summary>
    /// 启动前确认验收端口空闲。若端口已被监听（上一次验收残留的 ERP.Api，或人工启动的实例），
    /// 本次验收会静默连上那个「外部实例」：页面可能来自旧构建、数据库可能是另一个库，
    /// 产出的截图 / TRX 属于无效证据。因此这里 fail-closed，直接中止并提示清理残留进程。
    /// </summary>
    private static void EnsurePortIsFree()
    {
        var uri = new Uri(BaseUrl);
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            if (probe.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromMilliseconds(500)))
                throw new InvalidOperationException(
                    $"真实浏览器 UI 验收 已中止：端口 {uri.Port} 已被其它进程监听（很可能是上一次验收残留的 ERP.Api）。" +
                    "继续执行会让验收连上外部实例并产出无效证据，请先结束占用该端口的进程后重试。");
        }
        catch (AggregateException)
        {
            // 连接被拒绝 = 端口空闲，正常继续
        }
    }

    private Process StartApi()
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
        var process = Process.Start(psi)!;
        // 必须异步消费子进程输出：ERP.Api 启动时会输出建表/升级 SQL（远超管道缓冲区），
        // 若不读取会导致子进程写阻塞、HTTP 端口永不监听。此处异步读取并保留尾部日志用于诊断。
        process.OutputDataReceived += (_, e) => AppendApiConsole(e.Data);
        process.ErrorDataReceived += (_, e) => AppendApiConsole(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private void AppendApiConsole(string? line)
    {
        if (line is null) return;
        lock (_apiConsoleLock)
        {
            if (_apiConsole.Length > 400_000) _apiConsole.Remove(0, 200_000);   // 只保留尾部，避免长时间运行占用内存
            _apiConsole.Append(line).Append('\n');
        }
    }

    private string ApiConsoleTail(int maxChars = 4000)
    {
        lock (_apiConsoleLock)
        {
            var text = _apiConsole.ToString();
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
    }

    /// <summary>把 ERP.Api 控制台输出写入验收证据目录（无证据目录时跳过），便于浏览器门禁失败时定位。</summary>
    private void DumpApiConsole()
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("ERP_AI_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory)) return;
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            File.WriteAllText(Path.Combine(evidenceDirectory, "api-console.log"), ApiConsoleTail(int.MaxValue));
        }
        catch (IOException) { }
    }

    private void WaitForApiReady()
    {
        // 全新库首次启动需要建表 + 幂等升级 + 种子数据，耗时明显长于常规启动，因此放宽到 180 秒
        var deadline = DateTime.UtcNow.AddSeconds(180);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        string lastError = "尚未收到响应";
        while (DateTime.UtcNow < deadline)
        {
            if (_apiProcess?.HasExited == true)
            {
                throw new InvalidOperationException(
                    $"ERP.Api 启动过程中退出（ExitCode={_apiProcess.ExitCode}）。API 控制台尾部输出：\n{ApiConsoleTail()}");
            }
            try
            {
                var resp = client.GetAsync(BaseUrl + "/swagger/v1/swagger.json").GetAwaiter().GetResult();
                if ((int)resp.StatusCode == 200) return;
                lastError = $"HTTP {(int)resp.StatusCode}";
            }
            catch (Exception ex) { lastError = ex.Message; }
            Thread.Sleep(500);
        }
        throw new InvalidOperationException(
            $"ERP.Api 在 180 秒内未就绪（最后状态：{lastError}）。API 控制台尾部输出：\n{ApiConsoleTail()}");
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
        // 业务确认框（window.confirm，如报价单「转 PI」、PI「作废」）由测试代码显式处理：
        // 不交给驱动的默认策略自动 dismiss，否则确认会被吞掉导致业务动作未执行
        options.UnhandledPromptBehavior = UnhandledPromptBehavior.Ignore;

        // 驱动解析：优先使用本地已预置的 msedgedriver（离线环境 / 驱动 CDN 不可达时必需），
        // 本地没有时回退 Selenium Manager 自动下载（CI 联网环境）。
        var driverDirectory = ResolveEdgeDriverDirectory();
        Driver = driverDirectory is null
            ? new EdgeDriver(options)
            : new EdgeDriver(EdgeDriverService.CreateDefaultService(driverDirectory), options);
        Driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// 解析本地已预置的 msedgedriver 所在目录：
    /// 1) 环境变量 <c>ERP_AI_EDGE_DRIVER</c>（可指向 msedgedriver.exe 或其所在目录）；
    /// 2) <c>%LOCALAPPDATA%\erp-ai\webdrivers</c> 下最新的 msedgedriver.exe（离线预置约定位置）。
    /// 均未命中时返回 null，由 Selenium Manager 按 Edge 版本自动下载（需可访问驱动 CDN）。
    /// </summary>
    private static string? ResolveEdgeDriverDirectory()
    {
        const string ExecutableName = "msedgedriver.exe";
        var configured = Environment.GetEnvironmentVariable("ERP_AI_EDGE_DRIVER");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.GetFullPath(configured);
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, ExecutableName))) return path;
            if (File.Exists(path)) return Path.GetDirectoryName(path);
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "erp-ai", "webdrivers");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, ExecutableName, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault();
    }
}

[CollectionDefinition("UiTests")]
public class UiTestCollection : ICollectionFixture<UiTestFixture> { }
