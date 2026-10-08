using ERP.Application.Common;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>旧单据有限变更策略里的操作标识（与 BillProcController 的 Save / 状态流转 / Excel 导入一一对应）。</summary>
public enum LegacyBillOperation
{
    /// <summary>保存（新增 / 更新）。</summary>
    Save,

    /// <summary>删除。</summary>
    Delete,

    /// <summary>审核。</summary>
    Audit,

    /// <summary>销审。</summary>
    UnAudit,

    /// <summary>作废。</summary>
    Void,

    /// <summary>还原。</summary>
    Restore,

    /// <summary>Excel 导入（逐行 Save）。</summary>
    Import
}

/// <summary>
/// 旧单据族的有限、服务端自有变更策略（ERP-404）。全部字段为编译期常量，绝不来自客户端，
/// 也绝不落到数据库全量元数据发现。
/// <para><see cref="HasValidatedAdapter"/> 恒为 <c>false</c>：旧库单体表以 <c>Oid</c> 为主键，
/// 而规范 EF 实体以 <c>Id</c> 为主键，二者没有权威身份映射，也没有已验证的业务适配器，
/// 因此任何旧写路径都必须 fail closed，绝不推断 <c>旧 Oid = 规范 Id</c>、绝不静默重映射、
/// 绝不执行 <c>sp_Biz_*</c>。</para>
/// </summary>
public sealed record LegacyBillMutationPolicy(
    string FamilyKey,
    string TableName,
    string ProcedureName,
    string Title,
    string ModuleMenuCode,
    string ModuleMenuText,
    bool HasValidatedAdapter,
    string CanonicalRoute,
    string CanonicalRouteText);

/// <summary>
/// 旧单据（BillProc 通用控制器存单 / 状态流转 / Excel 导入）的<strong>有限服务端变更策略</strong>（ERP-404）。
/// <list type="number">
/// <item><b>有限目录</b>：显式登记全部 16 个旧单据族（与 <c>BillProcController.Bills</c> 一一对应）的表名 /
/// 存储过程 / 既有功能菜单 / 有限规范业务路由；未知 / 空白 / 畸形族标识在访问任何数据之前拒绝。</item>
/// <item><b>写前门禁</b>：<see cref="AuthorizeAsync"/> 依次执行「实时身份 + 既有功能模块授权」（导出菜单绝不
/// 当作模块权限）、「调用方 <c>Fields</c> 不得覆盖保留命令身份 / 动作（<c>@Action</c> / <c>@Oid</c> 等）」，
/// 最后按策略裁决；调用方（控制器）必须在默认值兜底、单号预约、任何 <c>sp_Biz_*</c> 调用、日志与钉钉通知
/// <b>之前</b>调用本方法。</item>
/// <item><b>fail closed</b>：今天不存在任何已验证适配器（<see cref="LegacyBillMutationPolicy.HasValidatedAdapter"/>
/// 恒为 <c>false</c>）—— 在权威旧 <c>Oid</c> ↔ 规范 <c>Id</c> 映射与已验证业务适配器落地之前，一律以稳定的
/// 业务错误拒绝，并给出有限、既有的规范业务路由指引；绝不执行 <c>sp_Biz_*</c>、绝不占用单号、绝不写日志 / 通知。</item>
/// <item><b>规范工作流不受影响</b>：本目录只覆盖旧写路径；规范业务路由（<c>api/sales-orders</c> 等）与旧报表 /
/// 导出路由（ERP-308 迁移退役门槛）完全不变。</item>
/// </list>
/// <para><b>边界</b>：本类只做纯判定与有限解析，不落库、不改写任何单据 / 库存 / 财务记录，也不新增菜单 /
/// 角色 / 用户授权；不编辑任何存储过程 / 数据库结构，也不复制一套独立 ERP 实现。</para>
/// </summary>
public static class LegacyBillMutationRules
{
    /// <summary>全部 16 个旧单据族的有限变更策略（顺序稳定，与 <c>BillProcController.Bills</c> 一致）。</summary>
    public static readonly IReadOnlyList<LegacyBillMutationPolicy> Families = BuildFamilies();

    /// <summary>调用方 <c>Fields</c> 中绝不允许出现、绝不允许覆盖的保留命令字段（大小写不敏感）。</summary>
    public static readonly IReadOnlyList<string> ReservedCommandFields = new[]
    {
        "Action", "Oid", "Result", "Msg", "BillNo", "DetailsJson"
    };

    private static readonly HashSet<string> ReservedCommandFieldSet =
        new(ReservedCommandFields, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, LegacyBillMutationPolicy> ByFamilyKey;

    static LegacyBillMutationRules()
    {
        ByFamilyKey = new Dictionary<string, LegacyBillMutationPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Families)
            ByFamilyKey[family.FamilyKey] = family;
    }

    /// <summary>按族键解析有限策略（未知 / 空白 / 畸形一律在访问数据之前拒绝）。</summary>
    public static LegacyBillMutationPolicy Resolve(string? familyKey)
    {
        if (string.IsNullOrWhiteSpace(familyKey))
            throw BusinessException.InvalidParameter("旧单据族标识不能为空");

        var key = familyKey.Trim();
        if (key.Length > 64 || ContainsUnsafeIdentifier(key))
            throw BusinessException.InvalidParameter($"旧单据族标识非法：{key}");

        if (!ByFamilyKey.TryGetValue(key, out var policy))
            throw BusinessException.InvalidParameter($"未知的旧单据族标识：{key}");

        return policy;
    }

    /// <summary>按族键解析有限策略（失败返回 false，不抛异常）。</summary>
    public static bool TryResolve(string? familyKey, out LegacyBillMutationPolicy policy)
    {
        policy = null!;
        if (string.IsNullOrWhiteSpace(familyKey))
            return false;

        var key = familyKey.Trim();
        if (key.Length > 64 || ContainsUnsafeIdentifier(key))
            return false;

        return ByFamilyKey.TryGetValue(key, out policy!);
    }

    /// <summary>解析操作标识（Save / Delete / Audit / UnAudit / Void / Restore / Import，大小写不敏感）；未知标识拒绝。</summary>
    public static LegacyBillOperation ParseOperation(string? operation)
    {
        if (TryParseOperation(operation, out var parsed))
            return parsed;

        throw BusinessException.InvalidParameter($"未知的旧单据操作标识：{operation}");
    }

    /// <summary>尝试解析操作标识（失败返回 false，不抛异常）。</summary>
    public static bool TryParseOperation(string? operation, out LegacyBillOperation parsed)
    {
        parsed = LegacyBillOperation.Save;
        if (string.IsNullOrWhiteSpace(operation))
            return false;

        switch (operation.Trim().ToLowerInvariant())
        {
            case "save": parsed = LegacyBillOperation.Save; return true;
            case "delete": parsed = LegacyBillOperation.Delete; return true;
            case "audit": parsed = LegacyBillOperation.Audit; return true;
            case "unaudit": parsed = LegacyBillOperation.UnAudit; return true;
            case "void": parsed = LegacyBillOperation.Void; return true;
            case "restore": parsed = LegacyBillOperation.Restore; return true;
            case "import": parsed = LegacyBillOperation.Import; return true;
            default: return false;
        }
    }

    /// <summary>操作标识的中文文案（拒绝消息与文档同源）。</summary>
    public static string OperationText(LegacyBillOperation operation) => operation switch
    {
        LegacyBillOperation.Save => "保存",
        LegacyBillOperation.Delete => "删除",
        LegacyBillOperation.Audit => "审核",
        LegacyBillOperation.UnAudit => "销审",
        LegacyBillOperation.Void => "作废",
        LegacyBillOperation.Restore => "还原",
        LegacyBillOperation.Import => "Excel 导入",
        _ => operation.ToString()
    };

    /// <summary>调用方字段键是否属于保留命令字段（大小写不敏感、去首尾空白）。</summary>
    public static bool IsReservedCommandField(string? key)
        => !string.IsNullOrWhiteSpace(key) && ReservedCommandFieldSet.Contains(key.Trim());

    /// <summary>从调用方字段键集合中找出全部保留命令字段（去重、大小写不敏感）。</summary>
    public static IReadOnlyList<string> FindReservedCommandFields(IEnumerable<string>? keys)
        => (keys ?? Array.Empty<string>())
            .Where(IsReservedCommandField)
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 拒绝调用方 <c>Fields</c> 中的保留命令字段：命令身份 / 动作（<c>@Action</c> / <c>@Oid</c> 等）
    /// 只能由服务端根据路由与策略确定，绝不允许调用方覆盖。
    /// </summary>
    public static void EnsureNoReservedCommandFields(IDictionary<string, object?>? fields)
    {
        if (fields is null || fields.Count == 0)
            return;

        var reserved = FindReservedCommandFields(fields.Keys);
        if (reserved.Count == 0)
            return;

        throw BusinessException.InvalidParameter(
            $"请求 Fields 不得覆盖保留命令字段（{string.Join(", ", reserved)}）："
            + "@Action / @Oid 等命令身份与动作只能由服务端根据路由确定（fail closed，不产生任何写入）");
    }

    /// <summary>剥离调用方字段中的保留命令字段，返回只含业务字段的副本（纵深防御；保留命令身份 / 动作永不被覆盖）。</summary>
    public static Dictionary<string, object?> StripReservedCommandFields(IDictionary<string, object?>? fields)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fields is null)
            return result;

        foreach (var kv in fields)
        {
            if (IsReservedCommandField(kv.Key))
                continue;
            result[kv.Key] = kv.Value;
        }

        return result;
    }

    /// <summary>
    /// 稳定的 fail-closed 业务文案：明确说明旧库 <c>Oid</c> 与规范 <c>Id</c> 无权威映射、未执行 <c>sp_Biz_*</c>、
    /// 未占用单号、未写日志 / 通知，并给出该族有限、既有的规范业务路由指引。
    /// </summary>
    public static string UnsupportedText(LegacyBillMutationPolicy policy, LegacyBillOperation operation)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return $"旧单据写入路由已停用（{policy.Title} / {OperationText(operation)}）：{policy.TableName} 为旧库单体表"
            + $"（主键 Oid、存储过程 {policy.ProcedureName}），当前没有权威的「旧 Oid ↔ 规范 Id」映射与已验证业务适配器，"
            + "因此 fail closed：未执行任何 sp_Biz_*、未占用单号、未写操作日志、未发送通知。"
            + $"请改用既有规范业务路由 {policy.CanonicalRoute}（{policy.CanonicalRouteText}）。";
    }

    /// <summary>
    /// 旧单据写前有限变更策略门禁：实时身份 + 既有功能模块授权（导出菜单不作模块权限）→ 保留命令字段拒绝 →
    /// 有限策略裁决（今天无任何已验证适配器，一律 fail closed）。放行时返回命中的策略，否则抛出稳定业务错误。
    /// </summary>
    public static async Task<LegacyBillMutationPolicy> AuthorizeAsync(
        IErpDbContext db, long? userId, string? familyKey, LegacyBillOperation operation,
        IDictionary<string, object?>? callerFields = null, CancellationToken ct = default)
    {
        // 1) 有限策略：未知 / 畸形族标识在访问任何数据之前拒绝。
        var policy = Resolve(familyKey);

        // 2) 实时身份 + 既有功能模块授权（fail closed；导出菜单绝不当作模块权限）。
        await LegacyBillAuthorizationRules.EnsureModuleAuthorizedAsync(
            db, userId, policy.ModuleMenuCode, policy.ModuleMenuText, ct);

        // 3) 调用方 Fields 绝不能覆盖保留命令身份 / 动作。
        EnsureNoReservedCommandFields(callerFields);

        // 4) 有限策略裁决：今天不存在任何已验证适配器 → fail closed，绝不执行 sp_Biz_*。
        if (!policy.HasValidatedAdapter)
            throw new BusinessException(UnsupportedText(policy, operation), ErrorCodes.RuleConflict);

        return policy;
    }

    private static LegacyBillMutationPolicy Family(
        string familyKey, string table, string procedure, string title,
        string menuCode, string menuText, string canonicalRoute, string canonicalRouteText)
        => new(
            familyKey, table, procedure, title, menuCode, menuText,
            // 无权威旧 Oid ↔ 规范 Id 映射、无已验证业务适配器：恒定 fail closed（绝不猜测 / 静默重映射）。
            HasValidatedAdapter: false,
            canonicalRoute, canonicalRouteText);

    private static List<LegacyBillMutationPolicy> BuildFamilies() => new()
    {
        Family("sales-order", "SalesOrder", "db_owner.sp_Biz_SalesOrder", "销售订单",
            "sales-order", "销售订单", "/api/sales-orders", "规范销售订单工作流（含来源链路 / 数量金额 / 生命周期守卫）"),
        Family("purchase-order", "PurchaseOrder", "db_owner.sp_Biz_PurchaseOrder", "采购订单",
            "purchase-order", "采购订单", "/api/purchase-orders", "规范采购订单工作流（含来源订单链接 / 交期 / 生命周期守卫）"),
        Family("inquiry", "Inquiry", "db_owner.sp_Biz_Inquiry", "询价单",
            "inquiry", "询价单", "/api/inquiries", "规范询价单工作流（含来源行锁 / 转报价单 / 生命周期守卫）"),
        Family("stock-in", "StockIn", "db_owner.sp_Biz_StockIn", "采购入库单",
            "stock-in", "采购入库", "/api/stock-ins", "规范采购入库工作流（含库存成本 / 流水 / 来源订单守卫）"),
        Family("stock-out", "StockOut", "db_owner.sp_Biz_StockOut", "销售出库单",
            "stock-out", "销售出库", "/api/stock-outs", "规范销售出库工作流（含库存成本 / 流水 / 来源订单守卫）"),
        Family("receipt", "FinanceReceipt", "db_owner.sp_Biz_FinanceReceipt", "收款单",
            "receipt", "收款单", "/api/finance/receipts", "规范收款单工作流（含收款分摊 / 资金来源证据 / 生命周期守卫）"),
        Family("payment", "FinancePayment", "db_owner.sp_Biz_FinancePayment", "付款单",
            "payment", "付款单", "/api/finance/payments", "规范付款单工作流（含付款分摊 / 资金来源证据 / 生命周期守卫）"),
        Family("deposit-apply", "FinanceDepositApply", "db_owner.sp_Biz_FinanceDepositApply", "定金申请单",
            "deposit-apply", "定金申请单", "/api/finance/deposit-applies", "规范定金申请工作流（含销售订单来源 / 生命周期守卫）"),
        Family("payment-apply", "FinancePaymentApply", "db_owner.sp_Biz_FinancePaymentApply", "货款申请单",
            "payment-apply", "货款申请单", "/api/finance/payment-applies", "规范货款申请工作流（含销售订单来源 / 生命周期守卫）"),
        Family("container-settlement", "FinanceContainerSettlement", "db_owner.sp_Biz_FinanceContainerSettlement", "装柜结算单",
            "container-settlement", "装柜结算单", "/api/finance/container-settlements", "规范装柜结算工作流（含装柜清单来源 / 金额守卫）"),
        Family("bulk-settlement", "FinanceBulkSettlement", "db_owner.sp_Biz_FinanceBulkSettlement", "散货结算单",
            "bulk-settlement", "散货结算单", "/api/finance/bulk-settlements", "规范散货结算工作流（含金额守卫 / 生命周期守卫）"),
        Family("complaint", "FinanceComplaint", "db_owner.sp_Biz_FinanceComplaint", "客诉单",
            "complaint", "客诉单", "/api/finance/complaints", "规范客诉单工作流（含销售订单来源 / 生命周期守卫）"),
        Family("receiving-plan", "ContainerReceivingPlan", "db_owner.sp_Biz_ContainerReceivingPlan", "收货计划",
            "receiving-plan", "收货计划", "/api/container/receiving-plans", "规范收货计划工作流（含订柜文本留痕 / 生命周期守卫）"),
        Family("booking", "ContainerBooking", "db_owner.sp_Biz_ContainerBooking", "订柜信息",
            "booking", "订柜信息", "/api/container/bookings", "规范订柜工作流（含跟踪字段 / 生命周期守卫）"),
        Family("pre-loading", "ContainerPreLoading", "db_owner.sp_Biz_ContainerPreLoading", "预装柜单",
            "pre-loading", "预装柜单", "/api/container/pre-loadings", "规范预装柜工作流（含来源需求 / 订柜链接 / 生命周期守卫）"),
        Family("loading-list", "ContainerLoadingList", "db_owner.sp_Biz_ContainerLoadingList", "装柜清单",
            "loading-list", "装柜清单", "/api/container/loading-lists", "规范装柜清单工作流（含多客户参与方 / 出库链接 / 生命周期守卫）"),
    };

    private static bool ContainsUnsafeIdentifier(string value)
        => value.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'));
}
