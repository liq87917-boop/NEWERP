using ERP.Application.Common;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace ERP.Api.Controllers;

/// <summary>
/// 单据元数据（存储过程驱动单据）
/// </summary>
public class BillMeta
{
    public string Table { get; set; } = string.Empty;
    public string Proc { get; set; } = string.Empty;
    public string? DetailTable { get; set; }
    public string? DetailFk { get; set; }
}

/// <summary>
/// 通用单据控制器（存储过程驱动：保存/删除/审核/作废/还原 + 查询 + 翻页）
/// </summary>
[ApiController]
[Route("api/v2/bills")]
[Authorize]
public partial class BillProcController : ControllerBase
{
    private readonly StoredProcedureService _sp;
    private readonly ERP.Application.Interfaces.IErpDbContext _db;
    private readonly Services.DingTalkService _dingTalk;

    /// <summary>单据元数据配置</summary>
    private static readonly Dictionary<string, BillMeta> Bills = new()
    {
        ["sales-order"] = new BillMeta { Table = "SalesOrder", Proc = "db_owner.sp_Biz_SalesOrder", DetailTable = "SalesOrderDetail", DetailFk = "SalesOrderId" },
        ["purchase-order"] = new BillMeta { Table = "PurchaseOrder", Proc = "db_owner.sp_Biz_PurchaseOrder" },
        ["inquiry"] = new BillMeta { Table = "Inquiry", Proc = "db_owner.sp_Biz_Inquiry" },
        ["stock-in"] = new BillMeta { Table = "StockIn", Proc = "db_owner.sp_Biz_StockIn" },
        ["stock-out"] = new BillMeta { Table = "StockOut", Proc = "db_owner.sp_Biz_StockOut" },
        ["receipt"] = new BillMeta { Table = "FinanceReceipt", Proc = "db_owner.sp_Biz_FinanceReceipt" },
        ["payment"] = new BillMeta { Table = "FinancePayment", Proc = "db_owner.sp_Biz_FinancePayment" },
        ["deposit-apply"] = new BillMeta { Table = "FinanceDepositApply", Proc = "db_owner.sp_Biz_FinanceDepositApply" },
        ["payment-apply"] = new BillMeta { Table = "FinancePaymentApply", Proc = "db_owner.sp_Biz_FinancePaymentApply" },
        ["container-settlement"] = new BillMeta { Table = "FinanceContainerSettlement", Proc = "db_owner.sp_Biz_FinanceContainerSettlement" },
        ["bulk-settlement"] = new BillMeta { Table = "FinanceBulkSettlement", Proc = "db_owner.sp_Biz_FinanceBulkSettlement" },
        ["complaint"] = new BillMeta { Table = "FinanceComplaint", Proc = "db_owner.sp_Biz_FinanceComplaint" },
        ["receiving-plan"] = new BillMeta { Table = "ContainerReceivingPlan", Proc = "db_owner.sp_Biz_ContainerReceivingPlan" },
        ["booking"] = new BillMeta { Table = "ContainerBooking", Proc = "db_owner.sp_Biz_ContainerBooking" },
        ["pre-loading"] = new BillMeta { Table = "ContainerPreLoading", Proc = "db_owner.sp_Biz_ContainerPreLoading", DetailTable = "ContainerPreLoadingDetail", DetailFk = "PreLoadingId" },
        ["loading-list"] = new BillMeta { Table = "ContainerLoadingList", Proc = "db_owner.sp_Biz_ContainerLoadingList", DetailTable = "ContainerLoadingDetail", DetailFk = "LoadingListId" },
    };

    public BillProcController(StoredProcedureService sp, ERP.Application.Interfaces.IErpDbContext db,
        Services.DingTalkService dingTalk)
    {
        _sp = sp;
        _db = db;
        _dingTalk = dingTalk;
    }

    /// <summary>保存（新增/更新）</summary>
    [HttpPost("{billType}/save")]
    public async Task<IActionResult> Save(string billType, [FromBody] BillSaveRequest request)
    {
        if (!Bills.TryGetValue(billType, out var meta))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));

        var parameters = new Dictionary<string, object?> { ["@Action"] = "Save", ["@Oid"] = request.Oid };
        foreach (var kv in request.Fields)
            parameters["@" + kv.Key] = kv.Value;

        // 兜底：币种/汇率为空或无效时，回退到系统参数默认值（用户手动传入的值优先）
        await ApplyCurrencyDefaultsAsync(parameters);

        // 明细（副表）序列化为 JSON，供存储过程 OPENJSON 解析
        if (request.Details is { Count: > 0 })
            parameters["@DetailsJson"] = System.Text.Json.JsonSerializer.Serialize(request.Details);

        var result = await _sp.ExecuteAsync(meta.Proc, parameters);
        if (!result.Success)
            return Ok(ApiResponse<object>.Fail(result.Msg, ErrorCodes.RuleConflict));

        // 记录单据级操作日志（保存/新增）
        await WriteBillLogAsync(billType, result.Oid, result.BillNo, request.Oid > 0 ? "Update" : "Save");

        // 钉钉通知（新增 / 保存；失败不影响业务）
        await NotifyDingTalkAsync(billType, result.BillNo, "save", request.Oid > 0 ? "Update" : "Save");
        return Ok(ApiResponse<object>.Success(new { Oid = result.Oid, BillNo = result.BillNo }, "保存成功"));
    }

    /// <summary>状态流转（Delete/Audit/UnAudit/Void/Restore）</summary>
    /// <remarks>
    /// 路由变量使用 op 而非 action：MVC 中 action 属于保留路由值（会被解析为动作名），
    /// 使用 action 作为路由变量名会导致 405（找不到对应动作）。
    /// </remarks>
    [HttpPost("{billType}/{oid:long}/{op}")]
    public async Task<IActionResult> Action(string billType, long oid, [FromRoute(Name = "op")] string op)
    {
        if (!Bills.TryGetValue(billType, out var meta))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));
        if (op is not ("delete" or "audit" or "unaudit" or "void" or "restore"))
            return Ok(ApiResponse<object>.Fail("无效操作", ErrorCodes.InvalidParameter));

        var actionMap = new Dictionary<string, string>
        {
            ["delete"] = "Delete", ["audit"] = "Audit",
            ["unaudit"] = "UnAudit", ["void"] = "Void", ["restore"] = "Restore"
        };
        var result = await _sp.ExecuteAsync(meta.Proc, new Dictionary<string, object?>
        {
            ["@Action"] = actionMap[op],
            ["@Oid"] = oid
        });
        if (!result.Success)
            return Ok(ApiResponse<object>.Fail(result.Msg, ErrorCodes.RuleConflict));

        // 结果校验：确保状态流转真正生效（未执行数据库升级脚本时及时给出明确提示）
        var verified = await VerifyActionAsync(meta.Table, oid, op);
        if (!verified)
        {
            var hint = op == "unaudit"
                ? "销审未生效：请先在数据库执行升级脚本 deploy/init4.sql（为业务单据存储过程增加销审分支）"
                : "操作未生效：单据状态与预期不符，请刷新后重试";
            Serilog.Log.Warning("单据状态流转未生效：{BillType} / {Oid} / {Op}", billType, oid, op);
            return Ok(ApiResponse<object>.Fail(hint, ErrorCodes.RuleConflict));
        }

        // 记录单据级操作日志（成功才记录），便于按单据号追溯
        var billNo = await ReadBillNoAsync(meta.Table, oid);
        await WriteBillLogAsync(billType, oid, billNo, actionMap[op]);

        // 钉钉通知（审核 / 销审 / 作废 / 还原 / 删除；失败不影响业务）
        await NotifyDingTalkAsync(billType, billNo, op, actionMap[op]);
        return Ok(ApiResponse<object>.Success(null, "操作成功"));
    }

    /// <summary>推送单据业务变化到钉钉（任何异常都不影响主流程；配置未启用或规则不匹配时静默跳过）</summary>
    private async Task NotifyDingTalkAsync(string billType, string billNo, string actionCode, string actionKey)
    {
        try
        {
            var typeName = BillTitles.TryGetValue(billType, out var t) ? t : billType;
            var actionName = ActionTitles.TryGetValue(actionKey, out var a) ? a : actionKey;
            var userName = User?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                           ?? User?.Identity?.Name ?? string.Empty;
            await _dingTalk.NotifyBillActionAsync(billType, typeName, billNo, actionCode, actionName, userName);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "钉钉通知调用失败：{BillType} / {BillNo} / {Action}", billType, billNo, actionCode);
        }
    }

    /// <summary>校验状态流转结果（审核=2 / 销审=1 / 作废=-1 / 还原=1 / 删除=记录不存在）</summary>
    private async Task<bool> VerifyActionAsync(string table, long oid, string action)
    {
        try
        {
            var status = await ReadStatusAsync(table, oid);
            return action switch
            {
                "audit" => status == 2,
                "unaudit" => status == 1,
                "void" => status == -1,
                "restore" => status == 1,
                "delete" => status is null,
                _ => true
            };
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "校验单据状态失败：{Table} / {Oid}", table, oid);
            return true; // 校验失败不阻塞业务（以存储过程返回结果为准）
        }
    }

    /// <summary>读取单据当前状态（记录不存在时返回 null）</summary>
    private async Task<int?> ReadStatusAsync(string table, long oid)
    {
        using var conn = new SqlConnection(_sp.GetConnectionString());
        await conn.OpenAsync();
        using var cmd = new SqlCommand($"SELECT Status FROM db_owner.{table} WHERE Oid = @oid", conn);
        cmd.Parameters.AddWithValue("@oid", oid);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    /// <summary>获取单据默认值（默认币种、默认汇率，取自系统参数）</summary>
    [HttpGet("{billType}/defaults")]
    public async Task<IActionResult> GetDefaults(string billType)
    {
        if (!Bills.TryGetValue(billType, out _))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));

        var (currency, exchangeRate) = await LoadCurrencyDefaultsAsync();
        return Ok(ApiResponse<object>.Success(new { currency, exchangeRate }));
    }

    /// <summary>从系统参数读取默认币种与默认汇率</summary>
    private async Task<(int Currency, decimal ExchangeRate)> LoadCurrencyDefaultsAsync()
    {
        var currency = (int)Currency.USD;
        var exchangeRate = 1m;

        using var conn = new SqlConnection(_sp.GetConnectionString());
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "SELECT ParamKey, ParamValue FROM db_owner.SysParameters WHERE IsDeleted = 0 AND ParamKey IN ('DefaultCurrency','ExchangeRate')",
            conn);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var key = reader.GetString(0);
            var value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (key == "DefaultCurrency")
                currency = MapCurrencyCode(value);
            else if (key == "ExchangeRate" && decimal.TryParse(value, out var rate) && rate > 0)
                exchangeRate = rate;
        }
        return (currency, exchangeRate);
    }

    /// <summary>币种代码映射为枚举值（默认美元）</summary>
    private static int MapCurrencyCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "CNY" => (int)Currency.CNY,
        "USD" => (int)Currency.USD,
        "EUR" => (int)Currency.EUR,
        "HKD" => (int)Currency.HKD,
        "GBP" => (int)Currency.GBP,
        "JPY" => (int)Currency.JPY,
        _ => (int)Currency.USD
    };

    /// <summary>币种/汇率为空或无效时，回退到系统参数默认值</summary>
    private async Task ApplyCurrencyDefaultsAsync(Dictionary<string, object?> parameters)
    {
        var hasCurrency = parameters.TryGetValue("@Currency", out var cur) && IsPositiveInteger(cur);
        var hasExchangeRate = parameters.TryGetValue("@ExchangeRate", out var rate) && IsPositiveDecimal(rate);
        if (hasCurrency && hasExchangeRate) return;

        var (defaultCurrency, defaultRate) = await LoadCurrencyDefaultsAsync();
        if (!hasCurrency) parameters["@Currency"] = defaultCurrency;
        if (!hasExchangeRate) parameters["@ExchangeRate"] = defaultRate;
    }

    /// <summary>判断是否为正整数</summary>
    private static bool IsPositiveInteger(object? value) => value switch
    {
        int i => i > 0,
        long l => l > 0,
        System.Text.Json.JsonElement je => je.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when je.TryGetInt64(out var l) => l > 0,
            System.Text.Json.JsonValueKind.String when int.TryParse(je.GetString(), out var n) => n > 0,
            _ => false
        },
        string s => int.TryParse(s, out var n) && n > 0,
        _ => false
    };

    /// <summary>判断是否为正数</summary>
    private static bool IsPositiveDecimal(object? value) => value switch
    {
        decimal d => d > 0,
        double d => d > 0,
        float f => f > 0,
        int i => i > 0,
        long l => l > 0,
        System.Text.Json.JsonElement je => je.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when je.TryGetDecimal(out var d) => d > 0,
            System.Text.Json.JsonValueKind.String when decimal.TryParse(je.GetString(), out var n) => n > 0,
            _ => false
        },
        string s => decimal.TryParse(s, out var n) && n > 0,
        _ => false
    };
}

/// <summary>单据保存请求</summary>
public class BillSaveRequest
{
    public long Oid { get; set; }
    public Dictionary<string, object?> Fields { get; set; } = new();
    public List<Dictionary<string, object?>>? Details { get; set; }
}
