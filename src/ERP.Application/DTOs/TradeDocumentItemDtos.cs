namespace ERP.Application.DTOs;

/// <summary>
/// 单证明细行保存请求（ERP-051，新增与修改共用）。
/// <para>客户端只能提交「行内容」：可选的商品资料引用、商品编码 / 中英文名称 / 规格 / 数量 / 单位 /
/// 单价 / 箱数 / 净重 / 毛重 / 行序 / 备注。</para>
/// <para><strong>不</strong>接受客户端提交的金额、币种、单证 Id 与任何快照派生值：行金额一律服务端按
/// 数量 × 单价与币种精度重算，币种取自单证台账；这些字段即使被提交也会被忽略（不做信任）。</para>
/// </summary>
public sealed class TradeDocumentItemSaveDto
{
    /// <summary>
    /// 引用的商品资料 Id（可选；大于 0 时必须是存在且未删除的商品，服务端按商品资料写入
    /// 编码 / 中英文名称 / 规格 / 单位快照；0 或缺省 = 人工录入的纯文本行）。
    /// </summary>
    public long ProductId { get; set; }

    /// <summary>商品编码（未引用商品资料时的人工录入值；与商品名称至少填写一项）</summary>
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>商品中文名称（未引用商品资料时的人工录入值；与商品编码至少填写一项）</summary>
    public string ProductNameCn { get; set; } = string.Empty;

    /// <summary>商品英文名称（报关口径人工录入值，可为空）</summary>
    public string ProductNameEn { get; set; } = string.Empty;

    /// <summary>规格型号（人工录入值，可为空）</summary>
    public string Spec { get; set; } = string.Empty;

    /// <summary>数量（必须大于 0，最多 4 位小数）</summary>
    public decimal Quantity { get; set; }

    /// <summary>单位（可为空；引用商品资料时由服务端取商品资料单位）</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>单价（原币，最多 4 位小数；装箱单行必须为 0 —— 装箱单不含价格口径）</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>箱数（装箱单行可选；负数一律拒绝；未登记留空而不是 0）</summary>
    public int? PackageCount { get; set; }

    /// <summary>净重 kg（装箱单行可选；负数一律拒绝；未登记留空而不是 0）</summary>
    public decimal? NetWeight { get; set; }

    /// <summary>毛重 kg（装箱单行可选；填写时不得小于净重）</summary>
    public decimal? GrossWeight { get; set; }

    /// <summary>
    /// 行序（可选）：留空由服务端追加到末尾（既有最大行序 + 1）；显式指定时必须大于 0，
    /// 且与同一单证内其他未删除行不重复（重复一律拒绝，不做静默重排）。
    /// </summary>
    public int? LineOrder { get; set; }

    /// <summary>行备注（可选，有界；只作为说明文本，不参与任何金额派生）</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 单证明细行（ERP-051，读取用）：行快照 + 只读派生字段（币种精度、商品引用可用性）。
/// <para><see cref="LineAmount"/> 为服务端计算值；<see cref="ProductAvailable"/> 只是**读取标注**：
/// 商品资料之后被停用 / 删除时历史快照照常可读，本行不会自动刷新。</para>
/// </summary>
public sealed record TradeDocumentItemDto(
    long Id,
    long TradeDocumentId,
    string DocNo,
    string DocType,
    int LineNo,
    long ProductId,
    string ProductCode,
    string ProductNameCn,
    string ProductNameEn,
    string Spec,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    string Currency,
    int AmountDecimals,
    decimal LineAmount,
    int? PackageCount,
    decimal? NetWeight,
    decimal? GrossWeight,
    string Remark,
    bool ProductAvailable,
    string ProductAvailabilityText,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>
/// 单证明细行清单 + 行合计 + 与单证表头的**显式差异提示**（ERP-051，只读派生）。
/// <para><see cref="LineAmountTotal"/> 只按本单证明细行的**服务端计算金额**合计；<see cref="AmountMismatch"/>
/// 只是提示「行合计与单证台账金额不一致」，**绝不**改写单证表头金额（单证金额仍由人工在单证中心维护）。</para>
/// <para>箱数 / 净重 / 毛重合计**只**累加已登记该值的行，并同时给出登记行数：没有行登记时返回 <c>null</c>，
/// 绝不把「未登记」当成 0。</para>
/// <para>单证类型不受支持（非商业发票 / 装箱单）或状态已冻结时，<see cref="Editable"/> 为 false，
/// 明细行只读，接口会明确说明原因。</para>
/// </summary>
public sealed record TradeDocumentItemListDto(
    long TradeDocumentId,
    string DocNo,
    string DocType,
    string Status,
    bool DocTypeSupported,
    string DocTypeText,
    bool Editable,
    string EditabilityText,
    string Currency,
    int AmountDecimals,
    decimal HeaderAmount,
    bool HeaderAmountRecorded,
    int LineCount,
    bool Truncated,
    int Take,
    decimal LineAmountTotal,
    bool AmountMismatch,
    string AmountMismatchText,
    int PackageCountRecordedLines,
    int? PackageCountTotal,
    int WeightRecordedLines,
    decimal? NetWeightTotal,
    decimal? GrossWeightTotal,
    string RuleText,
    string BoundaryText,
    List<TradeDocumentItemDto> Items);
