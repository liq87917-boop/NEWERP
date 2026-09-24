using ERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ERP.UnitTests;

/// <summary>
/// 测试数据库工厂：创建内存数据库上下文。
/// <para>ERP-052：生产代码在「单证生成」等**父单据 + 子明细**写入时使用显式事务
/// （<c>Database.BeginTransactionAsync</c>），而内存库不支持事务（EF 默认把
/// <c>InMemoryEventId.TransactionIgnoredWarning</c> 视为异常）。单元测试显式忽略该警告：
/// 事务在内存库上等价于「无事务」，被测的业务校验与写入语义不受影响；
/// 生产 SQL Server 仍走真实事务（见 <c>TradeDocumentGeneration.GenerateAsync</c>，
/// 失败即回滚、不留半成品单证或孤儿明细行）。</para>
/// <para>已知测试环境差异：内存库无事务回滚能力，因此「两次 SaveChanges 中的第二次失败」在单元测试里
/// 不会回滚第一次写入；该场景由生产事务保证，单元测试只断言「先校验后写入、失败不落任何数据」的路径。</para>
/// </summary>
public static class TestDbFactory
{
    /// <summary>创建独立的内存数据库上下文</summary>
    public static ErpDbContext Create()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new ErpDbContext(options);
    }
}
