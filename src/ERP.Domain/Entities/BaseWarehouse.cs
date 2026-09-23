using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 费用科目
/// </summary>
public class BaseExpenseAccount : BaseEntity
{
    /// <summary>科目编码（唯一）</summary>
    [Required, MaxLength(50)]
    public string AccountCode { get; set; } = string.Empty;

    /// <summary>科目名称</summary>
    [Required, MaxLength(100)]
    public string AccountName { get; set; } = string.Empty;

    /// <summary>科目类型（1=收入，2=支出）</summary>
    public int AccountType { get; set; } = 2;

    /// <summary>科目说明</summary>
    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    /// <summary>父级科目 Id（多级分组，顶级科目为空）</summary>
    public long? ParentId { get; set; }

    /// <summary>状态（1=启用，0=停用）</summary>
    public int Status { get; set; } = 1;
}

/// <summary>
/// 仓库资料
/// </summary>
public class BaseWarehouse : BaseEntity
{
    /// <summary>仓库编码（唯一）</summary>
    [Required, MaxLength(50)]
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>仓库名称</summary>
    [Required, MaxLength(100)]
    public string WarehouseName { get; set; } = string.Empty;

    /// <summary>仓库地址</summary>
    [MaxLength(500)]
    public string Address { get; set; } = string.Empty;

    /// <summary>负责人</summary>
    [MaxLength(50)]
    public string Manager { get; set; } = string.Empty;

    /// <summary>联系电话</summary>
    [MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    /// <summary>状态（1=启用，0=停用）</summary>
    public int Status { get; set; } = 1;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
