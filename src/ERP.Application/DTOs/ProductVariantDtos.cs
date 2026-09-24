namespace ERP.Application.DTOs;

/// <summary>
/// 商品规格变体写入 DTO（ERP-037）。
/// <para>客户端只提交「规格编码 / 颜色 / 尺码 / 备注」这类主数据字段；
/// 归一化后的编码、组合键、归属商品一律由服务端推导，客户端提交值不被采信。</para>
/// </summary>
public sealed class ProductVariantSaveDto
{
    /// <summary>规格编码（服务端校验并规范化：去首尾空白、压缩空白、转大写后同商品内唯一）</summary>
    public string VariantCode { get; set; } = string.Empty;

    /// <summary>颜色（可选；与尺码至少填一个）</summary>
    public string Color { get; set; } = string.Empty;

    /// <summary>尺码（可选；与颜色至少填一个）</summary>
    public string Size { get; set; } = string.Empty;

    /// <summary>状态（1=启用 / 0=停用；新增时为空按启用处理）</summary>
    public int? Status { get; set; }

    /// <summary>排序号（为空按 0）</summary>
    public int? SortOrder { get; set; }

    /// <summary>备注</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 商品规格变体读取 DTO（ERP-037）：规格自身的可靠字段 + 由服务端计算的展示与可选用标注。
/// </summary>
/// <param name="Id">规格 Id</param>
/// <param name="ProductId">归属商品 Id（商品身份不变，规格只是它的可选细分）</param>
/// <param name="VariantCode">规范化后的规格编码</param>
/// <param name="Color">颜色（可为空串）</param>
/// <param name="Size">尺码（可为空串）</param>
/// <param name="VariantName">展示名（颜色 / 尺码，均空时用编码）</param>
/// <param name="Status">状态（1=启用 / 0=停用）</param>
/// <param name="Selectable">当前是否可作为「启用中的主数据」被新选用（停用或已删除为 false）</param>
/// <param name="StatusText">状态文案（启用 / 停用）</param>
/// <param name="ColorSizeKey">归一化组合键（只读回显，便于排查重复判定）</param>
/// <param name="SortOrder">排序号</param>
/// <param name="Remark">备注</param>
/// <param name="CreatedAt">创建时间</param>
/// <param name="UpdatedAt">更新时间</param>
public sealed record ProductVariantDto(
    long Id,
    long ProductId,
    string VariantCode,
    string Color,
    string Size,
    string VariantName,
    int Status,
    bool Selectable,
    string StatusText,
    string ColorSizeKey,
    int SortOrder,
    string Remark,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
