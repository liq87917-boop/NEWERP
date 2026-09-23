using ERP.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// StoredProcedureService 单元测试：构造函数空连接串校验、GetConnectionString 返回值、私有 ConvertParamValue 的 JSON 元素转换逻辑。
/// 注：ExecuteAsync 与 GetNewOidAsync 触发的真实 SP 调用属于集成测试范畴，
///     需要 init2.sql + init3.sql + init4.sql 在真实库已部署才能验证，且会改变数据库状态。
///     建议另建 ERP.IntegrationTests 项目（在 CI 环境跑）覆盖 16 个 sp_Biz_* 的端到端调用。
/// </summary>
public class StoredProcedureServiceTests
{
    [Fact]
    public void 构造函数_无连接串_抛InvalidOperationException()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => new StoredProcedureService(config));
        Assert.Contains("数据库连接字符串", ex.Message);
    }

    [Fact]
    public void 构造函数_含连接串_初始化成功_GetConnectionString返回相同值()
    {
        const string connStr = "Server=test;Database=WMERP_Data;User Id=test;Password=test";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connStr
            })
            .Build();

        var sp = new StoredProcedureService(config);
        Assert.Equal(connStr, sp.GetConnectionString());
    }

    // =============== ConvertParamValue（反射访问私有静态方法）===============

    private static MethodInfo ConvertParamValueMethod =>
        typeof(StoredProcedureService).GetMethod("ConvertParamValue",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("未找到私有方法 ConvertParamValue");

    [Fact]
    public void ConvertParamValue_JsonElement_字符串_转换为string()
    {
        using var doc = JsonDocument.Parse("\"hello\"");
        var actual = ConvertParamValueMethod.Invoke(null, new object?[] { doc.RootElement });
        Assert.Equal("hello", actual);
    }

    [Fact]
    public void ConvertParamValue_JsonElement_整数_转换为Int64()
    {
        using var doc = JsonDocument.Parse("42");
        var actual = ConvertParamValueMethod.Invoke(null, new object?[] { doc.RootElement });
        Assert.Equal(42L, actual);
    }

    [Fact]
    public void ConvertParamValue_JsonElement_小数_转换为Decimal()
    {
        using var doc = JsonDocument.Parse("3.14");
        var actual = ConvertParamValueMethod.Invoke(null, new object?[] { doc.RootElement });
        Assert.Equal(3.14m, actual);
    }

    [Fact]
    public void ConvertParamValue_JsonElement_布尔true_转换为true()
    {
        using var doc = JsonDocument.Parse("true");
        var actual = ConvertParamValueMethod.Invoke(null, new object?[] { doc.RootElement });
        Assert.Equal(true, actual);
    }

    [Fact]
    public void ConvertParamValue_JsonElement_布尔false_转换为false()
    {
        using var doc = JsonDocument.Parse("false");
        var actual = ConvertParamValueMethod.Invoke(null, new object?[] { doc.RootElement });
        Assert.Equal(false, actual);
    }

    [Fact]
    public void ConvertParamValue_JsonElement_null_转换为null()
    {
        using var doc = JsonDocument.Parse("null");
        var actual = ConvertParamValueMethod.Invoke(null, new object?[] { doc.RootElement });
        Assert.Null(actual);
    }

    [Fact]
    public void ConvertParamValue_普通int_原样返回()
    {
        var actual = ConvertParamValueMethod.Invoke(null, new object?[] { 42 });
        Assert.Equal(42, actual);
    }

    [Fact]
    public void ConvertParamValue_普通string_原样返回()
    {
        var actual = ConvertParamValueMethod.Invoke(null, new object?[] { "hello" });
        Assert.Equal("hello", actual);
    }

    [Fact]
    public void ConvertParamValue_普通null_转换为DBNull()
    {
        var actual = ConvertParamValueMethod.Invoke(null, new object?[] { null });
        Assert.Equal(DBNull.Value, actual);
    }
}