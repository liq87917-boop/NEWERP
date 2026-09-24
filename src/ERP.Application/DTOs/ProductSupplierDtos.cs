namespace ERP.Application.DTOs;

/// <summary>
/// 商品 / SKU 货源关系写入 DTO（ERP-038）。
/// <para>客户端只提交「供应商 / 可选规格 / 供应商货号 / 采购单位 / 最小起订量 / 交期 / 首选 / 状态 / 备注」这类主数据字段；
/// 作用域键（商品级 / 规格级）、归一化文本与可用性判定一律由服务端推导，客户端提交值不被采信。</para>
/// <para>边界：本 DTO 不含任何单价字段 —— 货源关系是**指引**，不报价、不定价、不改写采购单据。</para>
/// </summary>
public sealed class ProductSupplierSaveDto
{
    /// <summary>关联的供应商 Id（新增 / 更换供应商时必须是未删除且启用中的供应商）</summary>
    public long SupplierId { get; set; }

    /// <summary>可选的归属规格 Id（0 或 null = 商品级货源关系；有值 = SKU 级货源关系）</summary>
    public long? VariantId { get; set; }

    /// <summary>供应商货号 / 款号（可选；服务端去首尾空白、压缩连续空白后落库）</summary>
    public string SupplierItemCode { get; set; } = string.Empty;

    /// <summary>采购单位（可选，如 箱 / 打 / 个）</summary>
    public string PurchaseUnit { get; set; } = string.Empty;

    /// <summary>最小起订量 MOQ（为空按 0 = 未指定；不允许负数）</summary>
    public decimal? MinOrderQty { get; set; }

    /// <summary>交期天数（为空按 0 = 未指定；不允许负数）</summary>
    public int? LeadTimeDays { get; set; }

    /// <summary>是否设为该范围的首选货源（同一范围只能有一条启用首选；为空按「否」/保持原值）</summary>
    public bool? IsPreferred { get; set; }

    /// <summary>状态（1=启用 / 0=停用；新增时为空按启用处理）</summary>
    public int? Status { get; set; }

    /// <summary>排序号（为空按 0；仅影响展示顺序，与首选判定无关）</summary>
    public int? SortOrder { get; set; }

    /// <summary>备注</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 商品 / SKU 货源关系读取 DTO（ERP-038）：关系自身字段 + 由服务端解析的商品 / 规格 / 供应商信息与可用性标注。
/// <para>商品侧（<c>/api/base/products/{productId}/suppliers</c>）与供应商侧
/// （<c>/api/base/suppliers/{supplierId}/sourcing</c>）共用本结构，两者都是有界列表。</para>
/// </summary>
/// <param name="Id">货源关系 Id</param>
/// <param name="ProductId">归属商品 Id</param>
/// <param name="ProductCode">商品编码（商品被删除时为空串，界面按名称提示历史）</param>
/// <param name="ProductName">商品名称</param>
/// <param name="VariantId">归属规格 Id（null = 商品级货源关系）</param>
/// <param name="VariantCode">规格编码（商品级或规格已删除时为空串）</param>
/// <param name="VariantName">规格展示名（颜色 / 尺码；商品级为空串）</param>
/// <param name="ScopeKey">作用域键（<c>P</c> = 商品级；<c>V{规格Id}</c> = 规格级）</param>
/// <param name="ScopeText">作用域文案（商品级（整品）/ 规格级（SKU）），用于清晰区分两类关系</param>
/// <param name="SupplierId">供应商 Id</param>
/// <param name="SupplierCode">供应商编码</param>
/// <param name="SupplierName">供应商名称（供应商行缺失时退化为「供应商#{Id}」，历史仍可辨识）</param>
/// <param name="SupplierAvailable">供应商当前是否可用（未删除且启用）</param>
/// <param name="SupplierItemCode">供应商货号 / 款号</param>
/// <param name="PurchaseUnit">采购单位</param>
/// <param name="MinOrderQty">最小起订量 MOQ（0 = 未指定）</param>
/// <param name="LeadTimeDays">交期天数（0 = 未指定）</param>
/// <param name="IsPreferred">是否为该范围内的首选货源</param>
/// <param name="Status">状态（1=启用 / 0=停用）</param>
/// <param name="Selectable">当前是否可作为「启用货源」被参考（停用或已删除为 false）</param>
/// <param name="StatusText">状态文案（启用 / 停用）</param>
/// <param name="AvailabilityText">可用性文案（可选用 / 已停用 / 供应商或规格不可用，界面显式提示，不静默消失）</param>
/// <param name="Remark">备注</param>
/// <param name="CreatedAt">创建时间</param>
/// <param name="UpdatedAt">更新时间</param>
public sealed record ProductSupplierDto(
    long Id,
    long ProductId,
    string ProductCode,
    string ProductName,
    long? VariantId,
    string VariantCode,
    string VariantName,
    string ScopeKey,
    string ScopeText,
    long SupplierId,
    string SupplierCode,
    string SupplierName,
    bool SupplierAvailable,
    string SupplierItemCode,
    string PurchaseUnit,
    decimal MinOrderQty,
    int LeadTimeDays,
    bool IsPreferred,
    int Status,
    bool Selectable,
    string StatusText,
    string AvailabilityText,
    string Remark,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
