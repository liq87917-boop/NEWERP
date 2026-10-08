using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 业务单据附件引用登记控制器（ERP-045）：为既有销售订单 / 采购订单 / 装柜清单 / 出口单证登记
/// <b>仅元数据</b>的附件引用（分类 / 安全显示名 / 不透明引用标识 / 可选内容类型 / 字节数 / 校验和 / 备注）。
/// <para>边界（控制器层同样遵守）：本模块<strong>不</strong>上传 / 下载 / 预览 / 抓取 / 覆盖 / 删除任何文件或
/// OSS 对象，<strong>不</strong>接受任意链接、HTML / 脚本 / <c>data:</c> 方案与文件系统穿越值，
/// <strong>不</strong>做服务端抓取、<strong>不</strong>嵌入不可信标记，也<strong>不</strong>提供硬删除或静默替换；
/// 所有接口只读写 <c>DocumentAttachmentReferences</c> 一张表，<strong>不</strong>改写父单据与库存 / 财务 / 出运 /
/// 审批数据。生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
/// <para>授权（ERP-408）：台账 / 按父单据清单 / 父单据候选 / 详情 / 登记 / 作废<strong>每一路由</strong>都先解析
/// 「实时启用身份 + 父单据类型对应的既有模块菜单授权 + ERP-097 客户数据范围」三重护栏
/// （<see cref="AttachmentOwnerAuthorizationRules"/>）；范围在计数 / 分页 / 候选 / 精确读取之前下推，
/// 未授权类型与范围外父单据一律 fail closed。调用方在请求体里的来源授权确认
/// （<c>AuthorizedBy</c> / <c>SourceAuthorizationAcknowledged</c>）<strong>不能</strong>替代本授权。</para>
/// </summary>
[ApiController]
[Route("api/document-attachment-references")]
[Authorize]
public class DocumentAttachmentReferenceController : ControllerBase
{
    private readonly IErpDbContext _db;

    public DocumentAttachmentReferenceController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 附件引用台账（分页，只读）：可按父单据类型 / 父单据 Id / 分类 / 状态 / 关键字过滤；
    /// 默认包含已作废历史（留痕保留可读），父单据可用性与引用安全性都是只读标注。
    /// <para>ERP-408：父单据类型 + 客户范围在计数 / 分页之前下推；显式未授权类型返回空页（不披露存在性）。</para>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged(
        [FromQuery] DocumentAttachmentReferenceQuery query, CancellationToken cancellationToken = default)
        => Ok(ApiResponse<PagedResult<DocumentAttachmentReferenceDto>>.Success(
            await DocumentAttachmentReferenceService.ListAsync(
                _db, query, await ResolveAccessAsync(cancellationToken), cancellationToken)));

    /// <summary>
    /// 模块元数据（只读）：白名单父单据类型 / 分类 / 状态、大小与分页上下界、引用 / 授权 / 只登记元数据口径文案，
    /// 供界面与接口同源显示（避免前端硬编码与后端校验口径漂移）。
    /// <para>ERP-408：模块元数据是静态口径（不读取任何记录 / 计数），但仍要求实时启用身份，缺失 / 已删除 / 已禁用一律 fail closed。</para>
    /// </summary>
    [HttpGet("metadata")]
    public async Task<IActionResult> Metadata(CancellationToken cancellationToken = default)
    {
        await AttachmentOwnerAuthorizationRules.EnsureLiveReferenceIdentityAsync(
            _db, CurrentUserId(), cancellationToken);
        return Ok(ApiResponse<DocumentAttachmentReferenceMetadataDto>.Success(
            await DocumentAttachmentReferenceService.GetMetadataAsync()));
    }

    /// <summary>
    /// 指定父单据的附件引用清单（仅元数据；**有界**，单据详情工作流用）：单次最多
    /// <c>DocumentAttachmentReferenceRules.MaxPerParent</c> 条；默认返回全部状态（含已作废历史，
    /// status 传 0 只看有效 / 传 1 只看已作废）。每条都明确标注「元数据引用，不是文件可用的证明」。
    /// <para>ERP-408：先复核父单据类型菜单授权与权威归属客户范围（fail closed）。</para>
    /// </summary>
    [HttpGet("by-parent")]
    public async Task<IActionResult> GetForParent(
        [FromQuery] string? parentType,
        [FromQuery] long parentId,
        [FromQuery] int? status = null,
        [FromQuery] int take = DocumentAttachmentReferenceRules.MaxPerParent,
        CancellationToken cancellationToken = default)
        => Ok(ApiResponse<List<DocumentAttachmentReferenceDto>>.Success(
            await DocumentAttachmentReferenceService.ListForParentAsync(
                _db, parentType, parentId, status, take,
                await ResolveAccessAsync(cancellationToken), cancellationToken)));

    /// <summary>
    /// 父单据候选（只读、**有界**）：只返回指定类型下存在且未删除的单据（单次最多
    /// <c>DocumentAttachmentReferenceService.MaxParentOptions</c> 条），关键字只匹配单据号码，
    /// 不按相似度猜测归属（父单据 Id 始终由用户显式选择）。
    /// <para>ERP-408：先复核父单据类型菜单授权，再按授权客户范围下推候选。</para>
    /// </summary>
    [HttpGet("parent-options")]
    public async Task<IActionResult> ParentOptions(
        [FromQuery] string? parentType,
        [FromQuery] string? keyword,
        [FromQuery] int take = DocumentAttachmentReferenceService.MaxParentOptions,
        CancellationToken cancellationToken = default)
        => Ok(ApiResponse<List<DocumentAttachmentReferenceParentOptionDto>>.Success(
            await DocumentAttachmentReferenceService.ListParentOptionsAsync(
                _db, parentType, keyword, take,
                await ResolveAccessAsync(cancellationToken), cancellationToken)));

    /// <summary>附件引用详情（含父单据可用性与引用安全标注；只读）；ERP-408：未授权 / 范围外一律按「不存在」。</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id, CancellationToken cancellationToken = default)
        => Ok(ApiResponse<DocumentAttachmentReferenceDto>.Success(
            await DocumentAttachmentReferenceService.GetAsync(
                _db, id, await ResolveAccessAsync(cancellationToken), cancellationToken)));

    /// <summary>
    /// 登记附件引用（仅元数据）：校验白名单父单据类型与存在性、有界元数据、不透明引用标识、
    /// 来源授权确认与有效身份唯一性，写入父单据号码 / 类型快照。
    /// <para>ERP-408：先复核父单据类型菜单授权与原始权威父单据的实时客户范围，全部通过之后才持久化。</para>
    /// <para>ERP-412：登记在**原子事务 + 确定性权威父单据行锁**内完成，锁内重新读取实时身份 / 菜单 /
    /// 客户数据范围与权威父单据之后才快照与写入；失败整体回滚（零部分写入），并发输家由既有过滤唯一索引
    /// 拒绝并映射为稳定的业务冲突（不暴露 SqlException / 内部路径）。</para>
    /// <para>本接口<strong>不</strong>接受文件内容或上传 / 下载地址，<strong>不</strong>访问对象存储，
    /// 也<strong>不</strong>改写父单据。</para>
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] DocumentAttachmentReferenceSaveDto dto, CancellationToken cancellationToken = default)
        => Ok(ApiResponse<DocumentAttachmentReferenceDto>.Success(
            await DocumentAttachmentReferenceService.CreateAsync(
                _db, dto, await ResolveAccessAsync(cancellationToken), cancellationToken),
            "附件引用已登记（仅元数据；系统未上传、未抓取任何文件）"));

    /// <summary>
    /// 作废附件引用（必须填写原因）：保留原始元数据、授权留痕与审计历史，不物理删除、不删除远端对象、
    /// 不静默替换，也不改写父单据。
    /// <para>ERP-408：作废写入之前按**持久化**父单据复核菜单授权与权威归属范围。</para>
    /// <para>ERP-412：作废在**原子事务 + 确定性附件引用行锁**内完成，锁内重新读取引用与实时身份 / 授权；
    /// 并发作废只有一个赢家，输家绝不覆盖赢家保留的原始作废原因与时间戳（历史不可变、完整回滚）。</para>
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(
        long id, [FromBody] DocumentAttachmentReferenceVoidRequest? request,
        CancellationToken cancellationToken = default)
        => Ok(ApiResponse<DocumentAttachmentReferenceDto>.Success(
            await DocumentAttachmentReferenceService.VoidAsync(
                _db, id, request?.Reason, await ResolveAccessAsync(cancellationToken), cancellationToken),
            "附件引用已作废（原始元数据与授权留痕保留，可读）"));

    /// <summary>
    /// ERP-408：解析当前请求的权威附件引用访问上下文（实时启用身份 + 父单据类型对应的既有模块菜单授权 +
    /// ERP-097 客户数据范围），必须在读取任何引用记录 / 计数 / 候选 / 父单据清单或写入之前调用；
    /// 缺失 / 非法 / 已删除身份按未认证拒绝，已禁用账号按权限不足拒绝，绝不退化为匿名或管理员。
    /// </summary>
    private Task<DocumentReferenceAccessContext> ResolveAccessAsync(CancellationToken cancellationToken)
        => AttachmentOwnerAuthorizationRules.ResolveDocumentReferenceAccessAsync(
            _db, CurrentUserId(), cancellationToken);

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，仅用于实时授权与审计；绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}
