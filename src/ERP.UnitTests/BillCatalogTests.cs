using System.Reflection;
using ERP.Api.Controllers;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 通用单据元数据（BillProcController.Bills）完整性测试
/// 验证八大模块所有单据均已配置存储过程驱动支持
/// </summary>
public class BillCatalogTests
{
    /// <summary>八大模块全部 16 种单据编码清单</summary>
    private static readonly string[] ExpectedBillTypes =
    {
        // 订单管理
        "sales-order", "purchase-order",
        // 询价管理
        "inquiry",
        // 物流管理
        "stock-in", "stock-out",
        // 账务管理
        "receipt", "payment", "deposit-apply", "payment-apply",
        "container-settlement", "bulk-settlement", "complaint",
        // 装柜管理
        "receiving-plan", "booking", "pre-loading", "loading-list"
    };

    /// <summary>通过反射读取私有静态字段 Bills 字典</summary>
    private static Dictionary<string, BillMeta> GetBills()
    {
        var field = typeof(BillProcController).GetField("Bills",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (Dictionary<string, BillMeta>)field!.GetValue(null)!;
    }

    [Fact]
    public void Bills_包含全部16种单据类型()
    {
        var bills = GetBills();
        foreach (var type in ExpectedBillTypes)
            Assert.True(bills.ContainsKey(type), $"缺少单据类型配置：{type}");
    }

    [Fact]
    public void Bills_所有单据均配置存储过程()
    {
        var bills = GetBills();
        foreach (var kv in bills)
        {
            Assert.False(string.IsNullOrWhiteSpace(kv.Value.Table), $"单据 {kv.Key} 缺少表名");
            Assert.False(string.IsNullOrWhiteSpace(kv.Value.Proc), $"单据 {kv.Key} 缺少存储过程名");
            Assert.StartsWith("db_owner.sp_Biz_", kv.Value.Proc, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Bills_带明细单据配置了副表元数据()
    {
        var bills = GetBills();
        // 销售订单与装柜单据必须配置明细表
        Assert.NotNull(bills["sales-order"].DetailTable);
        Assert.NotNull(bills["pre-loading"].DetailTable);
        Assert.NotNull(bills["loading-list"].DetailTable);
        Assert.Null(bills["receipt"].DetailTable);
        Assert.Null(bills["booking"].DetailTable);
    }
}
