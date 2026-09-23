using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ERP.Api.Services;

/// <summary>
/// 钉钉通知服务：读取系统参数中的钉钉配置，按「单据类型 + 动作」规则把业务变化推送到钉钉群机器人
/// <para>配置项（SysParameters.ParamKey）：DingTalk_Enabled / DingTalk_Webhook / DingTalk_Secret /</para>
/// <para>DingTalk_MsgType(text|markdown) / DingTalk_AtMobiles / DingTalk_AtAll / DingTalk_Rules / DingTalk_Template</para>
/// <para>DingTalk_Rules 形如 {"sales-order":["audit","void"],"*":["audit"]}，留空表示全部单据全部动作都推送</para>
/// </summary>
public class DingTalkService
{
    public const string KeyEnabled = "DingTalk_Enabled";
    public const string KeyWebhook = "DingTalk_Webhook";
    public const string KeySecret = "DingTalk_Secret";
    public const string KeyMsgType = "DingTalk_MsgType";
    public const string KeyAtMobiles = "DingTalk_AtMobiles";
    public const string KeyAtAll = "DingTalk_AtAll";
    public const string KeyRules = "DingTalk_Rules";
    public const string KeyTemplate = "DingTalk_Template";

    public static readonly string[] AllKeys =
    {
        KeyEnabled, KeyWebhook, KeySecret, KeyMsgType, KeyAtMobiles, KeyAtAll, KeyRules, KeyTemplate
    };

    /// <summary>默认消息模板（markdown）</summary>
    public const string DefaultTemplate =
        "### 🔔 单据状态变更提醒\n" +
        "- **单据类型**：{billType}\n" +
        "- **单据号**：{billNo}\n" +
        "- **操作动作**：{action}\n" +
        "- **操作人**：{user}\n" +
        "- **操作时间**：{time}";

    private readonly IErpDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DingTalkService> _logger;

    public DingTalkService(IErpDbContext db, IHttpClientFactory httpFactory, ILogger<DingTalkService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>读取全部钉钉配置（缺失项返回空串）</summary>
    public async Task<Dictionary<string, string>> GetConfigAsync()
    {
        var list = await _db.SysParameters.AsNoTracking()
            .Where(p => !p.IsDeleted && AllKeys.Contains(p.ParamKey))
            .ToListAsync();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in AllKeys) map[key] = string.Empty;
        foreach (var p in list) map[p.ParamKey] = p.ParamValue ?? string.Empty;
        if (string.IsNullOrWhiteSpace(map[KeyEnabled])) map[KeyEnabled] = "false";
        if (string.IsNullOrWhiteSpace(map[KeyMsgType])) map[KeyMsgType] = "markdown";
        if (string.IsNullOrWhiteSpace(map[KeyTemplate])) map[KeyTemplate] = DefaultTemplate;
        return map;
    }

    /// <summary>保存钉钉配置（仅接受白名单键）</summary>
    public async Task SaveConfigAsync(Dictionary<string, string> values)
    {
        foreach (var key in AllKeys)
        {
            if (!values.TryGetValue(key, out var value)) continue;
            value ??= string.Empty;
            var entity = await _db.SysParameters.FirstOrDefaultAsync(p => p.ParamKey == key && !p.IsDeleted);
            if (entity is null)
            {
                _db.SysParameters.Add(new SysParameter { ParamKey = key, ParamValue = value, CreatedAt = DateTime.Now });
            }
            else
            {
                entity.ParamValue = value;
                entity.UpdatedAt = DateTime.Now;
            }
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>发送消息（webhookOverride 用于「测试发送」时临时指定地址）</summary>
    public async Task<(bool Ok, string Message)> SendAsync(string title, string content, string? webhookOverride = null)
    {
        var cfg = await GetConfigAsync();
        var webhook = string.IsNullOrWhiteSpace(webhookOverride) ? cfg[KeyWebhook] : webhookOverride!;
        if (string.IsNullOrWhiteSpace(webhook)) return (false, "未配置钉钉 Webhook 地址");

        var secret = cfg[KeySecret];
        var msgType = (cfg[KeyMsgType] ?? "markdown").ToLowerInvariant();
        var atAll = string.Equals(cfg[KeyAtAll], "true", StringComparison.OrdinalIgnoreCase);
        var atMobiles = (cfg[KeyAtMobiles] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

        var url = BuildSignedUrl(webhook, secret);
        var payload = msgType == "text"
            ? (object)new { msgtype = "text", text = new { content = title + "\n" + content }, at = new { atMobiles, isAtAll = atAll } }
            : new { msgtype = "markdown", markdown = new { title, text = content }, at = new { atMobiles, isAtAll = atAll } };

        try
        {
            var client = _httpFactory.CreateClient("dingtalk");
            client.Timeout = TimeSpan.FromSeconds(10);
            var json = JsonSerializer.Serialize(payload);
            using var resp = await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return (false, $"HTTP {(int)resp.StatusCode}：{Truncate(text, 200)}");
            using var doc = JsonDocument.Parse(text);
            var code = doc.RootElement.TryGetProperty("errcode", out var c) ? c.GetInt32() : -1;
            var errmsg = doc.RootElement.TryGetProperty("errmsg", out var m) ? (m.GetString() ?? string.Empty) : string.Empty;
            return code == 0 ? (true, "发送成功") : (false, $"钉钉返回错误 {code}：{errmsg}");
        }
        catch (Exception ex)
        {
            return (false, "请求钉钉失败：" + ex.Message);
        }
    }

    /// <summary>单据业务动作通知（由 BillProcController 在保存 / 审核 / 销审 / 作废 / 还原 / 删除成功后调用）</summary>
    public async Task NotifyBillActionAsync(string billType, string billTypeName, string billNo,
        string actionCode, string actionName, string userName)
    {
        try
        {
            var cfg = await GetConfigAsync();
            if (!string.Equals(cfg[KeyEnabled], "true", StringComparison.OrdinalIgnoreCase)) return;
            if (!RuleMatch(cfg[KeyRules], billType, actionCode)) return;

            var time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var content = (cfg[KeyTemplate] ?? DefaultTemplate)
                .Replace("{billType}", billTypeName)
                .Replace("{billNo}", string.IsNullOrWhiteSpace(billNo) ? "-" : billNo)
                .Replace("{action}", actionName)
                .Replace("{user}", userName)
                .Replace("{time}", time)
                .Replace("{code}", billType);
            var title = $"{billTypeName} {actionName}";

            var (ok, msg) = await SendAsync(title, content);
            await WriteLogAsync(billType, billTypeName, billNo, actionCode, actionName,
                userName, title, content, cfg[KeyMsgType], cfg[KeyWebhook], ok, msg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "钉钉通知异常：{BillType} / {BillNo} / {Action}", billType, billNo, actionCode);
        }
    }

    /// <summary>重发指定记录（累加重试次数并更新结果）</summary>
    public async Task<(bool Ok, string Message)> ResendAsync(long logId)
    {
        var log = await _db.SysDingTalkLogs.FirstOrDefaultAsync(l => l.Id == logId && !l.IsDeleted);
        if (log is null) return (false, "记录不存在");
        var (ok, msg) = await SendAsync(log.Title, log.Content);
        log.Success = ok;
        log.ErrorMessage = ok ? string.Empty : Truncate(msg, 500);
        log.RetryCount += 1;
        log.SentAt = DateTime.Now;
        log.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return (ok, msg);
    }

    /// <summary>写入发送记录（Webhook 脱敏）</summary>
    private async Task WriteLogAsync(string billType, string billTypeName, string billNo, string actionCode,
        string actionName, string userName, string title, string content, string msgType, string webhook,
        bool ok, string message)
    {
        _db.SysDingTalkLogs.Add(new SysDingTalkLog
        {
            BillType = billType,
            BillTypeName = Truncate(billTypeName, 50),
            BillNo = Truncate(billNo, 100),
            ActionCode = actionCode,
            ActionName = actionName,
            Operator = Truncate(userName, 50),
            Title = Truncate(title, 200),
            Content = Truncate(content, 2000),
            MsgType = msgType,
            Webhook = MaskWebhook(webhook),
            Success = ok,
            ErrorMessage = ok ? string.Empty : Truncate(message, 500),
            SentAt = DateTime.Now,
            CreatedAt = DateTime.Now,
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>规则匹配：精确单据类型优先，其次通配 *；均未命中则不推送</summary>
    private static bool RuleMatch(string? rulesJson, string billType, string actionCode)
    {
        if (string.IsNullOrWhiteSpace(rulesJson)) return true;      // 未配置规则 = 全部推送
        try
        {
            using var doc = JsonDocument.Parse(rulesJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return true;
            if (root.TryGetProperty(billType, out var arr)) return ActionIn(arr, actionCode);
            if (root.TryGetProperty("*", out var all)) return ActionIn(all, actionCode);
            return false;
        }
        catch
        {
            return true;                                            // 规则格式错误时不阻断业务
        }
    }

    private static bool ActionIn(JsonElement arr, string actionCode)
    {
        if (arr.ValueKind != JsonValueKind.Array) return true;
        foreach (var item in arr.EnumerateArray())
        {
            if (string.Equals(item.GetString(), actionCode, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>钉钉加签：timestamp + secret → HMAC-SHA256 → Base64 → UrlEncode</summary>
    private static string BuildSignedUrl(string webhook, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return webhook;
        var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
        var stringToSign = timestamp + "\n" + secret;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret!));
        var sign = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        var sep = webhook.Contains('?') ? "&" : "?";
        return $"{webhook}{sep}timestamp={timestamp}&sign={Uri.EscapeDataString(sign)}";
    }

    /// <summary>Webhook 脱敏：仅保留域名与 token 的首尾字符</summary>
    private static string MaskWebhook(string webhook)
    {
        if (string.IsNullOrWhiteSpace(webhook)) return string.Empty;
        try
        {
            var uri = new Uri(webhook);
            var query = uri.Query.TrimStart('?');
            var token = string.Empty;
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("access_token", StringComparison.OrdinalIgnoreCase))
                {
                    token = Uri.UnescapeDataString(kv[1]);
                    break;
                }
            }
            var masked = token.Length > 12 ? token.Substring(0, 6) + "****" + token.Substring(token.Length - 4) : "****";
            return $"{uri.Host}{uri.AbsolutePath}?access_token={masked}";
        }
        catch
        {
            return Truncate(webhook, 60);
        }
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value.Substring(0, max);
    }
}
