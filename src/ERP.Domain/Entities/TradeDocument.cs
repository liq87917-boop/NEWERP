using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 出口单证台账（单证中心）
/// 用途：登记每一票出口业务所涉及的单证（报关单、装箱单、商业发票 CI、形式发票 PI、
///       产地证 CO / Form A / Form E、提单 B/L 等）及其制作与提交状态，
///       解决"单证散落在各人电脑、无统一台账、报关/清关进度说不清"的问题。
/// 说明：单表结构（不涉及主子表与存储过程），使用通用 CRUD 维护，可导入导出与打印。
/// </summary>
public class TradeDocument : BaseEntity
{
    /// <summary>单证编号（唯一，可自行编号或填写实际单证号码）</summary>
    [Required, MaxLength(50)]
    public string DocNo { get; set; } = string.Empty;

    /// <summary>单证类型（报关单 / 装箱单 / 商业发票 / 形式发票 / 产地证 / 提单 / 其他）</summary>
    [MaxLength(30)]
    public string DocType { get; set; } = string.Empty;

    /// <summary>出具 / 签发日期</summary>
    public DateTime? IssueDate { get; set; }

    /// <summary>关联报关单号</summary>
    [MaxLength(50)]
    public string DeclareNo { get; set; } = string.Empty;

    /// <summary>关联柜号 / 订舱号</summary>
    [MaxLength(50)]
    public string RefNo { get; set; } = string.Empty;

    /// <summary>关联销售订单号</summary>
    [MaxLength(50)]
    public string SalesOrderNo { get; set; } = string.Empty;

    /// <summary>客户 Id</summary>
    public long? CustomerId { get; set; }

    /// <summary>客户名称（冗余）</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>单证金额（发票金额等）</summary>
    public decimal Amount { get; set; }

    /// <summary>币种</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "USD";

    /// <summary>起运港</summary>
    [MaxLength(100)]
    public string DeparturePort { get; set; } = string.Empty;

    /// <summary>目的港</summary>
    [MaxLength(100)]
    public string DestinationPort { get; set; } = string.Empty;

    /// <summary>制作人（内部经办人 / 出证机构）</summary>
    [MaxLength(100)]
    public string IssuedBy { get; set; } = string.Empty;

    /// <summary>单证份数</summary>
    public int Copies { get; set; }

    /// <summary>状态（待制作 / 已制作 / 已提交客户 / 已使用）</summary>
    [MaxLength(20)]
    public string Status { get; set; } = "待制作";

    /// <summary>附件说明 / 存放位置（暂存文本，后续可挂附件）</summary>
    [MaxLength(300)]
    public string FileNote { get; set; } = string.Empty;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
