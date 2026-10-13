using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 供应商比价（<see cref="PurchaseQuote"/>，<c>api/purchase/quotes</c>）写入与**采购承诺**前的
/// <b>实时主数据引用</b>护栏（ERP-476）。
/// <para>背景：ERP-416 只做「供应商比价菜单 + 权威客户范围」护栏，ERP-417 只保护比价生命周期 /
/// 审批决定 / 删除；<see cref="PurchaseQuoteMutationRules.ValidateDraft"/> 只校验批次号 / 状态 / 数量 / 单价 /
/// 币种 / 总额，**从不加载**调用方提交的 <see cref="PurchaseQuote.ProductId"/>、
/// <see cref="PurchaseQuote.SupplierId"/> 与 <see cref="PurchaseQuote.CustomerId"/> 是否仍指向实时主数据；
/// ERP-418 只在转换时对「档案确实存在但已停用 / 已删除」的弱引用拒绝，ERP-427 只保护规范采购订单录入 ——
/// 失效引用（零 / 负数、已删除 / 已停用）因此可以被写进比价行并承诺成采购订单。</para>
/// <list type="number">
/// <item><b>草稿写入（新增 / 修改，含「选中」选择）</b>：可空引用保持既有语义 ——
/// <see cref="PurchaseQuote.SupplierId"/> 留空 = 未维护供应商的比价草稿、<see cref="PurchaseQuote.ProductId"/>
/// 留空 = 手工 / 未引用商品档案的比价草稿、<see cref="PurchaseQuote.CustomerId"/> 留空 = 未指定客户（代理采购）；
/// 一旦<b>显式填写</b>则必须为正整数（零 / 负数一律 <see cref="ErrorCodes.InvalidParameter"/>），且解析到既有档案时
/// 必须未删除（<see cref="ErrorCodes.NotFound"/>）/ 启用（<see cref="ErrorCodes.RuleConflict"/>）。</item>
/// <item><b>归属客户是硬引用</b>：填写时必须解析到实时客户档案（悬空 / 已删除 / 已停用一律拒绝）——
/// 客户是 ERP-097 权威数据范围与归属销售订单血缘的唯一锚点，绝不接受悬空客户；既有实时客户范围授权
/// （<see cref="PurchaseQuoteAuthorizationRules.EnsureProposedCustomerAllowed(SalespersonDataScope, long?)"/> 与
/// <see cref="PurchaseQuoteAuthorizationRules.EnsureQuoteAllowedAsync"/>）仍然最先且决定性。</item>
/// <item><b>承诺（选中 / 转采购订单）必须满足既有采购口径</b>：<see cref="PurchaseQuote.SupplierId"/> 与
/// <see cref="PurchaseQuote.ProductId"/> 必须<b>显式存在且为正整数</b> —— 绝不把缺失 / <c>0</c> 静默当作实时采购引用。</item>
/// <item><b>有界批量查询</b>：承诺复核按去重的供应商 / 商品 / 客户 Id 集合<b>各一次</b>取回实时档案
/// （最多 3 次查询，绝不逐行查库），任一行不合格即整批受控拒绝。</item>
/// <item><b>绝不改派 / 不回填</b>：解析不到档案的历史悬空弱引用保持既有口径（未引用档案的比价行仍可读、
/// 既有可转换口径不变），本类只做纯判定与有界只读投影查询，绝不写入 / 修正任何主数据或比价行字段。</item>
/// </list>
/// <para><b>接入点</b>：新增在单号预约与任何表头 / 明细赋值之前；修改在既有「比价行锁 + 原子事务」内、改写任何字段
/// 之前；带入预填 / 单行转采购订单 / 批次转采购订单在既有来源行锁内、映射草稿与采购单号预约之前。
/// 真实 HTTP 写入 / 选择 / 转换请求一律执行；未被请求绑定的进程内直调（历史单元测试口径）保持既有行为，
/// 与 ERP-427 / ERP-473 / ERP-475 同源取舍。</para>
/// <para><b>边界</b>：不落库、不改写供应商 / 商品 / 客户主数据，不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，
/// 也绝不要求或授予任何主数据维护权限；历史比价行的读取 / 打印 / 派生视图完全只读、不被自动回填或修正。</para>
/// </summary>
public static class PurchaseQuoteMasterReferenceRules
{
    /// <summary>既有启用状态取值（客户 / 供应商 / 商品：1=启用，0=停用）。</summary>
    public const int EnabledStatus = 1;

    /// <summary>「引用主数据不可用」受控错误后缀（历史读取 / 派生视图保持只读，接口 / 文档同源）。</summary>
    public const string UnavailableSuffixText = "（历史比价行仍可读取，读取 / 派生视图绝不自动回填或修正）";

    /// <summary>归属客户填写时必须为正整数的拒绝文案（留空 = 未指定客户）。</summary>
    public const string CustomerRequiredText =
        "比价行归属客户（CustomerId）填写时必须为正整数（留空 = 未指定客户的比价草稿）";

    /// <summary>供应商填写时必须为正整数的拒绝文案（留空 = 未维护供应商的比价草稿）。</summary>
    public const string SupplierRequiredText =
        "比价行供应商（SupplierId）填写时必须为正整数（留空 = 未维护供应商的比价草稿；零 / 负数一律拒绝）";

    /// <summary>商品填写时必须为正整数的拒绝文案（留空 = 手工 / 未引用档案的比价草稿）。</summary>
    public const string ProductRequiredText =
        "比价行商品（ProductId）填写时必须为正整数（留空 = 手工 / 未引用商品档案的比价草稿；零 / 负数一律拒绝）";

    /// <summary>承诺（选中 / 转采购订单）时供应商引用缺失的拒绝文案。</summary>
    public const string CommitmentSupplierText =
        "该比价行未维护供应商引用（SupplierId 必须为正整数）：采购承诺必须满足既有采购口径，绝不生成无供应商的采购订单";

    /// <summary>承诺（选中 / 转采购订单）时商品引用缺失的拒绝文案（绝不静默 0 占位）。</summary>
    public const string CommitmentProductText =
        "该比价行未维护商品引用（ProductId 必须为正整数）：采购承诺必须满足既有采购口径，绝不把 ProductId = 0 静默当作实时采购引用";

    /// <summary>口径说明（接口 / 文档同源）。</summary>
    public const string RuleText =
        "ERP-476：比价行的新增 / 修改在单号预约与任何字段赋值之前（修改另在比价行锁 + 原子事务内、改写字段之前），" +
        "带入预填 / 单行转采购订单 / 批次转采购订单在既有来源行锁内、映射草稿与采购单号预约之前，一律复核**实时主数据引用** ——" +
        "归属客户（填写时必须为正整数并解析到存在 / 未删除 / 启用客户，悬空一律拒绝；既有实时客户范围授权仍然决定性）、" +
        "可选供应商 / 商品（留空保持既有可空草稿语义；填写必须为正整数，解析到既有档案时不得已删除 / 已停用），" +
        "并在**采购承诺**（选中 / 转采购订单）时要求供应商与商品引用显式存在且为正整数（绝不把缺失 / 0 静默当作实时采购引用）；" +
        "任一项不满足即返回受控业务错误，绝不写库、绝不预约单号、绝不改写来源状态或产生下游副作用，历史读取完全只读且不被自动修正。";

    /// <summary>边界文案（不新增权限 / 表列，不改写主数据）。</summary>
    public const string BoundaryText =
        "本护栏只保护比价行写入与采购承诺入口：只做纯判定与有界只读投影查询（供应商 / 商品 / 客户各一次去重批量查询），" +
        "不落库、不改写供应商 / 商品 / 客户主数据，不新增菜单 / 角色 / 用户授权或表结构，也不把空身份当作管理员；" +
        "解析不到档案的历史悬空弱引用保持既有可读 / 可转换口径，绝不臆造档案、绝不回填、绝不静默改派；" +
        "历史比价行的读取 / 打印 / 派生视图保持完全只读。";

    // ==================== 1. 草稿 / 选择写入复核（逐项，单一引用的可空语义不变） ====================

    /// <summary>
    /// 草稿 / 选择写入前的实时引用复核：归属客户（硬引用，悬空拒绝）与可空供应商 / 商品
    /// （填写必须为正整数，解析到既有档案时不得已删除 / 已停用；解析不到档案的历史悬空弱引用保持既有口径）。
    /// </summary>
    public static async Task EnsureComparisonReferencesAsync(IErpDbContext db, PurchaseQuote quote,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(quote);

        await EnsureCustomerAvailableAsync(db, quote.CustomerId, ct);
        await EnsureSupplierAvailableAsync(db, quote.SupplierId, ct);
        await EnsureProductAvailableAsync(db, quote.ProductId, ct);
    }

    /// <summary>
    /// 归属客户解析（硬引用）：留空（<c>null</c>）= 未指定客户，保持既有可空语义并跳过；
    /// 填写时必须为正整数且解析到**存在、未删除、启用（<c>Status = 1</c>）**的客户，否则受控拒绝。
    /// </summary>
    public static async Task<BaseCustomer?> EnsureCustomerAvailableAsync(IErpDbContext db, long? customerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (customerId is null) return null;
        if (customerId.Value <= 0) throw BusinessException.InvalidParameter(CustomerRequiredText);

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId.Value, ct);
        if (customer is null || customer.IsDeleted)
            throw BusinessException.NotFound(
                $"比价行归属客户（Id={customerId.Value}）不存在或已删除，不能用于比价行的新增 / 修改 / 选择" +
                UnavailableSuffixText);
        if (customer.Status != EnabledStatus)
            throw BusinessException.RuleConflict(
                $"比价行归属客户「{customer.CustomerName}」已停用（Status != 1），不能用于比价行的新增 / 修改 / 选择" +
                UnavailableSuffixText);

        return customer;
    }

    /// <summary>
    /// 供应商引用解析（可选 / 弱引用）：留空（<c>null</c>）= 未维护供应商的比价草稿（保持既有语义）；
    /// 填写时必须为正整数；解析到既有档案时必须**未删除 / 启用**（否则受控拒绝）；
    /// <b>解析不到档案时返回 <c>null</c></b> —— 这是既有「未引用档案」弱引用口径（档口 / 现货比价），
    /// 保持可写 / 可转换，绝不臆造档案、绝不静默改派。
    /// </summary>
    public static async Task<BaseSupplier?> EnsureSupplierAvailableAsync(IErpDbContext db, long? supplierId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (supplierId is null) return null;
        if (supplierId.Value <= 0) throw BusinessException.InvalidParameter(SupplierRequiredText);

        var supplier = await db.BaseSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == supplierId.Value, ct);
        if (supplier is null) return null;
        if (supplier.IsDeleted)
            throw BusinessException.NotFound(
                $"比价行供应商（Id={supplierId.Value}）不存在或已删除，不能用于比价行的新增 / 修改 / 选择" +
                UnavailableSuffixText);
        if (supplier.Status != EnabledStatus)
            throw BusinessException.RuleConflict(
                $"比价行供应商「{supplier.SupplierName}」已停用（Status != 1），不能用于比价行的新增 / 修改 / 选择" +
                UnavailableSuffixText);

        return supplier;
    }

    /// <summary>
    /// 商品引用解析（可选 / 弱引用）：留空（<c>null</c>）= 手工 / 未引用商品档案的比价草稿（保持既有语义）；
    /// 填写时必须为正整数；解析到既有档案时必须**未删除 / 启用**（否则受控拒绝）；
    /// <b>解析不到档案时返回 <c>null</c></b> —— 既有「未引用档案」弱引用口径保持不变，绝不臆造档案、绝不静默改派。
    /// </summary>
    public static async Task<BaseProduct?> EnsureProductAvailableAsync(IErpDbContext db, long? productId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (productId is null) return null;
        if (productId.Value <= 0) throw BusinessException.InvalidParameter(ProductRequiredText);

        var product = await db.BaseProducts.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId.Value, ct);
        if (product is null) return null;
        if (product.IsDeleted)
            throw BusinessException.NotFound(
                $"比价行商品（Id={productId.Value}）不存在或已删除，不能用于比价行的新增 / 修改 / 选择" +
                UnavailableSuffixText);
        if (product.Status != EnabledStatus)
            throw BusinessException.RuleConflict(
                $"比价行商品「{product.ProductName}」已停用（Status != 1），不能用于比价行的新增 / 修改 / 选择" +
                UnavailableSuffixText);

        return product;
    }

    // ==================== 2. 采购承诺复核（有界批量，单行 / 批次共用） ====================

    /// <summary>
    /// 采购承诺（选中 / 带入预填 / 单行转采购订单 / 批次转采购订单）前的实时引用复核：
    /// <list type="bullet">
    /// <item>供应商与商品引用必须<b>显式存在且为正整数</b>（缺失 / 零 / 负数一律受控拒绝 —— 绝不静默生成
    /// <c>ProductId = 0</c> 的采购明细，也绝不生成无供应商的采购订单）；</item>
    /// <item>归属客户填写时必须为正整数且解析到**未删除 / 启用**的实时客户（悬空同样拒绝）；</item>
    /// <item>供应商 / 商品解析到既有档案时必须**未删除 / 启用**；<b>解析不到档案</b>的历史悬空弱引用保持
    /// 既有可转换口径（与 <see cref="PurchaseQuoteConversionMutationRules.SupplierMasterText"/> 同源判定），
    /// 绝不静默改派 / 回填来源引用；</item>
    /// <item>去重集合**各一次**有界批量查询（最多 3 次），任一行不合格即整批受控拒绝，绝不部分写入。</item>
    /// </list>
    /// </summary>
    public static async Task EnsureCommitmentReferencesAsync(IErpDbContext db, IReadOnlyList<PurchaseQuote>? quotes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var lines = (quotes ?? Array.Empty<PurchaseQuote>()).Where(q => q is not null).ToList();
        if (lines.Count == 0) return;

        foreach (var line in lines)
        {
            if (line.SupplierId is not > 0)
                throw BusinessException.RuleConflict($"{CommitmentSupplierText}（比价行 #{line.Id}）");
        }

        foreach (var line in lines)
        {
            if (line.ProductId is not > 0)
                throw BusinessException.RuleConflict($"{CommitmentProductText}（比价行 #{line.Id}）");
        }

        foreach (var line in lines)
        {
            if (line.CustomerId is { } owningCustomerId && owningCustomerId <= 0)
                throw BusinessException.InvalidParameter($"{CustomerRequiredText}（比价行 #{line.Id}）");
        }

        var supplierIds = lines.Select(l => l.SupplierId!.Value).Distinct().ToList();
        var productIds = lines.Select(l => l.ProductId!.Value).Distinct().ToList();
        var customerIds = lines.Where(l => l.CustomerId is > 0).Select(l => l.CustomerId!.Value)
            .Distinct().ToList();

        // 有界批量查询：去重集合各一次取回（供应商 / 商品 / 客户），绝不逐行查库。
        var suppliers = await db.BaseSuppliers.AsNoTracking()
            .Where(s => supplierIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);
        var products = await db.BaseProducts.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        var customers = customerIds.Count == 0
            ? new Dictionary<long, BaseCustomer>()
            : await db.BaseCustomers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, ct);

        foreach (var line in lines)
        {
            var supplierId = line.SupplierId!.Value;
            if (suppliers.TryGetValue(supplierId, out var supplier)
                && (supplier.IsDeleted || supplier.Status != EnabledStatus))
                throw BusinessException.RuleConflict(
                    $"{PurchaseQuoteConversionMutationRules.SupplierMasterText}（比价行 #{line.Id} / 供应商 {supplierId}）");

            var productId = line.ProductId!.Value;
            if (products.TryGetValue(productId, out var product)
                && (product.IsDeleted || product.Status != EnabledStatus))
                throw BusinessException.RuleConflict(
                    $"{PurchaseQuoteConversionMutationRules.ProductMasterText}（比价行 #{line.Id} / 商品 {productId}）");

            if (line.CustomerId is not { } customerId || customerId <= 0) continue;
            if (!customers.TryGetValue(customerId, out var customer) || customer.IsDeleted)
                throw BusinessException.NotFound(
                    $"比价行归属客户（Id={customerId}）不存在或已删除，不能承诺为采购订单归客户" +
                    UnavailableSuffixText);
            if (customer.Status != EnabledStatus)
                throw BusinessException.RuleConflict(
                    $"比价行归属客户「{customer.CustomerName}」已停用（Status != 1），不能承诺为采购订单归客户" +
                    UnavailableSuffixText);
        }
    }

    /// <summary>
    /// 比价行是否处于「选中」承诺（既有状态口径：<c>Status = 已选中</c> 且勾选「选中该供应商」）——
    /// 选择即供应商承诺，必须同时满足承诺级的实时引用要求。
    /// </summary>
    public static bool IsSelectionCommitment(PurchaseQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        return quote.IsSelected
               && string.Equals(PurchaseQuoteMutationRules.NormalizeStatus(quote.Status),
                   PurchaseQuoteMutationRules.SelectedStatus, StringComparison.Ordinal);
    }
}
