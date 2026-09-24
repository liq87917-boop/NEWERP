using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 商品资料
/// </summary>
public class BaseProduct : BaseEntity
{
    /// <summary>商品编码（唯一）</summary>
    [Required, MaxLength(50)]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>商品名称</summary>
    [Required, MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>英文名称</summary>
    [MaxLength(200)]
    public string EnglishName { get; set; } = string.Empty;

    /// <summary>规格型号</summary>
    [MaxLength(200)]
    public string Spec { get; set; } = string.Empty;

    /// <summary>计量单位</summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>商品分类</summary>
    [MaxLength(100)]
    public string Category { get; set; } = string.Empty;

    /// <summary>海关 HS 编码</summary>
    [MaxLength(50)]
    public string HsCode { get; set; } = string.Empty;

    /// <summary>条形码</summary>
    [MaxLength(100)]
    public string Barcode { get; set; } = string.Empty;

    /// <summary>采购价</summary>
    public decimal PurchasePrice { get; set; }

    /// <summary>销售价</summary>
    public decimal SalePrice { get; set; }

    /// <summary>成本价</summary>
    public decimal CostPrice { get; set; }

    /// <summary>单件毛重（kg）</summary>
    public decimal Weight { get; set; }

    /// <summary>单件体积（m³）</summary>
    public decimal Volume { get; set; }

    /// <summary>长（cm）</summary>
    public decimal Length { get; set; }

    /// <summary>宽（cm）</summary>
    public decimal Width { get; set; }

    /// <summary>高（cm）</summary>
    public decimal Height { get; set; }

    /// <summary>产品图片 1（OSS 原图地址）</summary>
    [MaxLength(500)]
    public string Image1 { get; set; } = string.Empty;

    /// <summary>产品图片 2（OSS 原图地址）</summary>
    [MaxLength(500)]
    public string Image2 { get; set; } = string.Empty;

    /// <summary>产品图片 3（OSS 原图地址）</summary>
    [MaxLength(500)]
    public string Image3 { get; set; } = string.Empty;

    /// <summary>状态（1=启用，0=停用）</summary>
    public int Status { get; set; } = 1;

    // ============ 阶段 1 补齐：多单位/箱规/报关/退税信息（全部可空） ============

    /// <summary>装箱单位（如 箱 / 打）</summary>
    [MaxLength(20)]
    public string PackageUnit { get; set; } = string.Empty;

    /// <summary>每箱数量（装箱数）</summary>
    public int UnitsPerPackage { get; set; }

    /// <summary>单位换算说明（如「1箱=12打=144个」）</summary>
    [MaxLength(100)]
    public string UnitConversion { get; set; } = string.Empty;

    /// <summary>外箱长（cm）</summary>
    public decimal OuterLength { get; set; }

    /// <summary>外箱宽（cm）</summary>
    public decimal OuterWidth { get; set; }

    /// <summary>外箱高（cm）</summary>
    public decimal OuterHeight { get; set; }

    /// <summary>外箱毛重（kg）</summary>
    public decimal OuterWeight { get; set; }

    /// <summary>体积重（kg）</summary>
    public decimal VolumeWeight { get; set; }

    /// <summary>英文报关品名</summary>
    [MaxLength(200)]
    public string EnglishDeclareName { get; set; } = string.Empty;

    /// <summary>出口退税率（%）</summary>
    public decimal RefundRate { get; set; }

    /// <summary>品牌</summary>
    [MaxLength(100)]
    public string Brand { get; set; } = string.Empty;

    /// <summary>认证（CE / ROHS / EN71 等）</summary>
    [MaxLength(200)]
    public string Certification { get; set; } = string.Empty;

    /// <summary>客户货号 / 款号</summary>
    [MaxLength(100)]
    public string CustomerItemNo { get; set; } = string.Empty;

    /// <summary>工厂货号</summary>
    [MaxLength(100)]
    public string FactoryItemNo { get; set; } = string.Empty;

    /// <summary>起订量 MOQ</summary>
    public int MinOrderQty { get; set; }

    /// <summary>价格是否含税</summary>
    public bool TaxIncluded { get; set; }

    /// <summary>安全库存（低于此值触发库存预警；0 = 不预警）</summary>
    public decimal MinStock { get; set; }

    /// <summary>库存上限（高于此值触发预警；0 = 不预警）</summary>
    public decimal MaxStock { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    // ============ ERP-037：颜色 / 尺码 SKU 规格变体（可选子表 BaseProductVariants） ============

    /// <summary>
    /// 该商品下**启用中**的规格条数（**非持久化列**，列表 / 详情读取时由服务端标注）；
    /// 没有维护任何规格的历史商品为 0，仍按单规格商品使用。
    /// </summary>
    [NotMapped]
    public int VariantCount { get; set; }

    /// <summary>
    /// 该商品下的规格总条数（含停用，**非持久化列**，读取时由服务端标注）：
    /// 与 <see cref="VariantCount"/> 的差值即「已停用规格数」，界面据此提示历史规格仍可读但不可再新选。
    /// </summary>
    [NotMapped]
    public int VariantTotalCount { get; set; }

}

/// <summary>
/// 其他资料（数据字典：币种、港口、货代、唛头、包装类型、结算方式、价格条款等）
/// 说明：阶段 1 起作为统一数据字典使用，InfoType 建议取值见 docs/菜单与业务流程优化建议-20260918.md
/// </summary>
public class BaseOtherInfo : BaseEntity
{
    /// <summary>资料类型（如 Currency/Port/Forwarder/ShippingMark/Package/TradeTerm）</summary>
    [Required, MaxLength(50)]
    public string InfoType { get; set; } = string.Empty;

    /// <summary>资料编码</summary>
    [Required, MaxLength(50)]
    public string InfoCode { get; set; } = string.Empty;

    /// <summary>资料名称</summary>
    [Required, MaxLength(100)]
    public string InfoName { get; set; } = string.Empty;

    /// <summary>英文名称</summary>
    [MaxLength(100)]
    public string EnglishName { get; set; } = string.Empty;

    /// <summary>是否默认</summary>
    public bool IsDefault { get; set; }

    /// <summary>排序号</summary>
    public int SortOrder { get; set; }

    /// <summary>状态（1=启用，0=停用）</summary>
    public int Status { get; set; } = 1;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
