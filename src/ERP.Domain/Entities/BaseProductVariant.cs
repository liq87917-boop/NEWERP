using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 商品规格变体（ERP-037：商品资料下的颜色 / 尺码 SKU 子表）。
/// <para>定位：**可选的主数据子表**——商品资料仍是唯一权威身份，规格只是它下面「零到多条」的
/// 可选细分；没有任何规格的商品继续按单规格商品使用，行为与历史数据完全一致。</para>
/// <para>边界（重要）：本表<strong>不被任何单据引用</strong>——询价、报价单、PI、销售 / 采购订单、
/// 库存与库存流水的行都保持原有 <c>ProductId</c> 口径，不因规格新增 / 修改 / 停用 / 删除而被改写、
/// 拆分或重算；删除规格只做主数据自身的软删除。</para>
/// </summary>
public class BaseProductVariant : BaseEntity
{
    /// <summary>归属商品 Id（引用 <see cref="BaseProduct"/>；刻意不建外键，避免商品软删除时连带影响历史规格）</summary>
    public long ProductId { get; set; }

    /// <summary>
    /// 规格编码（同一商品内唯一；服务端规范化后落库：去首尾空白、压缩连续空白、转大写，
    /// 因此「red-1」与「 Red-1 」视为同一编码并被拒绝重复）。
    /// </summary>
    [Required, MaxLength(50)]
    public string VariantCode { get; set; } = string.Empty;

    /// <summary>颜色（可选，与尺码至少填一个；展示值由服务端去首尾空白后写入，保留原大小写）</summary>
    [MaxLength(50)]
    public string Color { get; set; } = string.Empty;

    /// <summary>尺码 / 尺寸（可选，与颜色至少填一个）</summary>
    [MaxLength(50)]
    public string Size { get; set; } = string.Empty;

    /// <summary>
    /// 「颜色 + 尺码」归一化组合键（服务端写入，客户端提交值一律不被采信）：
    /// 形如 <c>RED|XL</c>，用于在数据库层用过滤唯一索引兜底「同一商品下启用规格的颜色/尺码组合不重复」。
    /// </summary>
    [Required, MaxLength(120)]
    public string ColorSizeKey { get; set; } = string.Empty;

    /// <summary>状态（1=启用，可被新选用；0=停用，历史可读但不可再被新选中）</summary>
    public int Status { get; set; } = 1;

    /// <summary>排序号（同商品内规格展示顺序）</summary>
    public int SortOrder { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>
    /// 规格展示名（**非持久化列**）：颜色 / 尺码按「 / 」拼接，两者都为空时退化为规格编码；
    /// 不参与唯一性判定（唯一性由 <see cref="VariantCode"/> 与 <see cref="ColorSizeKey"/> 决定）。
    /// </summary>
    [NotMapped]
    public string VariantName
    {
        get
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(Color)) parts.Add(Color.Trim());
            if (!string.IsNullOrWhiteSpace(Size)) parts.Add(Size.Trim());
            return parts.Count > 0 ? string.Join(" / ", parts) : VariantCode;
        }
    }
}
