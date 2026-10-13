using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 通用单据控制器：Excel 导入（下载导入模板 + 上传导入）
/// 导入规则：首行为系统导出的中文表头，逐行调用对应 sp_Biz_* 的 Save 动作写入
/// <para>ERP-495：模板下载属旧单据读侧，与查询 / 详情 / 默认值 / 导出同源，必须先通过既有读侧门禁
/// （实时身份 + 账号状态 + 该族既有功能菜单 + ERP-097 范围裁决）再生成任何列定义 / 示例行 / 工作簿字节。</para>
/// </summary>
public partial class BillProcController
{
    /// <summary>单次导入的最大行数（防止误操作批量写入过多数据）</summary>
    private const int MaxImportRows = 1000;

    /// <summary>下载导入模板（首行中文表头，可直接另存后填写；withSample=1 时附带一行示例数据）</summary>
    /// <remarks>
    /// ERP-495：与查询 / 详情 / 默认值 / 导出使用<b>同一份</b>既有读侧门禁
    /// （<see cref="LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync"/>：实时身份 + 账号状态 + 该族既有功能菜单 +
    /// ERP-097 业务员客户范围），并复用既有范围裁决（归属 / 受控上限）——被拒绝时返回既有受控失败信封
    /// （HTTP 200 + 业务错误码），绝不生成任何列定义、示例行或工作簿字节；未知单据类型仍返回既有 <c>1001</c> 提示。
    /// 模板内容（<c>ImportColumns</c> 派生自导出列并剔除系统字段、中文表头、withSample 仅日期列的示例行、
    /// 文件名与内容类型）对授权调用方保持完全不变；绝不新增菜单 / 授权，也绝不让 <c>*-export</c> 菜单顶替功能菜单。
    /// </remarks>
    [HttpGet("{billType}/import-template")]
    public async Task<IActionResult> DownloadImportTemplate(string billType, [FromQuery] bool withSample = false)
    {
        if (!Bills.TryGetValue(billType, out var meta))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));

        // ERP-495：模板下载属读侧，必须在生成任何列定义 / 示例行 / 工作簿字节之前 fail closed。
        try
        {
            var scope = await LegacyBillAuthorizationRules
                .EnsureReadAuthorizedAsync(_db, CurrentUserId(), billType);

            // 复用既有读侧范围裁决（行归属 / 受控上限）：受限账号在无权威客户归属的族或超限范围上 fail closed，
            // 绝不因为「模板不含行数据」而放开 ERP-405 / ERP-406 已建立的既有读侧边界。
            LegacyBillReadService.ResolveScopeClause(LegacyBillReadCatalog.Resolve(billType), scope);
        }
        catch (BusinessException ex)
        {
            LogLegacyReadDenied("导入模板", billType, ex);
            return Ok(ApiResponse<object>.Fail(ex.Message, ex.Code));
        }

        var columns = ImportColumns(billType);
        var sample = withSample ? BuildSampleRow(columns) : null;
        var bytes = ExcelImporter.BuildTemplate(meta.Table, columns, sample);
        var title = BillTitles.TryGetValue(billType, out var t) ? t : billType;
        var fileName = withSample ? $"{title}_导入示例_{DateTime.Now:yyyyMMdd}.xlsx" : $"{title}_导入模板_{DateTime.Now:yyyyMMdd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    /// <summary>构造示例行：仅填充日期类字段（其它字段留空，由存储过程使用默认值）</summary>
    private static Dictionary<string, string> BuildSampleRow(List<(string Key, string Title)> columns)
    {
        var sample = new Dictionary<string, string>();
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        foreach (var column in columns)
        {
            if (column.Key.EndsWith("Date", StringComparison.OrdinalIgnoreCase))
                sample[column.Key] = today;
        }
        return sample;
    }

    /// <summary>导入单据（Excel 首行中文表头，逐行调用存储过程保存）</summary>
    [HttpPost("{billType}/import")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> Import(string billType, IFormFile? file)
    {
        if (!Bills.TryGetValue(billType, out var meta))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));

        // ERP-404：Excel 导入与 Save 同源，必须在解析 / 默认值 / sp_Biz_* / 日志 / 通知之前 fail closed。
        var denied = await GuardLegacyMutationAsync(billType, LegacyBillOperation.Import);
        if (denied is not null) return denied;

        if (file is null || file.Length == 0)
            return Ok(ApiResponse<object>.Fail("请选择要导入的 Excel 文件", ErrorCodes.InvalidParameter));
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return Ok(ApiResponse<object>.Fail("仅支持 .xlsx 格式的 Excel 文件", ErrorCodes.InvalidParameter));

        var columns = ImportColumns(billType);
        var headerMap = columns.ToDictionary(c => c.Title, c => c.Key, StringComparer.OrdinalIgnoreCase);

        List<Dictionary<string, string>> rows;
        try
        {
            using var stream = file.OpenReadStream();
            rows = ExcelImporter.ReadRows(stream, headerMap);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Excel 导入解析失败：{BillType}", billType);
            return Ok(ApiResponse<object>.Fail("Excel 解析失败，请确认文件由「导入模板」填写，且未被其他程序占用"));
        }

        if (rows.Count == 0)
            return Ok(ApiResponse<object>.Fail("未读取到有效数据行，请核对表头是否与导入模板一致", ErrorCodes.InvalidParameter));
        if (rows.Count > MaxImportRows)
            return Ok(ApiResponse<object>.Fail($"单次导入不得超过 {MaxImportRows} 行，请拆分后分批导入", ErrorCodes.InvalidParameter));

        var success = 0;
        var failed = 0;
        var errors = new List<object>();

        for (var i = 0; i < rows.Count; i++)
        {
            var parameters = new Dictionary<string, object?> { ["@Action"] = "Save", ["@Oid"] = 0L };
            foreach (var kv in rows[i])
            {
                var value = ValueNormalizer.ToParameter(kv.Key, kv.Value);
                if (value is not null) parameters["@" + kv.Key] = value;
            }
            await ApplyCurrencyDefaultsAsync(parameters);

            try
            {
                var result = await _sp.ExecuteAsync(meta.Proc, parameters);
                if (result.Success)
                {
                    success++;
                    await WriteBillLogAsync(billType, result.Oid, result.BillNo, "Save");
                }
                else
                {
                    failed++;
                    if (errors.Count < 50)
                        errors.Add(new { row = i + 2, message = result.Msg });
                }
            }
            catch (Exception ex)
            {
                failed++;
                if (errors.Count < 50)
                    errors.Add(new { row = i + 2, message = ex.Message });
            }
        }

        var title = BillTitles.TryGetValue(billType, out var t) ? t : billType;
        var message = $"「{title}」导入完成：成功 {success} 条，失败 {failed} 条";
        Serilog.Log.Information("{Message}", message);
        return Ok(ApiResponse<object>.Success(new { total = rows.Count, success, failed, errors }, message));
    }

    /// <summary>获取导入列定义（复用导出列配置，自动剔除系统字段与状态列）</summary>
    private static List<(string Key, string Title)> ImportColumns(string billType)
    {
        if (!ExportColumns.TryGetValue(billType, out var columns))
            return new List<(string, string)>();
        return columns.Where(c => !ValueNormalizer.IsSystemField(c.Key)).ToList();
    }
}
