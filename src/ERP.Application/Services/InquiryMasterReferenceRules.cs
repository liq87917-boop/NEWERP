using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 询价单 Inquiry（<c>api/inquiries</c>）写入与运营承诺前的**实时主数据引用**护栏（ERP-475）。
/// <para>背景：ERP-402 只做「询价单菜单 + 权威客户范围」护栏，ERP-403 / SalesDocumentSourceLineageRules 只保护
/// 「询价单 → 报价单」来源血缘与锁协议，ERP-474 只校验币种 / 汇率 / 有效期 / 数量 / 单价等商业条款 —— 但普通
/// 新增 / 修改仍把调用方提交的**必填** <see cref="Inquiry.CustomerId"/>、可空 <see cref="Inquiry.SalesmanId"/>
/// 与明细 <see cref="InquiryDetail.ProductId"/> 直接落库，从未核对**实时主数据**是否存在、是否已删除、是否已停用；
/// 提交 / 审核 / 转报价单（运营承诺与转换）也不复核这些引用。失效引用因此可能被承诺成可转报价单的实时询价来源。</para>
/// <list type="number">
/// <item><b>必填客户</b>：询价单是必填客户单据（<see cref="Inquiry.CustomerId"/> 为既有非空契约），必须为正整数且
/// 解析到**存在、未删除、启用（Status = 1）**的客户 —— 零 / 负 / 缺失 / 已删除 / 已停用一律 fail closed；
/// 既有实时客户范围授权（<see cref="InquiryAuthorizationRules.EnsureProposedCustomerInScope"/> /
/// <see cref="InquiryAuthorizationRules.EnsureStoredCustomerInScope"/>）仍然决定性且先于本护栏执行。</item>
/// <item><b>可选业务员</b>：留空（<c>null</c>）保持可选；填写时必须为正整数 —— 解析到既有员工行时必须
/// **未删除 / 在职（Status = 1）/ 业务员（<see cref="BaseEmployee.IsSalesman"/>）**，即「可选业务员资格」语义；
/// 零 / 负 / 无对应员工行 / 已删除 / 已离职 / 非业务员一律受控拒绝（不臆造业务员，也不自动改派）。</item>
/// <item><b>明细商品</b>：每条**有效（未软删除）**明细的 <see cref="InquiryDetail.ProductId"/> 必须为正整数
/// （零 / 负数 = 非法引用，一律拒绝）；商品资料**一次有界批量查询**（<c>ProductId</c> 去重集合，
/// 绝不逐行查库）—— 解析到既有商品行时必须**未删除 / 启用（Status = 1）**；无对应商品行时，自带受界商品文本
/// （<see cref="HasManualProductSnapshot"/>）的**手工商品快照行**保留（历史悬空链接绝不被拒绝 / 回填 / 改派），
/// 而无自带文本的纯目录引用按**缺失引用**受控拒绝。</item>
/// </list>
/// <para><b>接入点</b>：新增在单号预约与任何表头 / 明细赋值<b>之前</b>；修改在既有「询价单来源行锁 + 原子事务」内、
/// 改写任何字段之前；提交 / 审核在既有行锁内、状态改写之前；带入预填 / 转报价单在既有行锁内、映射草稿与预约报价单号
/// 之前。校验失败一律抛出受控 <see cref="BusinessException"/>，被拒绝的请求表头 / 明细 / 来源单据 / 状态 /
/// 单号预约全部不变。</para>
/// <para><b>边界</b>：本类只做**纯判定与有界只读投影查询**，不落库、不改写客户 / 员工 / 商品主数据，
/// 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，也绝不要求或授予任何主数据维护权限；
/// 历史询价单的读取 / 打印 / 报表完全只读、不被自动回填或修正（引用失效的历史单据照常可读可打印），
/// 也绝不做任何历史数据回填或静默改派。</para>
/// </summary>
public static class InquiryMasterReferenceRules
{
    /// <summary>既有启用状态取值（客户 / 员工 / 商品：1=启用，0=停用 / 离职）。</summary>
    public const int EnabledStatus = 1;

    /// <summary>必填客户（CustomerId 非正整数）的拒绝文案。</summary>
    public const string CustomerRequiredText =
        "询价单必须指定客户（CustomerId 必须为正整数：询价单为必填客户单据）";

    /// <summary>业务员填写时必须为正整数的拒绝文案。</summary>
    public const string SalesmanRequiredText = "询价单业务员（SalesmanId）填写时必须为正整数（留空 = 未指定）";

    /// <summary>明细商品填写时必须为正整数的拒绝文案（零 / 负数 = 非法引用）。</summary>
    public const string ProductRequiredText =
        "询价单明细的商品 Id（ProductId）必须为正整数：零 / 负数一律拒绝，无对应商品行的历史手工快照行须自带受界商品文本";

    /// <summary>「引用主数据不可用」受控错误后缀（历史读取 / 打印保持只读，接口 / 文档同源）。</summary>
    public const string UnavailableSuffixText = "（历史询价单仍可读取，读取 / 打印绝不自动回填或修正）";

    /// <summary>口径说明（接口 / 文档同源）。</summary>
    public const string RuleText =
        "ERP-475：询价单的新增在单号预约与任何表头 / 明细赋值之前，修改在询价单来源行锁内改写字段之前，" +
        "提交 / 审核在既有行锁内改写状态之前，带入预填 / 转报价单在既有行锁内映射草稿与预约报价单号之前，" +
        "一律复核**实时主数据引用** —— 必填客户（CustomerId 为正整数，必须解析到存在 / 未删除 / 启用客户，" +
        "既有实时客户范围授权仍然决定性且先执行）、可选业务员（留空跳过；填写必须为正整数并解析到存在 / 未删除 / " +
        "在职且为业务员的员工）、明细商品（每条有效明细必须为正整数，商品资料一次有界批量查询；解析到既有商品行时" +
        "不得已删除 / 已停用，无对应商品行时自带受界商品文本的手工快照行保留、纯目录引用按缺失引用拒绝）；" +
        "任一项不满足即返回受控业务错误，绝不写库、绝不预约单号、绝不改写来源状态或产生下游副作用，" +
        "历史读取 / 打印完全只读且不被自动修正。";

    /// <summary>边界文案。</summary>
    public const string BoundaryText =
        "本护栏只保护询价单写入与运营承诺入口：只做纯判定与有界只读投影查询，不落库、不改写客户 / 员工 / 商品主数据，" +
        "不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，也绝不要求或授予主数据维护权限；" +
        "既有询价单实时身份 / 菜单 / 客户数据范围授权仍然决定性（授权先于主数据复核）；" +
        "来源血缘仍由既有 SalesDocumentSourceLineageRules 治理，绝不与本次客户 / 商品归属混淆；" +
        "历史询价单的读取 / 打印保持完全只读、不被自动回填或修正。";

    /// <summary>
    /// 写入 / 承诺前完整复核实时主数据引用：必填客户 → 可选业务员 → 每条有效明细的商品。
    /// 所有判定发生在表头 / 明细赋值、状态改写与单号预约之前，任一项失败即整体拒绝（不落库、不改写任何字段）。
    /// </summary>
    /// <param name="db">数据访问上下文</param>
    /// <param name="inquiry">待校验的询价单（新增 / 修改请求体、锁内权威重读行，或转换来源行）</param>
    /// <param name="ct">取消令牌</param>
    public static async Task EnsureMasterReferencesAsync(
        IErpDbContext db, Inquiry inquiry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(inquiry);
        ct.ThrowIfCancellationRequested();

        await EnsureCustomerAvailableAsync(db, inquiry.CustomerId, ct);
        await EnsureSalesmanAvailableAsync(db, inquiry.SalesmanId, ct);
        await EnsureDetailProductsAsync(db, inquiry.Details, ct);
    }

    /// <summary>
    /// 必填客户引用复核：非正整数 → <see cref="ErrorCodes.InvalidParameter"/>；不存在 / 已删除 →
    /// <see cref="ErrorCodes.NotFound"/>；已停用 → <see cref="ErrorCodes.RuleConflict"/>。
    /// 命中时返回实时客户行（只读投影）供既有映射复用。
    /// <para>本方法只核对**主数据行本身**；「是否在调用方当前客户数据范围内」仍由既有
    /// <see cref="InquiryAuthorizationRules"/> 范围授权裁决（既有口径，绝不重复实现或放宽）。</para>
    /// </summary>
    public static async Task<BaseCustomer> EnsureCustomerAvailableAsync(
        IErpDbContext db, long customerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (customerId <= 0)
            throw BusinessException.InvalidParameter(CustomerRequiredText);

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId, ct);
        if (customer is null || customer.IsDeleted)
            throw BusinessException.NotFound(
                $"客户（Id={customerId}）不存在或已删除，不能用于询价单的新增 / 修改 / 提交 / 审核 / 转报价单{UnavailableSuffixText}");
        if (customer.Status != EnabledStatus)
            throw BusinessException.RuleConflict(
                $"客户「{customer.CustomerName}」已停用，不能用于询价单的新增 / 修改 / 提交 / 审核 / 转报价单{UnavailableSuffixText}");

        return customer;
    }

    /// <summary>
    /// 可选业务员引用复核：<c>null</c> = 未指定（跳过）；非正整数 → <see cref="ErrorCodes.InvalidParameter"/>；
    /// 无对应员工行 / 已删除 → <see cref="ErrorCodes.NotFound"/>；已离职（<c>Status != 1</c>）或未被标记为业务员
    /// （<see cref="BaseEmployee.IsSalesman"/> = false）→ <see cref="ErrorCodes.RuleConflict"/>。
    /// 命中时返回实时员工行（只读投影）。
    /// </summary>
    public static async Task<BaseEmployee?> EnsureSalesmanAvailableAsync(
        IErpDbContext db, long? salesmanId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (salesmanId is null) return null;
        if (salesmanId.Value <= 0)
            throw BusinessException.InvalidParameter(SalesmanRequiredText);

        var employee = await db.BaseEmployees.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == salesmanId.Value, ct);
        if (employee is null || employee.IsDeleted)
            throw BusinessException.NotFound(
                $"业务员（员工 Id={salesmanId.Value}）不存在或已删除，不能用于询价单的新增 / 修改 / 提交 / 审核 / 转报价单{UnavailableSuffixText}");
        if (employee.Status != EnabledStatus)
            throw BusinessException.RuleConflict(
                $"业务员「{employee.EmployeeName}」已离职 / 停用（Status != 1），不能用于询价单的新增 / 修改 / 提交 / 审核 / 转报价单{UnavailableSuffixText}");
        if (!employee.IsSalesman)
            throw BusinessException.RuleConflict(
                $"员工「{employee.EmployeeName}」未被标记为业务员（IsSalesman = false），不能作为询价单业务员{UnavailableSuffixText}");

        return employee;
    }

    /// <summary>
    /// 每条**有效（未软删除）**明细的商品引用复核：非正整数 → <see cref="ErrorCodes.InvalidParameter"/>；
    /// 商品资料**一次有界批量查询**（<c>ProductId</c> 去重集合，绝不逐行查库）—— 命中时不得已删除
    /// （<see cref="ErrorCodes.NotFound"/>）或已停用（<see cref="ErrorCodes.RuleConflict"/>）；未命中时，
    /// 自带受界商品快照文本（<see cref="HasManualProductSnapshot"/>）的行按手工快照行保留，
    /// 纯目录引用按缺失引用（<see cref="ErrorCodes.NotFound"/>）受控拒绝。
    /// </summary>
    public static async Task EnsureDetailProductsAsync(
        IErpDbContext db, IEnumerable<InquiryDetail>? details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var lines = (details ?? Enumerable.Empty<InquiryDetail>())
            .Where(d => d is not null && !d.IsDeleted)
            .ToList();
        if (lines.Count == 0) return;

        foreach (var line in lines)
        {
            if (line.ProductId <= 0)
                throw BusinessException.InvalidParameter(ProductRequiredText);
        }

        var productIds = lines.Select(d => d.ProductId).Distinct().ToList();

        // 有界批量查询：一次取回全部被显式引用的商品（绝不逐行查库）。
        var products = await db.BaseProducts.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        foreach (var line in lines)
        {
            if (!products.TryGetValue(line.ProductId, out var product))
            {
                // 主数据行不存在：自带受界商品文本的行是**手工快照行**（询价明细以自带商品文本为准），
                // 保持既有语义（不回填、不改派）；没有任何自带文本可依赖时按缺失引用受控拒绝。
                if (HasManualProductSnapshot(line)) continue;
                throw BusinessException.NotFound(
                    $"商品（Id={line.ProductId}）不存在或已删除，不能用于询价单的新增 / 修改 / 提交 / 审核 / 转报价单{UnavailableSuffixText}");
            }

            if (product.IsDeleted)
                throw BusinessException.NotFound(
                    $"商品（Id={line.ProductId}）不存在或已删除，不能用于询价单的新增 / 修改 / 提交 / 审核 / 转报价单{UnavailableSuffixText}");
            if (product.Status != EnabledStatus)
                throw BusinessException.RuleConflict(
                    $"商品「{product.ProductName}」已停用，不能用于询价单的新增 / 修改 / 提交 / 审核 / 转报价单{UnavailableSuffixText}");
        }
    }

    /// <summary>
    /// 手工商品快照行判定：明细自带受界商品文本（名称 / 规格 / 单位任一去首尾空白后非空）时为
    /// **自足的手工快照行** —— 与询价单界面一致（明细以商品名称 / 规格 / 单位等自带文本为准），
    /// 既不要求主数据链接，也不臆造单位 / 名称；商品主数据行不存在时按手工快照行保留。
    /// </summary>
    public static bool HasManualProductSnapshot(InquiryDetail line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return !string.IsNullOrWhiteSpace(line.ProductName)
               || !string.IsNullOrWhiteSpace(line.Spec)
               || !string.IsNullOrWhiteSpace(line.Unit);
    }
}
