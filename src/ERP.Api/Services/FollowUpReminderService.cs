using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Services;

/// <summary>
/// 客户跟进提醒后台服务
/// 职责：按「系统参数」配置的时间（默认 09:00），每天定时把**已到期 / 逾期**的客户跟进记录
///       汇总后推送到钉钉群机器人，作为业务员的回访清单。
/// 参数（SysParameters）：
///   FollowUpReminder_Enabled  —— 0/1，默认 0（关闭，避免打扰）
///   FollowUpReminder_Time     —— 每日提醒时间，格式 HH:mm，默认 09:00
///   FollowUpReminder_LastSent —— 内部记录：最近一次成功发送日期（yyyy-MM-dd），用于防重复发送
/// 说明：每 30 分钟检查一次；未启用或未到时间直接跳过；发送结果写入 SysDingTalkLogs 便于追溯。
/// </summary>
public class FollowUpReminderService : BackgroundService
{
    public const string KeyEnabled = "FollowUpReminder_Enabled";
    public const string KeyTime = "FollowUpReminder_Time";
    public const string KeyLastSent = "FollowUpReminder_LastSent";
    /// <summary>启动后首次检查的延迟秒数（默认 60；设为 0 表示启动即检查，便于验证）</summary>
    public const string KeyStartDelay = "FollowUpReminder_StartDelaySeconds";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FollowUpReminderService> _logger;

    public FollowUpReminderService(IServiceScopeFactory scopeFactory, ILogger<FollowUpReminderService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后稍等再开始（默认 60 秒，避免与应用初始化争抢；可用参数 FollowUpReminder_StartDelaySeconds 调整）
        var delaySeconds = await ReadStartDelayAsync(stoppingToken);
        if (delaySeconds > 0)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (sent, message) = await TickAsync(ct: stoppingToken);
                if (sent) _logger.LogInformation("[跟进提醒] 已推送：{Message}", message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[跟进提醒] 本轮检查失败（不影响应用运行）");
            }

            try { await Task.Delay(CheckInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>
    /// 单次检查：到点且当日未发送时，汇总到期跟进并推送钉钉。
    /// force = true 时忽略「是否启用 / 是否到点 / 今日是否已发送」判断，用于人工手动补推。
    /// </summary>
    public async Task<(bool Sent, string Message)> TickAsync(bool force = false, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IErpDbContext>();
        var dingTalk = scope.ServiceProvider.GetRequiredService<DingTalkService>();

        var keys = new[] { KeyEnabled, KeyTime, KeyLastSent };
        var paramList = await db.SysParameters
            .Where(p => !p.IsDeleted && keys.Contains(p.ParamKey))
            .ToListAsync(ct);
        string Val(string key) => paramList.FirstOrDefault(p => p.ParamKey == key)?.ParamValue ?? string.Empty;

        if (!force && Val(KeyEnabled) != "1")
            return (false, "未启用跟进提醒（参数 FollowUpReminder_Enabled 需设为 1）");

        var timeText = Val(KeyTime);
        if (!TimeSpan.TryParse(string.IsNullOrWhiteSpace(timeText) ? "09:00" : timeText, out var sendAt))
            sendAt = new TimeSpan(9, 0, 0);

        var now = DateTime.Now;
        var today = now.Date;
        if (!force && now.TimeOfDay < sendAt) return (false, $"未到提醒时间（{sendAt:hh\\:mm}）");
        if (!force && Val(KeyLastSent) == today.ToString("yyyy-MM-dd")) return (false, "今日已发送过");

        var dueList = await db.CustomerFollowUps
            .Where(x => !x.IsDeleted && x.NextFollowDate != null && x.NextFollowDate <= today)
            .OrderBy(x => x.NextFollowDate)
            .Take(50)
            .ToListAsync(ct);
        if (dueList.Count == 0) return (false, "无到期跟进记录");

        var title = $"客户跟进提醒（{dueList.Count} 条待回访）";
        var lines = dueList.Select((x, i) =>
        {
            var next = x.NextFollowDate!.Value.Date;
            var days = (today - next).Days;
            var overdue = days > 0 ? $"逾期 {days} 天" : "今日到期";
            return $"{i + 1}. {x.CustomerName}｜{x.SalesmanName}｜{overdue}｜{x.Subject}";
        });
        var content = $"【客户跟进提醒】截至 {today:yyyy-MM-dd}，有 {dueList.Count} 条客户跟进已到期或逾期，请及时回访：\n"
                      + string.Join("\n", lines);

        var cfg = await dingTalk.GetConfigAsync();
        var (ok, message) = await dingTalk.SendAsync(title, content);

        db.SysDingTalkLogs.Add(new SysDingTalkLog
        {
            BillType = "follow-up",
            BillTypeName = "客户跟进提醒",
            BillNo = today.ToString("yyyyMMdd"),
            ActionCode = "follow-up-reminder",
            ActionName = "跟进提醒",
            Operator = "system",
            Title = title,
            Content = content.Length > 2000 ? content[..2000] : content,
            MsgType = cfg.TryGetValue(DingTalkService.KeyMsgType, out var mt) && !string.IsNullOrWhiteSpace(mt) ? mt : "text",
            Webhook = cfg.TryGetValue(DingTalkService.KeyWebhook, out var wh) ? wh : string.Empty,
            Success = ok,
            ErrorMessage = ok ? string.Empty : message,
            SentAt = DateTime.Now
        });
        await db.SaveChangesAsync(ct);

        if (ok) await SaveLastSentAsync(db, today, ct);
        return (ok, ok ? $"已推送 {dueList.Count} 条待回访记录：{message}" : $"推送失败：{message}");
    }

    /// <summary>读取启动延迟秒数（读取失败时回退默认 60 秒，绝不因参数异常影响应用启动）</summary>
    private async Task<int> ReadStartDelayAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IErpDbContext>();
            var p = await db.SysParameters.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ParamKey == KeyStartDelay && !x.IsDeleted, ct);
            if (p is not null && int.TryParse(p.ParamValue, out var seconds) && seconds >= 0 && seconds <= 3600)
                return seconds;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[跟进提醒] 读取启动延迟参数失败，使用默认 60 秒");
        }
        return 60;
    }

    /// <summary>记录最近一次成功发送日期（幂等：存在则更新，不存在则新增）</summary>
    private static async Task SaveLastSentAsync(IErpDbContext db, DateTime day, CancellationToken ct)
    {
        var value = day.ToString("yyyy-MM-dd");
        var entity = await db.SysParameters.FirstOrDefaultAsync(p => p.ParamKey == KeyLastSent && !p.IsDeleted, ct);
        if (entity is null)
        {
            db.SysParameters.Add(new SysParameter
            {
                ParamKey = KeyLastSent,
                ParamValue = value,
                ParamName = "跟进提醒-最近发送日期",
                Description = "内部使用：跟进提醒最近一次成功推送的日期",
                IsSystem = true,
                CreatedAt = DateTime.Now
            });
        }
        else
        {
            entity.ParamValue = value;
            entity.UpdatedAt = DateTime.Now;
        }
        await db.SaveChangesAsync(ct);
    }
}
