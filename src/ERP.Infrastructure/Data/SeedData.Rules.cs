using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Data;

/// <summary>
/// 种子数据：系统参数与单据号规则
/// </summary>
public static partial class SeedData
{
    /// <summary>系统参数种子</summary>
    private static async Task SeedParametersAsync(ErpDbContext db)
    {
        var defaults = new (string Key, string Value, string Name, string Desc)[]
        {
            ("CompanyName", "百纳外贸有限公司", "公司名称", "系统展示的公司名称"),
            ("CompanyAddress", "中国·浙江省", "公司地址", "系统展示的公司地址"),
            ("DefaultCurrency", "USD", "默认币种", "新建单据时的默认币种"),
            ("ExchangeRate", "1", "默认汇率", "新建单据时的默认汇率（允许手动修改）"),
            ("ExchangeRateSource", "manual", "汇率来源", "汇率获取方式：manual=手动维护"),
            ("InventoryWarning", "10", "库存预警阈值", "低于该数值时库存预警"),
            ("LogoUrl", "", "公司 Logo", "登录页与首页 Logo 地址")
        };

        foreach (var (key, value, name, desc) in defaults)
        {
            if (!await db.SysParameters.AnyAsync(p => p.ParamKey == key))
            {
                db.SysParameters.Add(new SysParameter
                {
                    ParamKey = key,
                    ParamValue = value,
                    ParamName = name,
                    Description = desc,
                    IsSystem = true,
                    CreatedAt = DateTime.Now
                });
            }
        }
    }

    /// <summary>单据号规则种子</summary>
    private static async Task SeedDocumentNumberRulesAsync(ErpDbContext db)
    {
        var defaults = new (DocumentType Type, string Code, string Name, string Prefix)[]
        {
            (DocumentType.Inquiry, "INQ", "询价单", "INQ"),
            (DocumentType.SalesOrder, "SO", "销售订单", "SO"),
            (DocumentType.PurchaseOrder, "PO", "采购订单", "PO"),
            (DocumentType.StockIn, "RK", "采购入库单", "RK"),
            (DocumentType.StockOut, "CK", "销售出库单", "CK"),
            (DocumentType.ReceivingPlan, "JH", "收货计划", "JH"),
            (DocumentType.ContainerBooking, "DG", "订柜信息", "DG"),
            (DocumentType.PreLoading, "YZ", "预装柜单", "YZ"),
            (DocumentType.LoadingList, "ZQ", "装柜清单", "ZQ"),
            (DocumentType.DepositApply, "DJ", "定金申请单", "DJ"),
            (DocumentType.PaymentApply, "HK", "货款申请单", "HK"),
            (DocumentType.Payment, "FK", "付款单", "FK"),
            (DocumentType.ContainerSettlement, "ZJ", "装柜结算单", "ZJ"),
            (DocumentType.BulkSettlement, "SJ", "散货结算单", "SJ"),
            (DocumentType.Receipt, "SK", "收款单", "SK"),
            (DocumentType.Complaint, "KS", "客诉单", "KS"),
            // 阶段 3：形式发票 PI 字轨（报价单 QT 由 SchemaUpgrader 幂等补齐，保持历史库升级一致）
            (DocumentType.ProformaInvoice, "PI", "形式发票 PI", "PI")
        };

        foreach (var (type, code, name, prefix) in defaults)
        {
            if (!await db.SysDocumentNumberRules.AnyAsync(r => r.RuleCode == code))
            {
                db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
                {
                    DocumentType = type,
                    RuleCode = code,
                    RuleName = name,
                    Prefix = prefix,
                    DateFormat = "yyyyMMdd",
                    SerialLength = 4,
                    Separator = string.Empty,
                    CurrentSequence = 0,
                    YearlyReset = true,
                    CreatedAt = DateTime.Now
                });
            }
        }
    }
}
