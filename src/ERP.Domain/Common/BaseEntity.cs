using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Common;

/// <summary>
/// 实体基类：包含主键、审计字段、软删除标记、乐观并发令牌
/// </summary>
public abstract class BaseEntity
{
    /// <summary>主键（自增）</summary>
    [Key]
    public long Id { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>创建人 Id</summary>
    public long? CreatedBy { get; set; }

    /// <summary>更新时间</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>更新人 Id</summary>
    public long? UpdatedBy { get; set; }

    /// <summary>软删除标记（0=正常，1=已删除）</summary>
    public bool IsDeleted { get; set; }

    /// <summary>乐观并发控制令牌</summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }
}
