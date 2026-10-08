using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-415 销售订单变更申请「登记 / 编辑 / 提交 / 取消」原子性与确定性行锁的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>两个独立连接竞态</b>：① 编辑 vs 提交（串行化后最终一致提交、陈旧输家受控 <c>RuleConflict</c>）；
/// ② 并发取消（恰好一个赢家，保留首次原因与时间，输家绝不覆盖）；③ 登记 vs 来源取消（先行取消被锁内复核拒绝，
/// 或登记先行则快照 <c>Approved</c> 且零改写来源）；④ 登记 vs 来源改派（受限账号 fail closed 或一致登记）。</item>
/// <item><b>权限撤销</b>：提交前实时撤销既有「销售订单」菜单 → 锁内重读 fail closed 且零写入。</item>
/// <item><b>强制明细写入失败</b>：明细替换 / 快照写入注入失败 → 受控业务冲突且完整回滚（零部分写入）。</item>
/// <item><b>一致性不变量</b>：来源快照（状态 / 更新时间 / 总额）不变、冻结行完整且逐行金额 = 数量 × 单价、
/// 原始取消原因与时间保留、父 / 商业 / 库存 / 财务计数前后不变（保留库存来源单据审计与既有失败日志）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SalesOrderChangeRequestMutationSqlServerTests
    : IClassFixture<SalesOrderChangeRequestMutationSqlServerFixture>
{
    private readonly SalesOrderChangeRequestMutationSqlServerFixture _fixture;

    public SalesOrderChangeRequestMutationSqlServerTests(
        SalesOrderChangeRequestMutationSqlServerFixture fixture) => _fixture = fixture;

    private static readonly string[] SnapshotTables =
    {
        "SalesOrders", "SalesOrderDetails", "StockOuts", "StockMovements", "Quotations", "FinanceReceipts",
        "SalesOrderChangeRequests", "SalesOrderChangeRequestDetails", "SysOperationLogs",
    };

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => SalesOrderChangeRequestMutationSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static SalesOrderChangeRequestController NewController(
        ErpDbContext db, long? userId, string path = "/api/sales-order-change-requests")
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.User = userId.HasValue
            ? new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());

        return new SalesOrderChangeRequestController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static async Task<T> OkDataAsync<T>(Task<IActionResult> action)
    {
        var ok = Assert.IsType<OkObjectResult>(await action);
        return Assert.IsType<ApiResponse<T>>(ok.Value).Data!;
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static SalesOrderChangeRequestDetailSaveDto Detail(
        long productId, string name, decimal quantity, decimal unitPrice)
        => new()
        {
            ProductId = productId, ProductName = name, Spec = "红", Unit = "PCS",
            Quantity = quantity, UnitPrice = unitPrice
        };

    private static SalesOrderChangeRequestSaveDto Save(long orderId, string reason,
        List<SalesOrderChangeRequestDetailSaveDto>? details = null)
        => new() { SalesOrderId = orderId, Reason = reason, Details = details };

    // ==================== 只读快照（独立连接，绝不写入） ====================

    private async Task<Dictionary<string, long>> SnapshotAsync()
    {
        var snapshot = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var table in SnapshotTables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM db_owner.[{table}]";
            snapshot[table] = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        }
        return snapshot;
    }

    private static void AssertUnchanged(Dictionary<string, long> before, Dictionary<string, long> after)
    {
        foreach (var (table, count) in before)
        {
            Assert.True(after.TryGetValue(table, out var current), $"快照缺少 {table}");
            Assert.Equal(count, current);
        }
    }

    /// <summary>来源销售订单一致性证明：状态 / 更新时间 / 总额 + 明细行数与合计在生命周期操作前后完全不变。</summary>
    private async Task<(int Status, DateTime? UpdatedAt, decimal TotalAmount, int DetailRows, decimal DetailSum)>
        OrderStateAsync(long orderId)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT o.[Status], o.[UpdatedAt], o.[TotalAmount], " +
            "(SELECT COUNT(*) FROM db_owner.[SalesOrderDetails] d WHERE d.[SalesOrderId] = o.[Id] AND d.[IsDeleted] = 0), " +
            "(SELECT ISNULL(SUM(d.[Quantity] * d.[UnitPrice]), 0) FROM db_owner.[SalesOrderDetails] d WHERE d.[SalesOrderId] = o.[Id] AND d.[IsDeleted] = 0) " +
            "FROM db_owner.[SalesOrders] o WHERE o.[Id] = @id";
        cmd.Parameters.AddWithValue("@id", orderId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"来源销售订单 {orderId} 不存在");
        return (
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetDateTime(1),
            reader.GetDecimal(2),
            reader.GetInt32(3),
            reader.GetDecimal(4));
    }

    // ==================== 1. 登记（锁内冻结来源快照，绝不改写来源订单与下游） ====================

    [Fact]
    public async Task 登记_冻结来源快照且不改写来源订单_零下游变更()
    {
        Guard();
        var before = await SnapshotAsync();
        var orderBefore = await OrderStateAsync(_fixture.OrderAId);

        long requestId;
        await using (var db = _fixture.CreateDbContext())
        {
            var created = await OkDataAsync<SalesOrderChangeRequestDto>(
                NewController(db, _fixture.RestrictedUserId).Create(Save(_fixture.OrderAId, "ERP415 登记")));
            requestId = created.Id;
            Assert.Equal(_fixture.OrderAId, created.SalesOrderId);
            Assert.Equal(orderBefore.TotalAmount, created.SourceTotalAmount);
            Assert.Equal("已审核", created.SourceStatusText);
        }

        // 来源订单状态 / 更新时间 / 总额 / 明细行数 / 明细合计全部不变（加锁不改写来源）
        Assert.Equal(orderBefore, await OrderStateAsync(_fixture.OrderAId));

        await using (var verify = _fixture.CreateDbContext())
        {
            var request = await verify.SalesOrderChangeRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
            Assert.Equal((int)DocumentStatus.Approved, request.SourceStatus);
            Assert.Equal(orderBefore.UpdatedAt, (DateTime?)request.SourceUpdatedAt);
            Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, request.Status);

            var lines = await verify.SalesOrderChangeRequestDetails.AsNoTracking()
                .Where(d => d.ChangeRequestId == requestId && !d.IsDeleted).OrderBy(d => d.LineNo).ToListAsync();
            Assert.Equal(2, lines.Count);
            Assert.All(lines, d => Assert.Equal(d.ProposedQuantity * d.ProposedUnitPrice, d.ProposedAmount));
        }

        // 只新增本模块的 1 张申请 + 2 行快照；父 / 商业 / 库存 / 财务 / 审计表计数不变
        var after = await SnapshotAsync();
        Assert.Equal(before["SalesOrderChangeRequests"] + 1, after["SalesOrderChangeRequests"]);
        Assert.Equal(before["SalesOrderChangeRequestDetails"] + 2, after["SalesOrderChangeRequestDetails"]);
        foreach (var table in new[]
                 {
                     "SalesOrders", "SalesOrderDetails", "StockOuts", "StockMovements", "Quotations",
                     "FinanceReceipts", "SysOperationLogs"
                 })
            Assert.Equal(before[table], after[table]);
    }

    // ==================== 2. 两个独立连接竞态：编辑 vs 提交 ====================

    [Fact]
    public async Task 两个独立连接竞态_编辑与提交_结果一致且陈旧输家受控()
    {
        Guard();
        var orderBefore = await OrderStateAsync(_fixture.OrderAId);
        long requestId;
        await using (var db = _fixture.CreateDbContext())
            requestId = (await OkDataAsync<SalesOrderChangeRequestDto>(
                NewController(db, _fixture.RestrictedUserId)
                    .Create(Save(_fixture.OrderAId, "ERP415 编辑提交竞态")))).Id;

        using var gate = new SemaphoreSlim(0, 2);

        async Task<(bool Ok, int? Code, string Message)> EditAsync()
        {
            await gate.WaitAsync();
            await using var db = _fixture.CreateDbContext();
            try
            {
                var dto = Save(_fixture.OrderAId, "编辑赢家", new List<SalesOrderChangeRequestDetailSaveDto>
                {
                    Detail(101, "A 商品", 10m, 5m),
                    Detail(102, "B 商品", 4m, 25m),
                    Detail(103, "C 商品", 2m, 10m)
                });
                await OkDataAsync<SalesOrderChangeRequestDto>(
                    NewController(db, _fixture.RestrictedUserId).Update(requestId, dto));
                return (true, null, string.Empty);
            }
            catch (BusinessException ex)
            {
                return (false, ex.Code, ex.Message);
            }
        }

        async Task<(bool Ok, int? Code, string Message)> SubmitAsync()
        {
            await gate.WaitAsync();
            await using var db = _fixture.CreateDbContext();
            try
            {
                await OkDataAsync<SalesOrderChangeRequestDto>(
                    NewController(db, _fixture.RestrictedUserId).Submit(requestId));
                return (true, null, string.Empty);
            }
            catch (BusinessException ex)
            {
                return (false, ex.Code, ex.Message);
            }
        }

        var edit = EditAsync();
        var submit = SubmitAsync();
        gate.Release(2);
        var editResult = await edit;
        var submitResult = await submit;

        Assert.True(submitResult.Ok, submitResult.Message);   // 提交是唯一的终态转出者，必然成功
        if (!editResult.Ok)
        {
            Assert.Equal(ErrorCodes.RuleConflict, editResult.Code);   // 陈旧输家受控拒绝
            Assert.Contains("已提交", editResult.Message);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var request = await verify.SalesOrderChangeRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
            Assert.Equal(SalesOrderChangeRequestRules.StatusSubmitted, request.Status);
            Assert.NotNull(request.SubmittedAt);

            var lines = await verify.SalesOrderChangeRequestDetails.AsNoTracking()
                .Where(d => d.ChangeRequestId == requestId && !d.IsDeleted).OrderBy(d => d.LineNo).ToListAsync();
            Assert.Contains(lines.Count, new[] { 2, 3 });   // 冻结的完整拟议快照（原样或编辑后）
            Assert.All(lines, d =>
            {
                Assert.True(d.ProposedQuantity > 0);
                Assert.Equal(d.ProposedQuantity * d.ProposedUnitPrice, d.ProposedAmount);
            });
            Assert.Equal(request.ProposedTotalAmount,
                lines.Where(d => !d.ProposedRemoved).Sum(d => d.ProposedAmount));
        }

        // 来源订单零改写
        Assert.Equal(orderBefore, await OrderStateAsync(_fixture.OrderAId));
    }

    // ==================== 3. 两个独立连接竞态：并发取消 ====================

    [Fact]
    public async Task 两个独立连接竞态_并发取消_一个赢家且保留首次原因与时间()
    {
        Guard();
        long requestId;
        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewController(db, _fixture.RestrictedUserId);
            requestId = (await OkDataAsync<SalesOrderChangeRequestDto>(
                controller.Create(Save(_fixture.OrderAId, "ERP415 并发取消竞态")))).Id;
            await OkDataAsync<SalesOrderChangeRequestDto>(controller.Submit(requestId));
        }

        using var gate = new SemaphoreSlim(0, 2);

        async Task<(bool Ok, string Reason)> CancelAsync(string reason)
        {
            await gate.WaitAsync();
            await using var db = _fixture.CreateDbContext();
            try
            {
                var result = await OkDataAsync<SalesOrderChangeRequestDto>(
                    NewController(db, _fixture.RestrictedUserId).Cancel(
                        requestId, new SalesOrderChangeRequestCancelRequest { Reason = reason }));
                return (true, result.CancelledReason);
            }
            catch (BusinessException)
            {
                return (false, reason);
            }
        }

        var first = CancelAsync("ERP415 第一次取消原因");
        var second = CancelAsync("ERP415 第二次取消原因");
        gate.Release(2);
        var a = await first;
        var b = await second;

        Assert.True(a.Ok ^ b.Ok, "并发取消必须恰好一个赢家");
        var winnerReason = a.Ok ? a.Reason : b.Reason;

        await using (var verify = _fixture.CreateDbContext())
        {
            var request = await verify.SalesOrderChangeRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
            Assert.Equal(SalesOrderChangeRequestRules.StatusCancelled, request.Status);
            Assert.Equal(winnerReason, request.CancelledReason);   // 输家绝不覆盖赢家原始原因
            Assert.NotNull(request.CancelledAt);
            Assert.NotNull(request.SubmittedAt);
        }
    }

    // ==================== 4. 两个独立连接竞态：登记 vs 来源取消 ====================

    [Fact]
    public async Task 两个独立连接竞态_登记与来源取消_串行化后一致零部分写入()
    {
        Guard();
        var before = await SnapshotAsync();
        var orderBefore = await OrderStateAsync(_fixture.OrderCId);

        using var gate = new SemaphoreSlim(0, 2);

        async Task<(bool Ok, int? Code, long RequestId)> CreateAsync()
        {
            await gate.WaitAsync();
            await using var db = _fixture.CreateDbContext();
            try
            {
                var created = await OkDataAsync<SalesOrderChangeRequestDto>(
                    NewController(db, _fixture.RestrictedUserId)
                        .Create(Save(_fixture.OrderCId, "ERP415 来源取消竞态")));
                return (true, null, created.Id);
            }
            catch (BusinessException ex)
            {
                return (false, ex.Code, 0L);
            }
        }

        async Task CancelSourceAsync()
        {
            await gate.WaitAsync();
            await using var db = _fixture.CreateDbContext();
            await using var transaction = await db.Database
                .BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            // 与登记共用同一把来源销售订单行锁，形成真正的「来源取消 vs 登记」竞态
            await db.Database
                .SqlQueryRaw<long>(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql, _fixture.OrderCId)
                .ToListAsync();
            var order = await db.SalesOrders.SingleAsync(o => o.Id == _fixture.OrderCId);
            order.Status = DocumentStatus.Cancelled;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var create = CreateAsync();
        var cancel = CancelSourceAsync();
        gate.Release(2);
        var createResult = await create;
        await cancel;

        Assert.Equal((int)DocumentStatus.Cancelled, (await OrderStateAsync(_fixture.OrderCId)).Status);

        await using (var verify = _fixture.CreateDbContext())
        {
            if (createResult.Ok)
            {
                // 登记先行：快照冻结登记当时的 Approved；之后来源取消不改写申请
                var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                    .SingleAsync(r => r.Id == createResult.RequestId);
                Assert.Equal((int)DocumentStatus.Approved, request.SourceStatus);
                Assert.Equal(orderBefore.TotalAmount, request.SourceTotalAmount);
            }
            else
            {
                // 来源取消先行：登记的锁内复核 fail closed，零申请行
                Assert.Equal(ErrorCodes.RuleConflict, createResult.Code);
                Assert.Equal(0, await verify.SalesOrderChangeRequests
                    .CountAsync(r => r.SalesOrderId == _fixture.OrderCId));
            }
        }

        // 来源订单明细不变；父 / 商业 / 库存 / 财务 / 审计表计数不变（只可能新增本模块申请行）
        var orderAfter = await OrderStateAsync(_fixture.OrderCId);
        Assert.Equal(orderBefore.DetailRows, orderAfter.DetailRows);
        Assert.Equal(orderBefore.DetailSum, orderAfter.DetailSum);
        var after = await SnapshotAsync();
        Assert.True(after["SalesOrderChangeRequests"] == before["SalesOrderChangeRequests"]
            || after["SalesOrderChangeRequests"] == before["SalesOrderChangeRequests"] + 1);
        foreach (var table in new[]
                 {
                     "SalesOrderDetails", "StockOuts", "StockMovements", "Quotations", "FinanceReceipts",
                     "SysOperationLogs"
                 })
            Assert.Equal(before[table], after[table]);
        Assert.Equal(before["SalesOrders"], after["SalesOrders"]);
    }

    // ==================== 5. 两个独立连接竞态：登记 vs 来源改派（受限账号 fail closed） ====================

    [Fact]
    public async Task 两个独立连接竞态_登记与来源改派_受限账号一致或fail_closed()
    {
        Guard();
        var orderId = _fixture.OrderDId;
        var before = await SnapshotAsync();

        using var gate = new SemaphoreSlim(0, 2);

        async Task<(bool Ok, int? Code, string Message, long RequestId)> CreateAsync()
        {
            await gate.WaitAsync();
            await using var db = _fixture.CreateDbContext();
            try
            {
                var created = await OkDataAsync<SalesOrderChangeRequestDto>(
                    NewController(db, _fixture.RestrictedUserId)
                        .Create(Save(orderId, "ERP415 来源改派竞态")));
                return (true, null, string.Empty, created.Id);
            }
            catch (BusinessException ex)
            {
                return (false, ex.Code, ex.Message, 0L);
            }
        }

        async Task ReassignAsync()
        {
            await gate.WaitAsync();
            await using var db = _fixture.CreateDbContext();
            await using var transaction = await db.Database
                .BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            await db.Database
                .SqlQueryRaw<long>(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql, orderId)
                .ToListAsync();
            var order = await db.SalesOrders.SingleAsync(o => o.Id == orderId);
            order.CustomerId = _fixture.CustomerBId;   // 改派给受限账号范围外的客户
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var create = CreateAsync();
        var reassign = ReassignAsync();
        gate.Release(2);
        var createResult = await create;
        await reassign;

        try
        {
            await using (var verify = _fixture.CreateDbContext())
            {
                if (createResult.Ok)
                {
                    // 登记先行：来源客户快照冻结登记当时的本人客户
                    var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                        .SingleAsync(r => r.Id == createResult.RequestId);
                    Assert.Equal(_fixture.CustomerAId, request.SourceCustomerId);
                }
                else
                {
                    // 改派先行：受限账号的锁内归属复核 fail closed（非披露，零申请行）
                    Assert.Equal(ErrorCodes.NotFound, createResult.Code);
                    Assert.Equal(SalesOrderChangeRequestAuthorizationRules.NotFoundText, createResult.Message);
                    Assert.Equal(0, await verify.SalesOrderChangeRequests.CountAsync(r => r.SalesOrderId == orderId));
                }
            }

            var after = await SnapshotAsync();
            Assert.True(after["SalesOrderChangeRequests"] == before["SalesOrderChangeRequests"]
                || after["SalesOrderChangeRequests"] == before["SalesOrderChangeRequests"] + 1);
            foreach (var table in new[]
                     {
                         "SalesOrderDetails", "StockOuts", "StockMovements", "Quotations", "FinanceReceipts",
                         "SysOperationLogs"
                     })
                Assert.Equal(before[table], after[table]);
        }
        finally
        {
            // 复原来源归属，避免影响其它用例
            await using var db = _fixture.CreateDbContext();
            var order = await db.SalesOrders.SingleAsync(o => o.Id == orderId);
            order.CustomerId = _fixture.CustomerAId;
            await db.SaveChangesAsync();
        }
    }

    // ==================== 6. 提交 vs 实时权限撤销（撤权在独立连接上生效） ====================

    [Fact]
    public async Task 提交与实时权限撤销竞态_fail_closed或提交先行且零部分写入()
    {
        Guard();
        long requestId;
        await using (var db = _fixture.CreateDbContext())
            requestId = (await OkDataAsync<SalesOrderChangeRequestDto>(
                NewController(db, _fixture.RestrictedUserId)
                    .Create(Save(_fixture.OrderAId, "ERP415 权限撤销竞态")))).Id;

        using var gate = new SemaphoreSlim(0, 2);

        async Task<(bool Ok, int? Code, string Message)> SubmitAsync()
        {
            await gate.WaitAsync();
            await using var db = _fixture.CreateDbContext();
            try
            {
                await OkDataAsync<SalesOrderChangeRequestDto>(
                    NewController(db, _fixture.RestrictedUserId).Submit(requestId));
                return (true, null, string.Empty);
            }
            catch (BusinessException ex)
            {
                return (false, ex.Code, ex.Message);
            }
        }

        async Task RevokeAsync()
        {
            await gate.WaitAsync();
            await using var conn = new SqlConnection(_fixture.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE db_owner.[SysRoleMenus] SET IsDeleted = 1 WHERE RoleId = @r AND IsDeleted = 0";
            cmd.Parameters.AddWithValue("@r", _fixture.RestrictedRoleId);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            var submit = SubmitAsync();
            var revoke = RevokeAsync();
            gate.Release(2);
            var submitResult = await submit;
            await revoke;

            await using (var verify = _fixture.CreateDbContext())
            {
                var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                    .SingleAsync(r => r.Id == requestId);
                if (submitResult.Ok)
                {
                    // 提交先行（撤权发生在提交之后）：终态为已提交
                    Assert.Equal(SalesOrderChangeRequestRules.StatusSubmitted, request.Status);
                }
                else
                {
                    // 锁内实时复核撤权：fail closed 且零写入
                    Assert.Equal(ErrorCodes.Forbidden, submitResult.Code);
                    Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, request.Status);
                    Assert.Null(request.SubmittedAt);
                }
            }
        }
        finally
        {
            // 复原既有菜单授权
            await using var conn = new SqlConnection(_fixture.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE db_owner.[SysRoleMenus] SET IsDeleted = 0 WHERE RoleId = @r";
            cmd.Parameters.AddWithValue("@r", _fixture.RestrictedRoleId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ==================== 7. 强制明细写入失败 → 受控业务冲突 + 完整回滚（零部分写入） ====================

    [Fact]
    public async Task 强制明细写入失败_受控业务冲突且零部分变更()
    {
        Guard();
        long requestId;
        string originalReason;
        int originalLines;
        await using (var db = _fixture.CreateDbContext())
        {
            var created = await OkDataAsync<SalesOrderChangeRequestDto>(
                NewController(db, _fixture.RestrictedUserId)
                    .Create(Save(_fixture.OrderAId, "ERP415 写入失败基线")));
            requestId = created.Id;
            originalReason = created.Reason;
            originalLines = created.Details.Count;
        }

        var orderBefore = await OrderStateAsync(_fixture.OrderAId);
        var before = await SnapshotAsync();

        await using (var db = new FailingProposalWriteDbContext(_fixture.Options())
        {
            FailNextProposalWrite = true
        })
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                NewController(db, _fixture.RestrictedUserId).Update(
                    requestId,
                    Save(_fixture.OrderAId, "不应落库的新原因", new List<SalesOrderChangeRequestDetailSaveDto>
                    {
                        Detail(101, "A 商品", 99m, 9m),
                        Detail(102, "B 商品", 99m, 9m)
                    })));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Equal(SalesOrderChangeRequestMutationRules.WriteConflictText, ex.Message);
            Assert.DoesNotContain("SqlException", ex.Message, StringComparison.Ordinal);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                .SingleAsync(r => r.Id == requestId);
            Assert.Equal(originalReason, request.Reason);              // 明细替换被完整回滚
            Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, request.Status);
            Assert.Equal(150m, request.ProposedTotalAmount);
            Assert.Equal(originalLines, await verify.SalesOrderChangeRequestDetails
                .CountAsync(d => d.ChangeRequestId == requestId && !d.IsDeleted));
            Assert.Equal(0, await verify.SalesOrderChangeRequestDetails
                .CountAsync(d => d.ChangeRequestId == requestId && d.ProposedQuantity == 99m));
        }

        // 来源订单与所有既有表计数完全不变
        Assert.Equal(orderBefore, await OrderStateAsync(_fixture.OrderAId));
        var after = await SnapshotAsync();
        AssertUnchanged(before, after);
    }

    /// <summary>强制「明细替换 / 快照写入」失败的上下文（真实 SQL Server 上当次保存抛出写入冲突）。</summary>
    private sealed class FailingProposalWriteDbContext : ErpDbContext
    {
        public FailingProposalWriteDbContext(DbContextOptions<ErpDbContext> options) : base(options)
        {
        }

        public bool FailNextProposalWrite { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (FailNextProposalWrite
                && ChangeTracker.Entries<SalesOrderChangeRequest>()
                    .Any(e => e.State is EntityState.Added or EntityState.Modified))
            {
                FailNextProposalWrite = false;
                return Task.FromException<int>(
                    new DbUpdateException("ERP-415 注入的明细替换 / 快照写入失败（写入冲突）"));
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>
/// ERP-415 变更申请原子性集成夹具：GUID 独占 <c>NEWERP_AUTOTEST</c> 库 + 既有授权 + 专用来源订单。
/// <para>不新增 / 不修改任何既有菜单 / 角色 / 用户授权；发现同名库已存在立即拒绝，绝不 drop / reset / 复用。</para>
/// </summary>
public sealed class SalesOrderChangeRequestMutationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    private const string DefaultDatabaseName = "NEWERP_AUTOTEST_SOCREQMUT";

    public string ConnectionString { get; private set; } = string.Empty;
    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }
    public long RestrictedRoleId { get; private set; }
    public long CustomerAId { get; private set; }
    public long CustomerBId { get; private set; }
    public long OrderAId { get; private set; }
    public long OrderBId { get; private set; }
    public long OrderCId { get; private set; }
    public long OrderDId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-415] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
        await CreateFreshDatabaseAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(Options());

    internal DbContextOptions<ErpDbContext> Options()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server={InstanceTarget};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

    /// <summary>专用目标护栏：必须精确命中专用 localdb + 库名前缀 + 集成安全，否则在任何库访问之前拒绝。</summary>
    public static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.Equal(InstanceTarget, builder.DataSource ?? string.Empty, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, builder.InitialCatalog ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task CreateFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何库访问 / 建库之前再次护栏：绝不使用生产或非专用目标。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // Never destroy a pre-existing fixture or another caller's database.
            cmd.CommandText = "SELECT DB_ID(@database)";
            cmd.Parameters.AddWithValue("@database", database);
            var existing = await cmd.ExecuteScalarAsync();
            if (existing is not null && existing != DBNull.Value)
                throw new InvalidOperationException(
                    "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-415] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 1) 特权账号（系统内置角色，沿用既有全部访问口径）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP415 特权角色", RoleCode = $"ERP415-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(privilegedRole);
        await db.SaveChangesAsync();
        var privileged = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(privileged);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = privileged.Id, RoleId = privilegedRole.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = privileged.Id;

        // 2) 受限业务员：既有「销售订单」功能菜单 + ERP-097 客户数据范围（登录账号 == 员工编码）。
        var restricted = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(restricted);
        await db.SaveChangesAsync();
        RestrictedRoleId = await AddRoleAsync(db, "sales-order");
        db.SysUserRoles.Add(new SysUserRole { UserId = restricted.Id, RoleId = RestrictedRoleId });
        await db.SaveChangesAsync();
        RestrictedUserId = restricted.Id;

        var employee = new BaseEmployee
        {
            EmployeeCode = restricted.UserName, EmployeeName = "ERP415 受限业务员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var customerA = new BaseCustomer
        {
            CustomerCode = $"C-A-{Guid.NewGuid():N}", CustomerName = "ERP415 可见客户",
            EmpId = employee.Id, Status = 1, CreditStatus = "正常", Currency = "USD"
        };
        var customerB = new BaseCustomer
        {
            CustomerCode = $"C-B-{Guid.NewGuid():N}", CustomerName = "ERP415 隐藏客户",
            EmpId = null, Status = 1, CreditStatus = "正常", Currency = "USD"
        };
        db.BaseCustomers.AddRange(customerA, customerB);
        await db.SaveChangesAsync();
        CustomerAId = customerA.Id;
        CustomerBId = customerB.Id;

        OrderAId = await SeedOrderAsync(db, customerA.Id, "SO-415-A");
        OrderBId = await SeedOrderAsync(db, customerB.Id, "SO-415-B");
        OrderCId = await SeedOrderAsync(db, customerA.Id, "SO-415-C");
        OrderDId = await SeedOrderAsync(db, customerA.Id, "SO-415-D");

        Console.WriteLine("[ERP-415] 既有授权 + 专用来源订单夹具就绪（受控只读，不新增权限模型）。");
    }

    private static SysUser NewUser(UserStatus status)
    {
        var code = $"E415_{Guid.NewGuid():N}";
        return new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code, Status = status
        };
    }

    /// <summary>新建既有角色并授予指定既有功能菜单（不新增任何菜单 / 权限模型）。</summary>
    private static async Task<long> AddRoleAsync(ErpDbContext db, params string[] menuCodes)
    {
        var role = new SysRole
        {
            RoleName = $"E415_R_{Guid.NewGuid():N}", RoleCode = $"E415_{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        foreach (var menuCode in menuCodes)
        {
            var menu = db.SysMenus.FirstOrDefault(m => !m.IsDeleted && m.MenuCode == menuCode);
            if (menu is null)
            {
                menu = new SysMenu
                {
                    ParentId = 0, MenuName = menuCode, MenuCode = menuCode, Path = $"/{menuCode}",
                    Icon = "test", SortOrder = 1, MenuType = MenuType.Menu
                };
                db.SysMenus.Add(menu);
                await db.SaveChangesAsync();
            }

            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }

        return role.Id;
    }

    /// <summary>播种一张已审核来源订单（2 行明细：10 × 5 + 4 × 25 = 150，定金比例 30% → 45）。</summary>
    private static async Task<long> SeedOrderAsync(ErpDbContext db, long customerId, string prefix)
    {
        var order = new SalesOrder
        {
            OrderNo = $"{prefix}-{Guid.NewGuid():N}"[..24], OrderDate = DateTime.Today, CustomerId = customerId,
            Currency = Currency.USD, ExchangeRate = 7.2m, DepositRatio = 30m,
            TotalAmount = 150m, DepositAmount = 45m, Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();

        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id, ProductId = 101, ProductName = "A 商品", Spec = "红", Unit = "PCS",
            Quantity = 10m, UnitPrice = 5m, Amount = 50m
        });
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id, ProductId = 102, ProductName = "B 商品", Spec = "蓝", Unit = "PCS",
            Quantity = 4m, UnitPrice = 25m, Amount = 100m
        });
        await db.SaveChangesAsync();
        return order.Id;
    }
}

/// <summary>ERP-415 专用目标护栏 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SalesOrderChangeRequestMutationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderChangeRequestMutationSqlServerFixture.AssertDedicatedTarget(connection));
}
