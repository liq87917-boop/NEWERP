namespace ERP.Application.DTOs;

/// <summary>
/// 代理服务费对账单**服务来源引用行**提交请求（ERP-070）。
/// <para>客户端只能提交**显式证据**：来源类型 + **来源记录 Id（持久化标识符）** + 行说明 + 可选计费基础数量 +
/// 计费基础说明 + 显式行金额 + 有界备注。来源单号 / 日期 / 状态 / 客户 / 币种快照、行号、状态、登记人
/// （按已认证身份写入）、登记时间与合计一律由服务端权威写入，客户端提交的同名字段（若有）不被采信。</para>
/// <para>系统<strong>不</strong>按单号文本、金额、日期或相似度猜测来源；<strong>不</strong>把协议费率、协议固定金额、
/// 客户默认值、来源单据金额或自由文本转换成行金额。</para>
/// </summary>
public sealed class AgencyServiceFeeStatementLineSaveDto
{
    /// <summary>来源类型（显式 allowlist：<c>sales-order</c> 销售订单 / <c>loading-list</c> 装柜清单；未知取值一律拒绝）</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>来源记录 Id（**必须显式选择**：销售订单 Id 或装柜清单 Id；不做文本 / 金额 / 相似度匹配）</summary>
    public long SourceId { get; set; }

    /// <summary>行说明（必填、≤200 字：说明这一行对应什么服务；仅作人工留痕，服务端不据此计算金额）</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>计费基础数量（**可选**显式留痕，4 位小数；留空 = 未知，服务端不从来源单据数量推算）</summary>
    public decimal? BasisQuantity { get; set; }

    /// <summary>计费基础说明（必填、≤200 字：说明数量 / 口径来自哪里；服务端不据此计算金额）</summary>
    public string BasisNote { get; set; } = string.Empty;

    /// <summary>行金额（**必填、显式提交**：按对账单币种精度取整后必须大于 0；原币，不做汇率换算）</summary>
    public decimal Amount { get; set; }

    /// <summary>行备注（≤500 字）</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 代理服务费对账单证据登记 / 修改请求（ERP-070）。
/// <para>客户端只能提交**显式内容**：对账单号 / 权威客户 / 币种 / 对账日期 / 可选到期日 / 服务期间 /
/// 显式关联的 ERP-069 协议 Id / 有界备注 / 一条或多条显式来源引用行。规范化对账单号、客户 / 协议快照、
/// 行号与来源快照、状态、**合计**、登记人（按已认证身份写入）与时间戳一律由服务端权威写入。</para>
/// <para>边界：本请求<strong>不</strong>包含也不允许任何「合计覆盖」「税率」「开票」「记账」「收款」「催收」字段；
/// 到期日留空即未知，服务端不会按客户账期、协议文字或对账日期补一个日期。</para>
/// </summary>
public sealed class AgencyServiceFeeStatementSaveDto
{
    /// <summary>对账单号（必填；按真实对账单填写，最长 50 个字符，系统不自动发号）</summary>
    public string StatementNo { get; set; } = string.Empty;

    /// <summary>客户 Id（必须存在、未删除且启用，且必须与关联协议客户一致）</summary>
    public long CustomerId { get; set; }

    /// <summary>币种（必须受支持，且必须与关联协议币种一致；所有行金额与合计都以该币种原币记录）</summary>
    public string Currency { get; set; } = "CNY";

    /// <summary>对账日期（必填；由用户显式填写，服务端不按当天推算）</summary>
    public DateTime? StatementDate { get; set; }

    /// <summary>到期日（**可选**；留空 = 未知，服务端不推算；填写时不得早于对账日期）</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>服务期间起始日期（必填）</summary>
    public DateTime? ServicePeriodFrom { get; set; }

    /// <summary>服务期间结束日期（必填；不得早于起始日期）</summary>
    public DateTime? ServicePeriodTo { get; set; }

    /// <summary>显式关联的 ERP-069 协议证据 Id（必须存在、未删除且为「已登记」）</summary>
    public long AgreementId { get; set; }

    /// <summary>备注（≤500 字）</summary>
    public string Remark { get; set; } = string.Empty;

    /// <summary>显式服务来源引用行（**至少一条**；行号、来源快照与金额校验全部由服务端重算）</summary>
    public List<AgencyServiceFeeStatementLineSaveDto> Lines { get; set; } = new();
}

/// <summary>作废代理服务费对账单证据请求（ERP-070）：必须显式填写原因，作废保留原始行与历史而不是删除。</summary>
public sealed class AgencyServiceFeeStatementVoidRequest
{
    /// <summary>作废原因（必填；用于解释更正原因，写入历史留痕）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>代理服务费对账单证据台账查询参数（ERP-070，全部为可选过滤；结果分页返回）</summary>
public sealed class AgencyServiceFeeStatementQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多对账单证据）</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>客户筛选（留空 = 全部客户；不同客户绝不合并汇总）</summary>
    public long? CustomerId { get; set; }

    /// <summary>关联协议筛选（留空 = 全部协议）</summary>
    public long? AgreementId { get; set; }

    /// <summary>状态筛选（0 草稿 / 1 已登记 / 2 已作废；留空 = 全部，含已作废历史）</summary>
    public int? Status { get; set; }

    /// <summary>币种筛选（留空 = 全部币种；不同币种分别成行，绝不合并为一个金额）</summary>
    public string? Currency { get; set; }

    /// <summary>来源类型筛选（sales-order / loading-list；留空 = 全部；按行内存在该类型来源过滤）</summary>
    public string? SourceType { get; set; }

    /// <summary>对账日期开始（含当天；留空 = 不限）</summary>
    public DateTime? StatementDateFrom { get; set; }

    /// <summary>对账日期结束（含当天；留空 = 不限）</summary>
    public DateTime? StatementDateTo { get; set; }

    /// <summary>关键字（匹配对账单号 / 客户名称 / 客户编码 / 协议号 / 备注；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>校验并修正分页参数（与 PageQuery 同口径）</summary>
    public void Normalize()
    {
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = DefaultPageSize;
        if (PageSize > MaxPageSize) PageSize = MaxPageSize;
    }
}


/// <summary>
/// 代理服务费对账单**服务来源引用行**（ERP-070，读取用）：来源身份与快照 / 行说明 / 计费基础快照 / 行金额与状态。
/// <para><see cref="SourceIdentityText"/>、<see cref="SourceTypeText"/>、<see cref="SourceAvailabilityText"/>、
/// <see cref="SourceCurrencyCompatibilityText"/>、<see cref="AmountText"/> 与 <see cref="AmountRuleText"/>
/// 由服务端生成、与前端同源文案；<see cref="Amount"/> 永远是**用户显式提供的证据**，
/// 不是系统计算结果，也不是应收 / 应付余额、发票金额、收入确认或付款授权。</para>
/// </summary>
public sealed record AgencyServiceFeeStatementLineDto(
    long Id,
    long StatementId,
    int LineNo,
    string SourceType,
    string SourceTypeText,
    long SourceId,
    string SourceNo,
    string SourceIdentityText,
    DateTime SourceDate,
    int SourceStatus,
    string SourceStatusText,
    long SourceCustomerId,
    string SourceCustomerCode,
    string SourceCustomerName,
    string SourceCurrency,
    string SourceCurrencyCompatibilityText,
    string Description,
    decimal? BasisQuantity,
    string BasisNote,
    decimal Amount,
    string Currency,
    int AmountDecimals,
    string AmountText,
    int Status,
    string StatusText,
    bool IsDraft,
    bool IsRecorded,
    bool IsVoided,
    DateTime? RecordedAt,
    string RecordedBy,
    DateTime? VoidedAt,
    string VoidReason,
    string Remark,
    bool SourceAvailable,
    string SourceAvailabilityText,
    string AmountRuleText);

/// <summary>
/// 代理服务费对账单证据（ERP-070，读取用）：对账单身份 / 客户与协议快照 / 币种 / 对账日期 / 可选到期日 /
/// 服务期间 / **服务端计算**的合计 / 状态与登记、作废留痕 / 有界行清单。
/// <para><see cref="DueDateText"/>、<see cref="ServicePeriodText"/>、<see cref="CurrencyCompatibilityText"/>、
/// <see cref="SourceLinkRuleText"/>、<see cref="AmountRuleText"/>、<see cref="UniquenessRuleText"/>、
/// <see cref="SeparationText"/> 与 <see cref="BoundaryText"/> 由服务端生成、与前端同源文案。</para>
/// </summary>
public sealed record AgencyServiceFeeStatementDto(
    long Id,
    string StatementNo,
    string IdentityText,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    bool CustomerAvailable,
    string CustomerAvailabilityText,
    string Currency,
    int AmountDecimals,
    DateTime StatementDate,
    DateTime? DueDate,
    string DueDateText,
    DateTime ServicePeriodFrom,
    DateTime ServicePeriodTo,
    string ServicePeriodText,
    long AgreementId,
    string AgreementNo,
    string AgreementCurrency,
    string AgreementFeeMethod,
    string AgreementTermsText,
    bool AgreementAvailable,
    string AgreementAvailabilityText,
    decimal TotalAmount,
    string TotalAmountText,
    int LineCount,
    int Status,
    string StatusText,
    bool IsDraft,
    bool IsRecorded,
    bool IsVoided,
    DateTime? RecordedAt,
    string RecordedBy,
    DateTime? VoidedAt,
    string VoidReason,
    string Remark,
    List<AgencyServiceFeeStatementLineDto> Lines,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string SourceLinkRuleText,
    string AmountRuleText,
    string UniquenessRuleText,
    string SeparationText,
    string BoundaryText);

/// <summary>白名单选项（ERP-070：值 + 中文文案），用于来源类型 / 状态等元数据。</summary>
public sealed record AgencyServiceFeeStatementOptionDto(string Value, string Label);

/// <summary>
/// 可引用的**显式服务来源**候选（ERP-070，只读、**有界**）：来源身份 / 日期 / 状态 / 客户 / 币种，
/// 以及服务端的资格判定文案。
/// <para>刻意**不回显来源单据金额**：来源金额不是费用依据，服务端不会把来源金额、协议费率或相似度
/// 转换成行金额（避免任何「按相似度猜费用」的暗示）。</para>
/// </summary>
public sealed record AgencyServiceFeeStatementSourceOptionDto(
    string SourceType,
    string SourceTypeText,
    long SourceId,
    string SourceNo,
    DateTime SourceDate,
    int SourceStatus,
    string SourceStatusText,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    string Currency,
    bool Eligible,
    string EligibilityText);

/// <summary>
/// 代理服务费对账单证据模块元数据（ERP-070，只读）：白名单与边界、有界额度、以及接口 / 界面 / 文档同源的口径文案。
/// </summary>
public sealed record AgencyServiceFeeStatementMetadataDto(
    List<AgencyServiceFeeStatementOptionDto> SourceTypes,
    List<AgencyServiceFeeStatementOptionDto> StatusOptions,
    List<string> SupportedCurrencies,
    int MaxLinesPerStatement,
    int MaxSourceOptions,
    int MaxPageSize,
    string SourceLinkRuleText,
    string AmountRuleText,
    string DueDateRuleText,
    string UniquenessRuleText,
    string SeparationText,
    string BoundaryText);
