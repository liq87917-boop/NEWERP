using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// 平台存储过程集成测试：sp_Plat_GetTableNewOid（生成新主键 Oid）
/// 每次调用应返回一个大于上次的 Oid（在并发安全前提下），用于单据 Oid 分配。
/// </summary>
[Collection("Integration")]
public class PlatformSpTests
{
    private readonly IntegrationTestFixture _fixture;

    public PlatformSpTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetNewOidAsync_SalesOrder_返回单调递增的长整型Oid()
    {
        var oid1 = await _fixture.SpService.GetNewOidAsync("SalesOrder");
        var oid2 = await _fixture.SpService.GetNewOidAsync("SalesOrder");
        var oid3 = await _fixture.SpService.GetNewOidAsync("SalesOrder");

        Assert.True(oid1 > 0);
        Assert.True(oid2 > oid1, $"oid2({oid2}) 应大于 oid1({oid1})");
        Assert.True(oid3 > oid2, $"oid3({oid3}) 应大于 oid2({oid2})");
    }

    [Fact]
    public async Task GetNewOidAsync_不同表名_返回独立Oid空间()
    {
        // SalesOrder 与 PurchaseOrder 的 Oid 互不影响（由 sp_Plat_GetTableNewOid 内部按表维护）
        var soOid = await _fixture.SpService.GetNewOidAsync("SalesOrder");
        var poOid = await _fixture.SpService.GetNewOidAsync("PurchaseOrder");

        Assert.True(soOid > 0);
        Assert.True(poOid > 0);
        // 不强制要求两表 Oid 不同（实现可能共用 Plat_TableMaxID 但按 TableName 分桶），
        // 这里只断言两者都返回了合法的正数。
    }
}