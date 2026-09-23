namespace ERP.IntegrationTests;

/// <summary>
/// 高风险验收（真实浏览器 UI 测试 / 数据库集成测试）的目标库安全护栏。
/// <para>判定边界（与本项目开发阶段真实的执行上下文一致，按顺序判定）：</para>
/// <list type="number">
/// <item>目标连接串必须非空且可解析（同时含 <c>Server</c>/<c>Data Source</c> 与 <c>Database</c>/<c>Initial Catalog</c>），
/// 否则立即 fail-closed：无法确认验收数据的写入目标时不得继续；</item>
/// <item>进程必须处于下列显式测试上下文之一，否则立即 fail-closed：
/// <list type="bullet">
/// <item>显式测试运行：<c>ERP_AI_TEST_RUN=1</c> 且 <c>ASPNETCORE_ENVIRONMENT=Development</c>
/// （<c>scripts/ai_browser_acceptance.py</c> 在启动验收进程时设置这两个变量）；</item>
/// <item>人工高风险批准：<c>ERP_AI_ALLOW_HIGH_RISK_TESTS=APPROVED</c>（项目既有的 Human Gate 第二层开关）；</item>
/// <item>CI 测试环境：<c>CI=true</c> / <c>GITHUB_ACTIONS=true</c>
/// （<c>.github/workflows/build.yml</c> 的 <c>erp-integration</c> / <c>erp-ui</c> 只注入测试环境密钥）。</item>
/// </list>
/// </item>
/// </list>
/// <para>
/// 重要：开发阶段不得把 <c>deploy/appsettings*.json</c> 当作生产权威，也不得用它做「数据库身份黑名单」。
/// 本机开发库与部署配置指向同一实例 + 同一库是当前开发环境的既定事实，护栏不再据此拒绝验收
/// （见 <c>.ai/prompts/developer.md</c> 第 8 条）。诊断信息只输出环境变量名与判定结果，绝不输出连接串内容。
/// </para>
/// </summary>
public static class TestDatabaseSafetyGuard
{
    /// <summary>显式测试运行标志（验收脚本设置为 <see cref="TestRunValue"/>）。</summary>
    public const string TestRunVariable = "ERP_AI_TEST_RUN";

    /// <summary>显式测试运行标志的期望值。</summary>
    public const string TestRunValue = "1";

    /// <summary>运行环境变量名（显式测试运行要求为 Development）。</summary>
    public const string EnvironmentVariable = "ASPNETCORE_ENVIRONMENT";

    /// <summary>显式测试运行要求的运行环境值。</summary>
    public const string DevelopmentEnvironment = "Development";

    /// <summary>人工高风险批准开关（Human Gate 第二层）。</summary>
    public const string ApprovalSwitch = "ERP_AI_ALLOW_HIGH_RISK_TESTS";

    /// <summary>人工高风险批准开关的期望值。</summary>
    public const string ApprovalValue = "APPROVED";

    /// <summary>CI 上下文变量名（GitHub Actions 同时设置 <c>CI</c> 与 <c>GITHUB_ACTIONS</c>）。</summary>
    public const string ContinuousIntegrationVariable = "CI";

    /// <summary>GitHub Actions 备用变量名。</summary>
    public const string GitHubActionsVariable = "GITHUB_ACTIONS";

    /// <summary>
    /// 校验验收目标库。缺少有效目标连接串、或进程不在显式测试上下文时抛出 <see cref="InvalidOperationException"/>：
    /// 此时不启动 API、不写任何数据（fail-closed）。
    /// </summary>
    /// <param name="connectionString">本次验收实际使用的连接串（来自 ERP_ConnectionStrings__Default）。</param>
    /// <param name="runName">用于诊断的验收名称（如「真实浏览器 UI 验收」）。</param>
    public static void EnsureApprovedTarget(string? connectionString, string runName)
    {
        var verdict = Evaluate(
            connectionString,
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            Environment.GetEnvironmentVariable(TestRunVariable),
            Environment.GetEnvironmentVariable(ApprovalSwitch),
            Environment.GetEnvironmentVariable(ContinuousIntegrationVariable) ?? Environment.GetEnvironmentVariable(GitHubActionsVariable));

        if (!verdict.Allowed)
            throw new InvalidOperationException($"{runName} 已中止（依据：{verdict.Channel}）：{verdict.Message}");

        Console.WriteLine($"[{runName}] 目标库安全护栏放行（依据：{verdict.Channel}）：{verdict.Message}");
    }

    /// <summary>
    /// 纯判定核心：不读取任何全局环境，便于单元测试逐条覆盖分支。
    /// </summary>
    /// <param name="connectionString">目标连接串（<c>ERP_ConnectionStrings__Default</c>）。</param>
    /// <param name="environmentName">进程的 <c>ASPNETCORE_ENVIRONMENT</c> 值。</param>
    /// <param name="testRunFlag">进程的 <c>ERP_AI_TEST_RUN</c> 值。</param>
    /// <param name="approvalSwitch">进程的 <c>ERP_AI_ALLOW_HIGH_RISK_TESTS</c> 值。</param>
    /// <param name="continuousIntegration">进程的 <c>CI</c> / <c>GITHUB_ACTIONS</c> 值。</param>
    /// <returns>是否放行、放行依据与诊断说明（不含连接串内容）。</returns>
    public static (bool Allowed, string Channel, string Message) Evaluate(
        string? connectionString,
        string? environmentName,
        string? testRunFlag,
        string? approvalSwitch,
        string? continuousIntegration)
    {
        if (ExtractTarget(connectionString) is null)
            return (false, "目标连接串无效",
                "未提供有效目标数据库连接串（ERP_ConnectionStrings__Default 必须同时含 Server/Data Source 与 Database/Initial Catalog）。" +
                "无法确认验收数据（建表 / 菜单授权 / 系统参数 / 测试单据）的写入目标，已 fail-closed。");

        if (string.Equals(approvalSwitch, ApprovalValue, StringComparison.Ordinal))
            return (true, "人工高风险批准", $"已由 {ApprovalSwitch}={ApprovalValue} 显式批准。");

        if (IsExplicitTestRun(environmentName, testRunFlag))
            return (true, "显式测试运行上下文",
                $"{TestRunVariable}={TestRunValue} 且 {EnvironmentVariable}={DevelopmentEnvironment}。");

        if (IsContinuousIntegration(continuousIntegration))
            return (true, "CI 测试环境",
                $"{ContinuousIntegrationVariable}={continuousIntegration}：CI 作业只注入测试环境密钥。");

        return (false, "缺少显式测试上下文",
            $"当前进程既未处于显式测试运行上下文（{TestRunVariable}={TestRunValue} 且 {EnvironmentVariable}={DevelopmentEnvironment}），" +
            $"也未提供人工高风险批准（{ApprovalSwitch}={ApprovalValue}），更不在 CI 测试环境中。" +
            "为避免把验收数据（建表 / 菜单授权 / 系统参数 / 测试单据）写入非测试库，已 fail-closed：" +
            "本地验收请使用 scripts/ai_browser_acceptance.py（它会设置测试上下文），或在进程内显式设置上述变量后重试。");
    }

    /// <summary>是否为显式测试运行上下文（验收脚本设置的两个变量同时成立）。</summary>
    private static bool IsExplicitTestRun(string? environmentName, string? testRunFlag) =>
        string.Equals(testRunFlag, TestRunValue, StringComparison.Ordinal)
        && string.Equals(environmentName, DevelopmentEnvironment, StringComparison.Ordinal);

    /// <summary>是否运行在 CI 测试环境。</summary>
    private static bool IsContinuousIntegration(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>提取「实例 + 库」标识（大小写、空格不敏感）；缺少库名 / 实例名时返回 null。</summary>
    private static (string Server, string Database)? ExtractTarget(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        string server = string.Empty, database = string.Empty;
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;
            var key = segment[..separator].Trim();
            var value = segment[(separator + 1)..].Trim();
            if (key.Equals("Server", StringComparison.OrdinalIgnoreCase) || key.Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                server = value;
            else if (key.Equals("Database", StringComparison.OrdinalIgnoreCase) || key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
                database = value;
        }
        if (server.Length == 0 || database.Length == 0) return null;
        return (server.ToLowerInvariant(), database.ToLowerInvariant());
    }
}
