using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 单证明细行快照的纯规则（ERP-051，无数据库依赖，便于逐条单测）：
/// 允许的单证类型（商业发票 / 装箱单）、可编辑状态（准备状态待制作 / 已制作）、
/// 快照文本与数值精度、行序、服务端金额计算与差异提示文案。
/// <para>边界：本类只做**校验与计算**，不写库；不改写单证、商品资料、销售订单、采购订单、装柜清单、
/// 库存与库存流水、发票、退税、费用或财务记录，也不做任何价格推断（不按商品资料现价覆盖历史单价）。</para>
/// </summary>
public static class TradeDocumentItemRules
{
    // ==================== 0. 口径常量 ====================

    /// <summary>允许明细行的单证类型：商业发票 CI（含单价与金额）</summary>
    public const string CommercialInvoiceDocType = "商业发票";

    /// <summary>允许明细行的单证类型：装箱单 PL（含箱数 / 净重 / 毛重，不含价格）</summary>
    public const string PackingListDocType = "装箱单";

    /// <summary>允许明细行的单证类型白名单（其余类型一律拒绝明细行变更，避免保存含义不明的记录）</summary>
    public static readonly IReadOnlyList<string> AllowedDocTypes = new[] { CommercialInvoiceDocType, PackingListDocType };

    /// <summary>单证状态：待制作（准备状态，可维护明细行）</summary>
    public const string StatusPreparing = "待制作";

    /// <summary>单证状态：已制作（准备状态，可维护明细行）</summary>
    public const string StatusPrepared = "已制作";

    /// <summary>单证状态：已提交客户（已出公司，明细行冻结）</summary>
    public const string StatusSubmitted = "已提交客户";

    /// <summary>单证状态：已使用（业务已归档，明细行冻结）</summary>
    public const string StatusUsed = "已使用";

    /// <summary>可维护明细行的单证状态（准备状态）：已提交客户 / 已使用 / 未知状态一律冻结</summary>
    public static readonly IReadOnlyList<string> EditableStatuses = new[] { StatusPreparing, StatusPrepared };

    /// <summary>单张单证允许的明细行数上限（保证清单、打印与导出有界）</summary>
    public const int MaxLinesPerDocument = 200;

    /// <summary>数量 / 单价 / 重量允许的最大小数位</summary>
    public const int MaxAmountDecimals = 4;

    /// <summary>箱数上限（有界；超过一律拒绝，避免把明显异常值写入快照）</summary>
    public const int MaxPackageCount = 1_000_000;

    /// <summary>行序上限（有界；与行数上限同量级）</summary>
    public const int MaxLineOrder = 1_000_000;

    /// <summary>商品编码上限</summary>
    public const int MaxProductCodeLength = 50;

    /// <summary>商品中英文名称 / 规格上限</summary>
    public const int MaxProductTextLength = 200;

    /// <summary>单位上限</summary>
    public const int MaxUnitLength = 20;

    /// <summary>行备注上限</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>系统支持的币种（与销售订单 / 采购订单币种枚举同源：CNY / USD / EUR / HKD / GBP / JPY）</summary>
    public static readonly string[] SupportedCurrencies = Enum.GetNames<Currency>();

    /// <summary>行口径文案（接口、界面与文档同源）</summary>
    public const string RuleText =
        "明细行口径：只有商业发票与装箱单允许明细行；一行 = 一个商品快照（可选引用商品资料，引用时自动带入"
        + "商品编码 / 中英文名称 / 规格 / 单位）。数量必须大于 0，单价最多 4 位小数，行金额一律由服务端按"
        + "「数量 × 单价」并按币种精度计算（不接受客户端金额）；商业发票行必须使用单证币种且币种在系统币种口径内，"
        + "装箱单行不含单价与金额（恒为 0）。箱数 / 净重 / 毛重可选：未登记即为空白，不臆造为 0；填写毛重时不得小于净重。"
        + "行序留空由服务端追加，显式指定时同一单证内不得重复。";

    /// <summary>模块边界文案（明确不是第二套商品主数据 / 库存交易 / 报关核定价格）</summary>
    public const string BoundaryText =
        "本明细只是单证的**行级快照证据**：不是第二套商品主数据、不是库存交易、不是报关核定价格，"
        + "也不是退税或税务依据；新增 / 修改 / 删除明细行都不会改动商品资料、销售订单、采购订单、装柜清单、"
        + "库存与库存流水、发票、退税、费用或财务记录，也不会回写单证表头金额。";

    // ==================== 1. 单证类型与状态 ====================

    /// <summary>单证类型是否允许明细行（只有商业发票与装箱单）</summary>
    public static bool IsSupportedDocType(string? docType)
    {
        var value = (docType ?? string.Empty).Trim();
        return AllowedDocTypes.Contains(value, StringComparer.Ordinal);
    }

    /// <summary>单证类型必须是商业发票或装箱单（其余类型拒绝明细行变更）</summary>
    public static void EnsureSupportedDocType(string? docType)
    {
        if (IsSupportedDocType(docType)) return;
        var value = (docType ?? string.Empty).Trim();
        var shown = value.Length == 0 ? "（未填写单证类型）" : $"「{value}」";
        throw BusinessException.RuleConflict(
            $"单证类型{shown}不允许明细行：只有 {string.Join(" / ", AllowedDocTypes)} 可维护商品明细行"
            + "（其余类型不接受明细行，避免保存含义不明的记录）");
    }

    /// <summary>单证类型支持文案（只读标注：说明为什么不能维护明细行）</summary>
    public static string DocTypeSupportText(bool supported, string? docType)
        => supported
            ? $"单证类型「{(docType ?? string.Empty).Trim()}」支持明细行"
            : $"单证类型「{(docType ?? string.Empty).Trim()}」不支持明细行：只有 {string.Join(" / ", AllowedDocTypes)} 可维护";

    /// <summary>单证状态是否处于准备状态（可维护明细行）</summary>
    public static bool IsEditableStatus(string? status)
    {
        var value = (status ?? string.Empty).Trim();
        return EditableStatuses.Contains(value, StringComparer.Ordinal);
    }

    /// <summary>
    /// 明细行只在单证处于准备状态（待制作 / 已制作）时可维护；已提交客户 / 已使用以及未知状态一律拒绝，
    /// 保证已提交或已归档的单证快照不被事后改写。
    /// </summary>
    public static void EnsureParentEditable(TradeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var status = (document.Status ?? string.Empty).Trim();
        if (IsEditableStatus(status)) return;

        var reason = status switch
        {
            StatusSubmitted => "单证已提交客户：明细行已冻结，不能再新增 / 修改 / 删除（只能查看）",
            StatusUsed => "单证已使用：明细行已冻结，不能再新增 / 修改 / 删除（只能查看）",
            _ => $"单证状态「{(status.Length == 0 ? "（未填写）" : status)}」不是准备状态，明细行已冻结"
                 + $"（可维护状态：{string.Join(" / ", EditableStatuses)}）"
        };
        throw BusinessException.RuleConflict($"单证「{Safe(document.DocNo)}」{reason}");
    }

    /// <summary>可维护性文案（只读标注：说明当前是否可维护明细行及原因）</summary>
    public static string EditabilityText(TradeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var status = (document.Status ?? string.Empty).Trim();
        return IsEditableStatus(status)
            ? $"单证状态「{status}」处于准备状态：明细行可新增 / 修改 / 删除（提交客户后自动冻结）"
            : $"单证状态「{(status.Length == 0 ? "（未填写）" : status)}」不是准备状态：明细行只读"
              + $"（可维护状态：{string.Join(" / ", EditableStatuses)}）";
    }

    // ==================== 2. 文本校验（有界；超长 / 不安全一律拒绝，不静默截断） ====================

    /// <summary>可选文本校验（去首尾空白；拒绝控制字符与 HTML / 脚本标记字符；超长拒绝）</summary>
    public static string NormalizeOptionalText(string? value, int maxLength, string fieldName)
        => NormalizeText(value, maxLength, fieldName, required: false);

    /// <summary>商品编码 / 商品中文名称的必填二选一（两者都空时无从核对，拒绝保存空行）</summary>
    public static void EnsureProductIdentity(string? productCode, string? productNameCn, long productId)
    {
        if (productId > 0) return;
        if ((productCode ?? string.Empty).Trim().Length > 0) return;
        if ((productNameCn ?? string.Empty).Trim().Length > 0) return;
        throw BusinessException.InvalidParameter(
            "请填写商品编码或商品中文名称（或选择商品资料）：空行无从核对，系统不保存无商品信息的明细行");
    }

    private static string NormalizeText(string? value, int maxLength, string fieldName, bool required)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            if (required) throw BusinessException.InvalidParameter($"{fieldName}不能为空");
            return string.Empty;
        }

        if (text.Length > maxLength)
            throw BusinessException.InvalidParameter($"{fieldName}长度不能超过 {maxLength} 个字符");

        foreach (var ch in text)
        {
            if (char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t')
                throw BusinessException.InvalidParameter($"{fieldName}不能包含控制字符");
        }

        if (text.Contains('<', StringComparison.Ordinal) || text.Contains('>', StringComparison.Ordinal))
            throw BusinessException.InvalidParameter($"{fieldName}不能包含 HTML / 脚本标记字符（< 或 >）");

        return text;
    }

    // ==================== 3. 数值与币种校验 ====================

    /// <summary>数量校验：必须大于 0，最多 4 位小数（超精度一律拒绝，不静默取整）</summary>
    public static decimal NormalizeQuantity(decimal quantity)
    {
        if (quantity <= 0)
            throw BusinessException.InvalidParameter($"数量必须大于 0：收到 {quantity}");
        EnsureDecimals(quantity, "数量");
        return quantity;
    }

    /// <summary>单价校验：不能为负，最多 4 位小数；装箱单行必须为 0（不含价格口径）</summary>
    public static decimal NormalizeUnitPrice(decimal unitPrice, bool pricingAllowed)
    {
        if (unitPrice < 0)
            throw BusinessException.InvalidParameter($"单价不能为负数：收到 {unitPrice}");

        if (!pricingAllowed)
        {
            if (unitPrice != 0m)
                throw BusinessException.InvalidParameter(
                    $"单证类型「{PackingListDocType}」不含价格口径：明细行单价只能为 0（收到 {unitPrice}）");
            return 0m;
        }

        EnsureDecimals(unitPrice, "单价");
        return unitPrice;
    }

    /// <summary>重量校验（净重 / 毛重，可选）：不能为负，最多 4 位小数；未填写保持 null（不臆造为 0）</summary>
    public static decimal? NormalizeWeight(decimal? weight, string fieldName)
    {
        if (weight is null) return null;
        if (weight.Value < 0)
            throw BusinessException.InvalidParameter($"{fieldName}不能为负数：收到 {weight.Value}");
        EnsureDecimals(weight.Value, fieldName);
        return weight.Value;
    }

    /// <summary>箱数校验（可选）：不能为负，且不超过有界上限；未填写保持 null（不臆造为 0）</summary>
    public static int? NormalizePackageCount(int? packageCount)
    {
        if (packageCount is null) return null;
        if (packageCount.Value < 0)
            throw BusinessException.InvalidParameter($"箱数不能为负数：收到 {packageCount.Value}");
        if (packageCount.Value > MaxPackageCount)
            throw BusinessException.InvalidParameter($"箱数不能超过 {MaxPackageCount}：收到 {packageCount.Value}");
        return packageCount.Value;
    }

    /// <summary>毛重不得小于净重（两项都填写时才判定；缺失值不被当成 0 参与比较）</summary>
    public static void EnsureGrossNotBelowNet(decimal? netWeight, decimal? grossWeight)
    {
        if (netWeight is null || grossWeight is null) return;
        if (grossWeight.Value < netWeight.Value)
            throw BusinessException.InvalidParameter(
                $"毛重（{grossWeight.Value}）不能小于净重（{netWeight.Value}）：请核对装箱单重量");
    }

    /// <summary>行序校验（显式指定时）：必须大于 0 且不超过有界上限</summary>
    public static int? NormalizeLineOrder(int? lineOrder)
    {
        if (lineOrder is null) return null;
        if (lineOrder.Value <= 0)
            throw BusinessException.InvalidParameter($"行序必须大于 0：收到 {lineOrder.Value}");
        if (lineOrder.Value > MaxLineOrder)
            throw BusinessException.InvalidParameter($"行序不能超过 {MaxLineOrder}：收到 {lineOrder.Value}");
        return lineOrder.Value;
    }

    /// <summary>币种规范化（去空白 + 大写；空值按 CNY）</summary>
    public static string NormalizeCurrency(string? currency) => CurrencyAmountRules.NormalizeCurrency(currency);

    /// <summary>商业发票行的币种必须在系统币种口径内（不做汇率换算、不接受未知币种参与定价）</summary>
    public static string NormalizePricingCurrency(string? currency)
    {
        var value = NormalizeCurrency(currency);
        if (!SupportedCurrencies.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"单证币种「{value}」不在系统币种口径内（{string.Join(" / ", SupportedCurrencies)}）："
                + "商业发票明细行无法计算金额（系统不做汇率换算）");
        return value;
    }

    /// <summary>
    /// 行金额（服务端权威计算）：数量 × 单价，按币种精度取整（0.5 进位，确定性）；
    /// 客户端提交的金额一律忽略，装箱单行单价为 0 时金额恒为 0。
    /// </summary>
    public static decimal ComputeLineAmount(decimal quantity, decimal unitPrice, string? currency)
        => CurrencyAmountRules.RoundAmount(quantity * unitPrice, currency);

    /// <summary>超精度校验（数量 / 单价 / 重量最多 4 位小数）</summary>
    private static void EnsureDecimals(decimal value, string fieldName)
    {
        if (decimal.Round(value, MaxAmountDecimals) != value)
            throw BusinessException.InvalidParameter($"{fieldName}最多 {MaxAmountDecimals} 位小数：收到 {value}");
    }

    // ==================== 4. 行合计与差异提示（只读派生，绝不回写表头） ====================

    /// <summary>单证表头金额是否已登记（沿用打印口径：金额 0 视为「未填写金额」，不参与差异判定）</summary>
    public static bool IsHeaderAmountRecorded(decimal headerAmount) => headerAmount != 0m;

    /// <summary>行金额合计与单证表头金额是否存在差异（表头未登记金额时不判定差异）</summary>
    public static bool HasAmountMismatch(decimal headerAmount, decimal lineAmountTotal, string? currency)
        => IsHeaderAmountRecorded(headerAmount)
           && CurrencyAmountRules.RoundAmount(headerAmount, currency)
              != CurrencyAmountRules.RoundAmount(lineAmountTotal, currency);

    /// <summary>
    /// 差异提示文案：只**提示**「行合计与单证表头金额不一致」，并明确系统不会自动改写单证金额
    /// （单证台账金额仍由人工维护）；表头未登记金额时照实说明不判定差异。
    /// </summary>
    public static string AmountMismatchText(
        decimal headerAmount, decimal lineAmountTotal, string? currency, int lineCount)
    {
        var cur = CurrencyAmountRules.NormalizeCurrency(currency);
        var total = CurrencyAmountRules.RoundAmount(lineAmountTotal, cur);

        if (lineCount == 0)
            return "本单证没有明细行：行金额合计为 0，不判定差异，也不改动单证台账金额";

        if (!IsHeaderAmountRecorded(headerAmount))
            return $"单证台账未登记金额（金额 0 视为未填写）：明细行金额合计 {total} {cur} 仅作明细参考，"
                   + "系统不判定差异、不改写单证金额";

        var header = CurrencyAmountRules.RoundAmount(headerAmount, cur);
        return HasAmountMismatch(headerAmount, lineAmountTotal, cur)
            ? $"提示：明细行金额合计 {total} {cur} 与单证台账金额 {header} {cur} 不一致"
              + "（差异只作提示，系统不会自动改写单证金额，请人工核对后自行维护）"
            : $"明细行金额合计 {total} {cur} 与单证台账金额 {header} {cur} 一致";
    }

    /// <summary>商品引用可用性文案（不可用 / 停用时照实说明，历史快照照常可读，绝不自动刷新）</summary>
    public static string ProductAvailabilityText(long productId, BaseProduct? product)
    {
        if (productId <= 0) return "未引用商品资料：本行为人工录入的纯文本快照";
        if (product is null || product.IsDeleted)
            return "商品资料已不存在或已删除：本行快照保持登记当时的值，不会自动刷新";
        if (product.Status != 1)
            return "商品资料已停用：本行快照保持登记当时的值，不会自动刷新";
        return "商品资料可用（本行保存的是写入当时的快照）";
    }

    /// <summary>有界回显（错误提示中不整段抛出超长 / 不安全原值）</summary>
    private static string Safe(string? value, int max = 60)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }
}
