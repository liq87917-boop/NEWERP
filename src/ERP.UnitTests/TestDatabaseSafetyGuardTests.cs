using ERP.IntegrationTests;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 目标库安全护栏（<see cref="TestDatabaseSafetyGuard"/>）判定规则的单元测试。
/// 只调用纯判定函数 <c>Evaluate</c>：不读取进程环境、不启动 API、不连接数据库、不启动浏览器。
/// 背景：开发阶段不得把 <c>deploy/appsettings*.json</c> 当作生产权威或数据库身份黑名单
/// （见 <c>.ai/prompts/developer.md</c> 第 8 条），护栏边界改为「有效目标连接串 + 显式测试上下文」。
/// </summary>
public class TestDatabaseSafetyGuardTests
{
    /// <summary>显式测试运行上下文（验收脚本设置）。</summary>
    private const string TestRun = TestDatabaseSafetyGuard.TestRunValue;

    /// <summary>显式测试运行要求的运行环境值。</summary>
    private const string Development = TestDatabaseSafetyGuard.DevelopmentEnvironment;

    /// <summary>人工高风险批准值。</summary>
    private const string Approved = TestDatabaseSafetyGuard.ApprovalValue;

    /// <summary>专用测试库形态的连接串（本地实例 + 机器认证，非生产凭据）。</summary>
    private const string TestDatabaseConnection =
        "Server=(localdb)\\MSSQLLocalDB;Initial Catalog=NEWERP_TEST;Integrated Security=true;TrustServerCertificate=true";

    /// <summary>与部署配置同库形态的连接串（占位主机名，测试不读取任何真实部署配置）。</summary>
    private const string DeploymentLikeConnection =
        "Server=deploy-sql-host;Database=WMERP_Data;Integrated Security=true";

    /// <summary>放行时不得泄露目标库实例 / 库名。</summary>
    [Theory]
    [InlineData("deploy-sql-host")]
    [InlineData("WMERP_Data")]
    public void 放行诊断信息不包含目标库实例或库名(string forbidden)
    {
        var verdict = TestDatabaseSafetyGuard.Evaluate(DeploymentLikeConnection, Development, TestRun, null, null);

        Assert.True(verdict.Allowed);
        Assert.DoesNotContain(forbidden, verdict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(forbidden, verdict.Channel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>缺少有效目标连接串时必须 fail-closed，即使提供了显式测试上下文 / 人工批准 / CI。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Server=localhost;")]                        // 缺 Database
    [InlineData("Database=NEWERP_TEST;")]                    // 缺 Server
    [InlineData("Server=localhost;Database=;")]              // 库名为空
    [InlineData("Integrated Security=true;")]                // 只有认证方式
    public void 缺少有效目标连接串时fail_closed(string? connectionString)
    {
        var verdict = TestDatabaseSafetyGuard.Evaluate(connectionString, Development, TestRun, Approved, "true");

        Assert.False(verdict.Allowed);
        Assert.Equal("目标连接串无效", verdict.Channel);
    }

    /// <summary>验收脚本设置的显式测试运行上下文（ERP_AI_TEST_RUN=1 + ASPNETCORE_ENVIRONMENT=Development）应放行。</summary>
    [Fact]
    public void 显式测试运行上下文放行()
    {
        var verdict = TestDatabaseSafetyGuard.Evaluate(TestDatabaseConnection, Development, TestRun, null, null);

        Assert.True(verdict.Allowed);
        Assert.Equal("显式测试运行上下文", verdict.Channel);
    }

    /// <summary>与部署配置同库形态的连接串不再按「数据库身份」拒绝：只依据显式测试上下文判定。</summary>
    [Fact]
    public void 与部署配置同库形态的连接串在测试上下文下放行_无上下文才拒绝()
    {
        var allowed = TestDatabaseSafetyGuard.Evaluate(DeploymentLikeConnection, Development, TestRun, null, null);
        Assert.True(allowed.Allowed, "deploy/appsettings*.json 不是生产权威，同库形态不得单独构成拒绝理由");

        var rejected = TestDatabaseSafetyGuard.Evaluate(DeploymentLikeConnection, null, null, null, null);
        Assert.False(rejected.Allowed);
        Assert.Equal("缺少显式测试上下文", rejected.Channel);   // 拒绝依据是缺少测试上下文，而不是目标库身份
    }

    /// <summary>非显式测试运行上下文（缺变量 / 非 Development / 值不符）必须 fail-closed。</summary>
    [Theory]
    [InlineData(null, "1")]                 // 缺 ASPNETCORE_ENVIRONMENT
    [InlineData("Development", null)]       // 缺 ERP_AI_TEST_RUN
    [InlineData("Production", "1")]         // 非 Development
    [InlineData("development", "1")]        // 环境名大小写不匹配（严格序数比较）
    [InlineData("Development", "0")]        // 标志值不符
    [InlineData("Development", "true")]     // 标志只接受字面量 1
    public void 非显式测试运行上下文时fail_closed(string? environmentName, string? testRunFlag)
    {
        var verdict = TestDatabaseSafetyGuard.Evaluate(TestDatabaseConnection, environmentName, testRunFlag, null, null);

        Assert.False(verdict.Allowed);
        Assert.Equal("缺少显式测试上下文", verdict.Channel);
    }

    /// <summary>人工高风险批准（Human Gate 第二层开关）单独成立即可放行。</summary>
    [Fact]
    public void 人工高风险批准放行()
    {
        var verdict = TestDatabaseSafetyGuard.Evaluate(TestDatabaseConnection, null, null, Approved, null);

        Assert.True(verdict.Allowed);
        Assert.Equal("人工高风险批准", verdict.Channel);
    }

    /// <summary>批准值必须严格等于 APPROVED；大小写不符不视为批准。</summary>
    [Fact]
    public void 批准值大小写不符时不视为批准()
    {
        var verdict = TestDatabaseSafetyGuard.Evaluate(TestDatabaseConnection, null, null, "approved", null);

        Assert.False(verdict.Allowed);
        Assert.Equal("缺少显式测试上下文", verdict.Channel);
    }

    /// <summary>CI 测试环境（CI / GITHUB_ACTIONS=true）放行，保证 GitHub Actions 的集成 / UI 作业不被误拦。</summary>
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    public void CI测试环境放行(string continuousIntegration)
    {
        var verdict = TestDatabaseSafetyGuard.Evaluate(TestDatabaseConnection, null, null, null, continuousIntegration);

        Assert.True(verdict.Allowed);
        Assert.Equal("CI 测试环境", verdict.Channel);
    }

    /// <summary>EnsureApprovedTarget 在目标连接串无效时无条件抛出（该判定先于任何环境变量判定）。</summary>
    [Fact]
    public void EnsureApprovedTarget在目标连接串无效时抛出()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => TestDatabaseSafetyGuard.EnsureApprovedTarget("Server=localhost;", "单元测试验收"));

        Assert.Contains("单元测试验收", exception.Message, StringComparison.Ordinal);
        Assert.Contains("未提供有效目标数据库连接串", exception.Message, StringComparison.Ordinal);
    }
}
