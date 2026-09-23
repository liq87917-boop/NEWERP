using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;

namespace ERP.Infrastructure.Data;

/// <summary>
/// 存储过程数据访问服务：封装业务存储过程（sp_Biz_xxx / sp_Plat_xxx）的调用
/// </summary>
public class StoredProcedureService
{
    private readonly string _connectionString;

    public StoredProcedureService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("未配置数据库连接字符串");
    }

    /// <summary>
    /// 执行业务存储过程（返回 Result 和 Msg 输出参数）
    /// </summary>
    /// <param name="procName">存储过程名</param>
    /// <param name="parameters">输入参数（键值对）</param>
    /// <returns>执行结果（含 Result、Msg、输出参数）</returns>
    public async Task<ProcResult> ExecuteAsync(string procName, IDictionary<string, object?> parameters)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(procName, conn)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 60
        };

        // 添加输入参数（跳过 @Oid/@BillNo，它们由下方 InputOutput 参数统一处理，避免重复添加）
        foreach (var kv in parameters)
        {
            if (kv.Key is "@Oid" or "@BillNo") continue;
            cmd.Parameters.AddWithValue(kv.Key, ConvertParamValue(kv.Value));
        }

        // 添加输出参数：Result / Msg / BillNo / Oid（如存在则声明为输出）
        var resultParam = cmd.Parameters.Add("@Result", SqlDbType.Int);
        resultParam.Direction = ParameterDirection.Output;

        var msgParam = cmd.Parameters.Add("@Msg", SqlDbType.NVarChar, 200);
        msgParam.Direction = ParameterDirection.Output;

        var billNoParam = cmd.Parameters.Add("@BillNo", SqlDbType.NVarChar, 100);
        billNoParam.Direction = ParameterDirection.InputOutput;

        var oidParam = cmd.Parameters.Add("@Oid", SqlDbType.BigInt);
        oidParam.Direction = ParameterDirection.InputOutput;

        // 若调用方已传入 @Oid，则保留其值（0 表示新增，必须显式赋 0，否则以 NULL 传入
        // 会导致存储过程中 IF @Oid = 0 判断失效（NULL 比较恒为 UNKNOWN），误走更新分支）
        var oidValue = parameters.TryGetValue("@Oid", out var rawOid) ? ConvertParamValue(rawOid) : null;
        oidParam.Value = oidValue is long oid ? oid : 0L;

        // 若调用方已传入 @BillNo，则保留其值
        if (parameters.TryGetValue("@BillNo", out var rawBillNo) &&
            ConvertParamValue(rawBillNo) is string billNo && !string.IsNullOrWhiteSpace(billNo))
            billNoParam.Value = billNo;

        await cmd.ExecuteNonQueryAsync();

        return new ProcResult
        {
            Result = (int)(resultParam.Value ?? 0),
            Msg = msgParam.Value?.ToString() ?? string.Empty,
            BillNo = billNoParam.Value?.ToString() ?? string.Empty,
            Oid = oidParam.Value is long o ? o : 0
        };
    }

    /// <summary>获取连接字符串（供查询使用）</summary>
    public string GetConnectionString() => _connectionString;

    /// <summary>转换参数值（处理 JsonElement 等类型到 CLR 原生类型）</summary>
    private static object? ConvertParamValue(object? value)
    {
        if (value is System.Text.Json.JsonElement je)
        {
            return je.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => je.GetString(),
                System.Text.Json.JsonValueKind.Number when je.TryGetInt64(out var l) => l,
                System.Text.Json.JsonValueKind.Number => je.GetDecimal(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => null,
                _ => je.GetRawText()
            };
        }
        return value ?? DBNull.Value;
    }

    /// <summary>
    /// 获取新主键 Oid（调用 sp_Plat_GetTableNewOid）
    /// </summary>
    public async Task<long> GetNewOidAsync(string tableName)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand("db_owner.sp_Plat_GetTableNewOid", conn)
        {
            CommandType = CommandType.StoredProcedure
        };
        cmd.Parameters.AddWithValue("@TableName", tableName);
        cmd.Parameters.AddWithValue("@FieldName", "Oid");
        var oidParam = cmd.Parameters.Add("@Oid", SqlDbType.BigInt);
        oidParam.Direction = ParameterDirection.Output;
        await cmd.ExecuteNonQueryAsync();
        return (long)(oidParam.Value ?? 0);
    }
}

/// <summary>
/// 存储过程执行结果
/// </summary>
public class ProcResult
{
    /// <summary>结果码（0=成功，负数=业务失败，-999=异常）</summary>
    public int Result { get; set; }

    /// <summary>提示信息</summary>
    public string Msg { get; set; } = string.Empty;

    /// <summary>生成的单据号</summary>
    public string BillNo { get; set; } = string.Empty;

    /// <summary>单据主键</summary>
    public long Oid { get; set; }

    /// <summary>是否成功</summary>
    public bool Success => Result == 0;
}
