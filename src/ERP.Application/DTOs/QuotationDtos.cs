using ERP.Domain.Enums;

namespace ERP.Application.DTOs;

/// <summary>
/// 报价单有效期提醒项（ERP-018）：报价单页「⏰ 有效期提醒」的数据契约。
/// </summary>
/// <remarks>
/// 全部字段取自现有 <c>Quotations</c> 表与来源关联表，**不新增数据库结构**；
/// 有效期状态与剩余天数由 <see cref="Services.QuotationValidityRules"/> 统一计算。
/// </remarks>
public class QuotationValidityItem
{
    /// <summary>报价单 Id（前端可直接跳转编辑）</summary>
    public long Id { get; set; }

    /// <summary>报价单号</summary>
    public string QuotationNo { get; set; } = string.Empty;

    /// <summary>客户名称</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>业务员姓名</summary>
    public string SalesmanName { get; set; } = string.Empty;

    /// <summary>报价日期</summary>
    public DateTime QuotationDate { get; set; }

    /// <summary>有效期至（未设置时为 null）</summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>剩余有效天数（负数 = 已过期、0 = 今日到期；未设置有效期时为 null）</summary>
    public int? ValidDays { get; set; }

    /// <summary>有效期状态文案（未设置有效期 / 已过期 / 今日到期 / 即将到期 / 有效）</summary>
    public string ValidityStatus { get; set; } = string.Empty;

    /// <summary>有效期状态级别（danger / warning / success / neutral）</summary>
    public string ValidityLevel { get; set; } = string.Empty;

    /// <summary>报价总额（原币）</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>报价总额（折人民币）</summary>
    public decimal TotalAmountCny { get; set; }

    /// <summary>币种</summary>
    public Currency Currency { get; set; }

    /// <summary>单据状态（草稿 / 已审核 / 已完成 …）</summary>
    public DocumentStatus Status { get; set; }

    /// <summary>是否已转出（已转形式发票 PI 或已转销售订单）</summary>
    public bool Converted { get; set; }
}

/// <summary>
/// 报价单版本链节点（ERP-035）：<c>GET /api/sales/quotations/{id}/revisions</c> 的数据契约。
/// </summary>
/// <remarks>
/// 版本号为服务端分配（初始版本 = 1，链内单调递增）；历史报价单（无版本元数据）按初始版本 1 返回，
/// 只读不写库。<c>Superseded</c> 表示链内已有指向它的下一版本（该版本为只读历史）。
/// </remarks>
public class QuotationRevisionChainItem
{
    /// <summary>报价单（版本）Id</summary>
    public long Id { get; set; }

    /// <summary>报价单号（初始版本 = 根单号；后续版本 = 根单号 + <c>-R版本号</c>）</summary>
    public string QuotationNo { get; set; } = string.Empty;

    /// <summary>版本号（初始版本为 1）</summary>
    public int RevisionNumber { get; set; }

    /// <summary>版本链根单 Id</summary>
    public long RootQuotationId { get; set; }

    /// <summary>版本链根单号</summary>
    public string RootQuotationNo { get; set; } = string.Empty;

    /// <summary>上一版本 Id（初始版本为 null）</summary>
    public long? PreviousRevisionId { get; set; }

    /// <summary>上一版本号（初始版本为空串）</summary>
    public string PreviousRevisionNo { get; set; } = string.Empty;

    /// <summary>是否初始版本（无前序版本）</summary>
    public bool IsInitialRevision { get; set; }

    /// <summary>是否为本次查询显式选中的版本</summary>
    public bool IsSelected { get; set; }

    /// <summary>是否为链内最新版本（草稿继续操作的目标）</summary>
    public bool IsLatest { get; set; }

    /// <summary>是否已被后续版本取代（链内只读历史）</summary>
    public bool Superseded { get; set; }

    /// <summary>是否已转出（已转 PI / 已转销售订单，口径与成交率报表一致）</summary>
    public bool Converted { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; }

    /// <summary>报价日期</summary>
    public DateTime QuotationDate { get; set; }

    /// <summary>有效期至</summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>报价总额（原币）</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>报价总额（折人民币）</summary>
    public decimal TotalAmountCny { get; set; }

    /// <summary>币种</summary>
    public Currency Currency { get; set; }

    /// <summary>客户名称</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>业务员姓名</summary>
    public string SalesmanName { get; set; } = string.Empty;
}

/// <summary>
/// 创建报价新版本的结果（ERP-035）：<c>POST /api/sales/quotations/{id}/revisions</c> 的数据契约。
/// </summary>
public class QuotationRevisionResult
{
    /// <summary>新版本 Id</summary>
    public long Id { get; set; }

    /// <summary>新版本单号（根单号 + <c>-R版本号</c>）</summary>
    public string QuotationNo { get; set; } = string.Empty;

    /// <summary>新版本版本号</summary>
    public int RevisionNumber { get; set; }

    /// <summary>版本链根单 Id</summary>
    public long RootQuotationId { get; set; }

    /// <summary>版本链根单号</summary>
    public string RootQuotationNo { get; set; } = string.Empty;

    /// <summary>源（上一）版本 Id</summary>
    public long PreviousRevisionId { get; set; }

    /// <summary>源（上一）版本号</summary>
    public string PreviousRevisionNo { get; set; } = string.Empty;
}
