using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace ERP.Application.Services;

/// <summary>
/// 业务单据附件内容证据服务（ERP-061 建立；ERP-062 把既有出口单证接入**同一**模型）。职责：
/// <list type="number">
/// <item><b>上传证据</b>（<see cref="UploadAsync"/>）：归属单据必须是白名单类型且**存在、未删除**，
/// 内容只接受 PDF / PNG / JPEG（按文件签名判定），扩展名 / 声明 Content-Type / 签名三者必须一致；
/// 大小有界（20 MiB），超限在读取时立即中止且不保存任何内容；</item>
/// <item><b>内容与元数据</b>：存储键由服务端生成（不透明），SHA-256 摘要、字节长度、媒体类型、
/// 净化后的文件名快照、归属单据号码 / 类型快照、上传人与登记时间全部**服务端权威写入**，
/// 客户端不能提交这些值；</item>
/// <item><b>下载</b>（<see cref="OpenContentAsync"/>）：每次请求都重新校验证据状态、归属单据存在性
/// 与内容长度 / 摘要一致性，返回只读流交给控制器以「附件」方式返回；</item>
/// <item><b>作废</b>（<see cref="VoidAsync"/>）：必须填写原因，只改状态与作废留痕，保留原始文件名、
/// 摘要、媒体类型、长度、归属与上传人，不物理删除、不替换内容、不改派归属；</item>
/// <item><b>台账 / 详情 / 候选</b>：分页与清单全部有界，页内归属单据可用性**批量装载**
/// （固定数量查询，无逐行查库）。</item>
/// </list>
/// <para>审计结论：本仓库既有的文件存储代码只有 <c>OssStorageService</c>（无接口、无下载、无删除），
/// 既有附件能力只有 ERP-045 的「仅元数据引用册」。因此本服务在**唯一**内容接缝
/// <c>IAttachmentContentStore</c> 之上建立唯一附件内容模型：开发与测试只使用隔离的非生产本地存储，
/// 生产 OSS 凭据 / 桶配置 / 迁移与激活一律不存在（属生产 OSS Human Gate）。</para>
/// <para>边界（重要）：本服务只读写 <c>AttachmentEvidences</c> 一张表（读取时只读归属单据），
/// <strong>不</strong>改写销售订单 / 采购订单的状态、金额、明细与备注，<strong>不</strong>生成库存移动，
/// <strong>不</strong>改动出库 / 装柜 / 单证 / 发票 / 费用与分摊 / 收付款 / 税务与结算记录，
/// 也<strong>不</strong>把证据内容转成标记、内联渲染或外发到任何第三方。
/// ERP-062 接入出口单证时口径完全一致：只读单证台账的 <c>DocNo</c> / <c>DocType</c> / <c>Status</c> 快照，
/// 不改写单证状态、明细行与来源销售订单 / 装柜清单，也<strong>不</strong>解析、抓取或回填单证既有的
/// 「附件说明 / 存放位置」自由文本（<see cref="AttachmentEvidenceRules.LegacyFileNotePolicyText"/>）。</para>
/// </summary>
public static class AttachmentEvidenceService
{
    /// <summary>单个归属单据的有界证据清单上限（单据详情工作流用）</summary>
    public const int MaxPerOwner = 200;

    /// <summary>归属单据候选上限（选择归属时使用；只列未删除单据）</summary>
    public const int MaxOwnerOptions = 200;

    // ==================== 1. 上传证据（服务端权威元数据） ====================

    /// <summary>
    /// 上传一条附件证据：全部校验通过后才写内容与元数据。
    /// <para>校验顺序：存储提供程序（必须是非生产隔离提供程序）→ 归属单据类型白名单 →
    /// 归属单据存在且未删除 → 有界读取内容（超限中止）→ 文件名 / 扩展名 / 声明类型 / 文件签名一致
    /// → 内容落盘（服务端生成存储键）→ 元数据落库。</para>
    /// </summary>
    public static async Task<AttachmentEvidenceDto> UploadAsync(
        IErpDbContext db,
        IAttachmentContentStore store,
        AttachmentEvidenceUploadRequest request,
        string? uploadedBy,
        long? uploadedById,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);

        // 0) 内容存储：开发 / 测试只允许隔离的非生产提供程序（生产 OSS 未实现、未注册、未激活）
        EnsureIsolatedStorageProvider(store);

        // 1) 归属单据：白名单类型 + 必须指向存在且未删除的权威单据（服务端复核，不接受客户端快照）
        var ownerType = AttachmentEvidenceRules.NormalizeOwnerType(request.OwnerType);
        var owner = await LoadOwnerAsync(db, ownerType, request.OwnerId, cancellationToken);

        // 2) 有界读取内容：声明长度先做快速拒绝，实际读取超限立即中止（不保存任何内容）
        var content = await ReadBoundedAsync(request.Content, request.DeclaredLength, cancellationToken);

        // 3) 文件名 / 格式校验：扩展名、声明 Content-Type、文件签名三者一致；拒绝可执行与标记类格式
        var probeLength = Math.Min(content.Length, AttachmentEvidenceRules.SignatureProbeLength);
        var identity = AttachmentEvidenceRules.ValidateUpload(
            request.FileName, request.DeclaredContentType, content.AsSpan(0, probeLength));

        var fileName = AttachmentEvidenceRules.SanitizeFileName(request.FileName);
        var description = AttachmentEvidenceRules.NormalizeDescription(request.Description);
        var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        // 4) 内容落盘：键与扩展名都由服务端决定（不传客户端文件名、不传任何路径）
        string storageKey;
        await using (var buffer = new MemoryStream(content, writable: false))
        {
            storageKey = await store.SaveAsync(buffer, identity.Extension, cancellationToken);
        }

        // 5) 元数据落库：摘要 / 长度 / 媒体类型 / 归属快照 / 上传人 / 登记时间全部服务端写入
        var row = new AttachmentEvidence
        {
            OwnerType = ownerType,
            OwnerId = owner.Id,
            OwnerNo = TrimTo(owner.No, AttachmentEvidenceRules.MaxOwnerNoLength),
            OwnerTypeText = AttachmentEvidenceRules.OwnerTypeText(ownerType),
            OriginalFileName = fileName,
            MediaType = identity.MediaType,
            SizeBytes = content.LongLength,
            Sha256 = digest,
            Description = description,
            StorageKey = storageKey,
            StorageProvider = store.ProviderCode,
            UploadedBy = AttachmentEvidenceRules.NormalizeUploadedBy(uploadedBy),
            RecordedAt = DateTime.Now,
            Status = AttachmentEvidenceRules.StatusActive,
            CreatedAt = DateTime.Now,
            CreatedBy = uploadedById
        };

        db.AttachmentEvidences.Add(row);
        await db.SaveChangesAsync(cancellationToken);

        return Map(row, ownerAvailable: true);
    }

    /// <summary>
    /// 存储提供程序守卫：只有**非生产**的隔离提供程序可用于写入。
    /// <para>生产对象存储（既有 <c>OssStorageService</c>）在本阶段不提供实现，
    /// 其配置 / 激活属于生产 OSS Human Gate，因此这里显式拒绝任何生产提供程序。</para>
    /// </summary>
    public static void EnsureIsolatedStorageProvider(IAttachmentContentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.IsProductionProvider)
            throw BusinessException.RuleConflict(
                "附件内容存储当前只允许隔离的非生产提供程序：生产对象存储（OSS）的凭据 / 桶配置 / 迁移与激活"
                + "属于生产 OSS Human Gate，本阶段不实现、不注册、不激活；请使用隔离的非生产存储进行验证");
    }

    /// <summary>
    /// 有界读取内容（最多 <c>AttachmentEvidenceRules.MaxSizeBytes</c>）：
    /// 声明长度超限时提前拒绝；实际读取超限时立即中止并抛错，调用方据此不保存任何内容；
    /// 空内容同样拒绝（避免登记无内容的证据）。
    /// </summary>
    public static async Task<byte[]> ReadBoundedAsync(
        Stream? content, long declaredLength, CancellationToken cancellationToken = default)
    {
        if (content is null)
            throw BusinessException.InvalidParameter("请选择要上传的附件文件（未收到文件内容）");
        if (declaredLength < 0)
            throw BusinessException.InvalidParameter("附件长度声明无效（不能为负数）");
        if (declaredLength > AttachmentEvidenceRules.MaxSizeBytes)
            throw BusinessException.InvalidParameter(
                $"附件大小（{AttachmentEvidenceRules.SizeText(declaredLength)}）超过上限 "
                + $"{AttachmentEvidenceRules.SizeText(AttachmentEvidenceRules.MaxSizeBytes)}，请压缩或拆分后再上传");

        // 预分配上限受 20 MiB 约束，内存峰值有界
        var buffer = new MemoryStream(
            capacity: (int)Math.Min(Math.Max(declaredLength, 4096), AttachmentEvidenceRules.MaxSizeBytes));
        var chunk = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await content.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            if (read <= 0) break;

            total += read;
            if (total > AttachmentEvidenceRules.MaxSizeBytes)
                throw BusinessException.InvalidParameter(
                    $"附件实际内容超过上限 {AttachmentEvidenceRules.SizeText(AttachmentEvidenceRules.MaxSizeBytes)}："
                    + "读取在到达上限时立即中止，未保存任何内容");

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        if (total == 0)
            throw BusinessException.InvalidParameter("附件文件为空（0 字节），拒绝上传空内容证据");

        return buffer.ToArray();
    }

    // ==================== 2. 台账（分页有界 + 批量可用性标注） ====================

    /// <summary>
    /// 证据台账（分页，只读）：可按归属单据类型 / 归属单据 Id / 状态 / 摘要 / 关键字过滤；
    /// 默认包含已作废历史（原始元数据保留可读）；页内归属单据可用性**批量装载**（每类型最多一次查询）。
    /// </summary>
    public static async Task<PagedResult<AttachmentEvidenceDto>> ListAsync(
        IErpDbContext db, AttachmentEvidenceQuery? query = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var request = query ?? new AttachmentEvidenceQuery();
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = Math.Clamp(
            request.PageSize <= 0 ? AttachmentEvidenceQuery.DefaultPageSize : request.PageSize,
            1, AttachmentEvidenceQuery.MaxPageSize);

        var ownerType = string.IsNullOrWhiteSpace(request.OwnerType)
            ? null
            : AttachmentEvidenceRules.NormalizeOwnerType(request.OwnerType);
        var ownerId = request.OwnerId;
        if (ownerId is <= 0)
            throw BusinessException.InvalidParameter("归属单据 Id 筛选值无效：必须为正整数");
        var status = AttachmentEvidenceRules.NormalizeStatusFilter(request.Status);
        var sha256 = AttachmentEvidenceRules.NormalizeSha256Filter(request.Sha256);
        var keyword = AttachmentEvidenceRules.NormalizeKeyword(request.Keyword);

        var source = db.AttachmentEvidences.AsNoTracking().Where(r => !r.IsDeleted);
        if (ownerType is not null) source = source.Where(r => r.OwnerType == ownerType);
        if (ownerId is not null) source = source.Where(r => r.OwnerId == ownerId.Value);
        if (status is not null) source = source.Where(r => r.Status == status.Value);
        if (sha256 is not null) source = source.Where(r => r.Sha256.StartsWith(sha256));
        if (keyword is not null)
            source = source.Where(r => r.OwnerNo.Contains(keyword)
                                       || r.OriginalFileName.Contains(keyword)
                                       || r.Description.Contains(keyword));

        var total = await source.CountAsync(cancellationToken);
        var rows = await source
            .OrderByDescending(r => r.RecordedAt).ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);

        var availability = await LoadOwnerAvailabilityAsync(db, rows, cancellationToken);

        return new PagedResult<AttachmentEvidenceDto>
        {
            Items = rows.Select(r => Map(r, OwnerAvailable(availability, r))).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// 指定归属单据的证据清单（**有界**，单据详情工作流用；默认含已作废历史）：
    /// 归属单据必须存在且未删除（服务器重新校验，查询参数不能绕过）。
    /// </summary>
    public static async Task<List<AttachmentEvidenceDto>> ListForOwnerAsync(
        IErpDbContext db,
        string? ownerType,
        long ownerId,
        int? status = null,
        int take = MaxPerOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var type = AttachmentEvidenceRules.NormalizeOwnerType(ownerType);
        var owner = await LoadOwnerAsync(db, type, ownerId, cancellationToken);
        var statusFilter = AttachmentEvidenceRules.NormalizeStatusFilter(status);
        var bounded = Math.Clamp(take <= 0 ? MaxPerOwner : take, 1, MaxPerOwner);

        var source = db.AttachmentEvidences.AsNoTracking()
            .Where(r => !r.IsDeleted && r.OwnerType == type && r.OwnerId == owner.Id);
        if (statusFilter is not null) source = source.Where(r => r.Status == statusFilter.Value);

        var rows = await source
            .OrderByDescending(r => r.RecordedAt).ThenByDescending(r => r.Id)
            .Take(bounded)
            .ToListAsync(cancellationToken);

        return rows.Select(r => Map(r, true)).ToList();
    }

    /// <summary>证据详情（只读）：归属单据已删除 / 缺失时照实标注不可用，历史证据仍可只读查看。</summary>
    public static async Task<AttachmentEvidenceDto> GetAsync(
        IErpDbContext db, long id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await LoadAsync(db, id, cancellationToken);
        var ownerAvailable = await IsOwnerAvailableAsync(db, row.OwnerType, row.OwnerId, cancellationToken);
        return Map(row, ownerAvailable);
    }

    // ==================== 3. 内容下载（每次请求重新校验 + 完整性校验） ====================

    /// <summary>
    /// 打开证据内容（只读、流式）：每次请求都重新校验
    /// <list type="number">
    /// <item>证据存在且未删除、状态为有效（已作废证据不再提供下载）；</item>
    /// <item>归属单据仍存在且未删除（权威复核，不能靠知道 Id 绕过）；</item>
    /// <item>存储对象长度与登记长度一致，且可定位时复核 SHA-256 摘要一致（防外部替换）。</item>
    /// </list>
    /// <para>返回的内容只含文件名 / 媒体类型 / 长度 / 摘要与只读流，<strong>不</strong>含存储键或任何路径。</para>
    /// </summary>
    public static async Task<AttachmentEvidenceContentDto> OpenContentAsync(
        IErpDbContext db, IAttachmentContentStore store, long id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(store);

        var row = await LoadAsync(db, id, cancellationToken);

        if (row.Status != AttachmentEvidenceRules.StatusActive)
            throw BusinessException.RuleConflict(
                $"该附件证据已作废（{row.VoidedAt:yyyy-MM-dd HH:mm}，原因：{row.VoidReason}）：不提供下载；"
                + "原始文件名 / 摘要 / 登记历史仍保留可读，系统不提供硬删除与二进制替换");

        var ownerAvailable = await IsOwnerAvailableAsync(db, row.OwnerType, row.OwnerId, cancellationToken);
        if (!ownerAvailable)
            throw BusinessException.RuleConflict(
                $"{AttachmentEvidenceRules.OwnerTypeText(row.OwnerType)}（Id={row.OwnerId}）已不存在或已删除："
                + "拒绝下载该证据内容；系统不会把证据改派到别的单据，也不会按号码或名称猜测归属");

        var stream = await store.OpenReadAsync(row.StorageKey, cancellationToken)
            ?? throw BusinessException.NotFound(
                "附件内容不可用（隔离存储中不存在该对象）：请核对登记时间与存储提供程序，"
                + "必要时重新上传一条新证据并显式作废本条");

        long? length = null;
        if (stream.CanSeek) length = stream.Length;
        length ??= await store.GetLengthAsync(row.StorageKey, cancellationToken);

        if (length != row.SizeBytes)
        {
            await stream.DisposeAsync();
            throw BusinessException.RuleConflict(
                $"附件内容长度（{length} 字节）与登记长度（{row.SizeBytes} 字节）不一致：内容可能被外部改动，"
                + "已拒绝下载；请显式作废本条证据后重新上传");
        }

        if (stream.CanSeek)
        {
            var actual = await ComputeSha256Async(stream, cancellationToken);
            if (!string.Equals(actual, row.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await stream.DisposeAsync();
                throw BusinessException.RuleConflict(
                    "附件内容摘要与登记摘要不一致：内容可能被外部替换，已拒绝下载；请显式作废本条证据后重新上传");
            }

            stream.Position = 0;
        }

        return new AttachmentEvidenceContentDto(
            stream,
            row.OriginalFileName,
            string.IsNullOrWhiteSpace(row.MediaType) ? "application/octet-stream" : row.MediaType,
            row.SizeBytes,
            row.Sha256 ?? string.Empty);
    }

    // ==================== 4. 作废（唯一更正方式） ====================

    /// <summary>
    /// 作废证据：必须填写原因；只改状态与作废留痕，保留原始文件名快照、摘要、媒体类型、长度、
    /// 存储键、归属与上传人，不物理删除、不替换内容、不改派归属；重复作废拒绝。
    /// </summary>
    public static async Task<AttachmentEvidenceDto> VoidAsync(
        IErpDbContext db, long id, string? reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var row = await LoadAsync(db, id, cancellationToken);
        if (row.Status == AttachmentEvidenceRules.StatusVoided)
            throw BusinessException.Duplicate(
                $"该附件证据已作废（{row.VoidedAt:yyyy-MM-dd HH:mm}，原因：{row.VoidReason}）：不重复作废；"
                + "系统不提供硬删除，也不提供二进制替换或改派归属");

        var voidReason = AttachmentEvidenceRules.NormalizeVoidReason(reason);

        row.Status = AttachmentEvidenceRules.StatusVoided;
        row.VoidedAt = DateTime.Now;
        row.VoidReason = voidReason;
        row.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(cancellationToken);

        var ownerAvailable = await IsOwnerAvailableAsync(db, row.OwnerType, row.OwnerId, cancellationToken);
        return Map(row, ownerAvailable);
    }

    // ==================== 5. 模块元数据（口径与界面同源） ====================

    /// <summary>
    /// 模块元数据（只读）：白名单归属类型 / 格式 / 状态、大小与分页上下界、当前内容存储提供程序
    /// （本阶段固定为隔离的非生产本地存储）与全部口径文案，供界面与接口同源显示。
    /// </summary>
    public static AttachmentEvidenceMetadataDto GetMetadata(IAttachmentContentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        return new AttachmentEvidenceMetadataDto(
            OwnerTypes: AttachmentEvidenceRules.SupportedOwnerTypes
                .Select(t => new AttachmentEvidenceOptionDto(t, AttachmentEvidenceRules.OwnerTypeText(t))).ToList(),
            MediaTypes: AttachmentEvidenceRules.SupportedMediaTypes
                .Select(m => new AttachmentEvidenceOptionDto(m, AttachmentEvidenceRules.MediaTypeText(m))).ToList(),
            StatusOptions: new List<AttachmentEvidenceOptionDto>
            {
                new(AttachmentEvidenceRules.StatusActive.ToString(), AttachmentEvidenceRules.StatusText(AttachmentEvidenceRules.StatusActive)),
                new(AttachmentEvidenceRules.StatusVoided.ToString(), AttachmentEvidenceRules.StatusText(AttachmentEvidenceRules.StatusVoided))
            },
            AllowedExtensions: AttachmentEvidenceRules.SupportedExtensions.ToList(),
            MaxSizeBytes: AttachmentEvidenceRules.MaxSizeBytes,
            MaxPageSize: AttachmentEvidenceQuery.MaxPageSize,
            MaxPerOwner: MaxPerOwner,
            MaxOwnerOptions: MaxOwnerOptions,
            MaxSummaryOwnerIds: AttachmentEvidenceRules.MaxSummaryOwnerIds,
            SizePolicyText:
                $"单个附件内容上限 {AttachmentEvidenceRules.SizeText(AttachmentEvidenceRules.MaxSizeBytes)}"
                + "（服务端实测；超限在读取时立即中止，不保存任何内容）",
            FormatPolicyText:
                $"只接受 {AttachmentEvidenceRules.SupportedFormatsText}：扩展名、声明的 Content-Type 与文件签名必须一致；"
                + "可执行 / 脚本 / 标记类格式与无法识别的二进制一律拒绝",
            StorageProviderCode: store.ProviderCode,
            StorageProviderText: store.ProviderText,
            StoragePolicyText:
                "内容存储只使用隔离的非生产提供程序（键由服务端生成的不透明标识）；生产对象存储（OSS）的凭据、桶配置、"
                + "迁移与激活属于生产 OSS Human Gate，本阶段不存在、不注册、不激活",
            DownloadPolicyText:
                "下载每次重新校验证据状态与归属单据存在性，并复核内容长度与 SHA-256 摘要；以「附件」方式流式返回，"
                + "附带 nosniff / sandbox / no-store 等防御性响应头，浏览器不内联渲染上传内容",
            BoundaryText: AttachmentEvidenceRules.BoundaryText,
            LegacyFileNotePolicyText: AttachmentEvidenceRules.LegacyFileNotePolicyText,
            TradeDocumentEvidenceBoundaryText: AttachmentEvidenceRules.TradeDocumentEvidenceBoundaryText);
    }

    // ==================== 6. 归属单据候选（有界、显式选择、批量统计） ====================

    /// <summary>
    /// 归属单据候选（只读、**有界**）：只列出未删除的销售订单 / 采购订单 / 出口单证，并批量统计已有
    /// 证据条数（含已作废历史）；只用于**显式选择**归属，绝不按号码、名称或文件名猜测。
    /// </summary>
    public static async Task<List<AttachmentEvidenceOwnerOptionDto>> ListOwnerOptionsAsync(
        IErpDbContext db,
        string? ownerType,
        string? keyword,
        int take = MaxOwnerOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var type = AttachmentEvidenceRules.NormalizeOwnerType(ownerType);
        var filter = AttachmentEvidenceRules.NormalizeKeyword(keyword);
        var bounded = Math.Clamp(take <= 0 ? MaxOwnerOptions : take, 1, MaxOwnerOptions);

        if (type == AttachmentEvidenceRules.OwnerTypeSalesOrder)
        {
            var query = db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted);
            if (filter is not null) query = query.Where(o => o.OrderNo.Contains(filter));

            var rows = await query
                .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id)
                .Take(bounded)
                .Select(o => new OwnerCandidate(o.Id, o.OrderNo, o.Status, $"客户 Id={o.CustomerId}；订单日期 {o.OrderDate:yyyy-MM-dd}"))
                .ToListAsync(cancellationToken);

            var counts = await CountEvidenceAsync(db, type, rows.Select(r => r.Id).ToList(), cancellationToken);
            return rows.Select(r => Option(type, r, counts)).ToList();
        }

        if (type == AttachmentEvidenceRules.OwnerTypePurchaseOrder)
        {
            var purchaseQuery = db.PurchaseOrders.AsNoTracking().Where(o => !o.IsDeleted);
            if (filter is not null) purchaseQuery = purchaseQuery.Where(o => o.OrderNo.Contains(filter));

            var purchaseRows = await purchaseQuery
                .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id)
                .Take(bounded)
                .Select(o => new OwnerCandidate(o.Id, o.OrderNo, o.Status, $"供应商 Id={o.SupplierId}；订单日期 {o.OrderDate:yyyy-MM-dd}"))
                .ToListAsync(cancellationToken);

            var purchaseCounts = await CountEvidenceAsync(db, type, purchaseRows.Select(r => r.Id).ToList(), cancellationToken);
            return purchaseRows.Select(r => Option(type, r, purchaseCounts)).ToList();
        }

        // 出口单证（ERP-062）：只读单证台账（编号 / 类型 / 状态原文），不读取明细行、不解析附件说明文本
        var documentQuery = db.TradeDocuments.AsNoTracking().Where(d => !d.IsDeleted);
        if (filter is not null) documentQuery = documentQuery.Where(d => d.DocNo.Contains(filter));

        var documentRows = await documentQuery
            .OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.Id)
            .Take(bounded)
            .Select(d => new TradeDocumentCandidate(d.Id, d.DocNo, d.DocType, d.Status))
            .ToListAsync(cancellationToken);

        var documentCounts = await CountEvidenceAsync(db, type, documentRows.Select(r => r.Id).ToList(), cancellationToken);
        return documentRows.Select(r => Option(type, r, documentCounts)).ToList();
    }

    // ==================== 6.1 出口单证附件证据摘要（ERP-062：列表只有有界计数，无逐行查库 / 无存储访问） ====================

    /// <summary>
    /// 归属单据的附件证据**有界摘要**（一次批量查询）：只返回「已登记证据条数（有效 / 已作废）」与
    /// 归属单据可用性，**不**读取任何存储、**不**返回内容、**不**逐行查库。
    /// <para>用于单证中心等列表页按**当前页 Id 集合**显示附件证据计数；内容只有在用户显式发起
    /// 带认证的下载请求（<see cref="OpenContentAsync"/>）时才会被读取。</para>
    /// </summary>
    public static async Task<List<AttachmentEvidenceOwnerSummaryDto>> SummarizeOwnersAsync(
        IErpDbContext db,
        string? ownerType,
        IEnumerable<long>? ownerIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var type = AttachmentEvidenceRules.NormalizeOwnerType(ownerType);
        var ids = AttachmentEvidenceRules.NormalizeOwnerIds(ownerIds);
        if (ids.Count == 0) return new List<AttachmentEvidenceOwnerSummaryDto>();

        var grouped = await CountEvidenceByStatusAsync(db, type, ids, cancellationToken);
        var owners = await LoadOwnerReferencesAsync(db, type, ids, cancellationToken);
        var typeText = AttachmentEvidenceRules.OwnerTypeText(type);

        return ids.Select(id =>
        {
            grouped.TryGetValue(id, out var counts);
            var ownerNo = owners.TryGetValue(id, out var owner) ? (owner.No ?? string.Empty).Trim() : string.Empty;
            var available = owners.TryGetValue(id, out var reference) && reference.Available;

            return new AttachmentEvidenceOwnerSummaryDto(
                type,
                typeText,
                id,
                ownerNo,
                AttachmentEvidenceRules.OwnerSnapshotText(typeText, ownerNo),
                available,
                counts.Total,
                counts.Active,
                counts.Voided,
                counts.Active > 0,
                AttachmentEvidenceRules.OwnerSummaryText(typeText, ownerNo, counts.Total, counts.Active, counts.Voided),
                type == AttachmentEvidenceRules.OwnerTypeTradeDocument
                    ? AttachmentEvidenceRules.TradeDocumentEvidenceBoundaryText
                    : AttachmentEvidenceRules.BoundaryText);
        }).ToList();
    }

    // ==================== 7. 内部：归属单据读写（权威复核 + 批量装载） ====================

    /// <summary>归属单据快照（服务端权威读取；**不**接受客户端提交的号码 / 类型快照）</summary>
    private sealed record OwnerSnapshot(string OwnerType, long Id, string No, string StatusText);

    /// <summary>候选行的中间投影（状态在内存里转文案，避免在 EF 投影中调用方法）</summary>
    private sealed record OwnerCandidate(long Id, string OrderNo, DocumentStatus Status, string SummaryText);

    /// <summary>出口单证候选行的中间投影（单证编号 / 类型 / 台账状态原文，同样在内存里转文案）</summary>
    private sealed record TradeDocumentCandidate(long Id, string DocNo, string DocType, string StatusText);

    /// <summary>归属单据权威引用（当前号码 + 是否可用）：软删除单据照样装载，读取侧标注不可用而不改派</summary>
    private sealed record OwnerReference(string No, bool Available);

    /// <summary>归属单据的证据条数（按状态拆分；有界：每归属最多两行分组结果）</summary>
    private readonly record struct EvidenceStatusCounts(int Total, int Active, int Voided);

    /// <summary>
    /// 加载归属单据（存在且未删除）：不存在 / 已删除 / Id 非法一律拒绝，
    /// 并给出「请显式选择有效单据」的明确提示（不按号码、名称或文件名猜测归属）。
    /// <para>出口单证（ERP-062）同样复核单证台账记录是否**存在且未删除**；系统绝不解析
    /// 单证的「附件说明 / 存放位置」历史文本，也不按单证编号文本反查归属。</para>
    /// </summary>
    private static async Task<OwnerSnapshot> LoadOwnerAsync(
        IErpDbContext db, string ownerType, long ownerId, CancellationToken cancellationToken)
    {
        if (ownerId <= 0)
            throw BusinessException.InvalidParameter(
                $"请显式选择归属单据（{AttachmentEvidenceRules.OwnerTypeText(ownerType)}）后再上传附件证据");

        if (ownerType == AttachmentEvidenceRules.OwnerTypeSalesOrder)
        {
            var order = await db.SalesOrders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == ownerId && !o.IsDeleted, cancellationToken);
            if (order is null)
                throw BusinessException.NotFound(
                    $"销售订单不存在或已删除（Id={ownerId}）：请选择有效的销售订单后再上传附件证据");
            return new OwnerSnapshot(ownerType, order.Id, order.OrderNo, StatusText(order.Status));
        }

        if (ownerType == AttachmentEvidenceRules.OwnerTypePurchaseOrder)
        {
            var order = await db.PurchaseOrders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == ownerId && !o.IsDeleted, cancellationToken);
            if (order is null)
                throw BusinessException.NotFound(
                    $"采购订单不存在或已删除（Id={ownerId}）：请选择有效的采购订单后再上传附件证据");
            return new OwnerSnapshot(ownerType, order.Id, order.OrderNo, StatusText(order.Status));
        }

        if (ownerType == AttachmentEvidenceRules.OwnerTypeTradeDocument)
        {
            var document = await db.TradeDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == ownerId && !d.IsDeleted, cancellationToken);
            if (document is null)
                throw BusinessException.NotFound(
                    $"出口单证不存在或已删除（Id={ownerId}）：请选择有效的单证台账记录后再上传附件证据"
                    + "（系统不会按单证编号文本或「附件说明」内容猜测归属）");
            return new OwnerSnapshot(
                ownerType, document.Id, document.DocNo, AttachmentEvidenceRules.TradeDocumentStatusText(document.Status));
        }

        throw BusinessException.InvalidParameter($"不支持的归属单据类型「{ownerType}」");
    }

    /// <summary>
    /// 单条归属单据可用性复核：未删除的白名单单据为可用；历史 / 未知类型一律不可用（不查询、不猜测）。
    /// </summary>
    private static async Task<bool> IsOwnerAvailableAsync(
        IErpDbContext db, string? ownerType, long ownerId, CancellationToken cancellationToken)
    {
        if (ownerId <= 0) return false;

        var type = ownerType?.Trim();
        if (string.Equals(type, AttachmentEvidenceRules.OwnerTypeSalesOrder, StringComparison.OrdinalIgnoreCase))
            return await db.SalesOrders.AsNoTracking().AnyAsync(o => o.Id == ownerId && !o.IsDeleted, cancellationToken);

        if (string.Equals(type, AttachmentEvidenceRules.OwnerTypePurchaseOrder, StringComparison.OrdinalIgnoreCase))
            return await db.PurchaseOrders.AsNoTracking().AnyAsync(o => o.Id == ownerId && !o.IsDeleted, cancellationToken);

        if (string.Equals(type, AttachmentEvidenceRules.OwnerTypeTradeDocument, StringComparison.OrdinalIgnoreCase))
            return await db.TradeDocuments.AsNoTracking()
                .AnyAsync(d => d.Id == ownerId && !d.IsDeleted, cancellationToken);

        return false;
    }

    /// <summary>
    /// 批量装载页内归属单据可用性：每种单据类型最多一次查询（无逐行数据库查询）；
    /// 已删除单据同样在装载范围内，读取侧据此标注不可用而不是把历史证据当作无效数据丢弃。
    /// </summary>
    private static async Task<Dictionary<(string OwnerType, long OwnerId), bool>> LoadOwnerAvailabilityAsync(
        IErpDbContext db, IReadOnlyList<AttachmentEvidence> rows, CancellationToken cancellationToken)
    {
        var map = new Dictionary<(string OwnerType, long OwnerId), bool>();

        foreach (var ownerType in AttachmentEvidenceRules.SupportedOwnerTypes)
        {
            var ids = OwnerIdsOf(rows, ownerType);
            if (ids.Count == 0) continue;

            var references = await LoadOwnerReferencesAsync(db, ownerType, ids, cancellationToken);
            foreach (var id in ids)
                map[(ownerType, id)] = references.TryGetValue(id, out var reference) && reference.Available;
        }

        return map;
    }

    /// <summary>
    /// 批量读取归属单据权威引用（当前号码 + 是否可用）：每种单据类型**一次**查询、按 Id 集合装载
    /// （无逐行查库）；已软删除单据照样返回号码并标记不可用，绝不改派、绝不按号码或文本猜测。
    /// </summary>
    private static async Task<Dictionary<long, OwnerReference>> LoadOwnerReferencesAsync(
        IErpDbContext db, string ownerType, List<long> ownerIds, CancellationToken cancellationToken)
    {
        var map = new Dictionary<long, OwnerReference>();
        if (ownerIds.Count == 0) return map;

        if (ownerType == AttachmentEvidenceRules.OwnerTypeSalesOrder)
        {
            var rows = await db.SalesOrders.AsNoTracking()
                .Where(o => ownerIds.Contains(o.Id))
                .Select(o => new { o.Id, o.OrderNo, o.IsDeleted })
                .ToListAsync(cancellationToken);
            foreach (var row in rows) map[row.Id] = new OwnerReference(row.OrderNo, !row.IsDeleted);
            return map;
        }

        if (ownerType == AttachmentEvidenceRules.OwnerTypePurchaseOrder)
        {
            var rows = await db.PurchaseOrders.AsNoTracking()
                .Where(o => ownerIds.Contains(o.Id))
                .Select(o => new { o.Id, o.OrderNo, o.IsDeleted })
                .ToListAsync(cancellationToken);
            foreach (var row in rows) map[row.Id] = new OwnerReference(row.OrderNo, !row.IsDeleted);
            return map;
        }

        // 出口单证（ERP-062）：只读取台账编号与软删除标记，不读取明细行、不读取「附件说明 / 存放位置」文本
        var documents = await db.TradeDocuments.AsNoTracking()
            .Where(d => ownerIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocNo, d.IsDeleted })
            .ToListAsync(cancellationToken);
        foreach (var document in documents)
            map[document.Id] = new OwnerReference(document.DocNo, !document.IsDeleted);
        return map;
    }

    /// <summary>归属单据可用性（历史 / 未知类型与未装载到的记录一律视为不可用）</summary>
    private static bool OwnerAvailable(
        IReadOnlyDictionary<(string OwnerType, long OwnerId), bool> map, AttachmentEvidence row)
        => map.TryGetValue((row.OwnerType ?? string.Empty, row.OwnerId), out var available) && available;

    /// <summary>取出指定类型的归属单据 Id 集合（用于批量装载，避免逐行查询）</summary>
    private static List<long> OwnerIdsOf(IReadOnlyList<AttachmentEvidence> rows, string ownerType)
        => rows.Where(r => string.Equals(r.OwnerType, ownerType, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.OwnerId)
            .Distinct()
            .ToList();

    /// <summary>加载一条有效证据（软删除行视为不存在）</summary>
    private static async Task<AttachmentEvidence> LoadAsync(
        IErpDbContext db, long id, CancellationToken cancellationToken)
    {
        if (id <= 0)
            throw BusinessException.InvalidParameter("附件证据 Id 无效：必须为正整数");

        var row = await db.AttachmentEvidences
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, cancellationToken);

        return row ?? throw BusinessException.NotFound($"附件证据不存在或已删除（Id={id}）");
    }

    /// <summary>批量统计指定归属单据的证据条数（含已作废历史；单次分组查询，无逐行查库）</summary>
    private static async Task<Dictionary<long, int>> CountEvidenceAsync(
        IErpDbContext db, string ownerType, List<long> ownerIds, CancellationToken cancellationToken)
    {
        if (ownerIds.Count == 0) return new Dictionary<long, int>();

        var rows = await db.AttachmentEvidences.AsNoTracking()
            .Where(r => !r.IsDeleted && r.OwnerType == ownerType && ownerIds.Contains(r.OwnerId))
            .GroupBy(r => r.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.OwnerId, r => r.Count);
    }

    /// <summary>
    /// 批量统计指定归属单据的证据条数（按状态拆分；单次分组查询、结果行数 ≤ 2 × 归属数，
    /// 无逐行查库、不读取任何存储内容）。
    /// </summary>
    private static async Task<Dictionary<long, EvidenceStatusCounts>> CountEvidenceByStatusAsync(
        IErpDbContext db, string ownerType, List<long> ownerIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, EvidenceStatusCounts>();
        if (ownerIds.Count == 0) return result;

        var rows = await db.AttachmentEvidences.AsNoTracking()
            .Where(r => !r.IsDeleted && r.OwnerType == ownerType && ownerIds.Contains(r.OwnerId))
            .GroupBy(r => new { r.OwnerId, r.Status })
            .Select(g => new { g.Key.OwnerId, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            result.TryGetValue(row.OwnerId, out var current);
            var active = current.Active + (row.Status == AttachmentEvidenceRules.StatusActive ? row.Count : 0);
            var voided = current.Voided + (row.Status == AttachmentEvidenceRules.StatusVoided ? row.Count : 0);
            result[row.OwnerId] = new EvidenceStatusCounts(current.Total + row.Count, active, voided);
        }

        return result;
    }

    /// <summary>归属单据候选（只读、显式选择；统计值只表示已登记证据条数，不代表任何确认）</summary>
    private static AttachmentEvidenceOwnerOptionDto Option(
        string ownerType, OwnerCandidate candidate, IReadOnlyDictionary<long, int> counts)
        => Option(ownerType, candidate.Id, candidate.OrderNo, StatusText(candidate.Status),
            candidate.SummaryText, counts);

    /// <summary>
    /// 出口单证候选（ERP-062）：号码取单证编号，状态取台账原文（只读标注，不推断、不改写），
    /// 说明里只列单证类型与台账状态，**不**读取明细行，也**不**读取「附件说明 / 存放位置」文本。
    /// </summary>
    private static AttachmentEvidenceOwnerOptionDto Option(
        string ownerType, TradeDocumentCandidate candidate, IReadOnlyDictionary<long, int> counts)
    {
        var docType = (candidate.DocType ?? string.Empty).Trim();
        var statusText = AttachmentEvidenceRules.TradeDocumentStatusText(candidate.StatusText);
        var summary = docType.Length == 0
            ? $"单证类型未登记；台账状态 {statusText}"
            : $"单证类型 {docType}；台账状态 {statusText}";

        return Option(ownerType, candidate.Id, candidate.DocNo, statusText, summary, counts);
    }

    /// <summary>候选行 → DTO 的共同映射（单据类型 / 号码 / 状态文案 / 批量统计条数）</summary>
    private static AttachmentEvidenceOwnerOptionDto Option(
        string ownerType,
        long ownerId,
        string? ownerNo,
        string statusText,
        string summaryText,
        IReadOnlyDictionary<long, int> counts)
    {
        var typeText = AttachmentEvidenceRules.OwnerTypeText(ownerType);
        var number = (ownerNo ?? string.Empty).Trim();
        var count = counts.TryGetValue(ownerId, out var value) ? value : 0;

        return new AttachmentEvidenceOwnerOptionDto(
            ownerType,
            typeText,
            ownerId,
            number,
            statusText,
            count,
            number.Length == 0
                ? summaryText
                : $"{typeText} {number}（{statusText}）；{summaryText}",
            true,
            $"可选择：{typeText} {number}（已有附件证据 {count} 条，含已作废历史）");
    }

    /// <summary>
    /// 实体 → DTO 映射：只输出服务端权威值；存储键 / 文件系统路径绝不进入 DTO，
    /// 只给出 API 相对下载地址（<c>/api/attachment-evidences/{id}/content</c>）。
    /// </summary>
    private static AttachmentEvidenceDto Map(AttachmentEvidence row, bool ownerAvailable)
    {
        var isVoided = row.Status == AttachmentEvidenceRules.StatusVoided;
        var downloadable = !isVoided && ownerAvailable;

        return new AttachmentEvidenceDto(
            row.Id,
            row.OwnerType ?? string.Empty,
            AttachmentEvidenceRules.OwnerTypeText(row.OwnerType),
            row.OwnerId,
            row.OwnerNo ?? string.Empty,
            AttachmentEvidenceRules.OwnerSnapshotText(AttachmentEvidenceRules.OwnerTypeText(row.OwnerType), row.OwnerNo),
            ownerAvailable,
            AttachmentEvidenceRules.OwnerAvailabilityText(ownerAvailable),
            row.OriginalFileName ?? string.Empty,
            row.MediaType ?? string.Empty,
            AttachmentEvidenceRules.MediaTypeText(row.MediaType),
            row.SizeBytes,
            AttachmentEvidenceRules.SizeText(row.SizeBytes),
            AttachmentEvidenceRules.DigestText(row.Sha256),
            row.Description ?? string.Empty,
            row.UploadedBy ?? string.Empty,
            row.RecordedAt,
            row.Status,
            AttachmentEvidenceRules.StatusText(row.Status),
            row.Status == AttachmentEvidenceRules.StatusActive,
            isVoided,
            downloadable,
            AttachmentEvidenceRules.DownloadAvailabilityText(downloadable, isVoided, ownerAvailable),
            AttachmentEvidenceRules.ContentApiPath(row.Id),
            row.VoidedAt,
            row.VoidReason ?? string.Empty,
            row.StorageProvider ?? string.Empty,
            AttachmentEvidenceRules.ProviderText(row.StorageProvider),
            row.CreatedAt,
            row.UpdatedAt,
            string.Equals(row.OwnerType, AttachmentEvidenceRules.OwnerTypeTradeDocument, StringComparison.OrdinalIgnoreCase)
                ? AttachmentEvidenceRules.TradeDocumentEvidenceBoundaryText
                : AttachmentEvidenceRules.BoundaryText);
    }

    /// <summary>计算 SHA-256（有界：调用方已限制内容大小；用于下载前的一致性复核）</summary>
    private static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>有界截断（仅用于快照字段，避免超长号码写入越界）</summary>
    private static string TrimTo(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
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
