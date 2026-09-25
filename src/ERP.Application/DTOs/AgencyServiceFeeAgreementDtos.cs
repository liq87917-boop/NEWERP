namespace ERP.Application.DTOs;

/// <summary>
/// 代理服务费协议证据登记 / 修改请求（ERP-069）。
/// <para>客户端只能提交**显式商业条款**：协议号 / 客户 / 生效日期区间 / 币种 / 计费方式 / 费率或固定金额 /
/// 计费依据说明 / 备注；规范化协议号、客户编码与名称快照、状态、登记人（按已认证身份写入）与时间戳
/// 一律由服务端权威写入，客户端提交的同名字段（若有）不被采信。</para>
/// <para>边界：本请求<strong>不</strong>包含也不允许任何「不披露 / 账外 / 隐匿佣金」字段；服务端不会把费率、固定金额或
/// 计费依据从业务员提成设置、客户主数据比例、历史订单、自由文本或金额相似度推断出来。</para>
/// </summary>
public sealed class AgencyServiceFeeAgreementSaveDto
{
    /// <summary>协议号（必填；按真实协议填写，最长 50 个字符）</summary>
    public string AgreementNo { get; set; } = string.Empty;

    /// <summary>客户 Id（必须存在、未删除且启用；停用 / 删除的客户一律拒绝）</summary>
    public long CustomerId { get; set; }

    /// <summary>生效起始日期（必填；留空按当天，服务端不按订单或参数推算）</summary>
    public DateTime? EffectiveFrom { get; set; }

    /// <summary>生效结束日期（可空 = 无固定结束日 / 长期有效；填写时不得早于生效起始日期）</summary>
    public DateTime? EffectiveTo { get; set; }

    /// <summary>币种（必须来自系统币种口径；固定金额以该币种原币记录，不做汇率换算）</summary>
    public string Currency { get; set; } = "CNY";

    /// <summary>披露的计费方式（比例费率 / 固定金额；未知取值一律拒绝）</summary>
    public string FeeMethod { get; set; } = "比例费率";

    /// <summary>费率百分比（比例费率时必填且 (0, 100]；固定金额时必须为空或 0，不得两种口径同时填写）</summary>
    public decimal? RatePercent { get; set; }

    /// <summary>固定金额（固定金额时必填且按币种精度取整后大于 0；比例费率时必须为空或 0）</summary>
    public decimal? FixedAmount { get; set; }

    /// <summary>计费依据说明（必填、≤200 字；仅作为人工留痕，服务端不据此推断金额）</summary>
    public string FeeBasis { get; set; } = string.Empty;

    /// <summary>备注（≤500 字）</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>作废代理服务费协议证据请求（ERP-069）：必须显式填写原因，作废保留历史而不是删除。</summary>
public sealed class AgencyServiceFeeAgreementVoidRequest
{
    /// <summary>作废原因（必填；用于解释更正原因，写入历史留痕）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>代理服务费协议证据台账查询参数（ERP-069，全部为可选过滤；结果分页返回）</summary>
public sealed class AgencyServiceFeeAgreementQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多条协议证据）</summary>
    public const int MaxPageSize = 200;

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>客户筛选（留空 = 全部客户；不同客户绝不合并汇总）</summary>
    public long? CustomerId { get; set; }

    /// <summary>状态筛选（0 草稿 / 1 已登记 / 2 已作废；留空 = 全部，含已作废历史）</summary>
    public int? Status { get; set; }

    /// <summary>币种筛选（留空 = 全部币种；不同币种分别成行，绝不合并为一个金额）</summary>
    public string? Currency { get; set; }

    /// <summary>计费方式筛选（比例费率 / 固定金额；留空 = 全部）</summary>
    public string? FeeMethod { get; set; }

    /// <summary>生效起始日期开始（含当天；留空 = 不限）</summary>
    public DateTime? EffectiveFromFrom { get; set; }

    /// <summary>生效起始日期结束（含当天；留空 = 不限）</summary>
    public DateTime? EffectiveFromTo { get; set; }

    /// <summary>关键字（匹配协议号 / 客户名称 / 客户编码 / 计费依据说明；留空 = 不过滤）</summary>
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
/// 代理服务费协议证据（ERP-069，读取用）：协议身份 / 客户快照 / 生效区间 / 币种 / 披露的计费方式与显式条款 /
/// 状态与登记、作废留痕。
/// <para><see cref="FeeTermsText"/>、<see cref="EffectiveRangeText"/>、<see cref="BoundaryText"/> 与
/// <see cref="CommissionSeparationText"/> 由服务端生成、与前端同源文案；所有金额均为**用户显式提供的证据**，
/// 不是系统计算结果，也不是应收 / 应付余额、发票金额或付款授权。</para>
/// </summary>
public sealed record AgencyServiceFeeAgreementDto(
    long Id,
    string AgreementNo,
    string IdentityText,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    bool CustomerAvailable,
    string CustomerAvailabilityText,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    string EffectiveRangeText,
    string Currency,
    int AmountDecimals,
    string FeeMethod,
    decimal RatePercent,
    decimal FixedAmount,
    string FeeTermsText,
    string FeeBasis,
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
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string FeeTermsRuleText,
    string CommissionSeparationText,
    string BoundaryText);
