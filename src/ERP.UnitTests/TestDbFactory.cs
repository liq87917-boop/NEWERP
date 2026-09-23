using ERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ERP.UnitTests;

/// <summary>
/// 测试数据库工厂：创建内存数据库上下文
/// </summary>
public static class TestDbFactory
{
    /// <summary>创建独立的内存数据库上下文</summary>
    public static ErpDbContext Create()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ErpDbContext(options);
    }
}
