using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 系统参数（全局配置）
/// </summary>
public class SysParameter : BaseEntity
{
    /// <summary>参数键（唯一）</summary>
    [Required, MaxLength(100)]
    public string ParamKey { get; set; } = string.Empty;

    /// <summary>参数值</summary>
    [MaxLength(500)]
    public string ParamValue { get; set; } = string.Empty;

    /// <summary>参数名称</summary>
    [Required, MaxLength(100)]
    public string ParamName { get; set; } = string.Empty;

    /// <summary>参数说明</summary>
    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    /// <summary>是否系统内置（内置不可删除）</summary>
    public bool IsSystem { get; set; }
}

/// <summary>
/// 用户参数（用户级个性化配置）
/// </summary>
public class SysUserParameter : BaseEntity
{
    /// <summary>用户 Id</summary>
    public long UserId { get; set; }

    /// <summary>参数键</summary>
    [Required, MaxLength(100)]
    public string ParamKey { get; set; } = string.Empty;

    /// <summary>参数值</summary>
    [MaxLength(500)]
    public string ParamValue { get; set; } = string.Empty;
}

/// <summary>
/// 单据号规则
/// </summary>
public class SysDocumentNumberRule : BaseEntity
{
    /// <summary>单据类型</summary>
    public DocumentType DocumentType { get; set; }

    /// <summary>规则编码（唯一）</summary>
    [Required, MaxLength(50)]
    public string RuleCode { get; set; } = string.Empty;

    /// <summary>规则名称</summary>
    [Required, MaxLength(100)]
    public string RuleName { get; set; } = string.Empty;

    /// <summary>前缀（如 INQ、SO、PO）</summary>
    [MaxLength(20)]
    public string Prefix { get; set; } = string.Empty;

    /// <summary>日期格式（如 yyyyMMdd，可空）</summary>
    [MaxLength(20)]
    public string? DateFormat { get; set; }

    /// <summary>流水号位数（不足补零）</summary>
    public int SerialLength { get; set; } = 4;

    /// <summary>分隔符</summary>
    [MaxLength(5)]
    public string Separator { get; set; } = string.Empty;

    /// <summary>当前流水号（自增基数）</summary>
    public long CurrentSequence { get; set; }

    /// <summary>是否按年重置流水号</summary>
    public bool YearlyReset { get; set; } = true;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 客户端访问限制
/// </summary>
public class SysClientLimit : BaseEntity
{
    /// <summary>限制类型</summary>
    public ClientLimitType LimitType { get; set; }

    /// <summary>限制值（IP 或机器码）</summary>
    [Required, MaxLength(200)]
    public string LimitValue { get; set; } = string.Empty;

    /// <summary>是否启用</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 系统操作日志
/// </summary>
public class SysOperationLog : BaseEntity
{
    /// <summary>操作用户 Id</summary>
    public long? UserId { get; set; }

    /// <summary>操作用户名</summary>
    [MaxLength(50)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>所属模块</summary>
    [MaxLength(50)]
    public string Module { get; set; } = string.Empty;

    /// <summary>操作动作</summary>
    [MaxLength(50)]
    public string Action { get; set; } = string.Empty;

    /// <summary>HTTP 方法</summary>
    [MaxLength(10)]
    public string Method { get; set; } = string.Empty;

    /// <summary>请求路径</summary>
    [MaxLength(500)]
    public string Path { get; set; } = string.Empty;

    /// <summary>关联单据号（业务单据操作日志专用，便于按单据追溯）</summary>
    [MaxLength(100)]
    public string BillNo { get; set; } = string.Empty;

    /// <summary>请求体</summary>
    public string? RequestBody { get; set; }

    /// <summary>客户端 IP</summary>
    [MaxLength(64)]
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>响应状态码</summary>
    public int StatusCode { get; set; }

    /// <summary>耗时（毫秒）</summary>
    public long DurationMs { get; set; }
}
