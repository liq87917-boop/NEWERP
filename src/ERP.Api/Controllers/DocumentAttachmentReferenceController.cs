using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 业务单据附件引用登记控制器（ERP-045）：为既有销售订单 / 采购订单 / 装柜清单 / 出口单证登记
/// <b>仅元数据</b>的附件引用（分类 / 安全显示名 / 不透明引用标识 / 可选内容类型 / 字节数 / 校验和 / 备注）。
/// <para>边界（控制器层同样遵守）：本模块<strong>不</strong>上传 / 下载 / 预览 / 抓取 / 覆盖 / 删除任何文件或
/// OSS 对象，<strong>不</strong>接受任意链接、HTML / 脚本 / <c>data:</c> 方案与文件系统穿越值，
/// <strong>不</strong>做服务端抓取、<strong>不</strong>嵌入不可信标记，也<strong>不</strong>提供硬删除或静默替换；
/// 所有接口只读写 <c>DocumentAttachmentReferences</c> 一张表，<strong>不</strong>改写父单据与库存 / 财务 / 出运 /
/// 审批数据。生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
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
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] DocumentAttachmentReferenceQuery query)
        => Ok(ApiResponse<PagedResult<DocumentAttachmentReferenceDto>>.Success(
            await DocumentAttachmentReferenceService.ListAsync(_db, query)));

    /// <summary>
    /// 模块元数据（只读）：白名单父单据类型 / 分类 / 状态、大小与分页上下界、引用 / 授权 / 只登记元数据口径文案，
    /// 供界面与接口同源显示（避免前端硬编码与后端校验口径漂移）。
    /// </summary>
    [HttpGet("metadata")]
    public async Task<IActionResult> Metadata()
        => Ok(ApiResponse<DocumentAttachmentReferenceMetadataDto>.Success(
            await DocumentAttachmentReferenceService.GetMetadataAsync()));

    /// <summary>
    /// 指定父单据的附件引用清单（仅元数据；**有界**，单据详情工作流用）：单次最多
    /// <c>DocumentAttachmentReferenceRules.MaxPerParent</c> 条；默认返回全部状态（含已作废历史，
    /// status 传 0 只看有效 / 传 1 只看已作废）。每条都明确标注「元数据引用，不是文件可用的证明」。
    /// </summary>
    [HttpGet("by-parent")]
    public async Task<IActionResult> GetForParent(
        [FromQuery] string? parentType,
        [FromQuery] long parentId,
        [FromQuery] int? status = null,
        [FromQuery] int take = DocumentAttachmentReferenceRules.MaxPerParent)
        => Ok(ApiResponse<List<DocumentAttachmentReferenceDto>>.Success(
            await DocumentAttachmentReferenceService.ListForParentAsync(_db, parentType, parentId, status, take)));

    /// <summary>
    /// 父单据候选（只读、**有界**）：只返回指定类型下存在且未删除的单据（单次最多
    /// <c>DocumentAttachmentReferenceService.MaxParentOptions</c> 条），关键字只匹配单据号码，
    /// 不按相似度猜测归属（父单据 Id 始终由用户显式选择）。
    /// </summary>
    [HttpGet("parent-options")]
    public async Task<IActionResult> ParentOptions(
        [FromQuery] string? parentType,
        [FromQuery] string? keyword,
        [FromQuery] int take = DocumentAttachmentReferenceService.MaxParentOptions)
        => Ok(ApiResponse<List<DocumentAttachmentReferenceParentOptionDto>>.Success(
            await DocumentAttachmentReferenceService.ListParentOptionsAsync(_db, parentType, keyword, take)));

    /// <summary>附件引用详情（含父单据可用性与引用安全标注；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<DocumentAttachmentReferenceDto>.Success(
            await DocumentAttachmentReferenceService.GetAsync(_db, id)));

    /// <summary>
    /// 登记附件引用（仅元数据）：校验白名单父单据类型与存在性、有界元数据、不透明引用标识、
    /// 来源授权确认与有效身份唯一性，写入父单据号码 / 类型快照。
    /// <para>本接口<strong>不</strong>接受文件内容或上传 / 下载地址，<strong>不</strong>访问对象存储，
    /// 也<strong>不</strong>改写父单据。</para>
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DocumentAttachmentReferenceSaveDto dto)
        => Ok(ApiResponse<DocumentAttachmentReferenceDto>.Success(
            await DocumentAttachmentReferenceService.CreateAsync(_db, dto),
            "附件引用已登记（仅元数据；系统未上传、未抓取任何文件）"));

    /// <summary>
    /// 作废附件引用（必须填写原因）：保留原始元数据、授权留痕与审计历史，不物理删除、不删除远端对象、
    /// 不静默替换，也不改写父单据。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] DocumentAttachmentReferenceVoidRequest? request)
        => Ok(ApiResponse<DocumentAttachmentReferenceDto>.Success(
            await DocumentAttachmentReferenceService.VoidAsync(_db, id, request?.Reason),
            "附件引用已作废（原始元数据与授权留痕保留，可读）"));
}
