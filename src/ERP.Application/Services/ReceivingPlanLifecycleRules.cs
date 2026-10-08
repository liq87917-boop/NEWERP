using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 收货计划（<see cref="ContainerReceivingPlan"/>，<c>api/container/receiving-plans</c>）实时授权、主数据可用性与
/// 生命周期护栏（ERP-361）。
/// <para><b>实时授权</b>：列表 / 详情 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除每一个路由都先重新解析
/// 实时身份（缺失 / 非法按未认证，账号不存在或已删除按未认证，账号禁用按权限不足）→ 既有「收货计划」
/// （<c>receiving-plan</c>）菜单授权 → 权威数据范围（复用 ERP-097 <see cref="SalespersonDataScopeService"/>），
/// 全部判定严格发生在任何计数、单据号生成与写入之前。</para>
/// <para><b>供应商计划没有权威业务员归属列</b>：<see cref="ContainerReceivingPlan"/> 上既没有客户也没有业务员字段，
/// 无法按归属过滤，因此这里只放行<b>特权账号</b>与<b>已映射业务员的受限账号</b>；<b>未映射业务员的受限账号 fail closed</b>，
/// 绝不降级为全局 / 管理员可见。既有 ERP-097 数据范围口径保持原样，本类不新增任何权限模型。</para>
/// <para><b>实时主数据与边界</b>：新增 / 修改 / 提交 / 审核 / 取消 / 删除之前，供应商必须真实可用
/// （存在、未删除、启用，且 <c>SupplierId &gt; 0</c>）；可选目的港（<c>PortId</c>）填写时必须指向启用且未删除的
/// 港口字典项（<c>BaseOtherInfo.InfoType = Port</c>）；柜型必须是已知 <see cref="ContainerType"/> 枚举；
/// 订柜单号 / 柜号 / 目的地 / 备注按实体长度上限去首尾空白后有界。总件数仅在<b>提交 / 审核</b>时要求为正数。</para>
/// <para><b>订柜单号只是显式 legacy 文本</b>：<see cref="ContainerReceivingPlan.BookingNo"/> 保留为历史文本，
/// 本护栏<b>绝不</b>按单号等自由文本反查 <c>ContainerBooking</c>、绝不臆造订柜链接，也绝不把收货计划审核
/// 当成收货 / 入库 / 出运：不写库存 / 库存流水 / 财务 / 单证，不调用任何外部系统。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>：不落库、不改单据、不新增表 / 列 / 菜单 / 权限模型，也不把空身份当作管理员；
/// 状态变更的行锁与可串行化事务由调用方（<c>ContainerReceivingPlanController</c>）负责。</para>
/// </summary>
public static class ReceivingPlanLifecycleRules
{
    /// <summary>收货计划操作所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）</summary>
    public const string RequiredMenuCode = "receiving-plan";

    /// <summary>收货计划模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "收货计划";

    /// <summary>目的港字典项资料类型（<c>BaseOtherInfo.InfoType</c>，与既有「港口」字典同源）</summary>
    public const string PortInfoType = "Port";

    /// <summary>订柜单号长度上限（与 <see cref="ContainerReceivingPlan.BookingNo"/> 的 <c>MaxLength</c> 同源）</summary>
    public const int BookingNoMaxLength = 50;

    /// <summary>柜号长度上限（与 <see cref="ContainerReceivingPlan.ContainerNo"/> 的 <c>MaxLength</c> 同源）</summary>
    public const int ContainerNoMaxLength = 50;

    /// <summary>目的地长度上限（与 <see cref="ContainerReceivingPlan.Destination"/> 的 <c>MaxLength</c> 同源）</summary>
    public const int DestinationMaxLength = 200;

    /// <summary>备注长度上限（与 <see cref="ContainerReceivingPlan.Remark"/> 的 <c>MaxLength</c> 同源）</summary>
    public const int RemarkMaxLength = 500;

    /// <summary>未映射业务员的受限账号拒绝文案（fail closed，不泄露任何范围外单据，也不降级为全局可见）</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（收货计划操作员），不能访问收货计划（fail closed，不泄露任何范围外单据）";

    /// <summary>历史读取时主数据不可用的显式证据前缀（接口文案 / 文档同源）</summary>
    public const string UnavailableEvidencePrefix =
        "历史收货计划来源主数据不可用（只读照常返回，不可用于新增 / 修改 / 状态变更）：";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "收货计划（列表 / 详情 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除）在读取任何计数或生成单据号之前，" +
        "都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）、既有「收货计划」" +
        "（receiving-plan）菜单授权与权威数据范围（复用 ERP-097）；供应商计划没有权威业务员归属列，因此未映射业务员的受限账号" +
        "fail closed，绝不降级为全局 / 管理员可见。";

    /// <summary>主数据 / 边界文案（不新增权限 / 表列 / 外部调用）</summary>
    public const string BoundaryText =
        "新增 / 修改 / 提交 / 审核 / 取消 / 删除之前，供应商必须为正整数且真实可用（存在、未删除、已启用），" +
        "可选目的港必须是启用且未删除的港口字典项（BaseOtherInfo.InfoType = Port），柜型必须是已知枚举，" +
        "订柜单号 / 柜号 / 目的地 / 备注去首尾空白后有界，总件数在提交 / 审核时为正数；" +
        "订柜单号只作显式 legacy 文本保留，绝不按单号推断订柜链接，也绝不把审核当收货 / 入库 / 出运；" +
        "本护栏不新增任何表 / 列 / 菜单 / 权限，不写库存 / 财务 / 单证，不调用外部系统。" +
        "状态变更共用同一把收货计划行锁与可串行化事务，历史主数据不可用时读取照常并给出显式证据。";

    /// <summary>
    /// 身份 / 账号状态 / 既有「收货计划」菜单授权 / 权威数据范围四重校验（fail closed），返回本次请求的数据范围。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>非特权账号无既有 receiving-plan 菜单授权 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>非特权账号未映射为业务员 → <see cref="ErrorCodes.Forbidden"/>（fail closed，不降级为全局可见）。</item>
    /// </list>
    /// 每次请求重新解析，授权 / 菜单 / 员工映射变更后下一次请求立即收敛；特权账号继承既有全部访问（与 ERP-097 同源）。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException($"请先登录后再访问{RequiredMenuText}", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException($"登录账号不存在或已删除，禁止访问{RequiredMenuText}", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException($"登录账号已禁用，禁止访问{RequiredMenuText}（fail closed）", ErrorCodes.Forbidden);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        // 特权账号继承既有全部访问（与 ERP-097 同源）；普通账号必须显式具备收货计划菜单。
        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService
                .LoadAuthorizedMenuCodesAsync(db, userId.Value);
            if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            {
                throw new BusinessException(
                    $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝访问{RequiredMenuText}" +
                    "（fail closed，不返回 / 不修改任何收货计划数据）",
                    ErrorCodes.Forbidden);
            }

            // 供应商计划没有权威业务员归属列：未映射业务员的受限账号 fail closed，绝不取得全局访问。
            if (scope.SalesmanId is null or <= 0)
                throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);
        }

        return scope;
    }

    /// <summary>供应商必须为正整数且真实可用（存在、未删除、已启用）；历史收货计划照常可读。</summary>
    public static async Task EnsureSupplierAvailableAsync(
        IErpDbContext db, long supplierId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (supplierId <= 0)
            throw BusinessException.InvalidParameter("收货计划必须指定供应商（SupplierId 必须为正整数）");

        var supplier = await db.BaseSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == supplierId, ct);
        if (supplier is null || supplier.IsDeleted)
            throw BusinessException.NotFound(
                $"供应商（Id={supplierId}）不存在或已删除，不能用于收货计划的新增 / 修改 / 状态变更（历史收货计划仍可读取）");
        if (supplier.Status != 1)
            throw BusinessException.RuleConflict(
                $"供应商「{supplier.SupplierName}」已停用，不能用于收货计划的新增 / 修改 / 状态变更（历史收货计划仍可读取）");
    }

    /// <summary>
    /// 可选目的港：未填写（<c>null</c>）跳过；填写时必须为正整数，且指向启用、未删除的港口字典项
    /// （<c>BaseOtherInfo.InfoType = Port</c>）。其他资料类型的 Id 一律不被采信。
    /// </summary>
    public static async Task EnsurePortAvailableAsync(
        IErpDbContext db, long? portId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (portId is null) return;
        if (portId.Value <= 0)
            throw BusinessException.InvalidParameter("目的港（PortId）填写时必须为正整数（留空 = 未指定）");

        var port = await db.BaseOtherInfos.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == portId.Value, ct);
        if (port is null || port.IsDeleted)
            throw BusinessException.NotFound(
                $"目的港字典项（Id={portId.Value}）不存在或已删除，不能用于收货计划的新增 / 修改 / 状态变更（历史收货计划仍可读取）");
        if (!string.Equals(port.InfoType, PortInfoType, StringComparison.OrdinalIgnoreCase))
            throw BusinessException.InvalidParameter(
                $"目的港（Id={portId.Value}）不是港口字典项（InfoType={PortInfoType}），不能用于收货计划");
        if (port.Status != 1)
            throw BusinessException.RuleConflict(
                $"目的港「{port.InfoName}」已停用，不能用于收货计划的新增 / 修改 / 状态变更（历史收货计划仍可读取）");
    }

    /// <summary>柜型必须是已知 <see cref="ContainerType"/> 枚举值（绝不落库未定义取值）。</summary>
    public static void EnsureContainerTypeValid(ContainerType containerType)
    {
        if (!Enum.IsDefined(typeof(ContainerType), containerType))
            throw BusinessException.InvalidParameter(
                $"柜型无效：{(int)containerType}（应为 20GP / 40GP / 40HQ / 45HQ / 散货(0) 之一）");
    }

    /// <summary>提交 / 审核前总件数必须为正数（草稿可暂存 0 / 负数，但不能提交 / 审核）。</summary>
    public static void EnsureQuantityPositive(decimal totalQuantity)
    {
        if (totalQuantity <= 0m)
            throw BusinessException.InvalidParameter(
                "收货计划提交 / 审核前总件数必须为正数（TotalQuantity > 0）");
    }

    /// <summary>去首尾空白并按实体长度上限有界归一化（超限 / 必填为空一律参数错误）。</summary>
    public static string NormalizeBounded(string? value, int maxLength, string fieldLabel)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length > maxLength)
            throw BusinessException.InvalidParameter($"{fieldLabel}长度超过上限（{maxLength}）");
        return normalized;
    }

    /// <summary>
    /// 校验并规范化「完整的拟提交内容」，返回**游离快照**（不触碰任何被跟踪实体）：
    /// 供应商实时可用 → 目的港实时可用 → 柜型为已知枚举 → 文本字段去空白并有界。
    /// 调用方必须先通过 <see cref="EnsureAuthorizedAsync"/>，并在本方法成功后才把快照复制到被跟踪实体
    /// （见 <see cref="ApplyValidated"/>），从而保证「被拒绝的修改不改变库中收货计划 / 状态」。
    /// </summary>
    public static async Task<ContainerReceivingPlan> ValidateProposedAsync(
        IErpDbContext db, ContainerReceivingPlan proposed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(proposed);

        await EnsureSupplierAvailableAsync(db, proposed.SupplierId, ct);
        await EnsurePortAvailableAsync(db, proposed.PortId, ct);
        EnsureContainerTypeValid(proposed.ContainerType);

        return new ContainerReceivingPlan
        {
            PlanDate = proposed.PlanDate,
            SupplierId = proposed.SupplierId,
            BookingNo = NormalizeBounded(proposed.BookingNo, BookingNoMaxLength, "订柜单号（legacy 文本）"),
            ContainerType = proposed.ContainerType,
            ContainerNo = NormalizeBounded(proposed.ContainerNo, ContainerNoMaxLength, "柜号"),
            ExpectedArrivalDate = proposed.ExpectedArrivalDate,
            PortId = proposed.PortId,
            Destination = NormalizeBounded(proposed.Destination, DestinationMaxLength, "目的地"),
            TotalQuantity = proposed.TotalQuantity,
            Remark = NormalizeBounded(proposed.Remark, RemarkMaxLength, "备注"),
        };
    }

    /// <summary>把已校验 / 已规范化的快照复制到目标实体的可编辑字段（不改状态、单号、审计与删除标记）。</summary>
    public static void ApplyValidated(ContainerReceivingPlan target, ContainerReceivingPlan validated)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(validated);

        target.PlanDate = validated.PlanDate;
        target.SupplierId = validated.SupplierId;
        target.BookingNo = validated.BookingNo;               // 显式 legacy 文本，不做任何订柜链接推断
        target.ContainerType = validated.ContainerType;
        target.ContainerNo = validated.ContainerNo;
        target.ExpectedArrivalDate = validated.ExpectedArrivalDate;
        target.PortId = validated.PortId;
        target.Destination = validated.Destination;
        target.TotalQuantity = validated.TotalQuantity;
        target.Remark = validated.Remark;
    }

    /// <summary>
    /// 状态变更（提交 / 审核 / 取消 / 删除）前的实时门槛：供应商 / 目的港必须真实可用；
    /// 提交 / 审核（<paramref name="requirePositiveQuantity"/> = <c>true</c>）额外要求总件数为正数。
    /// 全部判定发生在状态变更与写入之前。
    /// </summary>
    public static async Task EnsureTransitionAllowedAsync(
        IErpDbContext db, ContainerReceivingPlan plan, bool requirePositiveQuantity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(plan);

        if (requirePositiveQuantity) EnsureQuantityPositive(plan.TotalQuantity);
        await EnsureSupplierAvailableAsync(db, plan.SupplierId, ct);
        await EnsurePortAvailableAsync(db, plan.PortId, ct);
    }

    /// <summary>
    /// 历史读取的显式不可用证据（只读，不写库）：逐条列出供应商 / 目的港已停用或已删除的收货计划，
    /// 说明「历史照常可读、但不能用于新增 / 修改 / 状态变更」；全部可用时返回空串。
    /// </summary>
    public static async Task<string> DescribeUnavailableMasterDataAsync(
        IErpDbContext db, IEnumerable<ContainerReceivingPlan>? plans, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var list = plans?.Where(p => p is not null).ToList() ?? new List<ContainerReceivingPlan>();
        if (list.Count == 0) return string.Empty;

        var supplierIds = list.Where(p => p.SupplierId > 0).Select(p => p.SupplierId).Distinct().ToList();
        var portIds = list.Where(p => p.PortId is > 0).Select(p => p.PortId!.Value).Distinct().ToList();

        var suppliers = supplierIds.Count == 0
            ? new List<SupplierSnapshot>()
            : await db.BaseSuppliers.AsNoTracking()
                .Where(s => supplierIds.Contains(s.Id))
                .Select(s => new SupplierSnapshot { Id = s.Id, Name = s.SupplierName, Status = s.Status, IsDeleted = s.IsDeleted })
                .ToListAsync(ct);
        var supplierMap = suppliers.ToDictionary(s => s.Id);

        var ports = portIds.Count == 0
            ? new List<PortSnapshot>()
            : await db.BaseOtherInfos.AsNoTracking()
                .Where(p => portIds.Contains(p.Id))
                .Select(p => new PortSnapshot { Id = p.Id, Name = p.InfoName, InfoType = p.InfoType, Status = p.Status, IsDeleted = p.IsDeleted })
                .ToListAsync(ct);
        var portMap = ports.ToDictionary(p => p.Id);

        var evidence = new List<string>();
        foreach (var plan in list)
        {
            if (plan.SupplierId > 0)
            {
                if (!supplierMap.TryGetValue(plan.SupplierId, out var supplier))
                    evidence.Add($"收货计划 [{plan.PlanNo}] 的供应商（Id={plan.SupplierId}）已不存在或已删除");
                else if (supplier.IsDeleted || supplier.Status != 1)
                    evidence.Add($"收货计划 [{plan.PlanNo}] 的供应商「{supplier.Name}」{UnavailableReason(supplier.IsDeleted)}");
            }

            if (plan.PortId is > 0)
            {
                if (!portMap.TryGetValue(plan.PortId!.Value, out var port))
                    evidence.Add($"收货计划 [{plan.PlanNo}] 的目的港（Id={plan.PortId}）已不存在或已删除");
                else if (!string.Equals(port.InfoType, PortInfoType, StringComparison.OrdinalIgnoreCase))
                    evidence.Add($"收货计划 [{plan.PlanNo}] 的目的港（Id={plan.PortId}）已不是港口字典项（InfoType={port.InfoType}）");
                else if (port.IsDeleted || port.Status != 1)
                    evidence.Add($"收货计划 [{plan.PlanNo}] 的目的港「{port.Name}」{UnavailableReason(port.IsDeleted)}");
            }
        }

        return evidence.Count == 0 ? string.Empty : string.Join("；", evidence);
    }

    private static string UnavailableReason(bool isDeleted) => isDeleted ? "已删除" : "已停用";

    private sealed class SupplierSnapshot
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public int Status { get; init; }
        public bool IsDeleted { get; init; }
    }

    private sealed class PortSnapshot
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string InfoType { get; init; } = string.Empty;
        public int Status { get; init; }
        public bool IsDeleted { get; init; }
    }
}
