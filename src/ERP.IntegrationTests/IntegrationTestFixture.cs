using ERP.Infrastructure.Data;
using ERP.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// 集成测试 Fixture：读取 appsettings.Development.json 的真实数据库连接串，
/// 提供 StoredProcedureService 与 OssStorageService；测试结束后清理测试前缀的数据。
/// 所有测试必须使用 BillNo 前缀 <see cref="TestBillNoPrefix"/>，以便清理阶段精准定位。
/// </summary>
public class IntegrationTestFixture : IAsyncLifetime
{
    public const string TestBillNoPrefix = "INT_TEST_";        // 测试单据前缀
    public const string TestCustomerCode = "INT_TEST_CUST";     // 测试客户编码
    public const string TestSupplierCode = "INT_TEST_SUP";      // 测试供应商编码
    public const string TestProductCode = "INT_TEST_PROD";      // 测试商品编码

    public IConfiguration Configuration { get; private set; } = null!;
    public StoredProcedureService SpService { get; private set; } = null!;
    public OssStorageService OssService { get; private set; } = null!;

    public Task InitializeAsync()
    {
        // 配置加载策略：
        //   1. 在测试 bin 目录查找 appsettings.Development.json（CI 上复制配置的场景）
        //   2. 在 src/ERP.Api/ 目录查找（本地开发：从 ERP.Api 项目的开发配置复用连接串）
        //   3. 始终允许 ERP_ 前缀的环境变量覆盖（如 ERP_ConnectionStrings__Default）
        var builder = new ConfigurationBuilder();
        var baseDir = AppContext.BaseDirectory;
        var candidatePaths = new[]
        {
            baseDir,                                                                                          // bin 目录
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "ERP.Api")),         // src/ERP.Api
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."))                            // 解决方案根
        };
        foreach (var path in candidatePaths)
        {
            var file = Path.Combine(path, "appsettings.Development.json");
            if (File.Exists(file))
            {
                builder.SetBasePath(path).AddJsonFile("appsettings.Development.json", optional: false);
                break;
            }
        }
        builder.AddEnvironmentVariables(prefix: "ERP_");
        Configuration = builder.Build();
        // 安全护栏：数据库集成测试同样不允许默认写业务库（与部署配置同库时必须人工显式批准）
        TestDatabaseSafetyGuard.EnsureApprovedTarget(Configuration.GetConnectionString("Default"), "数据库集成测试");
        SpService = new StoredProcedureService(Configuration);
        OssService = new OssStorageService(Configuration);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // 留作扩展：可在此清理测试数据。当前 16 个 SP 测试用例自行负责清理（软删除 + BillNo 前缀）。
        return Task.CompletedTask;
    }

    /// <summary>读取 DbContextOptionsBuilder 用的连接串（供需要 DbContext 的测试使用）</summary>
    public string GetConnectionString() => SpService.GetConnectionString();

    /// <summary>打开一个新的 SqlConnection（调用方负责 using）。用于集成测试里直接查数据库验证副作用。</summary>
    public SqlConnection OpenSqlConnection()
    {
        var conn = new SqlConnection(GetConnectionString());
        conn.Open();
        return conn;
    }

    /// <summary>用 SQL 计数表内指定 Oid 的记录数（用于验证 SP 是否真删 / 是否真更新）。</summary>
    public int CountRecords(string tableName, long oidValue)
    {
        using var conn = OpenSqlConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM db_owner.{tableName} WHERE Oid = @oid";
        cmd.Parameters.Add(new SqlParameter("@oid", System.Data.SqlDbType.BigInt) { Value = oidValue });
        return (int)cmd.ExecuteScalar();
    }
}

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<IntegrationTestFixture> { }