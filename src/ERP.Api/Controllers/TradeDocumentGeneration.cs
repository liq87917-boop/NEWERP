using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 单证中心（Trade Document Center）「由来源单据生成 / 带入预填」共用逻辑（ERP-019）。
/// 业务链：销售订单 / 装柜清单 → 单证中心台账（报关单 · 装箱单 · 商业发票 · 产地证 · 提单 …）。
/// </summary>
/// <remarks>
/// 设计口径：
/// 1) 只使用单证台账**既有字段**（<c>DocNo / DocType / IssueDate / DeclareNo / RefNo / SalesOrderNo</c>、
///    客户与金额币种、起运港 / 目的港、制作人与份数、状态、附件说明、备注），不新增任何数据库结构；
///    来源留痕写入既有字段：销售订单 → <c>SalesOrderNo</c> + 备注「来源：销售订单 …」，
///    装柜清单 → <c>RefNo</c>（柜号）+ 备注「来源：装柜清单 …」。
/// 2) 「带入预填」返回**未落库**草稿（不占用单证编号流水、不写库），由前端在单证中心新增表单里继续编辑后再保存，
///    因此生成后的单证仍可人工修改（状态为「待制作」），人工修改不影响来源留痕；
/// 3) 「直接生成」在服务端一次落库，并以来源字段守卫**重复生成**（同一来源 + 同一单证类型只允许一张），
///    只新增单证、绝不更新 / 覆盖既有单证；
/// 4) 单证编号按「类型前缀-来源单号」确定性生成（如 <c>CI-SO202609230001</c>），与历史单证冲突时自动追加序号，
///    保证既有人可读性又满足 <c>DocNo</c> 唯一性。
/// </remarks>
public static class TradeDocumentGeneration
{
    /// <summary>来源单据类型：销售订单</summary>
    public const string SalesOrderSourceType = "SalesOrder";

    /// <summary>来源单据类型：装柜清单</summary>
    public const string LoadingListSourceType = "LoadingList";

    /// <summary>销售订单可生成的单证类型（顺序即对话框展示顺序）</summary>
    public static readonly IReadOnlyList<string> SalesOrderDocTypes =
        new[] { "商业发票", "装箱单", "报关单", "产地证", "提单" };

    /// <summary>装柜清单可生成的单证类型（顺序即对话框展示顺序）</summary>
    public static readonly IReadOnlyList<string> LoadingListDocTypes =
        new[] { "装箱单", "提单", "报关单", "订舱确认" };

    /// <summary>销售订单默认勾选的单证类型（出口必备的商业发票 + 装箱单）</summary>
    private static readonly IReadOnlyList<string> SalesOrderDefaultDocTypes = new[] { "商业发票", "装箱单" };

    /// <summary>装柜清单默认勾选的单证类型（装柜后第一份就是装箱单）</summary>
    private static readonly IReadOnlyList<string> LoadingListDefaultDocTypes = new[] { "装箱单" };

    /// <summary>单证编号前缀（按单证类型，便于人工一眼辨识；未知类型用 TD）</summary>
    private static readonly Dictionary<string, string> DocNoPrefixes = new()
    {
        ["报关单"] = "CD",
        ["装箱单"] = "PL",
        ["商业发票"] = "CI",
        ["形式发票"] = "PI",
        ["产地证"] = "CO",
        ["提单"] = "BL",
        ["订舱确认"] = "BK",
        ["外汇核销单"] = "VR",
        ["其他"] = "TD",
    };

    /// <summary>按单证类型的默认「制作人 / 出证机构」与「份数」（生成后可人工修改）</summary>
    private static readonly Dictionary<string, (string IssuedBy, int Copies)> DocTypeDefaults = new()
    {
        ["报关单"] = ("报关行", 1),
        ["装箱单"] = (string.Empty, 3),
        ["商业发票"] = (string.Empty, 3),
        ["形式发票"] = (string.Empty, 3),
        ["产地证"] = ("贸促会 / 海关", 2),
        ["提单"] = ("船公司", 3),
        ["订舱确认"] = ("货代", 1),
        ["外汇核销单"] = (string.Empty, 1),
        ["其他"] = (string.Empty, 1),
    };

    /// <summary>单证初始状态：待制作（人工确认后才流转为已制作 / 已提交客户 / 已使用）</summary>
    private const string DraftStatus = "待制作";

    /// <summary>备注来源留痕前缀（重复生成守卫的判定依据之一，修改文案需同步）</summary>
    public const string SalesOrderRemarkPrefix = "来源：销售订单 ";

    /// <summary>备注来源留痕前缀（重复生成守卫的判定依据之一，修改文案需同步）</summary>
    public const string LoadingListRemarkPrefix = "来源：装柜清单 ";

    /// <summary>来源单据类型是否受支持</summary>
    public static bool IsSupportedSource(string? sourceType)
        => sourceType is SalesOrderSourceType or LoadingListSourceType;

    /// <summary>来源单据类型对应的可生成单证类型</summary>
    public static IReadOnlyList<string> SupportedDocTypes(string sourceType) => sourceType switch
    {
        SalesOrderSourceType => SalesOrderDocTypes,
        LoadingListSourceType => LoadingListDocTypes,
        _ => Array.Empty<string>()
    };

    /// <summary>来源单据类型对应的默认单证类型</summary>
    public static IReadOnlyList<string> DefaultDocTypes(string sourceType) => sourceType switch
    {
        SalesOrderSourceType => SalesOrderDefaultDocTypes,
        LoadingListSourceType => LoadingListDefaultDocTypes,
        _ => Array.Empty<string>()
    };

    /// <summary>来源单据的业务名称（错误提示文案）</summary>
    public static string SourceLabel(string sourceType)
        => sourceType == SalesOrderSourceType ? "销售订单" : "装柜清单";

    // ==================== 单证草稿构造（映射） ====================

    /// <summary>
    /// 按销售订单构造单证草稿（未落库）：客户以档案为准，金额取订单总额，
    /// 目的港按「订单文本 → 客户档案」回退；起运港订单无对应字段，留空由人工填写。
    /// </summary>
    public static TradeDocument BuildFromSalesOrder(SalesOrder order, BaseCustomer? customer, string docType)
    {
        var (issuedBy, copies) = DocTypeDefaultsOf(docType);
        return new TradeDocument
        {
            DocNo = BuildDocNo(docType, order.OrderNo),
            DocType = docType,
            IssueDate = DateTime.Today,
            SalesOrderNo = Clamp(order.OrderNo, 50),
            RefNo = string.Empty,                       // 柜号 / 订舱号：装柜后人工回填
            DeclareNo = string.Empty,                   // 报关单号：报关后人工回填
            CustomerId = order.CustomerId > 0 ? order.CustomerId : null,
            CustomerName = Clamp(customer?.CustomerName, 200),
            Amount = order.TotalAmount,
            Currency = CurrencyOf(order.Currency.ToString(), customer),
            DeparturePort = string.Empty,
            DestinationPort = Prefer(order.DestinationPort, customer?.DestinationPort, 100),
            IssuedBy = issuedBy,
            Copies = copies,
            Status = DraftStatus,
            FileNote = string.Empty,
            Remark = BuildSalesOrderRemark(order),
            CreatedAt = DateTime.Now
        };
    }

    /// <summary>
    /// 按装柜清单构造单证草稿（未落库）：柜号写入「关联柜号 / 订舱号」，
    /// 港口按「预装柜单 → 订柜信息 → 客户档案目的港」回退（装柜清单自身无港口字段），
    /// 箱数 / 毛重 / 体积与唛头写入备注便于报关与清关核对；金额装柜阶段尚未结算，留 0 由人工填写。
    /// </summary>
    public static TradeDocument BuildFromLoadingList(ContainerLoadingList list, BaseCustomer? customer,
        ContainerBooking? booking, string docType)
    {
        var (issuedBy, copies) = DocTypeDefaultsOf(docType);
        return new TradeDocument
        {
            DocNo = BuildDocNo(docType, list.LoadingListNo),
            DocType = docType,
            IssueDate = list.LoadingDate == default ? DateTime.Today : list.LoadingDate.Date,
            SalesOrderNo = string.Empty,                // 装柜清单未关联销售订单号，留空
            RefNo = Clamp(list.ContainerNo, 50),
            DeclareNo = string.Empty,
            CustomerId = list.CustomerId > 0 ? list.CustomerId : null,
            CustomerName = Clamp(customer?.CustomerName, 200),
            Amount = 0m,
            Currency = CurrencyOf(customer?.Currency, customer),
            DeparturePort = Clamp(booking?.DeparturePort, 100),
            DestinationPort = Prefer(booking?.DestinationPort, customer?.DestinationPort, 100),
            IssuedBy = issuedBy,
            Copies = copies,
            Status = DraftStatus,
            FileNote = string.Empty,
            Remark = BuildLoadingListRemark(list),
            CreatedAt = DateTime.Now
        };
    }

    /// <summary>销售订单来源备注：固定以「来源：销售订单 {订单号}」开头（重复生成守卫据此判定）</summary>
    private static string BuildSalesOrderRemark(SalesOrder order)
    {
        var parts = new List<string> { $"{SalesOrderRemarkPrefix}{order.OrderNo}" };
        if (!string.IsNullOrWhiteSpace(order.CustomerPoNo)) parts.Add($"客户 PO：{order.CustomerPoNo}");
        if (!string.IsNullOrWhiteSpace(order.ContractNo)) parts.Add($"合同号：{order.ContractNo}");
        if (!string.IsNullOrWhiteSpace(order.TradeTerms)) parts.Add($"贸易条款：{order.TradeTerms}");
        if (!string.IsNullOrWhiteSpace(order.DestinationPort)) parts.Add($"目的港：{order.DestinationPort}");
        if (order.DeliveryDate.HasValue) parts.Add($"交货日期：{order.DeliveryDate.Value:yyyy-MM-dd}");
        if (order.DepositRatio > 0) parts.Add($"定金比例：{order.DepositRatio:0.####}%");
        return Clamp(string.Join(" ｜ ", parts), 500);
    }

    /// <summary>装柜清单来源备注：固定以「来源：装柜清单 {装柜清单号}」开头（重复生成守卫据此判定）</summary>
    private static string BuildLoadingListRemark(ContainerLoadingList list)
    {
        var parts = new List<string> { $"{LoadingListRemarkPrefix}{list.LoadingListNo}" };
        if (!string.IsNullOrWhiteSpace(list.ContainerNo)) parts.Add($"柜号：{list.ContainerNo}");
        if (!string.IsNullOrWhiteSpace(list.ShippingMark))
            parts.Add($"唛头：{list.ShippingMark.Replace("\r", " ").Replace("\n", " ").Trim()}");
        parts.Add($"箱数：{list.TotalCartons:0.####} ｜ 毛重：{list.TotalWeight:0.####}kg ｜ 体积：{list.TotalVolume:0.####}m³");
        if (list.LoadingDate != default) parts.Add($"装柜日期：{list.LoadingDate:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(list.Remark)) parts.Add($"装柜备注：{list.Remark}");
        return Clamp(string.Join(" ｜ ", parts), 500);
    }

    // ==================== 带入预填 / 直接生成 ====================

    /// <summary>
    /// 带入预填：返回未落库的单证草稿集合（每种可生成类型一份）+ 该来源**已生成过**的单证类型，
    /// 前端据此预览映射结果、置灰已生成类型，并把选中的草稿带入单证中心新增表单。
    /// 本方法不写库、不占用单证编号流水，重复生成只在落库接口上拦截。
    /// </summary>
    public static async Task<TradeDocPrefillResult> PrefillAsync(IErpDbContext db, string sourceType, long sourceId,
        string sourceNo, string? containerNo, string? salesOrderNo, string? loadingListNo,
        List<TradeDocument> drafts, CancellationToken ct = default)
    {
        var generated = new List<string>();
        foreach (var draft in drafts)
        {
            if (await FindExistingAsync(db, salesOrderNo, containerNo, loadingListNo, draft.DocType, ct) is not null)
                generated.Add(draft.DocType);
        }

        return new TradeDocPrefillResult
        {
            SourceType = sourceType,
            SourceId = sourceId,
            SourceNo = sourceNo,
            SourceRefNo = containerNo ?? string.Empty,
            Documents = drafts,
            GeneratedDocTypes = generated,
            DefaultDocTypes = DefaultDocTypes(sourceType).ToList(),
            SupportedDocTypes = SupportedDocTypes(sourceType).ToList()
        };
    }

    /// <summary>
    /// 直接生成：按请求的单证类型逐个落库（不传类型时使用来源默认类型）。
    /// 守卫顺序：来源合法性 → 未选择类型 / 类型不受支持 → 重复生成（同一来源 + 同一类型）；
    /// 任一类型已生成即整体拒绝（不产生半成品数据），成功后返回新建单证的 Id / 编号 / 类型。
    /// </summary>
    /// <exception cref="BusinessException">
    /// 重复生成（错误码 RuleConflict）；未选择类型、类型不受支持（错误码 InvalidParameter）。
    /// </exception>
    public static async Task<TradeDocGenerateResult> GenerateAsync(IErpDbContext db, string sourceType, long sourceId,
        string sourceNo, string? containerNo, string? salesOrderNo, string? loadingListNo,
        IReadOnlyList<string>? requestedDocTypes, Func<string, TradeDocument> buildDraft,
        CancellationToken ct = default)
    {
        var docTypes = NormalizeDocTypes(sourceType, requestedDocTypes);

        var conflicts = new List<string>();
        foreach (var docType in docTypes)
        {
            var existing = await FindExistingAsync(db, salesOrderNo, containerNo, loadingListNo, docType, ct);
            if (existing is not null) conflicts.Add($"「{docType}」（单证编号 {existing.DocNo}）");
        }
        if (conflicts.Count > 0)
            throw BusinessException.RuleConflict(
                $"该{SourceLabel(sourceType)}已生成 {string.Join("、", conflicts)}，不能重复生成；" +
                "如需多份请调整既有单证的「份数」，如需其他单证请另选类型");

        var created = new List<TradeDocument>();
        foreach (var docType in docTypes)
        {
            var doc = buildDraft(docType);
            doc.Status = DraftStatus;
            doc.CreatedAt = DateTime.Now;
            await EnsureUniqueDocNoAsync(db, doc, ct);
            db.TradeDocuments.Add(doc);
            created.Add(doc);
        }
        await db.SaveChangesAsync(ct);

        return new TradeDocGenerateResult
        {
            SourceType = sourceType,
            SourceId = sourceId,
            SourceNo = sourceNo,
            Documents = created.Select(d => new TradeDocGeneratedItem
            {
                Id = d.Id,
                DocNo = d.DocNo,
                DocType = d.DocType,
                Status = d.Status
            }).ToList()
        };
    }

    /// <summary>
    /// 重复生成守卫的判定口径：
    /// 销售订单来源 → 同一 <c>SalesOrderNo</c> + 同一单证类型；
    /// 装柜清单来源 → 同一柜号（<c>RefNo</c>：一柜一类单证只应有一份）**或**备注留痕指向同一装柜清单号，
    /// 两者任一命中即视为已生成（换单重录同一柜号同样不允许重复建单）。
    /// 已软删除的单证不参与判定（删除后可重新生成）。
    /// </summary>
    public static async Task<TradeDocument?> FindExistingAsync(IErpDbContext db, string? salesOrderNo,
        string? containerNo, string? loadingListNo, string docType, CancellationToken ct = default)
    {
        var query = db.TradeDocuments.AsNoTracking().Where(d => !d.IsDeleted && d.DocType == docType);

        if (!string.IsNullOrWhiteSpace(salesOrderNo))
            return await query.Where(d => d.SalesOrderNo == salesOrderNo)
                .OrderBy(d => d.Id).FirstOrDefaultAsync(ct);

        var hasContainer = !string.IsNullOrWhiteSpace(containerNo);
        var hasLoadingList = !string.IsNullOrWhiteSpace(loadingListNo);
        if (hasContainer && hasLoadingList)
        {
            var marker = LoadingListRemarkPrefix + loadingListNo;
            return await query.Where(d => d.RefNo == containerNo || d.Remark.Contains(marker))
                .OrderBy(d => d.Id).FirstOrDefaultAsync(ct);
        }
        if (hasContainer)
            return await query.Where(d => d.RefNo == containerNo)
                .OrderBy(d => d.Id).FirstOrDefaultAsync(ct);
        if (hasLoadingList)
        {
            var marker = LoadingListRemarkPrefix + loadingListNo;
            return await query.Where(d => d.Remark.Contains(marker))
                .OrderBy(d => d.Id).FirstOrDefaultAsync(ct);
        }
        return null;
    }

    /// <summary>请求类型归一化：空 → 来源默认类型；去重保序；不受支持的类型直接拒绝</summary>
    private static List<string> NormalizeDocTypes(string sourceType, IReadOnlyList<string>? requested)
    {
        var supported = SupportedDocTypes(sourceType);
        if (supported.Count == 0) throw BusinessException.InvalidParameter($"不支持的来源单据类型：{sourceType}");

        var source = requested is { Count: > 0 } ? requested : DefaultDocTypes(sourceType);
        var result = new List<string>();
        foreach (var raw in source)
        {
            var docType = (raw ?? string.Empty).Trim();
            if (docType.Length == 0) continue;
            if (!supported.Contains(docType))
                throw BusinessException.InvalidParameter(
                    $"不支持的单证类型：{docType}（{SourceLabel(sourceType)}可生成：{string.Join("、", supported)}）");
            if (!result.Contains(docType)) result.Add(docType);
        }

        if (result.Count == 0) throw BusinessException.InvalidParameter("请至少选择一种单证类型");
        return result;
    }

    /// <summary>单证编号：类型前缀-来源单号（来源单号超长时截断，保证不超过单证编号列长）</summary>
    private static string BuildDocNo(string docType, string sourceNo)
    {
        var prefix = DocNoPrefixes.GetValueOrDefault(docType, "TD");
        var body = Clamp(sourceNo, 50 - prefix.Length - 2);
        return $"{prefix}-{body}";
    }

    /// <summary>
    /// 保证单证编号唯一：与既有单证（含历史人工单据）冲突时追加 -2 / -3 …（最多尝试 200 次）。
    /// 单证台账为人工维护台账，历史数据可能使用任意编号，因此必须做冲突回退而不是直接写入。
    /// </summary>
    private static async Task EnsureUniqueDocNoAsync(IErpDbContext db, TradeDocument doc, CancellationToken ct)
    {
        var baseNo = Clamp(doc.DocNo, 45);
        var candidate = baseNo;
        for (var attempt = 2; attempt <= 200; attempt++)
        {
            var exists = await db.TradeDocuments.AnyAsync(d => !d.IsDeleted && d.DocNo == candidate, ct);
            if (!exists) { doc.DocNo = candidate; return; }
            candidate = $"{baseNo}-{attempt}";
        }
        throw BusinessException.RuleConflict($"单证编号 {baseNo} 已被占用，请人工填写单证编号");
    }

    /// <summary>按单证类型的默认制作人 / 份数（未知类型按空制作人 + 1 份）</summary>
    private static (string IssuedBy, int Copies) DocTypeDefaultsOf(string docType)
        => DocTypeDefaults.GetValueOrDefault(docType, (string.Empty, 1));

    /// <summary>币种：主值优先，为空回退客户档案币种，仍为空时按 USD（单证台账默认币种）</summary>
    private static string CurrencyOf(string? primary, BaseCustomer? customer)
    {
        var text = primary;
        if (string.IsNullOrWhiteSpace(text)) text = customer?.Currency;
        return string.IsNullOrWhiteSpace(text) ? "USD" : Clamp(text.Trim(), 20);
    }

    /// <summary>优先取主值，主值为空白时回退默认值，最后按目标列长度截断</summary>
    private static string Prefer(string? primary, string? fallback, int maxLength)
        => Clamp(string.IsNullOrWhiteSpace(primary) ? fallback : primary, maxLength);

    /// <summary>按目标列长度截断（来源字段列长与本表不一致时避免超长写入失败）</summary>
    private static string Clamp(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    // ==================== 来源数据装载 ====================

    /// <summary>装载客户档案（客户 Id 为空 / 0 时返回 null，生成结果使用安全空值）</summary>
    public static async Task<BaseCustomer?> LoadCustomerAsync(IErpDbContext db, long? customerId,
        CancellationToken ct = default)
        => customerId is null or <= 0
            ? null
            : await db.BaseCustomers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == customerId.Value && !c.IsDeleted, ct);

    /// <summary>
    /// 装载装柜清单对应的订柜信息（用于补起运港 / 目的港）：装柜清单 → 预装柜单 → 订柜信息；
    /// 任一环节缺失时返回 null，港口留空由人工填写。
    /// </summary>
    public static async Task<ContainerBooking?> LoadBookingAsync(IErpDbContext db, ContainerLoadingList list,
        CancellationToken ct = default)
    {
        if (list.PreLoadingId is null or <= 0) return null;

        var preLoading = await db.ContainerPreLoadings.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == list.PreLoadingId.Value && !p.IsDeleted, ct);
        if (preLoading?.BookingId is null or <= 0) return null;

        return await db.ContainerBookings.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == preLoading.BookingId.Value && !b.IsDeleted, ct);
    }
}

/// <summary>带入预填响应：来源单据信息 + 未落库单证草稿 + 已生成类型（前端据此置灰并提示）</summary>
public sealed class TradeDocPrefillResult
{
    /// <summary>来源单据类型（SalesOrder / LoadingList）</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>来源单据 Id</summary>
    public long SourceId { get; set; }

    /// <summary>来源单据号（销售订单号 / 装柜清单号）</summary>
    public string SourceNo { get; set; } = string.Empty;

    /// <summary>来源业务参考号（装柜清单来源时为柜号，销售订单来源时为空）</summary>
    public string SourceRefNo { get; set; } = string.Empty;

    /// <summary>带入后的单证草稿（未落库，与「直接生成」同一套映射）</summary>
    public List<TradeDocument> Documents { get; set; } = new();

    /// <summary>该来源已生成过的单证类型（重复生成守卫的预览结果，前端置灰不可再选）</summary>
    public List<string> GeneratedDocTypes { get; set; } = new();

    /// <summary>该来源默认勾选的单证类型</summary>
    public List<string> DefaultDocTypes { get; set; } = new();

    /// <summary>该来源支持的全部单证类型</summary>
    public List<string> SupportedDocTypes { get; set; } = new();
}

/// <summary>生成请求：需要生成的单证类型（为空时使用来源默认类型）</summary>
public sealed class TradeDocGenerateRequest
{
    /// <summary>单证类型列表（如 商业发票 / 装箱单 / 报关单 …）</summary>
    public List<string> DocTypes { get; set; } = new();
}

/// <summary>生成结果：来源信息 + 新建单证清单</summary>
public sealed class TradeDocGenerateResult
{
    /// <summary>来源单据类型（SalesOrder / LoadingList）</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>来源单据 Id</summary>
    public long SourceId { get; set; }

    /// <summary>来源单据号（销售订单号 / 装柜清单号）</summary>
    public string SourceNo { get; set; } = string.Empty;

    /// <summary>新建单证清单</summary>
    public List<TradeDocGeneratedItem> Documents { get; set; } = new();
}

/// <summary>新建单证的简要信息（Id / 编号 / 类型 / 状态）</summary>
public sealed class TradeDocGeneratedItem
{
    /// <summary>单证 Id</summary>
    public long Id { get; set; }

    /// <summary>单证编号</summary>
    public string DocNo { get; set; } = string.Empty;

    /// <summary>单证类型</summary>
    public string DocType { get; set; } = string.Empty;

    /// <summary>单证状态（新生成一律为「待制作」）</summary>
    public string Status { get; set; } = string.Empty;
}
