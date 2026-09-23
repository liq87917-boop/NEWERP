using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Common;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 单据控制器基类：封装状态流转（提交/审核/取消）、软删除等通用逻辑
/// 派生类只需实现列表、详情、创建、更新
/// </summary>
[ApiController]
[Authorize]
public abstract class DocumentControllerBase<TEntity> : ControllerBase where TEntity : BaseEntity
{
    protected readonly IErpDbContext Db;

    protected DocumentControllerBase(IErpDbContext db)
    {
        Db = db;
    }

    /// <summary>获取当前实体对应的 DbSet</summary>
    protected DbSet<TEntity> Set => GetDbSet();

    private DbSet<TEntity> GetDbSet()
    {
        var prop = typeof(IErpDbContext).GetProperties()
            .FirstOrDefault(p => p.PropertyType == typeof(DbSet<TEntity>))
            ?? throw new InvalidOperationException($"未找到实体 {typeof(TEntity).Name} 的 DbSet");
        return (DbSet<TEntity>)prop.GetValue(Db)!;
    }

    /// <summary>根据主键获取实体（未找到抛业务异常）</summary>
    protected async Task<TEntity> GetOrThrowAsync(long id, string message)
    {
        var entity = await Set.FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted)
            ?? throw BusinessException.NotFound(message);
        return entity;
    }

    /// <summary>读取单据状态（反射）</summary>
    protected static DocumentStatus GetStatus(TEntity entity)
        => (DocumentStatus)typeof(TEntity).GetProperty("Status")!.GetValue(entity)!;

    /// <summary>设置单据状态并更新时间戳</summary>
    protected static void SetStatus(TEntity entity, DocumentStatus status)
    {
        typeof(TEntity).GetProperty("Status")!.SetValue(entity, status);
        typeof(TEntity).GetProperty("UpdatedAt")?.SetValue(entity, DateTime.Now);
    }

    /// <summary>通用状态流转（校验源状态）</summary>
    protected async Task<bool> ChangeStatusAsync(long id, DocumentStatus from, DocumentStatus to, string message)
    {
        var entity = await GetOrThrowAsync(id, message);
        if (GetStatus(entity) != from)
            throw BusinessException.RuleConflict("当前状态不允许该操作");
        SetStatus(entity, to);
        await Db.SaveChangesAsync();
        return true;
    }

    /// <summary>提交</summary>
    [HttpPost("{id:long}/submit")]
    public virtual async Task<IActionResult> Submit(long id)
    {
        await ChangeStatusAsync(id, DocumentStatus.Pending, DocumentStatus.Submitted, "单据不存在");
        return Ok(ApiResponse<object>.Success(null, "提交成功"));
    }

    /// <summary>审核</summary>
    [HttpPost("{id:long}/approve")]
    public virtual async Task<IActionResult> Approve(long id)
    {
        await ChangeStatusAsync(id, DocumentStatus.Submitted, DocumentStatus.Approved, "单据不存在");
        return Ok(ApiResponse<object>.Success(null, "审核通过"));
    }

    /// <summary>取消</summary>
    [HttpPost("{id:long}/cancel")]
    public virtual async Task<IActionResult> Cancel(long id)
    {
        var entity = await GetOrThrowAsync(id, "单据不存在");
        SetStatus(entity, DocumentStatus.Cancelled);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "已取消"));
    }

    /// <summary>删除（软删除，仅待提交状态可删）</summary>
    [HttpDelete("{id:long}")]
    public virtual async Task<IActionResult> Delete(long id)
    {
        var entity = await GetOrThrowAsync(id, "单据不存在");
        if (GetStatus(entity) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可删除");
        entity.IsDeleted = true;
        entity.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }
}
