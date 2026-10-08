using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 旧单据（BillProc 通用控制器 <c>api/v2/bills</c>）的<strong>实时身份 + 既有模块授权</strong>护栏（ERP-404）。
/// <list type="number">
/// <item><b>实时身份</b>：缺失 / 非法身份按未认证拒绝（fail closed），账号不存在 / 已删除按未认证拒绝，
/// 账号已禁用按权限不足拒绝；绝不把空身份当作匿名或管理员，也不缓存（每次请求重新查询）。</item>
/// <item><b>既有模块授权</b>：普通账号必须实时具备该单据族<b>既有功能菜单</b>
/// （<c>seed</c>：<c>SalesOrder</c> / <c>PurchaseOrder</c> / <c>Inquiry</c> / <c>StockIn</c> / <c>StockOut</c> /
/// <c>FinanceReceipt</c> / <c>FinancePayment</c> / <c>FinanceDepositApply</c> / <c>FinancePaymentApply</c> /
/// <c>FinanceContainerSettlement</c> / <c>FinanceBulkSettlement</c> / <c>FinanceComplaint</c> /
/// <c>ContainerReceivingPlan</c> / <c>ContainerBooking</c> / <c>ContainerPreLoading</c> /
/// <c>ContainerLoadingList</c> 对应的中文菜单）；<b>绝不</b>把「导出」类菜单（<c>*-export</c>）当模块权限，
/// 也绝不新增任何菜单 / 角色 / 用户授权。特权账号（超级管理员 / 系统内置角色）沿用既有全部访问口径。</item>
/// <item><b>权威范围复用</b>：客户数据范围只复用唯一权威口径
/// <see cref="SalespersonDataScopeService"/>（ERP-097），本类不重新定义范围语义。</item>
/// </list>
/// <para><b>边界</b>：本类只做纯判定与有界只读查询，不落库、不调用任何存储过程、不写日志、不发送通知、
/// 不改写任何来源单据 / 库存 / 财务记录；调用方（控制器）负责在读取计数、生成单号或写入任何数据之前调用。</para>
/// </summary>
public static class LegacyBillAuthorizationRules
{
    /// <summary>无身份 / 非法身份的拒绝文案。</summary>
    public const string UnauthorizedText = "请先登录后再访问旧单据路由（fail closed）";

    /// <summary>账号不存在 / 已删除的拒绝文案。</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问旧单据路由（fail closed）";

    /// <summary>账号已禁用的拒绝文案。</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问旧单据路由（fail closed）";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）。</summary>
    public const string RuleText =
        "旧单据路由（存单 / 状态流转 / Excel 导入）在读取任何计数、生成 / 消耗单号、调用任何 sp_Biz_* 或写入任何数据之前，"
        + "都重新校验实时身份（缺失 / 非法 / 已删除按未认证拒绝，已禁用按权限不足拒绝）、该单据族既有功能菜单授权与业务员数据范围；"
        + "导出类菜单（*-export）绝不当作模块权限，也绝不新增任何用户授权或提供匿名 / 管理员降级。";

    /// <summary>缺少既有功能菜单授权时的拒绝文案（按族拼装，导出菜单绝不替代功能菜单）。</summary>
    public static string MenuDeniedText(string requiredMenuCode, string requiredMenuText)
        => $"当前账号没有「{requiredMenuText}」（{requiredMenuCode}）模块授权：拒绝访问旧单据路由"
           + "（fail closed，不执行任何写入、不返回任何数据）";

    /// <summary>
    /// 实时身份校验（fail closed）：返回当前账号的既有数据范围（供写 / 读两侧复用），
    /// <c>userId</c> 缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，账号已禁用按权限不足拒绝。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureLiveIdentityAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException(UnauthorizedText, ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException(UserDeletedText, ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException(UserDisabledText, ErrorCodes.Forbidden);

        return await SalespersonDataScopeService.ResolveAsync(db, userId.Value);
    }

    /// <summary>
    /// 既有功能模块授权校验（fail closed）：先复用 <see cref="EnsureLiveIdentityAsync"/> 的实时身份口径，
    /// 再要求普通账号实时具备 <paramref name="requiredMenuCode"/> 对应的既有功能菜单
    /// （<b>绝不</b>把导出菜单当模块权限）；特权账号沿用既有全部访问口径。授权撤销或账号停用后下一次请求立即收敛。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureModuleAuthorizedAsync(
        IErpDbContext db, long? userId, string requiredMenuCode, string requiredMenuText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requiredMenuCode))
            throw new ArgumentException("必须提供既有功能模块菜单编码", nameof(requiredMenuCode));

        var scope = await EnsureLiveIdentityAsync(db, userId, ct);

        // 特权账号（超级管理员 / 系统内置角色）沿用既有全部访问口径；其余账号必须实时具备既有功能菜单。
        if (scope.IsPrivileged)
            return scope;

        var menuCodes = await CustomerReceivableReconciliationService
            .LoadAuthorizedMenuCodesAsync(db, userId!.Value);
        if (!menuCodes.Contains(requiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(MenuDeniedText(requiredMenuCode, requiredMenuText), ErrorCodes.Forbidden);

        return scope;
    }

    /// <summary>受限账号在「无权威客户归属」旧单据族上的拒绝文案（fail closed，绝不猜测归属）。</summary>
    public static string ReadOwnershipDeniedText(string familyKey, string title)
        => $"{title}（{familyKey}）没有可核验的客户归属口径：受限账号禁止读取该旧单据族"
           + "（fail closed，不返回任何数据；绝不猜测旧 Oid = 规范 Id、绝不按客户名匹配、绝不放开为不受限）";

    /// <summary>受限账号客户数据范围超过受控上限时的拒绝文案（fail closed，绝不生成无界条件）。</summary>
    public static string ReadScopeTooLargeText(string familyKey, int maxCustomers)
        => $"{familyKey} 当前账号的客户数据范围超过受控上限 {maxCustomers}：拒绝读取旧单据族（fail closed）";

    /// <summary>旧单据读侧口径文案（接口 / 文档同源）。</summary>
    public const string ReadRuleText =
        "旧单据读侧（查询 / 翻页导航 / 详情 / 默认值）在读取任何计数、页码或表头之前，"
        + "都重新校验实时身份（缺失 / 非法 / 已删除按未认证拒绝，已禁用按权限不足拒绝）、该族既有功能菜单授权"
        + "（导出类菜单 *-export 绝不当作模块权限）与业务员客户数据范围；"
        + "受限账号在无权威客户归属的族上 fail closed，普通读取绝不放开为不受限。";

    /// <summary>旧单据操作历史口径文案（接口 / 文档同源，ERP-406）。</summary>
    public const string HistoryRuleText =
        "旧单据操作历史（{billType}/{oid}/logs）在返回任何单号 / 客户提示 / 计数之前，"
        + "先复用旧单据读侧门禁（实时身份 + 该族既有功能菜单 + 业务员客户数据范围），"
        + "并要求正数 Oid 命中调用方数据范围内的权威旧库行；随后只返回精确单据路径（含有限动作段）"
        + "与族既有模块标题、权威单号交叉匹配的历史行；零 / 负数 Oid 拒绝全局历史（既有入口为 api/sys/logs），"
        + "绝不新增任何用户授权或提供匿名 / 管理员降级，也不返回请求体等原始载荷。";

    /// <summary>
    /// 旧单据<strong>读侧</strong>授权（fail closed）：解析受控读侧族目录（未知 / 畸形族标识在访问任何数据之前拒绝），
    /// 再复用 <see cref="EnsureModuleAuthorizedAsync"/> 的实时身份 + 既有功能菜单口径，返回当前账号的数据范围
    /// 供计数 / 分页 / 导航 / 详情共用。
    /// <para>普通读取只要求该族<strong>既有功能菜单</strong>（<c>sales-order</c> / <c>purchase-order</c> / <c>inquiry</c> / …），
    /// <strong>绝不</strong>要求导出专用菜单（<c>*-export</c>）；特权账号沿用既有全部访问口径，但仍须通过实时身份校验。</para>
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureReadAuthorizedAsync(
        IErpDbContext db, long? userId, string? familyKey, CancellationToken ct = default)
    {
        var family = LegacyBillReadCatalog.Resolve(familyKey);
        return await EnsureModuleAuthorizedAsync(db, userId, family.ModuleMenuCode, family.ModuleMenuText, ct);
    }
}

/// <summary>
/// 旧单据读侧的<strong>有限、显式受控族目录</strong>（ERP-405）：16 个 <c>BillProcController.Bills</c> 条目的
/// 服务端唯一读侧清单。
/// <list type="number">
/// <item><b>复用既有有限目录</b>：表名与授权表头列<strong>派生自</strong> <see cref="LegacyBillExportCatalog"/>（ERP-308），
/// 不新增第二套元数据发现，也绝不来自客户端。</item>
/// <item><b>显式行归属</b>：每族的客户归属列（<c>CustId</c> / <c>CustomerId</c>）在下方显式登记，且必须命中该族授权表头白名单；
/// 供应商 / 业务员 / 无归属族显式登记为 <see cref="LegacyBillOwnershipKind.None"/>（受限账号 fail closed）；
/// 未登记归属的族在类型初始化时直接失败（fail closed，绝不猜测 <c>Oid = 规范 Id</c> 或按客户名匹配）。</item>
/// <item><b>副表明细</b>：只有具备确定表名 / 外键 / 列白名单的族才登记副表明细（其余族显式无副表 = 有意兼容省略）。</item>
/// </list>
/// </summary>
public static class LegacyBillReadCatalog
{
    /// <summary>授权表头首列（旧库主键，稳定排序键）。</summary>
    public const string OidColumn = "Oid";

    /// <summary>
    /// 显式行归属登记（族键 → 归属类型 + 归属列）：<b>客户归属</b>族可按既有客户数据范围约束；
    /// 其余族（供应商 / 业务员 / 无归属）显式登记为 <see cref="LegacyBillOwnershipKind.None"/>。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (LegacyBillOwnershipKind Kind, string? Column)> Ownership =
        new Dictionary<string, (LegacyBillOwnershipKind, string?)>(StringComparer.OrdinalIgnoreCase)
        {
            ["sales-order"] = (LegacyBillOwnershipKind.Customer, "CustId"),
            ["purchase-order"] = (LegacyBillOwnershipKind.None, null),
            ["inquiry"] = (LegacyBillOwnershipKind.Customer, "CustomerId"),
            ["stock-in"] = (LegacyBillOwnershipKind.None, null),
            ["stock-out"] = (LegacyBillOwnershipKind.Customer, "CustomerId"),
            ["receipt"] = (LegacyBillOwnershipKind.Customer, "CustomerId"),
            ["payment"] = (LegacyBillOwnershipKind.None, null),
            ["deposit-apply"] = (LegacyBillOwnershipKind.Customer, "CustomerId"),
            ["payment-apply"] = (LegacyBillOwnershipKind.Customer, "CustomerId"),
            ["container-settlement"] = (LegacyBillOwnershipKind.Customer, "CustomerId"),
            ["bulk-settlement"] = (LegacyBillOwnershipKind.Customer, "CustomerId"),
            ["complaint"] = (LegacyBillOwnershipKind.Customer, "CustomerId"),
            ["receiving-plan"] = (LegacyBillOwnershipKind.None, null),
            ["booking"] = (LegacyBillOwnershipKind.Customer, "CustomerId"),
            ["pre-loading"] = (LegacyBillOwnershipKind.None, null),
            ["loading-list"] = (LegacyBillOwnershipKind.Customer, "CustomerId"),
        };

    /// <summary>
    /// 副表明细登记（仅登记旧结构中确定存在的副表 / 外键 / 列白名单；其余族显式无副表 = 有意兼容省略）。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, LegacyBillDetailDefinition> Details =
        new Dictionary<string, LegacyBillDetailDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["sales-order"] = new LegacyBillDetailDefinition(
                "SalesOrderDetail", "SalesOrderId",
                new[] { OidColumn, "ProductId", "ProductName", "Spec", "Quantity", "Unit", "UnitPrice", "Amount", "DeliveryDate" }),
            ["pre-loading"] = new LegacyBillDetailDefinition(
                "ContainerPreLoadingDetail", "PreLoadingId",
                new[] { OidColumn, "ProductId", "ProductName", "Quantity", "Cartons", "Weight", "Volume" }),
            ["loading-list"] = new LegacyBillDetailDefinition(
                "ContainerLoadingDetail", "LoadingListId",
                new[] { OidColumn, "ProductId", "ProductName", "Quantity", "Cartons", "Weight", "Volume" }),
        };

    private static readonly Dictionary<string, LegacyBillReadFamilyDefinition> ByFamilyKey =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>全部 16 个旧单据读侧族（顺序稳定，与 <see cref="LegacyBillExportCatalog.Families"/> 一致）。</summary>
    public static readonly IReadOnlyList<LegacyBillReadFamilyDefinition> Families = BuildFamilies();

    static LegacyBillReadCatalog()
    {
        foreach (var family in Families)
            ByFamilyKey[family.FamilyKey] = family;
    }

    /// <summary>按族键解析读侧定义（未知 / 空白 / 畸形一律在访问任何数据之前拒绝）。</summary>
    public static LegacyBillReadFamilyDefinition Resolve(string? familyKey)
    {
        if (string.IsNullOrWhiteSpace(familyKey))
            throw BusinessException.InvalidParameter("旧单据族标识不能为空");

        var key = familyKey.Trim();
        if (key.Length > 64 || ContainsUnsafeIdentifier(key))
            throw BusinessException.InvalidParameter($"旧单据族标识非法：{key}");

        if (!ByFamilyKey.TryGetValue(key, out var family))
            throw BusinessException.InvalidParameter($"未知的旧单据族标识：{key}");

        return family;
    }

    /// <summary>按族键解析读侧定义（失败返回 false，不抛异常）。</summary>
    public static bool TryResolve(string? familyKey, out LegacyBillReadFamilyDefinition family)
    {
        family = null!;
        if (string.IsNullOrWhiteSpace(familyKey))
            return false;

        var key = familyKey.Trim();
        if (key.Length > 64 || ContainsUnsafeIdentifier(key))
            return false;

        return ByFamilyKey.TryGetValue(key, out family!);
    }

    private static bool ContainsUnsafeIdentifier(string value)
        => value.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'));

    private static List<LegacyBillReadFamilyDefinition> BuildFamilies()
    {
        var result = new List<LegacyBillReadFamilyDefinition>(LegacyBillExportCatalog.Families.Count);
        foreach (var export in LegacyBillExportCatalog.Families)
        {
            if (!Ownership.TryGetValue(export.FamilyKey, out var ownership))
                throw new InvalidOperationException($"旧单据读侧缺少显式行归属登记：{export.FamilyKey}");

            // 普通读取只要求该族既有功能菜单：目录首菜单必须等于族键（导出专用菜单绝不作为模块权限）。
            if (export.RequiredMenuCodes.Count == 0 ||
                !string.Equals(export.RequiredMenuCodes[0], export.FamilyKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"旧单据读侧既有功能菜单口径不匹配：{export.FamilyKey}");

            var header = new List<string> { OidColumn };
            header.AddRange(export.Columns.Select(c => c.Key));

            if (ownership.Kind == LegacyBillOwnershipKind.Customer)
            {
                if (string.IsNullOrWhiteSpace(ownership.Column) ||
                    !header.Contains(ownership.Column, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"旧单据读侧客户归属列不在授权表头白名单内：{export.FamilyKey}");
            }
            else if (!string.IsNullOrWhiteSpace(ownership.Column))
            {
                throw new InvalidOperationException($"旧单据读侧无归属族不得登记归属列：{export.FamilyKey}");
            }

            Details.TryGetValue(export.FamilyKey, out var detail);

            // 读侧查询只使用 BillNo（关键字）与 Status（状态筛选）两个受控列：必须命中授权白名单。
            if (!header.Contains("BillNo", StringComparer.OrdinalIgnoreCase) ||
                !header.Contains("Status", StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"旧单据读侧缺少 BillNo / Status 授权列：{export.FamilyKey}");

            result.Add(new LegacyBillReadFamilyDefinition(
                export.FamilyKey,
                export.TableName,
                export.Title,
                export.FamilyKey,
                export.Title,
                ownership.Kind,
                ownership.Column,
                header,
                detail));
        }

        return result;
    }
}

