using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 形式发票 PI（<c>api/sales/proforma-invoices</c>）写入与运营承诺前的**实时主数据引用**护栏（ERP-473）。
/// <para>背景：ERP-398 只做「PI 菜单 + 权威客户范围」护栏，ERP-403 / SalesDocumentSourceLineageRules 只保护
/// 报价单来源血缘与锁协议，ERP-399 只保护生命周期锁与转换唯一化，ERP-471 只校验币种 / 汇率 / 数量 / 单价 /
/// 定金等商业条款 —— 但**普通新增 / 修改**仍把调用方提交的 <see cref="ProformaInvoice.CustomerId"/> /
/// <see cref="ProformaInvoice.SalesmanId"/> 与明细 <see cref="ProformaInvoiceDetail.ProductId"/> 直接落库，
/// 从未核对**实时主数据**是否存在、是否已删除、是否已停用；提交 / 审核（运营承诺）与 PI → 销售订单（下游承诺）
/// 也不复核这些引用。失效引用因此可能被承诺成可转销售订单的实时履约。</para>
/// <list type="number">
/// <item><b>可选客户</b>：留空（<c>null</c>）保持既有「未指定客户」语义（既有菜单 / 客户范围授权仍然决定性）；
/// 一旦填写必须为正整数且解析到**存在、未删除、启用（Status = 1）**的客户 —— 零 / 负 / 缺失 / 已删除 / 已停用
/// 一律 fail closed。</item>
/// <item><b>可选业务员</b>：留空（<c>null</c>）保持可选；填写时必须为正整数 —— 解析到既有员工行时必须
/// **未删除 / 在职（Status = 1）/ 业务员（IsSalesman = true）**，即「可选业务员语义」；无对应员工行的历史悬空 Id
/// 作为既有手工快照保留（不拒绝、不回填、不自动改派），与报价单（ERP-472）同口径，使历史报价单 → PI 的
/// 既有血缘不被本次护栏破坏。</item>
/// <item><b>明细商品</b>：<c>null</c> = **手工商品快照行**（与 PI 界面一致：明细以编码 / 名称 / 规格 / 单位等
/// 自带文本为准），保持既有受界文本语义；填写时必须为正整数，商品资料**一次有界批量查询**（<c>ProductId</c>
/// 去重集合，绝不逐行查库）—— 解析到既有商品行时必须**未删除 / 启用（Status = 1）**；无对应商品行时，
/// 自带受界商品快照文本的行按**手工快照行**保留（历史悬空链接绝不被拒绝 / 回填 / 改派），
/// 而无自带文本的纯目录引用按**缺失引用**受控拒绝。</item>
/// </list>
/// <para><b>接入点</b>：新增在单号预约与任何表头 / 明细赋值<b>之前</b>；修改在改写锁内权威实体之前；
/// 提交 / 审核在既有「PI 来源行锁 + 原子事务」内、状态改写之前；PI → 销售订单在「PI 来源行锁 + 原子事务」内、
/// 构造订单与预约订单号之前；报价单 → PI 在「报价单来源行锁 + 原子事务」内、预约 PI 号与写入之前。
/// 校验失败一律抛出受控 <see cref="BusinessException"/>，被拒绝的请求表头 / 明细 / 来源单据 / 状态 / 单号预约全部不变。</para>
/// <para><b>边界</b>：本类只做**纯判定与有界只读投影查询**，不落库、不改写客户 / 员工 / 商品主数据，
/// 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，也绝不要求或授予任何主数据维护权限；
/// 历史形式发票 PI 的读取 / 打印 / 报表完全只读、不被自动回填或修正（引用失效的历史单据照常可读可打印），
/// 也绝不做任何历史数据回填或静默改派。</para>
/// </summary>
public static class ProformaInvoiceMasterReferenceRules
{
    /// <summary>既有启用状态取值（客户 / 员工 / 商品：1=启用，0=停用 / 离职）。</summary>
    public const int EnabledStatus = 1;

    /// <summary>客户填写时必须为正整数的拒绝文案。</summary>
    public const string CustomerRequiredText = "PI 客户（CustomerId）填写时必须为正整数（留空 = 未指定客户）";

    /// <summary>业务员填写时必须为正整数的拒绝文案。</summary>
    public const string SalesmanRequiredText = "PI 业务员（SalesmanId）填写时必须为正整数（留空 = 未指定）";

    /// <summary>明细商品填写时必须为正整数的拒绝文案。</summary>
    public const string ProductRequiredText =
        "PI 明细的商品 Id（ProductId）填写时必须为正整数（留空 = 手工商品快照行）";

    /// <summary>「引用主数据不可用」受控错误后缀（历史读取 / 打印保持只读，接口 / 文档同源）。</summary>
    public const string UnavailableSuffixText = "（历史形式发票 PI 仍可读取，读取 / 打印绝不自动回填或修正）";

    /// <summary>口径说明（接口 / 文档同源）。</summary>
    public const string RuleText =
        "ERP-473：形式发票 PI 的新增在单号预约与任何表头 / 明细赋值之前，修改在改写锁内权威实体之前，" +
        "提交 / 审核在既有 PI 来源行锁内改写状态之前，转销售订单在 PI 来源行锁内构造订单与预约订单号之前，" +
        "报价单转 PI 在报价单来源行锁内预约 PI 号与写入之前，一律复核**实时主数据引用** —— " +
        "可选客户（留空跳过；填写必须解析到存在 / 未删除 / 启用客户）、可选业务员（留空跳过；填写必须解析到存在 / " +
        "未删除 / 在职且为业务员的员工；无对应员工行的历史悬空 Id 保留为既有手工快照）、明细商品（留空 = 手工快照行；" +
        "填写必须为正整数，商品资料一次有界批量查询；解析到既有商品行时不得已删除 / 已停用，无对应商品行时自带受界商品文本的" +
        "手工快照行保留、纯目录引用按缺失引用拒绝）；任一项不满足即返回受控业务错误，绝不写库、绝不预约单号、" +
        "绝不产生下游副作用，历史读取 / 打印完全只读且不被自动修正。";

    /// <summary>边界文案。</summary>
    public const string BoundaryText =
        "本护栏只保护形式发票 PI 写入与运营承诺入口：只做纯判定与有界只读投影查询，不落库、不改写客户 / 员工 / 商品主数据，" +
        "不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，也绝不要求或授予主数据维护权限；" +
        "既有 PI 菜单 / 客户数据范围授权仍然决定性（授权先于主数据复核）；" +
        "来源报价单血缘仍由既有 SalesDocumentSourceLineageRules 治理，绝不与本次客户 / 商品归属混淆；" +
        "历史形式发票 PI 的读取 / 打印保持完全只读、不被自动回填或修正，也绝不静默改派到其它客户 / 商品 / 业务员。";

    /// <summary>
    /// 写入 / 运营承诺前完整复核实时主数据引用：可选客户 → 可选业务员 → 每条有效明细的商品。
    /// 所有判定发生在表头 / 明细赋值与单号预约之前，任一项失败即整体拒绝（不落库、不改写任何字段）。
    /// </summary>
    /// <param name="db">数据访问上下文</param>
    /// <param name="pi">待校验的 PI（新增 / 修改请求体、锁内权威重读行，或报价单映射草稿）</param>
    /// <param name="ct">取消令牌</param>
    public static async Task EnsureMasterReferencesAsync(
        IErpDbContext db, ProformaInvoice pi, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(pi);
        ct.ThrowIfCancellationRequested();

        await EnsureCustomerAvailableAsync(db, pi.CustomerId, ct);
        await EnsureSalesmanAvailableAsync(db, pi.SalesmanId, ct);
        await EnsureDetailProductsAsync(db, pi.Details, ct);
    }

    /// <summary>
    /// 可选客户引用复核：<c>null</c> = 未指定客户（既有授权 / 客户范围规则仍然决定性，直接跳过）；
    /// 非正整数 → <see cref="ErrorCodes.InvalidParameter"/>；不存在 / 已删除 → <see cref="ErrorCodes.NotFound"/>；
    /// 已停用 → <see cref="ErrorCodes.RuleConflict"/>。命中时返回实时客户行（只读）。
    /// </summary>
    public static async Task<BaseCustomer?> EnsureCustomerAvailableAsync(
        IErpDbContext db, long? customerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (customerId is null) return null;
        if (customerId.Value <= 0)
            throw BusinessException.InvalidParameter(CustomerRequiredText);

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId.Value, ct);
        if (customer is null || customer.IsDeleted)
            throw BusinessException.NotFound(
                $"客户（Id={customerId.Value}）不存在或已删除，不能用于形式发票 PI 的新增 / 修改 / 提交 / 审核 / 转销售订单{UnavailableSuffixText}");
        if (customer.Status != EnabledStatus)
            throw BusinessException.RuleConflict(
                $"客户「{customer.CustomerName}」已停用，不能用于形式发票 PI 的新增 / 修改 / 提交 / 审核 / 转销售订单{UnavailableSuffixText}");

        return customer;
    }

    /// <summary>
    /// 可选业务员引用复核：<c>null</c> = 未指定（跳过）；非正整数 → <see cref="ErrorCodes.InvalidParameter"/>；
    /// 解析到既有员工行时必须未删除 / 在职（<c>Status = 1</c>）/ 业务员（<see cref="BaseEmployee.IsSalesman"/>），
    /// 否则分别按不存在 / 规则冲突拒绝；无对应员工行的历史悬空 Id 保留为既有手工快照（返回 <c>null</c>，
    /// 不拒绝、不回填、不自动改派）。
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
        // 无对应员工行 = 既有手工 / 历史悬空链接（既有行从未校验实时主数据）：保留快照，绝不拒绝 / 回填 / 改派。
        if (employee is null) return null;

        if (employee.IsDeleted)
            throw BusinessException.NotFound(
                $"业务员（员工 Id={salesmanId.Value}）不存在或已删除，不能用于形式发票 PI 的新增 / 修改 / 提交 / 审核 / 转销售订单{UnavailableSuffixText}");
        if (employee.Status != EnabledStatus)
            throw BusinessException.RuleConflict(
                $"业务员「{employee.EmployeeName}」已离职 / 停用（Status != 1），不能用于形式发票 PI 的新增 / 修改 / 提交 / 审核 / 转销售订单{UnavailableSuffixText}");
        if (!employee.IsSalesman)
            throw BusinessException.RuleConflict(
                $"员工「{employee.EmployeeName}」未被标记为业务员（IsSalesman = false），不能作为 PI 业务员{UnavailableSuffixText}");

        return employee;
    }

    /// <summary>
    /// 每条**有效（未软删除）**明细的商品引用复核：<c>null</c> = 手工商品快照行（既有受界文本语义，跳过）；
    /// 非正整数 → <see cref="ErrorCodes.InvalidParameter"/>；商品资料**一次有界批量查询**
    /// （<c>ProductId</c> 去重集合，绝不逐行查库）—— 命中时不得已删除（<see cref="ErrorCodes.NotFound"/>）
    /// 或已停用（<see cref="ErrorCodes.RuleConflict"/>）；未命中时，自带受界商品快照文本
    /// （<see cref="HasManualProductSnapshot"/>）的行按手工快照行保留，纯目录引用按缺失引用
    /// （<see cref="ErrorCodes.NotFound"/>）受控拒绝。
    /// </summary>
    public static async Task EnsureDetailProductsAsync(
        IErpDbContext db, IEnumerable<ProformaInvoiceDetail>? details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var lines = (details ?? Enumerable.Empty<ProformaInvoiceDetail>())
            .Where(d => d is not null && !d.IsDeleted)
            .ToList();
        if (lines.Count == 0) return;

        foreach (var line in lines)
        {
            if (line.ProductId is { } id && id <= 0)
                throw BusinessException.InvalidParameter(ProductRequiredText);
        }

        var productIds = lines.Where(d => d.ProductId is > 0)
            .Select(d => d.ProductId!.Value).Distinct().ToList();
        if (productIds.Count == 0) return;

        // 有界批量查询：一次取回全部被显式引用的商品（绝不逐行查库）。
        var products = await db.BaseProducts.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        foreach (var line in lines)
        {
            if (line.ProductId is not { } id || id <= 0) continue;

            if (products.TryGetValue(id, out var product))
            {
                if (product.IsDeleted)
                    throw BusinessException.NotFound(
                        $"商品（Id={id}）不存在或已删除，不能用于形式发票 PI 的新增 / 修改 / 提交 / 审核 / 转销售订单{UnavailableSuffixText}");
                if (product.Status != EnabledStatus)
                    throw BusinessException.RuleConflict(
                        $"商品「{product.ProductName}」已停用，不能用于形式发票 PI 的新增 / 修改 / 提交 / 审核 / 转销售订单{UnavailableSuffixText}");
                continue;
            }

            // 主数据行不存在：自带受界商品文本的行是**手工快照行**（PI 明细以自带文本为准），
            // 保持既有语义（不回填、不改派）；没有任何自带文本可依赖时按缺失引用受控拒绝。
            if (HasManualProductSnapshot(line)) continue;
            throw BusinessException.NotFound(
                $"商品（Id={id}）不存在或已删除，不能用于形式发票 PI 的新增 / 修改 / 提交 / 审核 / 转销售订单{UnavailableSuffixText}");
        }
    }

    /// <summary>
    /// 手工商品快照行判定：明细自带受界商品文本（编码或名称，去首尾空白后非空）时为
    /// **自足的手工快照行** —— 与 PI 界面一致（明细以编码 / 名称 / 规格 / 单位等文本为准），
    /// 既不要求主数据链接，也不臆造单位 / 名称；商品主数据行不存在时按手工快照行保留。
    /// </summary>
    public static bool HasManualProductSnapshot(ProformaInvoiceDetail line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return !string.IsNullOrWhiteSpace(line.ProductCode) || !string.IsNullOrWhiteSpace(line.ProductName);
    }
}
