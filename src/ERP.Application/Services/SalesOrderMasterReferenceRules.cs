using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 规范销售订单（<c>api/sales-orders</c>）写入前的**实时主数据引用**护栏（ERP-423）。
/// <para>背景：ERP-420 只做「客户菜单 + 权威客户范围」护栏，ERP-401 / ERP-421 只保护来源血缘与锁协议，
/// ERP-422 只校验数量 / 单价 / 金额条款 —— 但**手工订单**（无来源）的表头 <c>CustomerId</c>、
/// <c>SalesmanId</c>、<c>PortId</c> 与明细 <c>ProductId</c> / 单位此前都直接照抄调用方提交值，
/// 从未核对**实时主数据**是否存在、是否已删除、是否已停用；来源为空的订单因此可以把不存在 / 已删除 /
/// 已停用的客户 / 商品 / 业务员 / 港口写进核心手工订单，并在提交 / 审核时把无效引用带进运营需求。</para>
/// <list type="number">
/// <item><b>必填客户</b>：<c>CustomerId</c> 必须为正整数且解析到**存在、未删除、启用（Status = 1）**的
/// 客户；缺失 / 已删除 / 已停用一律在表头与单号赋值之前 fail closed。</item>
/// <item><b>必填商品</b>：每一条**有效（未软删除）**明细的 <c>ProductId</c> 必须为正整数且解析到
/// **存在、未删除、启用（Status = 1）**的商品；缺失 / 已删除 / 已停用一律拒绝（有界批量查询，绝不逐行查库）。</item>
/// <item><b>可选业务员 / 目的港</b>：留空保持可选；一旦填写（显式值）必须为正整数且解析到对应实时主数据 ——
/// 业务员为存在、未删除、在职（Status = 1）的员工，目的港为存在、未删除、启用（Status = 1）的港口字典项
/// （<c>BaseOtherInfo.InfoType = Port</c>）。</item>
/// <item><b>既有有效单位口径</b>：商品明细单位必须在**既有单位口径**内 —— 等于商品基础单位
/// （<see cref="BaseProduct.Unit"/>）或等于商品装箱单位（<see cref="BaseProduct.PackageUnit"/> 且
/// <see cref="BaseProduct.UnitsPerPackage"/> &gt; 0，即 <c>StockUnitConversion</c> 认可的合法包装单位）。
/// 合法包装 / 替代单位一律保留，绝不臆造单位换算，也绝不假设明细单位等于默认基础单位；商品未维护任何单位
/// （基础与装箱单位均为空）时不做单位判定（历史商品保持可用）。</item>
/// </list>
/// <para><b>边界</b>：本类只做**纯判定与有界只读投影查询**，不落库、不改写客户 / 商品 / 员工 / 字典主数据，
/// 不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，也不删除任何历史证据与审计留痕；历史销售订单的读取 / 打印
/// 完全只读、不被自动回填或修正（引用失效的历史订单照常可读）。来源报价单 / PI 的历史链接仍由
/// <see cref="SalesOrderSourceLineageRules"/> 既有血缘规则治理，本类**绝不**把来源租户 / 来源 Id 与本次
/// 客户 / 商品归属混为一谈。</para>
/// </summary>
public static class SalesOrderMasterReferenceRules
{
    /// <summary>目的港字典项资料类型（<c>BaseOtherInfo.InfoType</c>，与既有「港口」字典同源）。</summary>
    public const string PortInfoType = "Port";

    /// <summary>既有启用状态取值（客户 / 商品 / 员工 / 字典项：1=启用，0=停用）。</summary>
    public const int EnabledStatus = 1;

    /// <summary>客户必填（CustomerId 非正整数）的拒绝文案。</summary>
    public const string CustomerRequiredText = "销售订单必须指定客户（CustomerId 必须为正整数）";

    /// <summary>业务员填写时必须为正整数的拒绝文案。</summary>
    public const string SalesmanRequiredText = "业务员（SalesmanId）填写时必须为正整数（留空 = 未指定）";

    /// <summary>目的港填写时必须为正整数的拒绝文案。</summary>
    public const string PortRequiredText = "目的港（PortId）填写时必须为正整数（留空 = 未指定）";

    /// <summary>明细必填商品（ProductId 非正整数）的拒绝文案。</summary>
    public const string ProductRequiredText = "销售订单明细必须指定商品（ProductId 必须为正整数）";

    /// <summary>「引用主数据不可用」受控错误后缀（历史读取 / 打印保持只读，接口 / 文档同源）。</summary>
    public const string UnavailableSuffixText = "（历史销售订单仍可读取，读取 / 打印绝不自动回填或修正）";

    /// <summary>口径说明（接口 / 文档同源）。</summary>
    public const string RuleText =
        "ERP-423：规范销售订单的新增 / 修改在单号预约与任何表头 / 明细赋值之前、提交 / 审核在既有订单行锁内提交之前，" +
        "一律复核实时主数据引用 —— 必填客户（CustomerId 为正整数，存在 / 未删除 / 启用）、每条有效明细的必填商品" +
        "（ProductId 为正整数，存在 / 未删除 / 启用）、可选业务员（SalesmanId 留空跳过，填写必须解析到存在 / 未删除 / 在职员工）、" +
        "可选目的港（PortId 留空跳过，填写必须是存在 / 未删除 / 启用的 Port 港口字典项），并在既有有效单位口径内核对明细单位" +
        "（基础单位或装箱单位，装箱数需大于 0）；任一项不满足即返回受控业务错误，绝不写库、绝不预约单号、绝不产生下游副作用，" +
        "历史读取 / 打印完全只读且不被自动修正。";

    /// <summary>边界文案。</summary>
    public const string BoundaryText =
        "本护栏只保护规范销售订单写入入口：只做纯判定与有界只读投影查询，不落库、不改写客户 / 商品 / 员工 / 港口字典主数据，" +
        "不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不臆造单位换算，也不假设明细单位等于默认基础单位；" +
        "来源报价单 / PI 历史链接仍由既有血缘规则治理，绝不与本次客户 / 商品归属混淆；" +
        "历史销售订单的读取 / 打印保持完全只读、不被自动回填或修正。";

    /// <summary>
    /// 写入前完整复核实时主数据引用：客户 → 可选业务员 → 可选目的港 → 每条有效明细的商品与单位。
    /// 所有判定发生在表头 / 明细赋值与单号预约之前，任一项失败即整体拒绝（不落库）。
    /// </summary>
    public static async Task EnsureMasterReferencesAsync(
        IErpDbContext db, SalesOrder order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(order);
        ct.ThrowIfCancellationRequested();

        await EnsureCustomerAvailableAsync(db, order.CustomerId, ct);
        await EnsureSalesmanAvailableAsync(db, order.SalesmanId, ct);
        await EnsurePortAvailableAsync(db, order.PortId, ct);
        await EnsureDetailProductsAsync(db, order.Details, ct);
    }

    /// <summary>
    /// 必填客户：<c>CustomerId</c> 为正整数且解析到存在、未删除、启用（<c>Status = 1</c>）的客户。
    /// 缺失 / 已删除 → <see cref="ErrorCodes.NotFound"/>；已停用 → <see cref="ErrorCodes.RuleConflict"/>。
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
                $"客户（Id={customerId}）不存在或已删除，不能用于销售订单的新增 / 修改 / 提交 / 审核{UnavailableSuffixText}");
        if (customer.Status != EnabledStatus)
            throw BusinessException.RuleConflict(
                $"客户「{customer.CustomerName}」已停用，不能用于销售订单的新增 / 修改 / 提交 / 审核{UnavailableSuffixText}");

        return customer;
    }

    /// <summary>
    /// 可选业务员：未填写（<c>null</c>）跳过；填写时必须为正整数，且解析到存在、未删除、在职
    /// （<c>Status = 1</c>）的员工（与既有「业务员下拉」状态口径同源）。
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
                $"业务员（员工 Id={salesmanId.Value}）不存在或已删除，不能用于销售订单的新增 / 修改 / 提交 / 审核{UnavailableSuffixText}");
        if (employee.Status != EnabledStatus)
            throw BusinessException.RuleConflict(
                $"业务员「{employee.EmployeeName}」已离职 / 停用（Status != 1），不能用于销售订单的新增 / 修改 / 提交 / 审核{UnavailableSuffixText}");

        return employee;
    }

    /// <summary>
    /// 可选目的港：未填写（<c>null</c>）跳过；填写时必须为正整数，且指向存在、未删除、启用（<c>Status = 1</c>）的
    /// 港口字典项（<c>BaseOtherInfo.InfoType = Port</c>）。其他资料类型的 Id 一律不被采信。
    /// </summary>
    public static async Task<BaseOtherInfo?> EnsurePortAvailableAsync(
        IErpDbContext db, long? portId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (portId is null) return null;
        if (portId.Value <= 0)
            throw BusinessException.InvalidParameter(PortRequiredText);

        var port = await db.BaseOtherInfos.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == portId.Value, ct);
        if (port is null || port.IsDeleted)
            throw BusinessException.NotFound(
                $"目的港字典项（Id={portId.Value}）不存在或已删除，不能用于销售订单的新增 / 修改 / 提交 / 审核{UnavailableSuffixText}");
        if (!string.Equals(port.InfoType, PortInfoType, StringComparison.OrdinalIgnoreCase))
            throw BusinessException.InvalidParameter(
                $"目的港（Id={portId.Value}）不是港口字典项（InfoType={PortInfoType}），不能用于销售订单");
        if (port.Status != EnabledStatus)
            throw BusinessException.RuleConflict(
                $"目的港「{port.InfoName}」已停用，不能用于销售订单的新增 / 修改 / 提交 / 审核{UnavailableSuffixText}");

        return port;
    }

    /// <summary>
    /// 每条**有效（未软删除）**明细的商品与单位复核：商品必须为正整数且存在、未删除、启用；
    /// 明细单位必须在既有有效单位口径内（基础单位或合法装箱单位）。商品资料**一次有界批量查询**
    /// （绝不逐行查库），失败消息按行内商品给出受控错误。
    /// </summary>
    public static async Task EnsureDetailProductsAsync(
        IErpDbContext db, IEnumerable<SalesOrderDetail>? details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var lines = (details ?? Enumerable.Empty<SalesOrderDetail>())
            .Where(d => d is not null && !d.IsDeleted)
            .ToList();
        if (lines.Count == 0) return;

        foreach (var line in lines)
        {
            if (line.ProductId <= 0)
                throw BusinessException.InvalidParameter(ProductRequiredText);
        }

        var productIds = lines.Select(d => d.ProductId).Distinct().ToList();
        var products = await db.BaseProducts.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        foreach (var line in lines)
        {
            if (!products.TryGetValue(line.ProductId, out var product) || product.IsDeleted)
                throw BusinessException.NotFound(
                    $"商品（Id={line.ProductId}）不存在或已删除，不能用于销售订单的新增 / 修改 / 提交 / 审核{UnavailableSuffixText}");
            if (product.Status != EnabledStatus)
                throw BusinessException.RuleConflict(
                    $"商品「{product.ProductName}」已停用，不能用于销售订单的新增 / 修改 / 提交 / 审核{UnavailableSuffixText}");

            if (!IsSupportedUnit(product, line.Unit))
                throw BusinessException.RuleConflict(UnsupportedUnitText(product, line.Unit));
        }
    }

    /// <summary>
    /// 既有有效单位口径判定（与 <c>StockUnitConversion</c> / <c>TryBaseUnitQuantity</c> 同源）：
    /// 明细单位去首尾空白后等于商品基础单位，或等于商品装箱单位且装箱数大于 0 时为合法（大小写不敏感）；
    /// 商品基础单位与装箱单位均为空时不产生单位判定（历史商品保持可用，故返回 <c>true</c>）；
    /// 其余（含单位为空但商品维护了单位）一律为**不支持**，绝不臆造换算、绝不假设等于默认基础单位。
    /// </summary>
    public static bool IsSupportedUnit(BaseProduct product, string? unit)
    {
        ArgumentNullException.ThrowIfNull(product);

        var supported = SupportedUnits(product);
        if (supported.Count == 0) return true;

        var normalized = (unit ?? string.Empty).Trim();
        return normalized.Length > 0 && supported.Contains(normalized);
    }

    /// <summary>
    /// 商品的既有有效单位集合：基础单位（非空）∪ 合法装箱单位（非空且装箱数大于 0），大小写不敏感去重。
    /// </summary>
    public static IReadOnlyCollection<string> SupportedUnits(BaseProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);

        var units = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseUnit = (product.Unit ?? string.Empty).Trim();
        if (baseUnit.Length > 0) units.Add(baseUnit);

        var packageUnit = (product.PackageUnit ?? string.Empty).Trim();
        if (packageUnit.Length > 0 && product.UnitsPerPackage > 0) units.Add(packageUnit);

        return units;
    }

    /// <summary>不支持单位时的受控错误文案（显式列出既有有效单位口径，绝不臆造换算）。</summary>
    public static string UnsupportedUnitText(BaseProduct product, string? unit)
    {
        ArgumentNullException.ThrowIfNull(product);
        var normalized = (unit ?? string.Empty).Trim();
        var shown = normalized.Length == 0 ? "(空)" : normalized;
        var supported = SupportedUnits(product);
        var supportedText = supported.Count == 0 ? "(商品未维护单位)" : string.Join(" / ", supported);
        return $"商品「{product.ProductName}」的明细单位「{shown}」不在既有有效单位口径内（基础单位 / 装箱单位：{supportedText}）；" +
               "拒绝臆造单位换算，也绝不假设明细单位等于默认基础单位";
    }
}
