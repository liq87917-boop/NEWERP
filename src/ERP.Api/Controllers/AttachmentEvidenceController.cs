using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 业务单据附件内容证据控制器（ERP-061）：把用户提供的 PDF / PNG / JPEG 证据挂到**既有**销售订单 /
/// 采购订单上，并支持有界台账、详情、安全附件下载与显式作废。
/// <para>ERP-062 在同一模型上接入**出口单证**（单证中心台账）扫描件证据：不新增第二张二进制表、
/// 不新增自由路径字段，也不新增单证专用的上传引擎；列表页只按当前页 Id 批量取回有界计数摘要。
/// ERP-063 在同一模型上接入**既有验货记录与样品记录**：验货记录以**既有采购订单上的 QC 字段**为权威记录
/// （本仓库没有独立验货实体，因此**不**新建验货主数据、**不**新建第二张表），样品记录以**既有 <c>Sample</c> 台账**
/// 为权威记录；两者都只读原文快照，**不**把上传解释成验货合格 / 不合格、质量认证、出运许可、
/// 样品批准或客户确认，也**不**改写采购订单与样品台账。</para>
/// <para>审计口径：本仓库既有的文件存储代码只有 <c>OssStorageService</c>（无接口 / 无下载 / 无删除），
/// 既有附件能力只有 ERP-045 的「仅元数据引用册」；因此本控制器使用**唯一**内容接缝
/// <see cref="IAttachmentContentStore"/>：开发 / 测试只启用隔离的非生产本地存储，
/// 生产 OSS 未实现、未注册、未激活（其凭据 / 桶配置 / 迁移 / 启用属生产 OSS Human Gate）。</para>
/// <para>边界（控制器层同样遵守）：只读写 <c>AttachmentEvidences</c> 一张表（读取时只读归属单据），
/// 上传内容一律按**不可信文件**处理：只接受按文件签名判定的 PDF / PNG / JPEG，扩展名 / 声明 Content-Type /
/// 签名必须一致；下载以「附件」方式流式返回并附带 nosniff / sandbox / no-store 等防御性响应头，
/// <strong>不</strong>内联渲染、<strong>不</strong>转成标记、<strong>不</strong>暴露存储键或任何路径；
/// <strong>不</strong>按号码 / 名称猜测归属，也<strong>不</strong>提供硬删除、二进制替换或改派归属；
/// ERP-062 接入出口单证时同样只读单证台账的编号 / 类型 / 状态，不改写单证状态与明细行，
/// 也不解析、抓取或回填单证既有的「附件说明 / 存放位置」自由文本；
/// 生产库结构变更仍由 Human Gate 控制（建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
/// <para>授权：全部接口（含内容下载）均要求与销售订单 / 采购订单工作流相同的 JWT 认证与模块授权；
/// 下载还会在服务端重新校验证据状态与归属单据存在性，不能靠知道 Id 绕过。</para>
/// </summary>
[ApiController]
[Route("api/attachment-evidences")]
[Authorize]
public class AttachmentEvidenceController : ControllerBase
{
    private readonly IErpDbContext _db;
    private readonly IAttachmentContentStore _store;

    public AttachmentEvidenceController(IErpDbContext db, IAttachmentContentStore store)
    {
        _db = db;
        _store = store;
    }

    /// <summary>
    /// 证据台账（分页，只读）：可按归属单据类型 / 归属单据 Id / 状态 / 摘要 / 关键字过滤；
    /// 默认包含已作废历史（原始元数据保留可读），归属单据可用性与可下载性都是只读标注。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged(
        [FromQuery] AttachmentEvidenceQuery query, CancellationToken cancellationToken = default)
        => Ok(ApiResponse<PagedResult<AttachmentEvidenceDto>>.Success(
            await AttachmentEvidenceService.ListAsync(_db, query, cancellationToken)));

    /// <summary>
    /// 模块元数据（只读）：白名单归属类型 / 格式 / 状态、大小与分页上下界、当前内容存储提供程序
    /// （本阶段固定为隔离的非生产本地存储）与全部口径文案，供界面与接口同源显示。
    /// </summary>
    [HttpGet("metadata")]
    public IActionResult Metadata()
        => Ok(ApiResponse<AttachmentEvidenceMetadataDto>.Success(AttachmentEvidenceService.GetMetadata(_store)));

    /// <summary>
    /// 指定归属单据的证据清单（**有界**，单据详情工作流用；默认含已作废历史）：
    /// 归属单据必须存在且未删除（服务端重新校验，查询参数不能绕过）。
    /// </summary>
    [HttpGet("by-owner")]
    public async Task<IActionResult> GetForOwner(
        [FromQuery] string? ownerType,
        [FromQuery] long ownerId,
        [FromQuery] int? status = null,
        [FromQuery] int take = AttachmentEvidenceService.MaxPerOwner,
        CancellationToken cancellationToken = default)
        => Ok(ApiResponse<List<AttachmentEvidenceDto>>.Success(
            await AttachmentEvidenceService.ListForOwnerAsync(
                _db, ownerType, ownerId, status, take, cancellationToken)));

    /// <summary>
    /// 可挂附件证据的单据候选（只读、**有界**）：只列出未删除的销售订单 / 采购订单 / 出口单证 /
    /// 验货记录（既有采购订单 QC 记录）/ 样品记录，
    /// 并批量统计已有证据条数（含已作废历史）；仅用于**显式选择**归属，绝不按号码、名称或文件名猜测。
    /// </summary>
    [HttpGet("owner-options")]
    public async Task<IActionResult> OwnerOptions(
        [FromQuery] string? ownerType,
        [FromQuery] string? keyword,
        [FromQuery] int take = AttachmentEvidenceService.MaxOwnerOptions,
        CancellationToken cancellationToken = default)
        => Ok(ApiResponse<List<AttachmentEvidenceOwnerOptionDto>>.Success(
            await AttachmentEvidenceService.ListOwnerOptionsAsync(_db, ownerType, keyword, take, cancellationToken)));

    /// <summary>
    /// 归属单据的附件证据**有界摘要**（ERP-062，只读）：按 <c>ids=1,2,3</c>（或重复 <c>ids</c> 参数）
    /// 一次取回这些单据的「仓库附件证据条数（有效 / 已作废）」与归属可用性，供单证中心等列表页
    /// 按**当前页** Id 批量展示；单次最多 <see cref="AttachmentEvidenceRules.MaxSummaryOwnerIds"/> 个 Id。
    /// <para>本接口**不**读取任何存储内容、**不**返回证据明细；内容只有在用户显式发起带认证的
    /// <c>GET /api/attachment-evidences/{id}/content</c> 时才被读取。</para>
    /// </summary>
    [HttpGet("owner-summary")]
    public async Task<IActionResult> OwnerSummary(
        [FromQuery] string? ownerType,
        [FromQuery] string? ids,
        CancellationToken cancellationToken = default)
        => Ok(ApiResponse<List<AttachmentEvidenceOwnerSummaryDto>>.Success(
            await AttachmentEvidenceService.SummarizeOwnersAsync(
                _db, ownerType, AttachmentEvidenceRules.ParseOwnerIds(ids), cancellationToken)));

    // ==================== ERP-064：附件中心工作台（只读、分页、按既有「角色 → 菜单」授权收敛） ====================

    /// <summary>
    /// 附件中心工作台台账（**只读、分页、有界**）：一次列出 ERP-061 / ERP-062 / ERP-063 交付的
    /// 同一附件证据册中的**权威元数据**（归属快照 / 文件名快照 / 媒体类型 / 长度 / 摘要 / 上传人 /
    /// 登记时间 / 状态与作废留痕），不复制二进制内容、不新增第二个附件登记表。
    /// <para>结果**只包含**当前账号已获菜单授权的归属类型（授权口径 = 既有 <c>SysUserRoles</c> →
    /// <c>SysRoleMenus</c> → <c>SysMenus.MenuCode</c>）：未授权类型既不返回记录也不返回计数，
    /// 显式传入未授权类型时按「无可见记录」返回空页，不披露其存在性、文件名或摘要。</para>
    /// <para>筛选只用已持久化的显式元数据，且分页有界；列表与计数都**不**访问任何存储内容。</para>
    /// </summary>
    [HttpGet("center")]
    public async Task<IActionResult> GetCenterPaged(
        [FromQuery] AttachmentEvidenceCenterQuery query, CancellationToken cancellationToken = default)
        => Ok(ApiResponse<PagedResult<AttachmentEvidenceDto>>.Success(
            await AttachmentEvidenceService.ListForCenterAsync(
                _db, query, CurrentUserId(), cancellationToken)));

    /// <summary>
    /// 附件中心工作台摘要（**只读、有界**）：当前账号可见范围（哪些归属类型已授权 / 未授权、
    /// 各需哪个既有菜单）+ **授权范围内**按状态拆分的计数 + 筛选项白名单 + 全部口径文案。
    /// <para>未授权类型连计数行都不返回（不披露不可访问记录的存在性）；摘要不读取任何存储内容，
    /// 也不返回文件名 / 摘要 / 存储键。</para>
    /// </summary>
    [HttpGet("center/summary")]
    public async Task<IActionResult> GetCenterSummary(CancellationToken cancellationToken = default)
        => Ok(ApiResponse<AttachmentEvidenceCenterSummaryDto>.Success(
            await AttachmentEvidenceService.GetCenterSummaryAsync(
                _db, CurrentUserId(), CurrentUserName(), cancellationToken)));

    /// <summary>
    /// 工作台内的证据元数据 / 历史（只读）：打开时**重新校验**当前账号对该归属类型的授权，
    /// 未授权一律按「不存在」返回（fail closed，不披露证据 Id 与归属类型）；归属单据已删除 / 缺失时
    /// 照实标注不可用，历史证据仍可只读查看（绝不改派、绝不静默修复）。
    /// </summary>
    [HttpGet("center/{id:long}")]
    public async Task<IActionResult> GetCenterDetail(long id, CancellationToken cancellationToken = default)
        => Ok(ApiResponse<AttachmentEvidenceDto>.Success(
            await AttachmentEvidenceService.GetForCenterAsync(_db, id, CurrentUserId(), cancellationToken)));

    /// <summary>
    /// 工作台内的内容下载（**流式**、只读）：先重新校验归属类型授权（未授权 fail closed），
    /// 再复用既有下载口径（证据有效 + 归属单据存在且未删除 + 长度 / 摘要一致）；
    /// 以「附件」方式返回并附带防御性响应头，浏览器不内联渲染上传内容。
    /// <para>内容只在用户**显式**发起下载时读取。</para>
    /// </summary>
    [HttpGet("center/{id:long}/content")]
    public async Task<IActionResult> DownloadCenterContent(long id, CancellationToken cancellationToken = default)
    {
        var content = await AttachmentEvidenceService.OpenCenterContentAsync(
            _db, _store, id, CurrentUserId(), cancellationToken);

        SetDefensiveDownloadHeaders();
        return File(content.Content, content.MediaType, content.FileName, enableRangeProcessing: false);
    }

    /// <summary>
    /// 下载响应头统一口径：内容按**不可信文件**处理，强制 nosniff / sandbox / no-store，
    /// 不允许内联渲染、不允许缓存、不暴露来源地址。既有与工作台两个下载动作共用同一口径。
    /// </summary>
    private void SetDefensiveDownloadHeaders()
    {
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["X-Download-Options"] = "noopen";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    /// <summary>证据详情（含归属单据可用性与可下载性标注；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id, CancellationToken cancellationToken = default)
        => Ok(ApiResponse<AttachmentEvidenceDto>.Success(
            await AttachmentEvidenceService.GetAsync(_db, id, cancellationToken)));

    /// <summary>
    /// 上传附件证据（multipart/form-data）：只接受 PDF / PNG / JPEG，大小有界（20 MiB）；
    /// 归属单据类型 / Id 必须显式提供且指向存在、未删除的单据；文件名只用于生成净化后的快照，
    /// 摘要 / 长度 / 媒体类型 / 存储键 / 上传人 / 登记时间全部由服务端权威生成。
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(AttachmentEvidenceRules.MaxRequestBytes)]
    public async Task<IActionResult> Upload(
        [FromForm] IFormFile? file,
        [FromForm] string? ownerType,
        [FromForm] long ownerId,
        [FromForm] string? description,
        CancellationToken cancellationToken = default)
    {
        if (file is null)
            throw BusinessException.InvalidParameter("请选择要上传的附件文件（表单字段名必须为 file）");

        AttachmentEvidenceDto result;
        await using (var content = file.OpenReadStream())
        {
            result = await AttachmentEvidenceService.UploadAsync(
                _db,
                _store,
                new AttachmentEvidenceUploadRequest
                {
                    OwnerType = ownerType ?? string.Empty,
                    OwnerId = ownerId,
                    FileName = file.FileName ?? string.Empty,
                    DeclaredContentType = file.ContentType ?? string.Empty,
                    DeclaredLength = file.Length,
                    Description = description ?? string.Empty,
                    Content = content
                },
                CurrentUserName(),
                CurrentUserId(),
                cancellationToken);
        }

        return Ok(ApiResponse<AttachmentEvidenceDto>.Success(
            result,
            "附件证据已登记（只登记用户提供的仓库证据：未提交给任何第三方，也未改写任何业务单据）"));
    }

    /// <summary>
    /// 下载证据内容（**流式**、只读）：每次都重新校验证据状态与归属单据存在性，并复核内容长度与
    /// SHA-256 摘要；以「附件」方式返回（<c>Content-Disposition: attachment</c>）并附带
    /// nosniff / sandbox / no-store 等防御性响应头，浏览器不会内联渲染上传内容。
    /// </summary>
    [HttpGet("{id:long}/content")]
    public async Task<IActionResult> DownloadContent(long id, CancellationToken cancellationToken = default)
    {
        var content = await AttachmentEvidenceService.OpenContentAsync(_db, _store, id, cancellationToken);

        SetDefensiveDownloadHeaders();

        return File(content.Content, content.MediaType, content.FileName, enableRangeProcessing: false);
    }

    /// <summary>
    /// 作废证据（必须填写原因）：保留原始文件名快照、摘要、媒体类型、长度、归属与登记历史，
    /// 内容不再提供下载；不物理删除、不替换二进制、不改派归属；重复作废被拒绝。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(
        long id, [FromBody] AttachmentEvidenceVoidRequest? request, CancellationToken cancellationToken = default)
        => Ok(ApiResponse<AttachmentEvidenceDto>.Success(
            await AttachmentEvidenceService.VoidAsync(_db, id, request?.Reason, cancellationToken),
            "附件证据已作废（原始文件名 / 摘要 / 登记历史保留可读；内容不再提供下载，不提供硬删除与二进制替换）"));

    /// <summary>当前登录用户名（缺失时返回 null，由服务端统一记为「未知用户」，绝不猜测身份）</summary>
    private string? CurrentUserName()
        => User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，仅用于审计字段）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}
