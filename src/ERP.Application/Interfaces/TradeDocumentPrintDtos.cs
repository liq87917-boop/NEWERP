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
/// 3) ERP-052 起，本模型在表头字段之外还可携带**明细行快照**（<see cref="TradeDocumentPrintModel.DetailLines"/>，
///    来自 ERP-051 的 `TradeDocumentItems`，由调用方按有界查询装载后传入）：明细列按单证类型适配
///    （商业发票 → 单价与行金额；装箱单 → 箱数与净重 / 毛重），未登记值留空而不是 0，
///    行金额按币种分开合计；老单证（无明细行）的 <see cref="TradeDocumentPrintModel.HasDetailLines"/> 为 false，
///    照常打印 / 导出。
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

    // ==================== ERP-052：明细行快照的打印 / 导出列口径 ====================

    /// <summary>明细列键：行序</summary>
    public const string LineNo = "lineNo";

    /// <summary>明细列键：商品编码</summary>
    public const string ProductCode = "productCode";

    /// <summary>明细列键：商品中文名称</summary>
    public const string ProductNameCn = "productNameCn";

    /// <summary>明细列键：商品英文名称</summary>
    public const string ProductNameEn = "productNameEn";

    /// <summary>明细列键：规格型号</summary>
    public const string Spec = "spec";

    /// <summary>明细列键：数量</summary>
    public const string Quantity = "quantity";

    /// <summary>明细列键：单位</summary>
    public const string Unit = "unit";

    /// <summary>明细列键：单价（仅商业发票）</summary>
    public const string UnitPrice = "unitPrice";

    /// <summary>明细列键：行金额（仅商业发票；服务端计算）</summary>
    public const string LineAmount = "lineAmount";

    /// <summary>明细列键：箱数（仅装箱单；未登记为空白）</summary>
    public const string PackageCount = "packageCount";

    /// <summary>明细列键：净重 kg（仅装箱单；未登记为空白）</summary>
    public const string NetWeight = "netWeight";

    /// <summary>明细列键：毛重 kg（仅装箱单；未登记为空白）</summary>
    public const string GrossWeight = "grossWeight";

    /// <summary>明细列键：行备注</summary>
    public const string LineRemark = "remark";

    /// <summary>商业发票明细列（含单价与行金额；不含箱数与重量口径）</summary>
    public static readonly IReadOnlyList<TradeDocumentPrintLineColumn> CommercialInvoiceLineColumns =
        new List<TradeDocumentPrintLineColumn>
        {
            new(LineNo, "序号", isNumeric: true),
            new(ProductCode, "商品编码"),
            new(ProductNameCn, "商品中文名称"),
            new(ProductNameEn, "商品英文名称"),
            new(Spec, "规格型号"),
            new(Quantity, "数量", isNumeric: true),
            new(Unit, "单位"),
            new(UnitPrice, "单价", isMonetary: true, isNumeric: true),
            new(LineAmount, "行金额", isMonetary: true, isNumeric: true),
            new(LineRemark, "行备注"),
        };

    /// <summary>装箱单明细列（含箱数 / 净重 / 毛重；不含价格口径）</summary>
    public static readonly IReadOnlyList<TradeDocumentPrintLineColumn> PackingListLineColumns =
        new List<TradeDocumentPrintLineColumn>
        {
            new(LineNo, "序号", isNumeric: true),
            new(ProductCode, "商品编码"),
            new(ProductNameCn, "商品中文名称"),
            new(ProductNameEn, "商品英文名称"),
            new(Spec, "规格型号"),
            new(Quantity, "数量", isNumeric: true),
            new(Unit, "单位"),
            new(PackageCount, "箱数", isNumeric: true),
            new(NetWeight, "净重kg", isNumeric: true),
            new(GrossWeight, "毛重kg", isNumeric: true),
            new(LineRemark, "行备注"),
        };

    /// <summary>明细行打印 / 导出口径文案（与界面提示同一份文字）</summary>
    public const string DetailRuleText =
        "明细行按行序输出单证制作当时的行快照：商业发票输出单价与**服务端计算**的行金额，装箱单输出箱数与净重 / 毛重；"
        + "未登记的箱数 / 净重 / 毛重照实留空（不写成 0，也不按商品资料或自由文本推断）；"
        + "行金额不做汇率换算与跨币种合并（不同币种分开列示）。";

    /// <summary>
    /// 按单证类型给出**类型适配**的明细列（商业发票 → 价格口径；装箱单 → 箱数与重量口径）；
    /// 其余类型不支持明细行，返回空列集合并由调用方明确说明原因。
    /// </summary>
    public static IReadOnlyList<TradeDocumentPrintLineColumn> LineColumnsFor(string? docType) => docType switch
    {
        Services.TradeDocumentItemRules.CommercialInvoiceDocType => CommercialInvoiceLineColumns,
        Services.TradeDocumentItemRules.PackingListDocType => PackingListLineColumns,
        _ => Array.Empty<TradeDocumentPrintLineColumn>(),
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
/// 单证中心打印模型（ERP-030；ERP-052 扩展明细行）：打印预览 / 直接打印 / 打印设计共用同一份台账字段投影，
/// 只读派生、不落库、不改单据状态、不新增或修改任何表列；明细行为 ERP-051 已落库快照的**只读投影**，
/// 未登记值照实留空，行金额不被重算、不被回写到单证表头金额。
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

    /// <summary>
    /// 是否存在可打印 / 可导出的商品明细行：只有商业发票与装箱单可含明细行，
    /// 由 <see cref="DetailLines"/> 是否为空派生（老单证无明细行时为 false，照常打印 / 导出）。
    /// </summary>
    public bool HasDetailLines => DetailLines.Count > 0;

    /// <summary>明细行是否因有界读取被截断（true 时合计只是部分合计，界面必须说明）</summary>
    public bool DetailLinesTruncated { get; set; }

    /// <summary>类型适配的明细列（商业发票 → 价格口径；装箱单 → 箱数与重量口径；其余类型为空集合）</summary>
    public List<TradeDocumentPrintLineColumn> DetailColumns { get; set; } = new();

    /// <summary>有序明细行（按行序升序；不含推断值，未登记项为空 / null）</summary>
    public List<TradeDocumentPrintLine> DetailLines { get; set; } = new();

    /// <summary>明细行合计（行金额按币种分开；箱数 / 重量只累加已登记行；无明细行时为 null）</summary>
    public TradeDocumentPrintLineTotals? DetailTotals { get; set; }

    /// <summary>明细行口径文案（无明细行时为空）</summary>
    public string DetailRuleText { get; set; } = string.Empty;

    /// <summary>打印字段（顺序 = 默认打印顺序；无值字段 Available=false、Value 为空）</summary>
    public List<TradeDocumentPrintField> Fields { get; set; } = new();

    /// <summary>
    /// 按单证台账既有字段生成打印模型（纯映射，不访问数据库、不做任何档案回查与文本推断）。
    /// <para>明细行需由调用方按有界查询装载后经 <see cref="From(TradeDocument, IReadOnlyList{TradeDocumentPrintLine}, bool)"/>
    /// 传入；本重载不输出任何明细行（老单证与无子表读数的调用方照常可用）。</para>
    /// </summary>
    public static TradeDocumentPrintModel From(TradeDocument document)
        => From(document, null, truncated: false);

    /// <summary>
    /// 按单证台账字段 + 有序明细行快照生成打印模型（纯映射，不访问数据库）：
    /// 明细列按单证类型适配，合计由明细行派生（行金额按币种分开，绝不回写单证表头金额）。
    /// </summary>
    public static TradeDocumentPrintModel From(TradeDocument document,
        IReadOnlyList<TradeDocumentPrintLine>? detailLines, bool truncated = false)
    {
        var model = BuildFields(document);
        var lines = detailLines ?? Array.Empty<TradeDocumentPrintLine>();

        model.DetailLinesTruncated = truncated;
        model.DetailLines = lines.ToList();
        model.DetailColumns = TradeDocumentPrintSemantics.LineColumnsFor(document.DocType).ToList();
        model.DetailTotals = lines.Count == 0 && !truncated
            ? null
            : TradeDocumentPrintLineTotals.From(lines, truncated);
        model.DetailRuleText = model.DetailColumns.Count == 0 ? string.Empty
            : TradeDocumentPrintSemantics.DetailRuleText;

        return model;
    }

    /// <summary>单证台账字段投影（表头部分；与明细行无关）</summary>
    private static TradeDocumentPrintModel BuildFields(TradeDocument document)
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

/// <summary>单证明细行的打印 / 导出列定义（列键与明细行视图属性同名，前端与导出按列定义取值）</summary>
public sealed class TradeDocumentPrintLineColumn
{
    public TradeDocumentPrintLineColumn(string key, string label, bool isMonetary = false, bool isNumeric = false)
    {
        Key = key;
        Label = label;
        IsMonetary = isMonetary;
        IsNumeric = isNumeric;
    }

    /// <summary>列键（与 <see cref="TradeDocumentPrintLine"/> 属性同名，camelCase）</summary>
    public string Key { get; }

    /// <summary>列中文标签</summary>
    public string Label { get; }

    /// <summary>是否金额列（前端按货币格式输出；Excel 导出保留原始数值）</summary>
    public bool IsMonetary { get; }

    /// <summary>是否数值列（打印右对齐）</summary>
    public bool IsNumeric { get; }
}

/// <summary>
/// 单证明细行快照的只读视图（ERP-052）：生成前预览、共享打印与 Excel 导出**共用同一份**有序行投影，
/// 避免三处各写一套取值口径而漂移。
/// <para>口径：数值一律取自登记当时的值；未登记的箱数 / 净重 / 毛重为 <c>null</c> 且显示文本为空串
/// （**绝不**写成 0，也不回查商品资料或按自由文本推断）；行金额为服务端计算值，不做汇率换算。</para>
/// </summary>
public sealed class TradeDocumentPrintLine
{
    /// <summary>明细行 Id（未落库的生成前预览行为 0）</summary>
    public long Id { get; set; }

    /// <summary>行序（同一单证内唯一；输出顺序按它升序）</summary>
    public int LineNo { get; set; }

    /// <summary>所属单证类型（生成前预览时用于按单证类型分组）</summary>
    public string DocType { get; set; } = string.Empty;

    /// <summary>币种快照（原币；不做汇率换算）</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>币种金额小数位（前端与导出按它格式化金额）</summary>
    public int AmountDecimals { get; set; }

    /// <summary>引用的商品资料 Id（0 = 人工录入的纯文本快照）</summary>
    public long ProductId { get; set; }

    /// <summary>商品编码快照</summary>
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>商品中文名称快照</summary>
    public string ProductNameCn { get; set; } = string.Empty;

    /// <summary>商品英文名称快照（没有英文名时为空，绝不自动翻译）</summary>
    public string ProductNameEn { get; set; } = string.Empty;

    /// <summary>规格型号快照</summary>
    public string Spec { get; set; } = string.Empty;

    /// <summary>数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>单位快照</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>单价（原币；装箱单行恒为 0）</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>行金额（服务端计算：数量 × 单价并按币种精度取整；装箱单行恒为 0）</summary>
    public decimal LineAmount { get; set; }

    /// <summary>箱数（未登记为 null，不写成 0）</summary>
    public int? PackageCount { get; set; }

    /// <summary>净重 kg（未登记为 null，不写成 0）</summary>
    public decimal? NetWeight { get; set; }

    /// <summary>毛重 kg（未登记为 null，不写成 0）</summary>
    public decimal? GrossWeight { get; set; }

    /// <summary>行备注（纯文本）</summary>
    public string Remark { get; set; } = string.Empty;

    /// <summary>本行是否含价格口径（商业发票行 = true；装箱单行 = false）</summary>
    public bool HasPricing { get; set; }

    /// <summary>本行是否含箱数与重量口径（装箱单行 = true；商业发票行 = false）</summary>
    public bool HasPackaging { get; set; }

    /// <summary>数量显示文本（如 10 / 2.5）</summary>
    public string QuantityText { get; set; } = string.Empty;

    /// <summary>单价显示文本（无价格口径时为空）</summary>
    public string UnitPriceText { get; set; } = string.Empty;

    /// <summary>行金额显示文本（无价格口径时为空）</summary>
    public string LineAmountText { get; set; } = string.Empty;

    /// <summary>箱数显示文本（未登记为空）</summary>
    public string PackageCountText { get; set; } = string.Empty;

    /// <summary>净重显示文本（未登记为空）</summary>
    public string NetWeightText { get; set; } = string.Empty;

    /// <summary>毛重显示文本（未登记为空）</summary>
    public string GrossWeightText { get; set; } = string.Empty;

    /// <summary>
    /// 按明细行实体与单证类型构造只读视图（纯映射，不访问数据库、不做推断）：
    /// 单证类型决定是否输出价格口径与箱数 / 重量口径。
    /// </summary>
    public static TradeDocumentPrintLine From(TradeDocumentItem row, string? docType, string? currency)
    {
        ArgumentNullException.ThrowIfNull(row);

        var type = (docType ?? string.Empty).Trim();
        var lineCurrency = Services.CurrencyAmountRules.NormalizeCurrency(
            string.IsNullOrWhiteSpace(row.Currency) ? currency : row.Currency);
        var pricing = string.Equals(type, Services.TradeDocumentItemRules.CommercialInvoiceDocType,
            StringComparison.Ordinal);
        var packaging = string.Equals(type, Services.TradeDocumentItemRules.PackingListDocType,
            StringComparison.Ordinal);

        return new TradeDocumentPrintLine
        {
            Id = row.Id,
            LineNo = row.LineNo,
            DocType = type,
            Currency = lineCurrency,
            AmountDecimals = Services.CurrencyAmountRules.PrecisionOf(lineCurrency),
            ProductId = row.ProductId,
            ProductCode = row.ProductCode ?? string.Empty,
            ProductNameCn = row.ProductNameCn ?? string.Empty,
            ProductNameEn = row.ProductNameEn ?? string.Empty,
            Spec = row.Spec ?? string.Empty,
            Quantity = row.Quantity,
            Unit = row.Unit ?? string.Empty,
            UnitPrice = pricing ? row.UnitPrice : 0m,
            LineAmount = pricing ? row.LineAmount : 0m,
            PackageCount = packaging ? row.PackageCount : null,
            NetWeight = packaging ? row.NetWeight : null,
            GrossWeight = packaging ? row.GrossWeight : null,
            Remark = row.Remark ?? string.Empty,
            HasPricing = pricing,
            HasPackaging = packaging,
            QuantityText = FormatNumber(row.Quantity),
            UnitPriceText = pricing ? FormatMoney(row.UnitPrice, lineCurrency) : string.Empty,
            LineAmountText = pricing ? FormatMoney(row.LineAmount, lineCurrency) : string.Empty,
            PackageCountText = packaging ? FormatNumber(row.PackageCount) : string.Empty,
            NetWeightText = packaging ? FormatNumber(row.NetWeight) : string.Empty,
            GrossWeightText = packaging ? FormatNumber(row.GrossWeight) : string.Empty,
        };
    }

    /// <summary>按币种精度格式化金额（USD → 1250.00；JPY → 1250）</summary>
    private static string FormatMoney(decimal value, string? currency)
        => value.ToString("0." + new string('0', Services.CurrencyAmountRules.PrecisionOf(currency)),
            CultureInfo.InvariantCulture);

    /// <summary>数值格式化（最多 4 位小数并去掉尾随 0；null = 未登记 → 空串而不是 0）</summary>
    private static string FormatNumber(decimal? value)
        => value is null ? string.Empty : FormatNumber(value.Value);

    private static string FormatNumber(decimal value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>单币种行金额合计（不同币种分开列示，绝不合并、不做汇率换算）</summary>
public sealed class TradeDocumentPrintCurrencyTotal
{
    /// <summary>币种</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>该币种下的行金额合计（服务端计算值之和）</summary>
    public decimal Amount { get; set; }

    /// <summary>该币种下的行数</summary>
    public int LineCount { get; set; }

    /// <summary>合计显示文本（按该币种精度格式化）</summary>
    public string AmountText { get; set; } = string.Empty;
}

/// <summary>
/// 明细行合计（只读派生，绝不回写单证表头）：
/// 行金额按**币种分开**合计；箱数 / 净重 / 毛重只累加**已登记**这些值的行并给出登记行数，
/// 没有任何行登记时保持 <c>null</c>（不把「未登记」当成 0）；有界读取被截断时明确标注合计不完整。
/// </summary>
public sealed class TradeDocumentPrintLineTotals
{
    /// <summary>参与合计的明细行数（截断时为已读取行数）</summary>
    public int LineCount { get; set; }

    /// <summary>是否因有界读取被截断（true 时合计只是部分合计）</summary>
    public bool Truncated { get; set; }

    /// <summary>行金额合计（按币种分开；只统计含价格口径的行）</summary>
    public List<TradeDocumentPrintCurrencyTotal> AmountByCurrency { get; set; } = new();

    /// <summary>是否存在多种币种的行（true 时界面与导出必须分开列示）</summary>
    public bool MixedCurrency => AmountByCurrency.Count > 1;

    /// <summary>已登记箱数的行数</summary>
    public int PackageCountRecordedLines { get; set; }

    /// <summary>箱数合计（无行登记时为 null，不写成 0）</summary>
    public int? PackageCountTotal { get; set; }

    /// <summary>已登记重量的行数（净重或毛重任一有值即计入）</summary>
    public int WeightRecordedLines { get; set; }

    /// <summary>净重合计 kg（无行登记时为 null，不写成 0）</summary>
    public decimal? NetWeightTotal { get; set; }

    /// <summary>毛重合计 kg（无行登记时为 null，不写成 0）</summary>
    public decimal? GrossWeightTotal { get; set; }

    /// <summary>明细口径文案</summary>
    public string RuleText { get; set; } = string.Empty;

    /// <summary>缺值 / 截断说明（照实陈述，不给任何推断值）</summary>
    public string MissingEvidenceText { get; set; } = string.Empty;

    /// <summary>按有序明细行视图派生合计（纯计算，不访问数据库）</summary>
    public static TradeDocumentPrintLineTotals From(
        IReadOnlyList<TradeDocumentPrintLine>? lines, bool truncated = false)
    {
        var rows = lines ?? Array.Empty<TradeDocumentPrintLine>();

        var totals = new TradeDocumentPrintLineTotals
        {
            LineCount = rows.Count,
            Truncated = truncated,
            RuleText = TradeDocumentPrintSemantics.DetailRuleText,
        };

        totals.AmountByCurrency = rows.Where(r => r.HasPricing)
            .GroupBy(r => r.Currency, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TradeDocumentPrintCurrencyTotal
            {
                Currency = g.Key,
                Amount = g.Sum(r => r.LineAmount),
                LineCount = g.Count(),
                AmountText = g.Sum(r => r.LineAmount)
                    .ToString("0." + new string('0', Services.CurrencyAmountRules.PrecisionOf(g.Key)),
                        CultureInfo.InvariantCulture),
            })
            .ToList();

        var packageLines = rows.Where(r => r.PackageCount is not null).ToList();
        totals.PackageCountRecordedLines = packageLines.Count;
        totals.PackageCountTotal = packageLines.Count == 0
            ? null
            : packageLines.Sum(r => r.PackageCount!.Value);

        var netLines = rows.Where(r => r.NetWeight is not null).ToList();
        var grossLines = rows.Where(r => r.GrossWeight is not null).ToList();
        totals.WeightRecordedLines = rows.Count(r => r.NetWeight is not null || r.GrossWeight is not null);
        totals.NetWeightTotal = netLines.Count == 0 ? null : netLines.Sum(r => r.NetWeight!.Value);
        totals.GrossWeightTotal = grossLines.Count == 0 ? null : grossLines.Sum(r => r.GrossWeight!.Value);

        totals.MissingEvidenceText = BuildMissingEvidenceText(rows, truncated);
        return totals;
    }

    /// <summary>缺值 / 截断说明：只陈述事实与登记行数，不给任何推断值</summary>
    private static string BuildMissingEvidenceText(IReadOnlyList<TradeDocumentPrintLine> rows, bool truncated)
    {
        var parts = new List<string>();
        if (truncated)
            parts.Add($"明细行超过单次读取上限 {Services.TradeDocumentItemRules.MaxLinesPerDocument} 行："
                      + "以上合计只是已读取行的部分合计（请缩小范围核对）");

        if (rows.Count == 0)
        {
            parts.Add("本单证没有明细行：不输出明细表、不做任何合计（老单证照常可打印 / 导出）");
            return string.Join(" ｜ ", parts);
        }

        var packaging = rows.Where(r => r.HasPackaging).ToList();
        if (packaging.Count > 0)
        {
            parts.Add($"装箱单行 {packaging.Count} 行：箱数未登记 {packaging.Count(r => r.PackageCount is null)} 行、"
                      + $"净重未登记 {packaging.Count(r => r.NetWeight is null)} 行、"
                      + $"毛重未登记 {packaging.Count(r => r.GrossWeight is null)} 行 —— "
                      + "未登记一律留空，不写成 0、不按商品资料或自由文本推断");
        }

        var pricing = rows.Where(r => r.HasPricing).ToList();
        if (pricing.Count > 0)
        {
            parts.Add($"商业发票行 {pricing.Count} 行：单价与行金额为单证制作当时的服务端计算值（按币种精度取整）");
            if (pricing.Select(r => r.Currency).Distinct(StringComparer.Ordinal).Count() > 1)
                parts.Add("存在多种币种的行：合计按币种分开列示，系统不做汇率换算与跨币种合并");
        }

        return string.Join(" ｜ ", parts);
    }
}

