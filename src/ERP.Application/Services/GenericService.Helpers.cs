using System.Linq.Expressions;

namespace ERP.Application.Services;

/// <summary>
/// 通用 CRUD 服务辅助方法：表达式构建、审计字段、属性拷贝
/// </summary>
public partial class GenericService<TEntity> where TEntity : class
{
    private static Expression<Func<TEntity, bool>> IsNotDeleted()
    {
        var p = Expression.Parameter(typeof(TEntity), "e");
        var body = Expression.Equal(
            Expression.Property(p, "IsDeleted"),
            Expression.Constant(false));
        return Expression.Lambda<Func<TEntity, bool>>(body, p);
    }

    private Expression<Func<TEntity, bool>> BuildKeywordPredicate(string keyword)
    {
        var p = Expression.Parameter(typeof(TEntity), "e");
        Expression? body = null;
        var containsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) })!;

        foreach (var prop in typeof(TEntity).GetProperties()
                     .Where(x => x.PropertyType == typeof(string) && x.CanRead))
        {
            var propExpr = Expression.Property(p, prop);
            var call = Expression.Call(propExpr, containsMethod, Expression.Constant(keyword));
            body = body is null ? call : Expression.OrElse(body, call);
        }

        if (body is null)
            return e => true;
        return Expression.Lambda<Func<TEntity, bool>>(body, p);
    }

    private static long GetId(TEntity entity)
    {
        var prop = typeof(TEntity).GetProperty("Id")
            ?? throw new InvalidOperationException("实体缺少主键 Id 属性");
        return (long)prop.GetValue(entity)!;
    }

    private static void SetAudit(TEntity entity, bool isNew)
    {
        var now = DateTime.Now;
        SetProperty(entity, "CreatedAt", now, onlyIfNull: isNew);
        SetProperty(entity, "UpdatedAt", now);
    }

    private static void SetDeleted(TEntity entity)
    {
        SetProperty(entity, "IsDeleted", true);
        SetProperty(entity, "UpdatedAt", DateTime.Now);
    }

    private static bool IsDeleted(TEntity entity)
        => (bool)(typeof(TEntity).GetProperty("IsDeleted")?.GetValue(entity) ?? false);

    private static void CopyProperties(TEntity source, TEntity target)
    {
        foreach (var prop in typeof(TEntity).GetProperties())
        {
            // 跳过主键、审计、并发令牌字段
            if (prop.Name is "Id" or "CreatedAt" or "CreatedBy" or "UpdatedAt" or "UpdatedBy" or "IsDeleted" or "RowVersion")
                continue;
            if (!prop.CanWrite || !prop.CanRead)
                continue;
            prop.SetValue(target, prop.GetValue(source));
        }
    }

    private static void SetProperty(TEntity entity, string name, object value, bool onlyIfNull = false)
    {
        var prop = typeof(TEntity).GetProperty(name);
        if (prop is null || !prop.CanWrite)
            return;
        if (onlyIfNull && prop.GetValue(entity) is not null && IsNonDefault(prop.GetValue(entity)!))
            return;
        prop.SetValue(entity, value);
    }

    private static bool IsNonDefault(object value)
        => value is DateTime dt ? dt != default : value is not null;
}
