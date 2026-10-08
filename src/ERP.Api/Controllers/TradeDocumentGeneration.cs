using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
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

    /// <summary>
    /// ERP-394：生成目标（单证草稿）必须归属到范围内客户 —— 受限账号的草稿缺失权威客户归属（<c>CustomerId</c> 为空）
    /// 或归属越界一律 fail closed。<c>null</c> 范围（进程内直接调用）保持既有内部口径。
    /// <para>草稿的 <c>CustomerId</c> 只来自来源单据（销售订单 <c>CustomerId</c> / 装柜清单 <c>CustomerId</c>），
    /// 绝不按单号 / 柜号 / 客户名等自由文本推断，因此本守卫即「生成目标属于授权客户」的权威判定。</para>
    /// </summary>
    public static void EnsureGeneratedTargetAuthorized(SalespersonDataScope? scope, TradeDocument target)
    {
        ArgumentNullException.ThrowIfNull(target);
        TradeDocumentAuthorizationRules.EnsureStoredScopeAllowed(scope, target);
    }

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
    /// 带入预填：返回未落库的单证草稿集合（每种可生成类型一份）+ 该来源**已生成过**的单证类型 +
    /// 由来源明细构造的**明细行快照预览**（ERP-052），前端据此预览映射结果、置灰已生成类型，
    /// 并把选中的草稿带入单证中心新增表单。
    /// 本方法不写库、不占用单证编号流水，重复生成只在落库接口上拦截；
    /// 来源明细非法 / 超过有界行数时**同样拒绝**（与直接生成同一套校验），以免用户在落库时才发现问题。
    /// </summary>
    public static async Task<TradeDocPrefillResult> PrefillAsync(IErpDbContext db, string sourceType, long sourceId,
        string sourceNo, string? containerNo, string? salesOrderNo, string? loadingListNo,
        List<TradeDocument> drafts, Func<string, TradeDocumentLineSnapshotResult>? buildLines = null,
        SalespersonDataScope? targetScope = null, CancellationToken ct = default)
    {
        var generated = new List<string>();
        var previews = new List<TradeDocumentPrintLine>();
        var summaries = new List<string>();
        var evidences = new List<string>();
        var lineCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var draft in drafts)
        {
            // ERP-394：带入预填与直接生成同口径 —— 受限账号的来源 / 目标客户必须在范围内，
            // 绝不通过预填泄露范围外客户的名称 / 金额 / 明细快照。
            EnsureGeneratedTargetAuthorized(targetScope, draft);

            if (await FindExistingAsync(db, salesOrderNo, containerNo, loadingListNo, draft.DocType, ct) is not null)
                generated.Add(draft.DocType);

            // 明细行预览：只做只读投影（不落库），行序与落库时完全一致
            var built = buildLines?.Invoke(draft.DocType);
            if (built is null) continue;

            lineCounts[draft.DocType] = built.Lines.Count;
            foreach (var line in built.Lines)
                previews.Add(TradeDocumentPrintLine.From(line, draft.DocType, draft.Currency));

            if (built.SummaryText.Length > 0) summaries.Add(built.SummaryText);
            if (built.EvidenceText.Length > 0) evidences.Add(built.EvidenceText);
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
            SupportedDocTypes = SupportedDocTypes(sourceType).ToList(),
            LinePreviews = previews,
            LineCounts = lineCounts,
            LineSummaryText = string.Join(" ｜ ", summaries),
            LineEvidenceText = string.Join(" ｜ ", evidences),
            LineRuleText = TradeDocumentPrintSemantics.DetailRuleText,
        };
    }

    /// <summary>
    /// 直接生成：按请求的单证类型逐个构造单证草稿与**来源明细行快照**（ERP-052），随后在**同一事务**内
    /// 把单证表头与明细行一起落库（任一步失败整体回滚，不留半成品单证）。
    /// <para>守卫顺序：来源合法性 → 未选择类型 / 类型不受支持 → 来源明细校验（数量 / 单价 / 商品身份 / 有界行数）
    /// → 重复生成（同一来源 + 同一类型）；任一类型已生成即整体拒绝（不产生半成品数据）。</para>
    /// <para>生成后返回新建单证的 Id / 编号 / 类型 / 明细行数（可在单证中心继续人工维护明细行，
    /// 但绝不改写来源单据、商品资料与任何库存 / 财务记录）。</para>
    /// <para>本重载为**进程内 / 既有单元测试口径**（不加来源行锁，也不做锁内权威重读）；HTTP 生成入口一律走
    /// <see cref="GenerateAtomicAsync"/>：先对来源单据行加锁、锁内权威重读来源（状态 / 客户 / 金额 /
    /// 数量 / 单位）后，在同一原子事务内完成重复检测、编号预约、表头与明细行写入。</para>
    /// </summary>
    /// <exception cref="BusinessException">
    /// 重复生成、来源明细非法 / 超限（错误码 RuleConflict）；未选择类型、类型不受支持（错误码 InvalidParameter）。
    /// </exception>
    public static async Task<TradeDocGenerateResult> GenerateAsync(IErpDbContext db, string sourceType, long sourceId,
        string sourceNo, string? containerNo, string? salesOrderNo, string? loadingListNo,
        IReadOnlyList<string>? requestedDocTypes, Func<string, TradeDocument> buildDraft,
        Func<string, TradeDocumentLineSnapshotResult>? buildLines = null,
        SalespersonDataScope? targetScope = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var docTypes = NormalizeDocTypes(sourceType, requestedDocTypes);

        await EnsureNoConflictsAsync(db, sourceType, salesOrderNo, containerNo, loadingListNo, docTypes, ct);

        // 先把全部单证草稿与明细行快照在内存中构造并校验完成：来源明细非法 / 超限直接拒绝，不写任何半成品数据
        var created = new List<TradeDocument>();
        var lineResults = new List<TradeDocumentLineSnapshotResult>();
        foreach (var docType in docTypes)
        {
            var (doc, lines) = await BuildDocumentAsync(db, docType, buildDraft, buildLines, targetScope, ct);
            created.Add(doc);
            lineResults.Add(lines);
        }

        // 父单证与明细行快照在同一事务内写库（明细行需要父单证 Id，因此分两次 SaveChanges，但同一事务）：
        // 任一步失败都回滚，既不会留下没有明细的半成品单证，也不会留下孤儿明细行
        await PersistAsync(db, created, lineResults, ct);

        return BuildResult(sourceType, sourceId, sourceNo, created, lineResults);
    }

    /// <summary>
    /// HTTP 生成入口（ERP-397）：在**确定性来源行锁 + 同一原子事务**内完成
    /// 「锁内权威重读来源 → 重复生成检测 → 单证编号预约 → 表头与明细行构造 → 写入」。
    /// <para>锁序：来源单据行 → 新建单证行（<c>INSERT</c>，不取既有单证行锁），与 ERP-395 父单证行锁互不反向获取；
    /// 来源行锁与既有「来源取消 / 编辑 / 删除」共用同一把锁，因此同一来源的并发生成与来源失效不可能同时成功。</para>
    /// <para>锁内权威复核：来源状态（已作废 fail closed）、来源客户实时范围、表头金额 / 客户与明细行数量 / 单位
    /// 必须全部取自 <paramref name="reloadSource"/> 在锁内的重读结果；任一步失败整体回滚，
    /// 表头 / 明细行 / 已预约单证编号一起回滚（不留半成品单证、孤儿明细行或已占用编号）。</para>
    /// </summary>
    /// <param name="db">数据访问上下文（与既有生成同源）。</param>
    /// <param name="sourceType">来源单据类型（<see cref="SalesOrderSourceType"/> / <see cref="LoadingListSourceType"/>）。</param>
    /// <param name="sourceId">来源单据 Id（用于来源行锁；正数）。</param>
    /// <param name="requestedDocTypes">请求的单证类型（空 = 来源默认类型）。</param>
    /// <param name="reloadSource">锁内权威重读委托（返回 <c>null</c> = 来源已被并发删除）。</param>
    /// <param name="targetScope">当前账号实时客户数据范围（<c>null</c> = 进程内调用，保持既有内部口径）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="BusinessException">
    /// 来源不存在 / 已删除（<c>NotFound</c>）；来源已作废、重复生成、来源被并发改写、明细行非法 / 超限（<c>RuleConflict</c>）；
    /// 未选择类型 / 类型不受支持（<c>InvalidParameter</c>）；受限账号来源越界（<c>Forbidden</c>）。
    /// </exception>
    public static async Task<TradeDocGenerateResult> GenerateAtomicAsync(IErpDbContext db, string sourceType,
        long sourceId, IReadOnlyList<string>? requestedDocTypes,
        Func<CancellationToken, Task<TradeDocumentGenerationSource?>> reloadSource,
        SalespersonDataScope? targetScope = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(reloadSource);
        if (sourceId <= 0)
            throw BusinessException.InvalidParameter($"请指定{SourceLabel(sourceType)}");

        // 类型归一化（纯校验，不读库）：未选择类型 / 类型不受支持在加锁与任何写入之前 fail closed。
        var docTypes = NormalizeDocTypes(sourceType, requestedDocTypes);

        await using var transaction =
            await TradeDocumentGenerationMutationRules.BeginGenerationTransactionAsync(db, ct);
        try
        {
            // 1) 确定性来源行锁（与来源取消 / 编辑 / 删除共用同一把来源行锁；锁序：来源行先行）。
            if (!await TradeDocumentGenerationMutationRules.LockSourceRowAsync(db, sourceType, sourceId, ct))
                throw BusinessException.NotFound(TradeDocumentGenerationMutationRules.SourceMissingText);

            // 2) 锁内权威重读：状态 / 客户 / 金额 / 数量 / 单位全部以本次重读为准（绝不复用锁前快照）。
            var source = await reloadSource(ct)
                ?? throw BusinessException.NotFound(TradeDocumentGenerationMutationRules.SourceMissingText);
            TradeDocumentGenerationMutationRules.EnsureSourceStable(source, sourceType, sourceId);
            TradeDocumentGenerationMutationRules.EnsureSourceScopeAllowed(targetScope, source);

            // 3) 锁内重复生成检测：同一来源 + 同一单证类型只允许一套完整单证（既有冲突语义不变）。
            await EnsureNoConflictsAsync(db, sourceType, source.SalesOrderNo, source.ContainerNo,
                source.LoadingListNo, docTypes, ct);

            // 4) 表头 + 明细行构造（全部来自本次权威重读；编号预约在锁内完成），并在写入前复核一致性。
            var created = new List<TradeDocument>();
            var lineResults = new List<TradeDocumentLineSnapshotResult>();
            foreach (var docType in docTypes)
            {
                var (doc, lines) = await BuildDocumentAsync(db, docType, source.BuildDraft, source.BuildLines,
                    targetScope, ct);
                TradeDocumentGenerationMutationRules.EnsureDraftMatchesSource(source, doc);
                TradeDocumentGenerationMutationRules.EnsureLinesMatchSource(source, lines);
                created.Add(doc);
                lineResults.Add(lines);
            }

            // 5) 同一事务写入（复用既有写入体，不嵌套事务）：失败整体回滚表头 / 明细行 / 已预约编号。
            await WriteDocumentsAsync(db, created, lineResults, ct);
            if (transaction is not null) await transaction.CommitAsync(ct);

            return BuildResult(source.SourceType, source.SourceId, source.SourceNo, created, lineResults);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            TradeDocumentMutationRules.DiscardTrackedChanges(db);
            throw;
        }
    }

    /// <summary>重复生成守卫：请求类型中任一类型已生成即整体拒绝（不产生任何半成品数据）。</summary>
    private static async Task EnsureNoConflictsAsync(IErpDbContext db, string sourceType, string? salesOrderNo,
        string? containerNo, string? loadingListNo, IReadOnlyList<string> docTypes, CancellationToken ct)
    {
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
    }

    /// <summary>
    /// 构造单张单证草稿 + 明细行快照：目标授权 → 唯一编号预约 → 明细行有界校验，并把表头加入跟踪上下文
    /// （尚未写库，由调用方在同一事务内统一提交）。
    /// </summary>
    private static async Task<(TradeDocument Document, TradeDocumentLineSnapshotResult Lines)> BuildDocumentAsync(
        IErpDbContext db, string docType, Func<string, TradeDocument> buildDraft,
        Func<string, TradeDocumentLineSnapshotResult>? buildLines, SalespersonDataScope? targetScope,
        CancellationToken ct)
    {
        var doc = buildDraft(docType);
        doc.Status = DraftStatus;
        doc.CreatedAt = DateTime.Now;
        // ERP-394：生成目标必须归属到来源单据的权威客户并在实时范围内（受限账号无主 / 越界 fail closed），
        // 在唯一编号守卫与任何写入之前校验，绝不产生半成品单证或绕过目标授权。
        EnsureGeneratedTargetAuthorized(targetScope, doc);
        await EnsureUniqueDocNoAsync(db, doc, ct);

        var lines = buildLines?.Invoke(docType) ?? new TradeDocumentLineSnapshotResult { DocType = docType };
        EnsureLineBound(docType, lines);

        db.TradeDocuments.Add(doc);
        return (doc, lines);
    }

    /// <summary>明细行快照行数有界校验：超出上限一律拒绝（不截断写入、不静默丢弃来源明细行）。</summary>
    private static void EnsureLineBound(string docType, TradeDocumentLineSnapshotResult lines)
    {
        if (lines.Lines.Count > TradeDocumentItemRules.MaxLinesPerDocument)
            throw BusinessException.RuleConflict(
                $"「{docType}」带入的明细行超过 {TradeDocumentItemRules.MaxLinesPerDocument} 行上限："
                + "系统不截断写入，请拆分来源单据后分别生成单证");
    }

    /// <summary>
    /// 父单证与明细行快照在同一事务内写库（明细行需要父单证 Id，因此分两次 SaveChanges，但同一事务；
    /// 任一步失败都回滚，既不会留下没有明细的半成品单证，也不会留下孤儿明细行）。
    /// </summary>
    private static async Task PersistAsync(IErpDbContext db, IReadOnlyList<TradeDocument> created,
        IReadOnlyList<TradeDocumentLineSnapshotResult> lineResults, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await WriteDocumentsAsync(db, created, lineResults, ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// 表头 + 明细行快照的分两次写入体（调用方负责事务边界）：来源行锁路径复用本方法以共享既有事务，
    /// 绝不嵌套事务。
    /// </summary>
    private static async Task WriteDocumentsAsync(IErpDbContext db, IReadOnlyList<TradeDocument> created,
        IReadOnlyList<TradeDocumentLineSnapshotResult> lineResults, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);

        for (var index = 0; index < created.Count; index++)
        {
            foreach (var line in lineResults[index].Lines)
            {
                line.TradeDocumentId = created[index].Id;
                db.TradeDocumentItems.Add(line);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>生成结果组装（单证清单 + 明细行统计 + 行口径文案）。</summary>
    private static TradeDocGenerateResult BuildResult(string sourceType, long sourceId, string sourceNo,
        IReadOnlyList<TradeDocument> created, IReadOnlyList<TradeDocumentLineSnapshotResult> lineResults)
    {
        var documents = new List<TradeDocGeneratedItem>(created.Count);
        for (var index = 0; index < created.Count; index++)
        {
            documents.Add(new TradeDocGeneratedItem
            {
                Id = created[index].Id,
                DocNo = created[index].DocNo,
                DocType = created[index].DocType,
                Status = created[index].Status,
                LineCount = lineResults[index].Lines.Count,
                LineSummaryText = lineResults[index].SummaryText,
                LineEvidenceText = lineResults[index].EvidenceText,
            });
        }

        return new TradeDocGenerateResult
        {
            SourceType = sourceType,
            SourceId = sourceId,
            SourceNo = sourceNo,
            LineRuleText = TradeDocumentPrintSemantics.DetailRuleText,
            TotalLineCount = lineResults.Sum(result => result.Lines.Count),
            Documents = documents,
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

    /// <summary>
    /// 由来源明细构造的**明细行快照预览**（ERP-052，未落库）：按单证类型与行序输出，
    /// 与「直接生成」写入的行逐字段一致（含服务端计算的行金额与留空的箱数 / 重量）。
    /// </summary>
    public List<TradeDocumentPrintLine> LinePreviews { get; set; } = new();

    /// <summary>每个可生成单证类型将带入的明细行数（前端在勾选表里展示，0 = 来源没有明细行）</summary>
    public Dictionary<string, int> LineCounts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>明细行带入摘要文案（行数与口径）</summary>
    public string LineSummaryText { get; set; } = string.Empty;

    /// <summary>明细行证据说明（哪些值来源未提供、按什么口径留空）</summary>
    public string LineEvidenceText { get; set; } = string.Empty;

    /// <summary>明细行口径文案（与打印 / 导出口径同源）</summary>
    public string LineRuleText { get; set; } = string.Empty;
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

    /// <summary>本次生成带入的明细行快照总数（0 = 来源没有明细行 / 类型不支持明细行）</summary>
    public int TotalLineCount { get; set; }

    /// <summary>明细行口径文案（与打印 / 导出口径同源）</summary>
    public string LineRuleText { get; set; } = string.Empty;
}

/// <summary>新建单证的简要信息（Id / 编号 / 类型 / 状态 + ERP-052 明细行带入信息）</summary>
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

    /// <summary>带入的明细行快照数（0 = 来源没有明细行 / 该类型不支持明细行）</summary>
    public int LineCount { get; set; }

    /// <summary>明细行带入摘要（行数与口径）</summary>
    public string LineSummaryText { get; set; } = string.Empty;

    /// <summary>明细行证据说明（哪些值来源未提供、按什么口径留空）</summary>
    public string LineEvidenceText { get; set; } = string.Empty;
}
