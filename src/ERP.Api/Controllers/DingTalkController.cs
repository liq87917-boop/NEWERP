using ERP.Api.Services;
using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 钉钉通知控制器：通知配置（Webhook / 加签 / 触发规则 / 消息模板）、测试发送、发送记录查询与重发
/// <para>ERP-460 起每条路由在读取配置、发送消息或读取 / 重发 / 删除任何发送记录<b>之前</b>都先复核实时启用身份
/// 与既有功能菜单（配置侧 <c>dingtalk-config</c>、记录侧 <c>dingtalk-log</c>，见
/// <see cref="DingTalkAuthorizationRules"/>）；保存配置另校验只提交白名单键且取值不超既有持久化上界。
/// 缺失 / 禁用 / 已删除 / 撤销菜单的身份一律以既有受控非披露错误 fail closed，不新增任何授权，
/// 也不把空身份当作管理员。</para>
/// </summary>
[ApiController]
[Route("api/sys/dingtalk")]
[Authorize]
public class DingTalkController : ControllerBase
{
    private readonly IErpDbContext _db;
    private readonly DingTalkService _dingTalk;
    private readonly FollowUpReminderService _followUpReminder;

    public DingTalkController(IErpDbContext db, DingTalkService dingTalk, FollowUpReminderService followUpReminder)
    {
        _db = db;
        _dingTalk = dingTalk;
        _followUpReminder = followUpReminder;
    }

    /// <summary>当前登录账号 Id（只来自已认证请求主体；缺失 / 非数字 / 非正返回 <c>null</c>，由规则层 fail closed）。</summary>
    private long? CurrentUserId()
    {
        var value = ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0 ? id : null;
    }

    /// <summary>配置侧入口授权（实时身份 + 账号状态 + 既有 dingtalk-config 菜单）。</summary>
    private Task EnsureConfigAuthorizedAsync()
        => DingTalkAuthorizationRules.EnsureConfigAuthorizedAsync(_db, CurrentUserId());

    /// <summary>记录侧入口授权（实时身份 + 账号状态 + 既有 dingtalk-log 菜单）。</summary>
    private Task EnsureLogAuthorizedAsync()
        => DingTalkAuthorizationRules.EnsureLogAuthorizedAsync(_db, CurrentUserId());

    /// <summary>
    /// 手动推送一次「客户跟进提醒」：把已到期 / 逾期的客户跟进汇总推送到钉钉。
    /// 不受每日定时与「今日已发送」限制，用于补推或验证配置。
    /// </summary>
    [HttpPost("follow-up-reminder")]
    public async Task<IActionResult> SendFollowUpReminder()
    {
        await EnsureConfigAuthorizedAsync();
        var (ok, message) = await _followUpReminder.TickAsync(force: true);
        return Ok(ok
            ? ApiResponse<object>.Success(null, message)
            : ApiResponse<object>.Fail(message, ErrorCodes.RuleConflict));
    }

    /// <summary>读取钉钉通知配置（返回全部配置键，前端按字段展示）</summary>
    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        await EnsureConfigAuthorizedAsync();
        var cfg = await _dingTalk.GetConfigAsync();
        return Ok(ApiResponse<Dictionary<string, string>>.Success(cfg));
    }

    /// <summary>保存钉钉通知配置（仅白名单键生效）</summary>
    [HttpPost("config")]
    public async Task<IActionResult> SaveConfig([FromBody] Dictionary<string, string> values)
    {
        await EnsureConfigAuthorizedAsync();
        var normalized = values ?? new Dictionary<string, string>();
        DingTalkAuthorizationRules.ValidateConfigValues(normalized);
        await _dingTalk.SaveConfigAsync(normalized);
        return Ok(ApiResponse<object>.Success(null, "配置已保存"));
    }

    /// <summary>发送测试消息（不传 webhook 时使用已保存的配置）</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] DingTalkTestRequest? request)
    {
        await EnsureConfigAuthorizedAsync();
        request ??= new DingTalkTestRequest();
        var title = string.IsNullOrWhiteSpace(request.Title) ? "ERP 钉钉通知测试" : request.Title!;
        var content = string.IsNullOrWhiteSpace(request.Content)
            ? "### ✅ 钉钉通知测试成功\n- 这是一条来自外贸 ERP 系统的测试消息\n- 时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            : request.Content!;

        var (ok, msg) = await _dingTalk.SendAsync(title, content, request.Webhook);
        return Ok(ok
            ? ApiResponse<object>.Success(null, "测试消息已发送，请查看钉钉群")
            : ApiResponse<object>.Fail(msg, ErrorCodes.RuleConflict));
    }

    /// <summary>分页查询发送记录（keyword 匹配单据号 / 操作人 / 内容）</summary>
    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? keyword = null, [FromQuery] bool? success = null, [FromQuery] string? actionCode = null)
    {
        await EnsureLogAuthorizedAsync();
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 500) pageSize = 20;

        var query = _db.SysDingTalkLogs.AsNoTracking().Where(l => !l.IsDeleted);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            query = query.Where(l => l.BillNo.Contains(kw) || l.Operator.Contains(kw)
                                     || l.Content.Contains(kw) || l.BillTypeName.Contains(kw));
        }
        if (success.HasValue) query = query.Where(l => l.Success == success.Value);
        if (!string.IsNullOrWhiteSpace(actionCode)) query = query.Where(l => l.ActionCode == actionCode);

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(l => l.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(l => new
            {
                l.Id, l.BillType, l.BillTypeName, l.BillNo, l.ActionCode, l.ActionName,
                l.Operator, l.Title, l.Content, l.MsgType, l.Webhook, l.Success,
                l.ErrorMessage, l.RetryCount, l.SentAt, l.CreatedAt
            })
            .ToListAsync();

        return Ok(ApiResponse<object>.Success(new
        {
            items, total, page, pageSize,
            totalPages = (int)Math.Ceiling(total / (double)pageSize)
        }));
    }

    /// <summary>重发指定记录</summary>
    [HttpPost("logs/{id:long}/resend")]
    public async Task<IActionResult> Resend(long id)
    {
        await EnsureLogAuthorizedAsync();
        var (ok, msg) = await _dingTalk.ResendAsync(id);
        return Ok(ok
            ? ApiResponse<object>.Success(null, "重发成功")
            : ApiResponse<object>.Fail(msg, ErrorCodes.RuleConflict));
    }

    /// <summary>删除发送记录（软删除，用于清理）</summary>
    [HttpDelete("logs/{id:long}")]
    public async Task<IActionResult> DeleteLog(long id)
    {
        await EnsureLogAuthorizedAsync();
        var log = await _db.SysDingTalkLogs.FirstOrDefaultAsync(l => l.Id == id && !l.IsDeleted)
            ?? throw BusinessException.NotFound("记录不存在");
        log.IsDeleted = true;
        log.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }
}

/// <summary>钉钉测试发送请求</summary>
public class DingTalkTestRequest
{
    public string? Webhook { get; set; }
    public string? Title { get; set; }
    public string? Content { get; set; }
}
