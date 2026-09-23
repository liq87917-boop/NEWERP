using ERP.Application.Common;
using ERP.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace ERP.Application.Services;

/// <summary>
/// 通用 CRUD 服务实现：适用于基础资料等简单实体，统一处理分页、软删除、审计字段
/// </summary>
public partial class GenericService<TEntity> : IGenericService<TEntity> where TEntity : class
{
    private readonly IErpDbContext _db;
    private readonly DbSet<TEntity> _set;

    public GenericService(IErpDbContext db)
    {
        _db = db;
        _set = GetDbSet(db);
    }

    /// <summary>从数据上下文接口中反射获取对应的 DbSet</summary>
    private static DbSet<TEntity> GetDbSet(IErpDbContext db)
    {
        var prop = typeof(IErpDbContext).GetProperties()
            .FirstOrDefault(p => p.PropertyType == typeof(DbSet<TEntity>))
            ?? throw new InvalidOperationException($"数据上下文中未找到实体 {typeof(TEntity).Name} 对应的 DbSet");
        return (DbSet<TEntity>)prop.GetValue(db)!;
    }

    /// <summary>分页查询</summary>
    public async Task<PagedResult<TEntity>> GetPagedAsync(PageQuery query, Expression<Func<TEntity, bool>>? filter = null)
    {
        query.Normalize();

        var source = _set.AsNoTracking().Where(IsNotDeleted());
        if (filter is not null)
            source = source.Where(filter);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
            source = source.Where(BuildKeywordPredicate(query.Keyword));

        var total = await source.CountAsync();
        var items = await source
            .OrderByDescending(e => EF.Property<long>(e, "Id"))
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        return new PagedResult<TEntity>
        {
            Items = items,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <summary>根据主键获取</summary>
    public async Task<TEntity> GetByIdAsync(long id)
    {
        var entity = await _set.FindAsync(id)
            ?? throw BusinessException.NotFound();
        if (IsDeleted(entity))
            throw BusinessException.NotFound();
        return entity;
    }

    /// <summary>新增</summary>
    public async Task<TEntity> CreateAsync(TEntity entity)
    {
        SetAudit(entity, isNew: true);
        _set.Add(entity);
        await _db.SaveChangesAsync();
        return entity;
    }

    /// <summary>更新</summary>
    public async Task<TEntity> UpdateAsync(TEntity entity)
    {
        var id = GetId(entity);
        var existing = await _set.FindAsync(id)
            ?? throw BusinessException.NotFound();

        CopyProperties(entity, existing);
        SetAudit(existing, isNew: false);
        await _db.SaveChangesAsync();
        return existing;
    }

    /// <summary>删除（软删除）</summary>
    public async Task DeleteAsync(long id)
    {
        var entity = await _set.FindAsync(id) ?? throw BusinessException.NotFound();
        SetDeleted(entity);
        await _db.SaveChangesAsync();
    }

    /// <summary>批量删除（软删除）</summary>
    public async Task BatchDeleteAsync(IEnumerable<long> ids)
    {
        foreach (var id in ids)
        {
            var entity = await _set.FindAsync(id);
            if (entity is not null)
                SetDeleted(entity);
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>查询全部</summary>
    public async Task<List<TEntity>> GetAllAsync(Expression<Func<TEntity, bool>>? filter = null)
    {
        var source = _set.AsNoTracking().Where(IsNotDeleted());
        if (filter is not null)
            source = source.Where(filter);
        return await source.OrderByDescending(e => EF.Property<long>(e, "Id")).ToListAsync();
    }
}
