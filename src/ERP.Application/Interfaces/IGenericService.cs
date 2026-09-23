using ERP.Application.Common;
using System.Linq.Expressions;

namespace ERP.Application.Interfaces;

/// <summary>
/// 通用 CRUD 服务接口：适用于基础资料等简单实体
/// </summary>
public interface IGenericService<TEntity> where TEntity : class
{
    /// <summary>分页查询（支持关键字与过滤条件）</summary>
    Task<PagedResult<TEntity>> GetPagedAsync(PageQuery query, Expression<Func<TEntity, bool>>? filter = null);

    /// <summary>根据主键获取</summary>
    Task<TEntity> GetByIdAsync(long id);

    /// <summary>新增</summary>
    Task<TEntity> CreateAsync(TEntity entity);

    /// <summary>更新</summary>
    Task<TEntity> UpdateAsync(TEntity entity);

    /// <summary>删除（软删除）</summary>
    Task DeleteAsync(long id);

    /// <summary>批量删除（软删除）</summary>
    Task BatchDeleteAsync(IEnumerable<long> ids);

    /// <summary>查询全部（不分页，供下拉框等使用）</summary>
    Task<List<TEntity>> GetAllAsync(Expression<Func<TEntity, bool>>? filter = null);
}
