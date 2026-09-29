using ERP.Application.Common;
using ERP.Application.DTOs;

namespace ERP.Application.Services;

/// <summary>
/// 客户报告包预览（ERP-122）的纯规则：正整数客户 Id、日期区间与页大小有界校验、双菜单授权（销售订单 + 客户资料）
/// 与只读 / 边界 / 免责文案。无数据库依赖，便于逐条单测。
/// <para>边界：本规则只做校验与纯映射，不写库、不执行任何 SQL、不推断发票到销售订单的链接、不结算、不计算账户余额或催收状态；</para>
/// <para>两个分区各自复用既有作用域化报表查询接口（<see cref="DynamicSalesOrderReportRules"/> / <see cref="DynamicReceivableReportRules"/>），
/// 由查询层独立重检各自的菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）数据范围，任一失败即拒绝整个响应。</para>
/// </summary>
public static class CustomerReportPacketRules
{
    // ==================== 0. 常量 ====================

    /// <summary>销售订单分区所需的既有菜单编码（与 ERP-112 同源）</summary>
    public const string RequiredOrderMenuCode = DynamicSalesOrderReportRules.RequiredMenuCode;

    /// <summary>销售订单分区所需菜单的中文文案</summary>
    public const string RequiredOrderMenuText = DynamicSalesOrderReportRules.RequiredMenuText;

    /// <summary>发票 / 收款分摊证据分区所需的既有菜单编码（与 ERP-117 同源，即「客户资料」菜单）</summary>
    public const string RequiredReceivableMenuCode = DynamicReceivableReportRules.RequiredMenuCode;

    /// <summary>发票 / 收款分摊证据分区所需菜单的中文文案</summary>
    public const string RequiredReceivableMenuText = DynamicReceivableReportRules.RequiredMenuText;

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 20;

    /// <summary>每页条数上限（有界：取两个分区中更严格的上限，保证两个查询接口都接受）</summary>
    public const int MaxPageSize = DynamicReceivableReportRules.MaxPageSize; // 100（销售订单分区上限 200，取交集更严格值）

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（接口与界面统一声明）</summary>
    public const string ReadOnlyText =
        "只读客户报告包预览：销售订单与发票 / 收款分摊证据分两个独立有界分区读取当前账号数据范围内的数据，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：销售订单分区复用销售订单菜单授权与销售订单持久化字段白名单；发票 / 收款分摊证据分区复用客户资料菜单授权与 ERP-074 对账证据口径；"
        + "两个分区各自独立计数、金额按原币呈现、剩余证据保留 known / unknown / over_allocated；"
        + "绝不推断发票到销售订单的链接、不结算、不计算账户余额或催收状态";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读快照：两个分区绝不相互推导或拼接跨单链接，金额按原币呈现、不做汇率换算或跨币种合并";

    // ==================== 2. 校验（fail closed） ====================

    /// <summary>校验身份（未登录 / 无效用户 Id 直接拒绝，绝不猜测身份）</summary>
    public static void EnsureAuthenticated(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览客户报告包", ErrorCodes.Unauthorized);
    }

    /// <summary>校验客户 Id（必须为正整数，fail closed）</summary>
    public static void ValidateCustomerId(long customerId)
    {
        if (customerId <= 0)
            throw BusinessException.InvalidParameter($"客户 Id 必须为正整数：{customerId}");
    }

    /// <summary>校验日期区间（开始晚于结束 = 无效，fail closed）</summary>
    public static void ValidateDateRange(DateTime? start, DateTime? end)
    {
        if (start.HasValue && end.HasValue && start.Value.Date > end.Value.Date)
            throw BusinessException.InvalidParameter("开始日期不能晚于结束日期");
    }

    /// <summary>校验每页条数（1 ~ 100，超出直接拒绝，fail closed）</summary>
    public static void ValidatePageSize(int pageSize)
    {
        if (pageSize < 1 || pageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1~{MaxPageSize} 之间");
    }

    /// <summary>归一化页码（小于 1 归一到第 1 页）</summary>
    public static int NormalizePage(int page) => page < 1 ? 1 : page;

    /// <summary>
    /// 双菜单授权校验（fail closed）：销售订单菜单与客户资料菜单都必须具备，任一缺失即拒绝整个响应。
    /// <para>两个分区的查询接口还会各自独立重检本分区的菜单授权，这里是端点级的先行双授权兜底。</para>
    /// </summary>
    public static void EnsureBothMenuPermissions(HashSet<string> authorizedMenuCodes)
    {
        ArgumentNullException.ThrowIfNull(authorizedMenuCodes);

        if (!authorizedMenuCodes.Contains(RequiredOrderMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{RequiredOrderMenuText}」（{RequiredOrderMenuCode}）模块授权：拒绝预览客户报告包（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        if (!authorizedMenuCodes.Contains(RequiredReceivableMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{RequiredReceivableMenuText}」（{RequiredReceivableMenuCode}）模块授权：拒绝预览客户报告包（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }
    }

    // ==================== 3. 纯映射 ====================

    /// <summary>把两个作用域化报表查询结果组装为独立的两个分区（不拼接、不推导跨单链接）</summary>
    public static CustomerReportPacketDto BuildPacket(
        long customerId,
        DynamicSalesOrderReportPageDto salesOrders,
        DynamicReceivableReportPageDto receivableEvidence)
    {
        ArgumentNullException.ThrowIfNull(salesOrders);
        ArgumentNullException.ThrowIfNull(receivableEvidence);

        return new CustomerReportPacketDto(
            customerId,
            salesOrders,
            receivableEvidence,
            ReadOnlyText,
            BoundaryText,
            DisclaimerText);
    }

    // ==================== 4. Excel 导出（ERP-123） ====================

    /// <summary>销售订单工作表名称（与预览分区同源，清晰标注、互不混淆）</summary>
    public const string SalesOrderSheetName = "销售订单";

    /// <summary>发票 / 收款分摊证据工作表名称（与预览分区同源，清晰标注、互不混淆）</summary>
    public const string ReceivableEvidenceSheetName = "应收证据";

    /// <summary>电子表格公式注入风险首字符（OWASP：= / + / - / @ 及制表符 / 回车 / 换行）</summary>
    private static bool IsFormulaLeadingChar(char c)
        => c is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n';

    /// <summary>文本是否以电子表格公式字符开头（会触发 Excel 公式注入）</summary>
    public static bool IsFormulaLeading(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return IsFormulaLeadingChar(value[0]);
    }

    /// <summary>
    /// 转义 Excel 公式前导文本：以危险字符开头的文本前缀单引号，使单元格保持字面文本、不被当作公式执行。
    /// <para>仅对字符串生效；数值 / 日期 / 布尔等类型原样返回（由导出端按其类型写入对应单元格）。</para>
    /// </summary>
    public static object? EscapeFormulaLeading(object? value)
    {
        if (value is string s && IsFormulaLeading(s))
            return "'" + s;
        return value;
    }

    /// <summary>把一页预览行转成导出行：对每个单元格做公式注入转义，键保持不变（两个工作表共用）</summary>
    public static Dictionary<string, object?> BuildExportRow(Dictionary<string, object?> row)
    {
        var export = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in row)
            export[kv.Key] = EscapeFormulaLeading(kv.Value);
        return export;
    }
}
