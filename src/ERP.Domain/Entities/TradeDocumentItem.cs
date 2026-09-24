using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 单证明细行快照（ERP-051）：商业发票（CI）/ 装箱单（PL）等单证之下的**商品明细证据行**。
/// <para>定位：单证台账（<see cref="TradeDocument"/>）的**行级快照证据**——它记录「这张单证在制作当时
/// 列了哪些商品、多少数量、什么单位、单价与金额（商业发票）、箱数与净重 / 毛重（装箱单）」，
/// 让单证内容可被逐行核对与追溯。</para>
/// <para>它<strong>不是</strong>第二套商品主数据、<strong>不是</strong>库存交易、<strong>不是</strong>报关核定价格，
/// 也<strong>不是</strong>退税或税务依据：新增 / 修改 / 删除明细行都不会改动商品资料、销售订单、采购订单、
/// 装柜清单、库存与库存流水、发票、退税、费用或财务记录。</para>
/// <para>快照口径（重要）：</para>
/// <list type="bullet">
/// <item>商品引用（<see cref="ProductId"/>）为**可选**：引用商品资料时，写入当时由服务端取商品资料的
/// 编码 / 中英文名称 / 规格 / 单位作为快照；未引用商品资料时保存人工录入的纯文本行；</item>
/// <item>商品资料或来源单据（销售订单 / 装柜清单）之后被修改、停用或删除时，本行**保持登记当时的值**，
/// 绝不自动刷新、绝不回填、绝不被主数据覆盖；</item>
/// <item>金额一律服务端计算（<see cref="LineAmount"/> = 数量 × 单价，按币种精度取整），
/// 不接受客户端提交的金额；装箱单行不含单价与金额（恒为 0，不做金额派生）；</item>
/// <item>未填写的箱数 / 净重 / 毛重保存为 <c>null</c>（**不臆造为 0**），读取时照实显示为空白。</item>
/// </list>
/// <para>边界（重要）：</para>
/// <list type="bullet">
/// <item>只有商业发票与装箱单两类单证允许明细行；其余类型（报关单 / 形式发票 / 产地证 / 提单 / 订舱确认 /
/// 外汇核销单 / 其他）一律拒绝明细行变更，而不是保存含义不明的记录；</item>
/// <item>明细行只在单证处于**准备状态**（待制作 / 已制作）时可维护；已提交客户 / 已使用（以及未知状态）
/// 的单证明细为冻结快照，既不能新增 / 修改，也不能删除，绝不静默替换；</item>
/// <item>删除为**显式删除**（仅在准备状态允许），删除后该行仍保留审计字段与历史可读，
/// 不提供「整表静默替换」语义；</item>
/// <item>本表只保存快照，**刻意不建**到商品资料的外键（商品可能被停用 / 软删除，历史行必须始终可读），
/// 只与单证主表保持外键（单证本身只做软删除）。</item>
/// </list>
/// </summary>
public class TradeDocumentItem : BaseEntity
{
    /// <summary>所属单证 Id（引用 <see cref="TradeDocument"/>；单证只做软删除，本表随主表物理清理）</summary>
    public long TradeDocumentId { get; set; }

    /// <summary>行序（服务端权威写入：未指定时按既有最大行序 + 1 追加；同一单证内不重复，供打印 / 导出固定顺序）</summary>
    public int LineNo { get; set; }

    /// <summary>引用的商品资料 Id（0 = 未引用商品资料，本行为人工录入的纯文本快照；**刻意不建外键**）</summary>
    public long ProductId { get; set; }

    /// <summary>商品编码快照（引用商品资料时为服务端写入的权威值；否则为人工录入值）</summary>
    [MaxLength(50)]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>商品中文名称快照（服务端 / 人工写入后不再随商品资料变化）</summary>
    [MaxLength(200)]
    public string ProductNameCn { get; set; } = string.Empty;

    /// <summary>商品英文名称快照（报关 / 清关口径；没有英文名时为空，绝不自动翻译或推测）</summary>
    [MaxLength(200)]
    public string ProductNameEn { get; set; } = string.Empty;

    /// <summary>规格型号快照</summary>
    [MaxLength(200)]
    public string Spec { get; set; } = string.Empty;

    /// <summary>数量（必须大于 0，最多 4 位小数）</summary>
    public decimal Quantity { get; set; }

    /// <summary>计量单位快照（如 箱 / 打 / 个）</summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>单价（原币；最多 4 位小数；装箱单行恒为 0，不接受单价）</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>行金额（服务端权威计算：数量 × 单价，按币种精度取整；装箱单行恒为 0）</summary>
    public decimal LineAmount { get; set; }

    /// <summary>箱数（装箱单行可选；未登记为 <c>null</c>，不臆造为 0）</summary>
    public int? PackageCount { get; set; }

    /// <summary>净重 kg（装箱单行可选；未登记为 <c>null</c>，不臆造为 0）</summary>
    public decimal? NetWeight { get; set; }

    /// <summary>毛重 kg（装箱单行可选；填写时不得小于净重；未登记为 <c>null</c>，不臆造为 0）</summary>
    public decimal? GrossWeight { get; set; }

    /// <summary>币种快照（写入时取自单证台账币种；商业发票行必须为系统支持币种，不做汇率换算）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "USD";

    /// <summary>备注（有界；只作为行说明，不参与任何金额派生）</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    // ============ 读取侧标注（**非持久化列**，读取时由服务端计算，不落库） ============

    /// <summary>引用的商品资料当前是否可用（存在且未删除；**非持久化列**）</summary>
    [NotMapped]
    public bool ProductAvailable { get; set; }

    /// <summary>商品引用可用性 / 停用文案（不可用时照实说明，历史快照照常可读；**非持久化列**）</summary>
    [NotMapped]
    public string ProductAvailabilityText { get; set; } = string.Empty;
}
