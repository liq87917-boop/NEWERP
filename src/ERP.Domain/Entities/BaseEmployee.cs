using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 员工资料
/// </summary>
public class BaseEmployee : BaseEntity
{
    /// <summary>员工编码（唯一）</summary>
    [Required, MaxLength(50)]
    public string EmployeeCode { get; set; } = string.Empty;

    /// <summary>员工姓名</summary>
    [Required, MaxLength(50)]
    public string EmployeeName { get; set; } = string.Empty;

    /// <summary>部门</summary>
    [MaxLength(100)]
    public string Department { get; set; } = string.Empty;

    /// <summary>职位</summary>
    [MaxLength(100)]
    public string Position { get; set; } = string.Empty;

    /// <summary>联系电话</summary>
    [MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    /// <summary>邮箱</summary>
    [MaxLength(100)]
    public string Email { get; set; } = string.Empty;

    /// <summary>入职日期</summary>
    public DateTime? HireDate { get; set; }

    /// <summary>状态（1=在职，0=离职）</summary>
    public int Status { get; set; } = 1;

    /// <summary>是否业务员（参与产值统计）</summary>
    public bool IsSalesman { get; set; }
}
