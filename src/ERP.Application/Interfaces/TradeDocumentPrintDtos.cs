using ERP.Domain.Entities;
using System.Globalization;

namespace ERP.Application.Interfaces;

/// <summary>
/// 单证中心打印字段口径（ERP-030）：后端打印模型、前端打印渲染与测试断言共用同一套字段键，
/// 避免「样式设计」勾选的字段与打印输出错位。
/// </summary>
/// <remarks>
/// 口径（不臆造数据）：
/// 1) 打印字段只取 <see cref="TradeDocument"/> 已落库的字段，键名与实体一致（camelCase，供前端直接取值）；
/// 2) 台账没有落库的字段一律 Available=false + Value=null，由前端按**空白**渲染，
///    绝不按客户 Id 回查客户档案、不按文本匹配推断单证号 / 柜号 / 报关单号，也不把「0」当成有效金额；
/// 3) 本打印模型只投影单证**表头**字段（商业发票 / 装箱单的商品明细行已在 ERP-051 落库为
///    `TradeDocumentItems`，但本模型<strong>不</strong>返回明细行）：
///    <see cref="TradeDocumentPrintModel.HasDetailLines"/> 在当前实现中恒为 false，
///    由后续任务 ERP-052 把明细行纳入打印输出；在此之前打印件不输出任何明细行。
/// </remarks>
public static class TradeDocumentPrintSemantics
{
    /// <summary>打印字段键（与前端 MODULES['doc-center'] 字段键、打印模型字段键完全同名）</summary>
    public const string DocNo = "docNo";
    public const string DocType = "docType";
    public const string Status = "status";
    public const string IssueDate = "issueDate";
    public const string CustomerName = "customerName";
    public const string SalesOrderNo = "salesOrderNo";
    public const string RefNo = "refNo";
    public const string DeclareNo = "declareNo";
    public const string Amount = "amount";
    public const string Currency = "currency";
    public const string DeparturePort = "departurePort";
    public const string DestinationPort = "destinationPort";
    public const string IssuedBy = "issuedBy";
    public const string Copies = "copies";
    public const string FileNote = "fileNote";
    public const string Remark = "remark";

    /// <summary>
    /// 打印字段定义（顺序 = 未保存打印模板时的默认打印顺序）：
    /// Key 与实体字段同名，Label 为打印页中文标签，IsMonetary / IsDate 供前端按金额与日期格式化。
    /// </summary>
    public static readonly IReadOnlyList<TradeDocumentPrintFieldDefinition> FieldDefinitions =
        new List<TradeDocumentPrintFieldDefinition>
        {
            new(DocNo, "单证编号"),
            new(DocType, "单证类型"),
            new(Status, "状态"),
            new(IssueDate, "出具/签发日期", isDate: true),
            new(CustomerName, "客户名称"),
            new(SalesOrderNo, "关联销售订单号"),
            new(RefNo, "关联柜号/订舱号"),
            new(DeclareNo, "关联报关单号"),
            new(Amount, "单证金额", isMonetary: true),
            new(Currency, "币种"),
            new(DeparturePort, "起运港"),
            new(DestinationPort, "目的港"),
            new(IssuedBy, "制作人/出证机构"),
            new(Copies, "份数"),
            new(FileNote, "附件说明/存放位置"),
            new(Remark, "备注"),
        };
}

/// <summary>单证打印字段定义（字段键 / 中文标签 / 金额与日期标记）</summary>
public sealed class TradeDocumentPrintFieldDefinition
{
    public TradeDocumentPrintFieldDefinition(string key, string label, bool isMonetary = false, bool isDate = false)
    {
        Key = key;
        Label = label;
        IsMonetary = isMonetary;
        IsDate = isDate;
    }

    /// <summary>字段键（与实体属性同名，camelCase）</summary>
    public string Key { get; }

    /// <summary>打印页中文标签</summary>
    public string Label { get; }

    /// <summary>是否金额字段（前端按货币格式输出）</summary>
    public bool IsMonetary { get; }

    /// <summary>是否日期字段（前端按 yyyy-MM-dd 输出）</summary>
    public bool IsDate { get; }
}

/// <summary>
/// 单证打印字段值：<see cref="Available"/>=false 表示单证台账中该字段**没有落库值**，
/// 前端按空白渲染（不是「0」，也不是从其他字段 / 档案推测出来的值）。
/// </summary>
public sealed class TradeDocumentPrintField
{
    /// <summary>字段键</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>打印页中文标签</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>原样字符串值（日期 yyyy-MM-dd、金额 0.00）；没有落库值时为空字符串</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>单证台账中是否存在该字段的落库值（false = 明确无值，前端空白渲染）</summary>
    public bool Available { get; set; }

    /// <summary>是否金额字段</summary>
    public bool IsMonetary { get; set; }

    /// <summary>是否日期字段</summary>
    public bool IsDate { get; set; }
}

/// <summary>
/// 单证中心打印模型（ERP-030）：打印预览 / 直接打印 / 打印设计共用同一份台账字段投影，
/// 只读派生、不落库、不改单据状态、不新增或修改任何表列。
/// </summary>
public sealed class TradeDocumentPrintModel
{
    /// <summary>单证 Id</summary>
    public long Id { get; set; }

    /// <summary>单证编号（打印页眉使用）</summary>
    public string DocNo { get; set; } = string.Empty;

    /// <summary>单证类型</summary>
    public string DocType { get; set; } = string.Empty;

    /// <summary>状态（待制作 / 已制作 / 已提交客户 / 已使用，原样输出，不做状态码映射）</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>打印标题默认值（打印模板未配置 Title 时使用）：单证类型，未维护类型时为「单证」</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>是否存在可打印的商品明细行：单证为单表台账，恒为 false（不新增明细结构）</summary>
    public bool HasDetailLines => false;

    /// <summary>打印字段（顺序 = 默认打印顺序；无值字段 Available=false、Value 为空）</summary>
    public List<TradeDocumentPrintField> Fields { get; set; } = new();

    /// <summary>
    /// 按单证台账既有字段生成打印模型（纯映射，不访问数据库、不做任何档案回查与文本推断）。
    /// </summary>
    public static TradeDocumentPrintModel From(TradeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var model = new TradeDocumentPrintModel
        {
            Id = document.Id,
            /* 打印页眉取值与字段表同一口径：去除首尾空白（台账历史数据可能带空格），内部空格与换行保留 */
            DocNo = (document.DocNo ?? string.Empty).Trim(),
            DocType = (document.DocType ?? string.Empty).Trim(),
            Status = (document.Status ?? string.Empty).Trim(),
            Title = string.IsNullOrWhiteSpace(document.DocType) ? "单证" : document.DocType.Trim(),
        };

        foreach (var definition in TradeDocumentPrintSemantics.FieldDefinitions)
        {
            var (value, available) = Resolve(document, definition.Key);
            model.Fields.Add(new TradeDocumentPrintField
            {
                Key = definition.Key,
                Label = definition.Label,
                Value = available ? value : string.Empty,
                Available = available,
                IsMonetary = definition.IsMonetary,
                IsDate = definition.IsDate,
            });
        }

        return model;
    }

    /// <summary>
    /// 取单证台账中一个打印字段的落库值：仅按本字段自身取值，
    /// 不加任何「为空时回退到别的字段 / 回查档案」的规则（缺失即 Available=false）。
    /// </summary>
    private static (string Value, bool Available) Resolve(TradeDocument document, string key) => key switch
    {
        TradeDocumentPrintSemantics.DocNo => Text(document.DocNo),
        TradeDocumentPrintSemantics.DocType => Text(document.DocType),
        TradeDocumentPrintSemantics.Status => Text(document.Status),
        TradeDocumentPrintSemantics.IssueDate => document.IssueDate.HasValue
            ? (document.IssueDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), true)
            : (string.Empty, false),
        TradeDocumentPrintSemantics.CustomerName => Text(document.CustomerName),
        TradeDocumentPrintSemantics.SalesOrderNo => Text(document.SalesOrderNo),
        TradeDocumentPrintSemantics.RefNo => Text(document.RefNo),
        TradeDocumentPrintSemantics.DeclareNo => Text(document.DeclareNo),
        // 金额 0 视为「未填写金额」（报关单 / 装箱单等本就不含金额口径），打印空白而不是 0.00
        TradeDocumentPrintSemantics.Amount => document.Amount != 0m
            ? (document.Amount.ToString("0.00", CultureInfo.InvariantCulture), true)
            : (string.Empty, false),
        TradeDocumentPrintSemantics.Currency => Text(document.Currency),
        TradeDocumentPrintSemantics.DeparturePort => Text(document.DeparturePort),
        TradeDocumentPrintSemantics.DestinationPort => Text(document.DestinationPort),
        TradeDocumentPrintSemantics.IssuedBy => Text(document.IssuedBy),
        // 份数 0 视为「未填写份数」，与金额同一口径
        TradeDocumentPrintSemantics.Copies => document.Copies > 0
            ? (document.Copies.ToString(CultureInfo.InvariantCulture), true)
            : (string.Empty, false),
        TradeDocumentPrintSemantics.FileNote => Text(document.FileNote),
        TradeDocumentPrintSemantics.Remark => Text(document.Remark),
        _ => (string.Empty, false),
    };

    /// <summary>文本字段：去除首尾空白，空串表示台账无值</summary>
    private static (string Value, bool Available) Text(string? value)
        => string.IsNullOrWhiteSpace(value) ? (string.Empty, false) : (value.Trim(), true);
}
