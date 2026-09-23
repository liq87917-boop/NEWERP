using ERP.Api.Services;
using ERP.Application.Common;
using ERP.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 钉钉通知控制器：通知配置（Webhook / 加签 / 触发规则 / 消息模板）、测试发送、发送记录查询与重发
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

    /// <summary>
    /// 手动推送一次「客户跟进提醒」：把已到期 / 逾期的客户跟进汇总推送到钉钉。
    /// 不受每日定时与「今日已发送」限制，用于补推或验证配置。
    /// </summary>
    [HttpPost("follow-up-reminder")]
    public async Task<IActionResult> SendFollowUpReminder()
    {
        var (ok, message) = await _followUpReminder.TickAsync(force: true);
        return Ok(ok
            ? ApiResponse<object>.Success(null, message)
            : ApiResponse<object>.Fail(message, ErrorCodes.RuleConflict));
    }

    /// <summary>读取钉钉通知配置（返回全部配置键，前端按字段展示）</summary>
    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        var cfg = await _dingTalk.GetConfigAsync();
        return Ok(ApiResponse<Dictionary<string, string>>.Success(cfg));
    }

    /// <summary>保存钉钉通知配置（仅白名单键生效）</summary>
    [HttpPost("config")]
    public async Task<IActionResult> SaveConfig([FromBody] Dictionary<string, string> values)
    {
        await _dingTalk.SaveConfigAsync(values ?? new Dictionary<string, string>());
        return Ok(ApiResponse<object>.Success(null, "配置已保存"));
    }

    /// <summary>发送测试消息（不传 webhook 时使用已保存的配置）</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] DingTalkTestRequest? request)
    {
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
        var (ok, msg) = await _dingTalk.ResendAsync(id);
        return Ok(ok
            ? ApiResponse<object>.Success(null, "重发成功")
            : ApiResponse<object>.Fail(msg, ErrorCodes.RuleConflict));
    }

    /// <summary>删除发送记录（软删除，用于清理）</summary>
    [HttpDelete("logs/{id:long}")]
    public async Task<IActionResult> DeleteLog(long id)
    {
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
