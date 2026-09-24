using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 业务单据附件引用登记服务（ERP-045）。职责：
/// <list type="number">
/// <item><b>登记</b>（<see cref="CreateAsync"/>）：只允许白名单父单据类型（销售订单 / 采购订单 / 装柜清单 / 出口单证），
/// 父单据必须存在且未删除；服务端写入父单据号码 / 类型快照，并校验有界元数据、不透明引用标识与来源授权确认；</item>
/// <item><b>唯一口径</b>：同一父单据（类型 + Id）+ 分类 + 引用标识在**有效**记录内唯一，重复登记一律拒绝
/// （不静默合并、不覆盖）；已作废记录保留可读但不占用身份；</item>
/// <item><b>台账读取</b>（<see cref="ListAsync"/> / <see cref="GetAsync"/> / <see cref="ListForParentAsync"/>）：
/// 分页 / 有界，父单据快照按类型**一次批量装载**，绝无逐行数据库查询；</item>
/// <item><b>作废</b>（<see cref="VoidAsync"/>）：必须填写原因，保留原始元数据 / 授权留痕 / 审计历史，
/// <strong>不</strong>提供硬删除、<strong>不</strong>删除任何远端对象、<strong>不</strong>静默替换；</item>
/// <item><b>元数据与候选</b>（<see cref="GetMetadataAsync"/> / <see cref="ListParentOptionsAsync"/>）：
/// 供界面与接口同源显示白名单、口径与边界文案。</item>
/// </list>
/// <para>边界（重要）：本服务<strong>不</strong>上传 / 下载 / 预览 / 抓取 / 覆盖 / 删除任何文件或 OSS 对象，
/// <strong>不</strong>读取文件内容、<strong>不</strong>用引用标识发起任何网络或文件系统访问，也不校验文件是否存在、
/// 内容是否安全、是否真实或是否已获下载授权；除本模块登记表外<strong>不</strong>写任何数据，
/// 父单据、库存与库存成本、财务、出运与审批状态一律不变。</para>
/// </summary>
public static class DocumentAttachmentReferenceService
{
    /// <summary>列表关键字长度上限（超长直接拒绝，避免全表无界模糊扫描）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>父单据候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxParentOptions = 200;

    // ==================== 1. 登记 ====================

    /// <summary>
    /// 登记一条附件引用（元数据）：校验父单据类型 / 存在性、分类白名单、有界元数据、不透明引用标识、
    /// 来源授权确认与有效身份唯一性，写入父单据号码 / 类型快照。
    /// <para>本方法<strong>不</strong>访问对象存储、<strong>不</strong>上传或下载任何内容，也<strong>不</strong>改写父单据。</para>
    /// </summary>
    public static async Task<DocumentAttachmentReferenceDto> CreateAsync(
        IErpDbContext db, DocumentAttachmentReferenceSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);

        var parentType = DocumentAttachmentReferenceRules.NormalizeParentType(dto.ParentType);
        if (dto.ParentId <= 0)
            throw BusinessException.InvalidParameter(
                "请选择要登记附件引用的父单据（销售订单 / 采购订单 / 装柜清单 / 出口单证）");

        var parent = await ResolveParentAsync(db, parentType, dto.ParentId)
            ?? throw BusinessException.NotFound(
                $"{DocumentAttachmentReferenceRules.ParentTypeText(parentType)}（Id={dto.ParentId}）不存在或已删除："
                + "只能对存在且未删除的父单据登记附件引用");

        var category = DocumentAttachmentReferenceRules.NormalizeCategory(dto.Category);
        var displayName = DocumentAttachmentReferenceRules.NormalizeDisplayName(dto.DisplayName);
        var referenceId = DocumentAttachmentReferenceRules.NormalizeReferenceId(dto.ReferenceId);
        var contentType = DocumentAttachmentReferenceRules.NormalizeContentType(dto.ContentType);
        var sizeBytes = DocumentAttachmentReferenceRules.NormalizeSizeBytes(dto.SizeBytes);
        var checksum = DocumentAttachmentReferenceRules.NormalizeChecksum(dto.Checksum);
        var notes = DocumentAttachmentReferenceRules.NormalizeNotes(dto.Notes);
        var authorizationNote = DocumentAttachmentReferenceRules.EnsureSourceAuthorized(
            dto.SourceAuthorizationAcknowledged, dto.SourceAuthorizationNote);
        var authorizedBy = DocumentAttachmentReferenceRules.NormalizeAuthorizedBy(dto.AuthorizedBy);

        await EnsureIdentityAvailableAsync(db, parentType, parent.ParentId, category, referenceId, excludeId: null);

        var now = DateTime.Now;
        var entity = new DocumentAttachmentReference
        {
            ParentType = parentType,
            ParentId = parent.ParentId,
            ParentNo = parent.ParentNo,
            ParentTypeText = parent.ParentTypeText,
            Category = category,
            DisplayName = displayName,
            ReferenceId = referenceId,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Checksum = checksum,
            Notes = notes,
            SourceAuthorizationAcknowledged = true,
            SourceAuthorizationNote = authorizationNote,
            AuthorizedBy = authorizedBy,
            AuthorizedAt = now,
            RegisteredAt = now,
            Status = DocumentAttachmentReferenceRules.StatusActive
        };

        db.DocumentAttachmentReferences.Add(entity);
        await db.SaveChangesAsync();

        return await MapAsync(db, entity);
    }

    /// <summary>分页参数规整（页码下限 1；每页条数收敛到 1 ~ <see cref="DocumentAttachmentReferenceQuery.MaxPageSize"/>）</summary>
    private static (int Page, int PageSize) NormalizePaging(int page, int pageSize)
    {
        var normalizedPage = page < 1 ? 1 : page;
        var normalizedSize = pageSize < 1 ? DocumentAttachmentReferenceQuery.DefaultPageSize : pageSize;
        if (normalizedSize > DocumentAttachmentReferenceQuery.MaxPageSize)
            normalizedSize = DocumentAttachmentReferenceQuery.MaxPageSize;
        return (normalizedPage, normalizedSize);
    }

    /// <summary>列表关键字规整（去首尾空白并按上限校验；超长直接拒绝，不静默截断）</summary>
    private static string NormalizeKeyword(string? keyword)
    {
        var value = (keyword ?? string.Empty).Trim();
        if (value.Length > MaxKeywordLength)
            throw BusinessException.InvalidParameter($"关键字长度不能超过 {MaxKeywordLength} 个字符");
        return value;
    }

    // ==================== 2. 台账读取 ====================

    /// <summary>附件引用详情（含父单据可用性与引用安全标注；只读，不写库）</summary>
    public static async Task<DocumentAttachmentReferenceDto> GetAsync(IErpDbContext db, long id)
    {
        ArgumentNullException.ThrowIfNull(db);
        var entity = await LoadAsync(db, id);
        return await MapAsync(db, entity);
    }

    /// <summary>
    /// 附件引用台账（分页、只读）：可按父单据类型 / 父单据 Id / 分类 / 状态 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读），父单据可用性与引用安全性都是**只读标注**。
    /// </summary>
    public static async Task<PagedResult<DocumentAttachmentReferenceDto>> ListAsync(
        IErpDbContext db, DocumentAttachmentReferenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = NormalizePaging(query.Page, query.PageSize);
        var source = db.DocumentAttachmentReferences.AsNoTracking().Where(x => !x.IsDeleted);

        if (!string.IsNullOrWhiteSpace(query.ParentType))
        {
            var parentType = DocumentAttachmentReferenceRules.NormalizeParentType(query.ParentType);
            source = source.Where(x => x.ParentType == parentType);
        }

        if (query.ParentId is not null)
        {
            if (query.ParentId.Value <= 0) throw BusinessException.InvalidParameter("父单据 Id 不合法");
            source = source.Where(x => x.ParentId == query.ParentId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var category = DocumentAttachmentReferenceRules.NormalizeCategory(query.Category);
            source = source.Where(x => x.Category == category);
        }

        if (query.Status is not null)
        {
            var status = query.Status.Value;
            if (status != DocumentAttachmentReferenceRules.StatusActive
                && status != DocumentAttachmentReferenceRules.StatusVoided)
                throw BusinessException.InvalidParameter("状态筛选只允许 0（有效）或 1（已作废）");

            source = source.Where(x => x.Status == status);
        }

        var keyword = NormalizeKeyword(query.Keyword);
        if (keyword.Length > 0)
        {
            source = source.Where(x => x.ParentNo.Contains(keyword)
                || x.DisplayName.Contains(keyword)
                || x.ReferenceId.Contains(keyword)
                || x.Notes.Contains(keyword));
        }

        var total = await source.CountAsync();
        var rows = await source
            .OrderByDescending(x => x.RegisteredAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PagedResult<DocumentAttachmentReferenceDto>
        {
            Items = await MapManyAsync(db, rows),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// 指定父单据的附件引用清单（单据详情工作流用，**有界**：单次最多 <c>MaxPerParent</c> 条；
    /// 默认返回全部状态，含已作废历史且逐条自带状态 / 作废原因）。
    /// <para>返回的每条都明确标注「这是元数据引用，不是文件可用的证明」；父单据可用性与引用安全性均为只读标注。</para>
    /// </summary>
    public static async Task<List<DocumentAttachmentReferenceDto>> ListForParentAsync(
        IErpDbContext db, string? parentType, long parentId, int? status = null, int take = DocumentAttachmentReferenceRules.MaxPerParent)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (parentId <= 0) throw BusinessException.InvalidParameter("请选择父单据（Id 不合法）");

        var type = DocumentAttachmentReferenceRules.NormalizeParentType(parentType);
        var size = take <= 0 ? DocumentAttachmentReferenceRules.MaxPerParent
            : Math.Min(take, DocumentAttachmentReferenceRules.MaxPerParent);

        var source = db.DocumentAttachmentReferences.AsNoTracking()
            .Where(x => !x.IsDeleted && x.ParentType == type && x.ParentId == parentId);

        if (status is not null)
        {
            if (status.Value != DocumentAttachmentReferenceRules.StatusActive
                && status.Value != DocumentAttachmentReferenceRules.StatusVoided)
                throw BusinessException.InvalidParameter("状态筛选只允许 0（有效）或 1（已作废）");

            source = source.Where(x => x.Status == status.Value);
        }

        var rows = await source
            .OrderByDescending(x => x.RegisteredAt)
            .ThenByDescending(x => x.Id)
            .Take(size)
            .ToListAsync();

        return await MapManyAsync(db, rows);
    }

    // ==================== 3. 作废（保留历史） ====================

    /// <summary>
    /// 作废附件引用：必须填写原因；**保留**原始元数据（分类 / 显示名 / 引用标识 / 大小 / 校验和 / 备注）、
    /// 来源授权留痕与审计历史，不物理删除、不删除任何远端对象、不静默替换；重复作废被拒绝。
    /// <para>作废<strong>不</strong>改写父单据，也<strong>不</strong>改动库存、财务、出运与审批数据。</para>
    /// </summary>
    public static async Task<DocumentAttachmentReferenceDto> VoidAsync(
        IErpDbContext db, long id, string? reason)
    {
        ArgumentNullException.ThrowIfNull(db);

        var entity = await LoadAsync(db, id);
        DocumentAttachmentReferenceRules.EnsureVoidable(entity.Status, Label(entity));
        var reasonText = DocumentAttachmentReferenceRules.NormalizeVoidReason(reason);

        entity.Status = DocumentAttachmentReferenceRules.StatusVoided;
        entity.VoidedAt = DateTime.Now;
        entity.VoidReason = reasonText;
        entity.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();

        return await MapAsync(db, entity);
    }

    // ==================== 4. 父单据候选与模块元数据 ====================

    /// <summary>
    /// 父单据候选（只读、**有界**）：只列出指定类型下**存在且未删除**的单据（单次最多
    /// <see cref="MaxParentOptions"/> 条），用于选择「既有」父单据；
    /// 关键字只匹配单据号码，不做模糊跨字段扫描、不按相似度猜测归属。
    /// </summary>
    public static async Task<List<DocumentAttachmentReferenceParentOptionDto>> ListParentOptionsAsync(
        IErpDbContext db, string? parentType, string? keyword, int take = MaxParentOptions)
    {
        ArgumentNullException.ThrowIfNull(db);

        var type = DocumentAttachmentReferenceRules.NormalizeParentType(parentType);
        var keywordText = NormalizeKeyword(keyword);
        var size = take <= 0 ? MaxParentOptions : Math.Min(take, MaxParentOptions);

        switch (type)
        {
            case DocumentAttachmentReferenceRules.ParentTypeSalesOrder:
            {
                var source = db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted);
                if (keywordText.Length > 0) source = source.Where(o => o.OrderNo.Contains(keywordText));
                var rows = await source.OrderByDescending(o => o.Id).Take(size).ToListAsync();
                return rows.Select(o => Option(type, o.Id, o.OrderNo, StatusText(o.Status),
                    $"客户 Id={o.CustomerId}；订单日期 {o.OrderDate:yyyy-MM-dd}")).ToList();
            }

            case DocumentAttachmentReferenceRules.ParentTypePurchaseOrder:
            {
                var source = db.PurchaseOrders.AsNoTracking().Where(o => !o.IsDeleted);
                if (keywordText.Length > 0) source = source.Where(o => o.OrderNo.Contains(keywordText));
                var rows = await source.OrderByDescending(o => o.Id).Take(size).ToListAsync();
                return rows.Select(o => Option(type, o.Id, o.OrderNo, StatusText(o.Status),
                    $"供应商 Id={o.SupplierId}；订单日期 {o.OrderDate:yyyy-MM-dd}"
                    + (string.IsNullOrWhiteSpace(o.OwningCustomerName)
                        ? string.Empty
                        : $"；归属客户 {o.OwningCustomerName}"))).ToList();
            }

            case DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList:
            {
                var source = db.ContainerLoadingLists.AsNoTracking().Where(o => !o.IsDeleted);
                if (keywordText.Length > 0) source = source.Where(o => o.LoadingListNo.Contains(keywordText));
                var rows = await source.OrderByDescending(o => o.Id).Take(size).ToListAsync();
                return rows.Select(o => Option(type, o.Id, o.LoadingListNo, StatusText(o.Status),
                    $"柜号 {o.ContainerNo}；装柜日期 {o.LoadingDate:yyyy-MM-dd}")).ToList();
            }

            default:
            {
                var source = db.TradeDocuments.AsNoTracking().Where(o => !o.IsDeleted);
                if (keywordText.Length > 0) source = source.Where(o => o.DocNo.Contains(keywordText));
                var rows = await source.OrderByDescending(o => o.Id).Take(size).ToListAsync();
                return rows.Select(o => Option(type, o.Id, o.DocNo,
                    string.IsNullOrWhiteSpace(o.Status) ? "未标注状态" : o.Status,
                    $"单证类型 {o.DocType}；关联销售订单 {o.SalesOrderNo}")).ToList();
            }
        }
    }

    /// <summary>父单据候选 DTO 组装（候选只包含存在且未删除的单据，因此恒为可选）</summary>
    private static DocumentAttachmentReferenceParentOptionDto Option(
        string parentType, long parentId, string? parentNo, string statusText, string summaryText)
    {
        var typeText = DocumentAttachmentReferenceRules.ParentTypeText(parentType);
        var no = DocumentAttachmentReferenceRules.NormalizeParentNo(parentNo);
        return new DocumentAttachmentReferenceParentOptionDto(
            parentType,
            typeText,
            parentId,
            no,
            statusText,
            no.Length == 0 ? summaryText : $"{typeText} {no}（{statusText}）；{summaryText}",
            true,
            "可选：父单据存在且未删除");
    }

    /// <summary>
    /// 附件引用模块元数据（只读）：白名单父单据类型 / 分类 / 状态、上下界与口径文案，
    /// 供界面与接口同源显示，避免前端硬编码与后端校验口径漂移。
    /// </summary>
    public static Task<DocumentAttachmentReferenceMetadataDto> GetMetadataAsync()
    {
        var parentTypes = DocumentAttachmentReferenceRules.SupportedParentTypes
            .Select(t => new DocumentAttachmentReferenceOptionDto(
                t, DocumentAttachmentReferenceRules.ParentTypeText(t)))
            .ToList();

        var categories = DocumentAttachmentReferenceRules.SupportedCategories
            .Select(c => new DocumentAttachmentReferenceOptionDto(
                c, DocumentAttachmentReferenceRules.CategoryText(c)))
            .ToList();

        var statuses = new List<DocumentAttachmentReferenceOptionDto>
        {
            new(DocumentAttachmentReferenceRules.StatusActive.ToString(),
                DocumentAttachmentReferenceRules.StatusText(DocumentAttachmentReferenceRules.StatusActive)),
            new(DocumentAttachmentReferenceRules.StatusVoided.ToString(),
                DocumentAttachmentReferenceRules.StatusText(DocumentAttachmentReferenceRules.StatusVoided))
        };

        return Task.FromResult(new DocumentAttachmentReferenceMetadataDto(
            parentTypes,
            categories,
            statuses,
            DocumentAttachmentReferenceRules.MaxSizeBytes,
            DocumentAttachmentReferenceQuery.MaxPageSize,
            DocumentAttachmentReferenceRules.MaxPerParent,
            DocumentAttachmentReferenceRules.SizePolicyText,
            DocumentAttachmentReferenceRules.ReferencePolicyText,
            DocumentAttachmentReferenceRules.AuthorizationPolicyText,
            DocumentAttachmentReferenceRules.MetadataOnlyNoticeText,
            DocumentAttachmentReferenceRules.BoundaryText));
    }

    // ==================== 5. 内部辅助 ====================

    /// <summary>父单据快照（服务端权威读取：类型 / 号码 / 状态 / 可用性；只读，不写入父单据）</summary>
    private sealed record ParentSnapshot(
        string ParentType, string ParentTypeText, long ParentId, string ParentNo,
        string StatusText, string SummaryText, bool Available);

    /// <summary>
    /// 解析父单据（只读）：<paramref name="includeDeleted"/> 为 false 时「不存在 / 已删除」都返回 null
    /// （登记路径据此拒绝），为 true 时只有「不存在」返回 null（读取路径据此标注历史引用）。
    /// 每种类型只查一张表，不跨模块关联、不按相似度猜测归属。
    /// </summary>
    private static async Task<ParentSnapshot?> ResolveParentAsync(
        IErpDbContext db, string parentType, long parentId, bool includeDeleted = false)
    {
        ParentSnapshot? snapshot;
        switch (parentType)
        {
            case DocumentAttachmentReferenceRules.ParentTypeSalesOrder:
            {
                var order = await db.SalesOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == parentId);
                snapshot = order is null ? null : Snapshot(parentType, order.Id, order.OrderNo, order.IsDeleted,
                    StatusText(order.Status), $"客户 Id={order.CustomerId}；订单日期 {order.OrderDate:yyyy-MM-dd}");
                break;
            }

            case DocumentAttachmentReferenceRules.ParentTypePurchaseOrder:
            {
                var order = await db.PurchaseOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == parentId);
                snapshot = order is null ? null : Snapshot(parentType, order.Id, order.OrderNo, order.IsDeleted,
                    StatusText(order.Status), $"供应商 Id={order.SupplierId}；订单日期 {order.OrderDate:yyyy-MM-dd}");
                break;
            }

            case DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList:
            {
                var list = await db.ContainerLoadingLists.AsNoTracking().FirstOrDefaultAsync(o => o.Id == parentId);
                snapshot = list is null ? null : Snapshot(parentType, list.Id, list.LoadingListNo, list.IsDeleted,
                    StatusText(list.Status), $"柜号 {list.ContainerNo}；装柜日期 {list.LoadingDate:yyyy-MM-dd}");
                break;
            }

            default:
            {
                var doc = await db.TradeDocuments.AsNoTracking().FirstOrDefaultAsync(o => o.Id == parentId);
                snapshot = doc is null ? null : Snapshot(parentType, doc.Id, doc.DocNo, doc.IsDeleted,
                    string.IsNullOrWhiteSpace(doc.Status) ? "未标注状态" : doc.Status,
                    $"单证类型 {doc.DocType}；关联销售订单 {doc.SalesOrderNo}");
                break;
            }
        }

        if (snapshot is null) return null;
        return !includeDeleted && !snapshot.Available ? null : snapshot;
    }

    /// <summary>父单据快照组装（号码按有界口径校验；已删除时明确标注并置为不可用）</summary>
    private static ParentSnapshot Snapshot(
        string parentType, long parentId, string? parentNo, bool deleted, string statusText, string summaryText)
    {
        var no = DocumentAttachmentReferenceRules.NormalizeParentNo(parentNo);
        var typeText = DocumentAttachmentReferenceRules.ParentTypeText(parentType);
        var summary = no.Length == 0
            ? summaryText
            : $"{typeText} {no}（{statusText}）；{summaryText}";

        return new ParentSnapshot(
            parentType, typeText, parentId, no, statusText,
            deleted ? summary + "；已删除" : summary,
            !deleted);
    }

    /// <summary>取出指定类型的父单据 Id 集合（用于批量装载父单据快照，避免逐行查询）</summary>
    private static List<long> ParentIdsOf(IReadOnlyList<DocumentAttachmentReference> rows, string parentType)
        => rows.Where(r => string.Equals(r.ParentType, parentType, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ParentId)
            .Distinct()
            .ToList();

    /// <summary>
    /// 批量装载父单据快照（台账分页用）：每种父单据类型最多一次查询，绝无逐行数据库查询；
    /// 已删除父单据也在装载范围内，读取侧据此标注「已删除」而不是把历史引用当成失效数据丢弃。
    /// </summary>
    private static async Task<Dictionary<(string ParentType, long ParentId), ParentSnapshot>> LoadParentSnapshotsAsync(
        IErpDbContext db, IReadOnlyList<DocumentAttachmentReference> rows)
    {
        var map = new Dictionary<(string ParentType, long ParentId), ParentSnapshot>();

        var salesOrderIds = ParentIdsOf(rows, DocumentAttachmentReferenceRules.ParentTypeSalesOrder);
        if (salesOrderIds.Count > 0)
        {
            var entities = await db.SalesOrders.AsNoTracking().Where(o => salesOrderIds.Contains(o.Id)).ToListAsync();
            foreach (var order in entities)
            {
                map[(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id)] = Snapshot(
                    DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id, order.OrderNo, order.IsDeleted,
                    StatusText(order.Status), $"客户 Id={order.CustomerId}；订单日期 {order.OrderDate:yyyy-MM-dd}");
            }
        }

        var purchaseOrderIds = ParentIdsOf(rows, DocumentAttachmentReferenceRules.ParentTypePurchaseOrder);
        if (purchaseOrderIds.Count > 0)
        {
            var entities = await db.PurchaseOrders.AsNoTracking()
                .Where(o => purchaseOrderIds.Contains(o.Id)).ToListAsync();
            foreach (var order in entities)
            {
                map[(DocumentAttachmentReferenceRules.ParentTypePurchaseOrder, order.Id)] = Snapshot(
                    DocumentAttachmentReferenceRules.ParentTypePurchaseOrder, order.Id, order.OrderNo, order.IsDeleted,
                    StatusText(order.Status), $"供应商 Id={order.SupplierId}；订单日期 {order.OrderDate:yyyy-MM-dd}");
            }
        }

        var loadingListIds = ParentIdsOf(rows, DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList);
        if (loadingListIds.Count > 0)
        {
            var entities = await db.ContainerLoadingLists.AsNoTracking()
                .Where(o => loadingListIds.Contains(o.Id)).ToListAsync();
            foreach (var list in entities)
            {
                map[(DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList, list.Id)] = Snapshot(
                    DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList, list.Id, list.LoadingListNo,
                    list.IsDeleted, StatusText(list.Status),
                    $"柜号 {list.ContainerNo}；装柜日期 {list.LoadingDate:yyyy-MM-dd}");
            }
        }

        var tradeDocumentIds = ParentIdsOf(rows, DocumentAttachmentReferenceRules.ParentTypeTradeDocument);
        if (tradeDocumentIds.Count > 0)
        {
            var entities = await db.TradeDocuments.AsNoTracking()
                .Where(o => tradeDocumentIds.Contains(o.Id)).ToListAsync();
            foreach (var doc in entities)
            {
                map[(DocumentAttachmentReferenceRules.ParentTypeTradeDocument, doc.Id)] = Snapshot(
                    DocumentAttachmentReferenceRules.ParentTypeTradeDocument, doc.Id, doc.DocNo, doc.IsDeleted,
                    string.IsNullOrWhiteSpace(doc.Status) ? "未标注状态" : doc.Status,
                    $"单证类型 {doc.DocType}；关联销售订单 {doc.SalesOrderNo}");
            }
        }

        return map;
    }

    /// <summary>
    /// 有效身份唯一性检查（只读）：同一父单据（类型 + Id）+ 分类 + 引用标识在**有效**（未作废）记录内唯一；
    /// 重复请求一律拒绝，不静默合并、不覆盖；已作废记录保留可读但不占用身份。
    /// </summary>
    private static async Task EnsureIdentityAvailableAsync(
        IErpDbContext db, string parentType, long parentId, string category, string referenceId, long? excludeId)
    {
        var excluded = excludeId ?? 0;
        var duplicate = await db.DocumentAttachmentReferences.AsNoTracking()
            .Where(x => !x.IsDeleted
                && x.Status == DocumentAttachmentReferenceRules.StatusActive
                && x.ParentType == parentType
                && x.ParentId == parentId
                && x.Category == category
                && x.ReferenceId == referenceId
                && x.Id != excluded)
            .Select(x => new { x.Id, x.DisplayName, x.RegisteredAt })
            .FirstOrDefaultAsync();

        if (duplicate is null) return;

        throw BusinessException.Duplicate(
            $"父单据（{DocumentAttachmentReferenceRules.ParentTypeText(parentType)} Id={parentId}）已存在同一分类"
            + $"「{DocumentAttachmentReferenceRules.CategoryText(category)}」+ 引用标识「{referenceId}」的有效记录"
            + $"（Id={duplicate.Id}，显示名 {duplicate.DisplayName}，登记时间 {duplicate.RegisteredAt:yyyy-MM-dd HH:mm}）；"
            + "重复登记被拒绝而不是静默合并（如需更正请先作废原记录再重新登记）");
    }

    /// <summary>按 Id 装载未删除附件引用（不存在 / 已删除 → 数据不存在）</summary>
    private static async Task<DocumentAttachmentReference> LoadAsync(IErpDbContext db, long id)
    {
        if (id <= 0) throw BusinessException.InvalidParameter("附件引用 Id 不合法");
        return await db.DocumentAttachmentReferences.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted)
            ?? throw BusinessException.NotFound($"附件引用（Id={id}）不存在或已删除");
    }

    /// <summary>附件引用对外标签（提示与作废留痕共用；父单据快照 + 显示名，均为有界文本）</summary>
    private static string Label(DocumentAttachmentReference entity)
        => $"{DocumentAttachmentReferenceRules.ParentSnapshotText(entity.ParentTypeText, entity.ParentNo)} / {entity.DisplayName}";

    /// <summary>单条映射（详情与单条操作返回用；只读标注，不写库）</summary>
    private static async Task<DocumentAttachmentReferenceDto> MapAsync(
        IErpDbContext db, DocumentAttachmentReference entity)
    {
        var map = await LoadParentSnapshotsAsync(db, new List<DocumentAttachmentReference> { entity });
        return Map(entity, map.TryGetValue((entity.ParentType, entity.ParentId), out var parent) ? parent : null);
    }

    /// <summary>
    /// 批量映射（台账分页用）：父单据快照按类型一次批量装载，绝无逐行数据库查询；
    /// 父单据可用性与引用安全性都是**只读标注**：已删除父单据与历史不安全引用值不改变记录可读性。
    /// </summary>
    private static async Task<List<DocumentAttachmentReferenceDto>> MapManyAsync(
        IErpDbContext db, IReadOnlyList<DocumentAttachmentReference> rows)
    {
        if (rows.Count == 0) return new List<DocumentAttachmentReferenceDto>();

        var map = await LoadParentSnapshotsAsync(db, rows);
        return rows.Select(row => Map(
            row,
            map.TryGetValue((row.ParentType, row.ParentId), out var parent) ? parent : null)).ToList();
    }

    /// <summary>
    /// 附件引用实体 → DTO（含父单据可用性与引用安全标注；纯映射，不写库）。
    /// <para><c>ReferenceId</c> 原样回显（有界且经登记校验），但界面必须按 <c>ReferenceAvailable</c> 决定是否显示；
    /// 不安全值只显示 <c>ReferenceUnavailableText</c>，绝不作为链接或标记渲染。</para>
    /// </summary>
    private static DocumentAttachmentReferenceDto Map(DocumentAttachmentReference entity, ParentSnapshot? parent)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var referenceAvailable = DocumentAttachmentReferenceRules.IsSafeReferenceId(entity.ReferenceId);
        var parentAvailable = parent is not null && parent.Available;
        var typeText = string.IsNullOrWhiteSpace(entity.ParentTypeText)
            ? DocumentAttachmentReferenceRules.ParentTypeText(entity.ParentType)
            : entity.ParentTypeText;

        return new DocumentAttachmentReferenceDto(
            entity.Id,
            entity.ParentType ?? string.Empty,
            typeText,
            entity.ParentId,
            entity.ParentNo ?? string.Empty,
            DocumentAttachmentReferenceRules.ParentSnapshotText(typeText, entity.ParentNo),
            parentAvailable,
            DocumentAttachmentReferenceRules.ParentAvailabilityText(parentAvailable),
            entity.Category ?? string.Empty,
            DocumentAttachmentReferenceRules.CategoryText(entity.Category),
            entity.DisplayName ?? string.Empty,
            referenceAvailable,
            entity.ReferenceId ?? string.Empty,
            DocumentAttachmentReferenceRules.ReferenceUnavailableText(entity.ReferenceId),
            entity.ContentType ?? string.Empty,
            entity.SizeBytes,
            DocumentAttachmentReferenceRules.SizeText(entity.SizeBytes),
            entity.Checksum ?? string.Empty,
            entity.Notes ?? string.Empty,
            entity.SourceAuthorizationAcknowledged,
            entity.SourceAuthorizationNote ?? string.Empty,
            entity.AuthorizedBy ?? string.Empty,
            entity.AuthorizedAt,
            entity.RegisteredAt,
            entity.Status,
            DocumentAttachmentReferenceRules.StatusText(entity.Status),
            entity.Status == DocumentAttachmentReferenceRules.StatusActive,
            entity.Status == DocumentAttachmentReferenceRules.StatusVoided,
            entity.VoidedAt,
            entity.VoidReason ?? string.Empty,
            entity.CreatedAt,
            entity.UpdatedAt,
            DocumentAttachmentReferenceRules.MetadataOnlyNoticeText,
            DocumentAttachmentReferenceRules.BoundaryText);
    }

    /// <summary>单据状态文案（只读标注；未知状态返回枚举名而不是猜测）</summary>
    private static string StatusText(DocumentStatus status) => status switch
    {
        DocumentStatus.Pending => "待提交",
        DocumentStatus.Submitted => "已提交",
        DocumentStatus.Approved => "已审核",
        DocumentStatus.Rejected => "已驳回",
        DocumentStatus.Completed => "已完成",
        DocumentStatus.Cancelled => "已取消",
        _ => status.ToString()
    };
}
