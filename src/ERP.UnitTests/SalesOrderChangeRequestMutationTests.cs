using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-415 销售订单变更申请「登记 / 编辑 / 提交 / 取消」原子性与确定性行锁单元测试（内存库）。
/// 覆盖：行锁契约（复用既有规范销售订单行锁协议、锁定 SQL 不变更来源 <c>UpdatedAt</c>、无 DDL）；
/// 登记在锁内重读来源主表 + 明细且不改写来源订单；编辑整体替换但来源快照列与来源订单不变；
/// 并发提交的陈旧输家被状态门拒绝且不覆盖赢家冻结的拟议快照；提交冻结完整正数快照并按既有权威算法重算；
/// 并发取消保留首次原因与时间、输家绝不覆盖；拟议快照为空拒绝；写入失败受控业务冲突且零部分写入；
/// 锁内重读实时权限（提交前菜单撤销 fail closed 且零写入）；非关系型提供程序事务 / 行锁等价无操作。
/// <para>全部使用内存库（<c>TestDbFactory</c> / 共享内存库 + 失败注入上下文）；
/// 真实 SQL 两个独立连接竞态见 <c>ERP.IntegrationTests.SalesOrderChangeRequestMutationSqlServerTests</c>。
/// 不连接 SQL Server、不做浏览器 / UI 验收。</para>
/// </summary>
public class SalesOrderChangeRequestMutationTests
{
    private const string BasePath = "/api/sales-order-change-requests";

    // ==================== 0. 脚手架 ====================

    private static DbContextOptions<ErpDbContext> InMemoryOptions(string database)
        => new DbContextOptionsBuilder<ErpDbContext>()
            .UseInMemoryDatabase(database)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static SalesOrderChangeRequestController Controller(ErpDbContext db, long? userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = BasePath;
        http.User = userId.HasValue
            ? new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());

        return new SalesOrderChangeRequestController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static async Task<SalesOrderChangeRequestDto> CreateAsync(
        SalesOrderChangeRequestController controller, long salesOrderId, string reason = "客户要求改数量与交期",
        List<SalesOrderChangeRequestDetailSaveDto>? details = null)
        => AssertOk<SalesOrderChangeRequestDto>(
            await controller.Create(new SalesOrderChangeRequestSaveDto
            {
                SalesOrderId = salesOrderId, Reason = reason, Details = details
            }));

    private static async Task<SalesOrderChangeRequestDto> UpdateAsync(
        SalesOrderChangeRequestController controller, long id, SalesOrderChangeRequestSaveDto dto)
        => AssertOk<SalesOrderChangeRequestDto>(await controller.Update(id, dto));

    private static async Task<SalesOrderChangeRequestDto> SubmitAsync(
        SalesOrderChangeRequestController controller, long id)
        => AssertOk<SalesOrderChangeRequestDto>(await controller.Submit(id));

    private static async Task<SalesOrderChangeRequestDto> CancelAsync(
        SalesOrderChangeRequestController controller, long id, string reason)
        => AssertOk<SalesOrderChangeRequestDto>(
            await controller.Cancel(id, new SalesOrderChangeRequestCancelRequest { Reason = reason }));

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static SalesOrderChangeRequestDetailSaveDto Detail(
        long productId, string name, decimal quantity, decimal unitPrice, bool removed = false)
        => new()
        {
            ProductId = productId, ProductName = name, Spec = "红", Unit = "PCS",
            Quantity = quantity, UnitPrice = unitPrice, Removed = removed
        };

    // ==================== 0.1 种子（既有菜单 / ERP-097 客户范围 / 来源订单 / 单号规则） ====================

    private sealed record Seed(long UserId, long RoleId, long EmployeeId, long CustomerId, long OrderId);

    private static async Task<Seed> SeedAsync(ErpDbContext db)
    {
        var code = $"E415-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });

        var menu = db.SysMenus.FirstOrDefault(m => !m.IsDeleted
            && m.MenuCode == SalesOrderChangeRequestAuthorizationRules.RequiredMenuCode);
        if (menu is null)
        {
            menu = new SysMenu
            {
                ParentId = 0, MenuName = "销售订单",
                MenuCode = SalesOrderChangeRequestAuthorizationRules.RequiredMenuCode,
                Path = "/sales-order", Icon = "test", SortOrder = 1, MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });

        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.SalesOrderChangeRequest, RuleCode = code, RuleName = code,
            Prefix = "SOC", DateFormat = "yyyyMMdd", SerialLength = 4, CurrentSequence = 0
        });
        await db.SaveChangesAsync();

        var customer = new BaseCustomer
        {
            CustomerCode = $"C-{code}", CustomerName = code, Status = 1,
            CreditStatus = "正常", Currency = "USD", EmpId = employee.Id
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();

        var order = new SalesOrder
        {
            OrderNo = $"SO-415-{Guid.NewGuid():N}"[..20], OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customer.Id, SalesmanId = employee.Id, Currency = Currency.USD, ExchangeRate = 7.2m,
            DepositRatio = 30m, TotalAmount = 150m, DepositAmount = 45m,
            PaymentTerms = "T/T 30%", Status = DocumentStatus.Approved
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

        return new Seed(user.Id, role.Id, employee.Id, customer.Id, order.Id);
    }

    /// <summary>回收既有「销售订单」菜单授权（软删除既有「角色 → 菜单」授权行，不改写任何权限模型）。</summary>
    private static async Task RevokeMenuAsync(ErpDbContext db, long roleId)
    {
        var grants = await db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    /// <summary>失败注入上下文（内存库）：在保存「新增 / 更新变更申请」时按需抛出一次异常。</summary>
    private sealed class InjectOnProposalWriteDbContext : ErpDbContext
    {
        public InjectOnProposalWriteDbContext(DbContextOptions<ErpDbContext> options) : base(options)
        {
        }

        /// <summary>下一次「新增 / 更新变更申请行」的保存抛出的异常（触发后自动清零）；null = 正常保存。</summary>
        public Exception? NextWriteException { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var pending = NextWriteException;
            if (pending is not null
                && ChangeTracker.Entries<SalesOrderChangeRequest>().Any(e =>
                    e.State is EntityState.Added or EntityState.Modified))
            {
                NextWriteException = null;
                throw pending;
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // ==================== 1. 护栏契约与确定性辅助 ====================

    [Fact]
    public void 护栏契约_锁定SQL复用规范来源行锁且无DDL与内部细节泄露()
    {
        // 来源行锁直接复用既有规范销售订单行锁常量（同一把锁，锁身份同源）
        Assert.Equal(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql,
            SalesOrderChangeRequestMutationRules.SalesOrderRowLockSql);
        Assert.Contains("UPDLOCK, HOLDLOCK", SalesOrderChangeRequestMutationRules.SalesOrderRowLockSql);
        Assert.Contains("db_owner.SalesOrders", SalesOrderChangeRequestMutationRules.SalesOrderRowLockSql);
        Assert.Contains("UPDLOCK, HOLDLOCK", SalesOrderChangeRequestMutationRules.ChangeRequestRowLockSql);
        Assert.Contains("db_owner.SalesOrderChangeRequests",
            SalesOrderChangeRequestMutationRules.ChangeRequestRowLockSql);
        Assert.Contains("来源销售订单行", SalesOrderChangeRequestMutationRules.LockOrderText);
        Assert.Contains("变更申请行", SalesOrderChangeRequestMutationRules.LockOrderText);
        Assert.Contains("不改写", SalesOrderChangeRequestMutationRules.BoundaryText);
        Assert.Contains("数据库写入冲突", SalesOrderChangeRequestMutationRules.WriteConflictText);
        Assert.Equal(8, SalesOrderChangeRequestMutationRules.RowLockRetryAttempts);

        // 受控写入文案绝不内联驱动异常 / 错误码 / 索引名
        var write = SalesOrderChangeRequestMutationRules.WriteConflictText;
        Assert.DoesNotContain("2601", write, StringComparison.Ordinal);
        Assert.DoesNotContain("2627", write, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlException", write, StringComparison.Ordinal);

        var rules = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "SalesOrderChangeRequestMutationRules.cs"));
        Assert.Contains("BeginMutationTransactionAsync", rules);
        Assert.Contains("SaveProposalAsync", rules);
        Assert.Contains("ResolveLiveScopeAsync", rules);
        Assert.Contains("MergeLockIds", rules);
        Assert.DoesNotContain("using Microsoft.Data.SqlClient", rules);
        Assert.DoesNotContain("catch (SqlException", rules);
        Assert.DoesNotContain("CREATE TABLE", rules, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", rules, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 接线契约_控制器在同一事务内按来源行再申请行加锁且服务锁内重读()
    {
        var controller = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "Controllers", "SalesOrderChangeRequestController.cs"));
        Assert.Contains("RunAtomicAsync", controller);
        Assert.Contains("IsolationLevel.Serializable", controller);
        Assert.Contains("SqlQueryRaw<long>(SalesOrderChangeRequestMutationRules.SalesOrderRowLockSql", controller);
        Assert.Contains("SqlQueryRaw<long>(SalesOrderChangeRequestMutationRules.ChangeRequestRowLockSql", controller);
        // 确定性锁序「来源销售订单行 → 变更申请行」，绝不反向
        Assert.Contains("await AcquireSourceOrderLockAsync(salesOrderId, ct);", controller);
        Assert.Contains("await AcquireChangeRequestLockAsync(requestId, ct);", controller);
        // 登记只锁来源订单行（申请行尚不存在）
        Assert.Contains("await AcquireSourceOrderLockAsync(dto.SalesOrderId);", controller);
        Assert.True(CountOf(controller, "await RunAtomicAsync(") >= 4,
            "登记 / 编辑 / 提交 / 取消都必须落在原子事务内");
        /* 8 条数据 / 状态路由的实时授权保持（ERP-414 回归） */
        Assert.True(CountOf(controller, "var scope = await EnsureAuthorizedAsync();") >= 8);

        var service = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "SalesOrderChangeRequestService.cs"));
        Assert.Contains("SalesOrderChangeRequestMutationRules.BeginMutationTransactionAsync", service);
        Assert.Contains("SalesOrderChangeRequestMutationRules.ResolveLiveScopeAsync", service);
        Assert.Contains("SalesOrderChangeRequestMutationRules.SaveProposalAsync", service);
        Assert.Contains("SalesOrderChangeRequestMutationRules.TryRollbackAsync", service);
        Assert.Contains("EnsureProposedSnapshotComplete", service);
        Assert.Contains("ResolveStoredSourceOrderIdAsync", service);
        // 锁内状态门：编辑 / 提交 / 取消都在重读 tracked 实体之后判定
        var reloadAt = service.IndexOf(
            "var entity = await db.SalesOrderChangeRequests.Include(x => x.Details)", StringComparison.Ordinal);
        Assert.True(reloadAt > 0);
        Assert.True(service.IndexOf("EnsureDraftEditable(entity.Status)", StringComparison.Ordinal) > reloadAt);
        Assert.True(service.IndexOf("EnsureSubmittable(entity.Status)", StringComparison.Ordinal) > reloadAt);
        Assert.True(service.IndexOf("EnsureCancellable(entity.Status)", StringComparison.Ordinal) > reloadAt);
    }

    [Fact]
    public async Task 非关系型提供程序_事务与行锁等价无操作_加锁键去重升序()
    {
        using var db = TestDbFactory.Create();
        Assert.False(SalesOrderChangeRequestMutationRules.IsRelationalProvider(db));
        Assert.Null(await SalesOrderChangeRequestMutationRules.BeginMutationTransactionAsync(db));
        Assert.Equal(new long[] { 2, 5, 9 },
            SalesOrderChangeRequestMutationRules.MergeLockIds(new long[] { 5, 0, -1, 2, 5, 9 }).ToArray());
    }

    // ==================== 2. 登记（锁内冻结来源快照，绝不改写来源订单） ====================

    [Fact]
    public async Task 登记_锁内冻结来源快照且不改写来源订单及其UpdatedAt()
    {
        var options = InMemoryOptions("erp415-create-" + Guid.NewGuid().ToString("N"));
        long userId, orderId;
        DateTime expectedSourceUpdatedAt;
        DateTime? sourceUpdatedAtBefore;
        int sourceRowsBefore;

        await using (var seed = new ErpDbContext(options))
        {
            var s = await SeedAsync(seed);
            userId = s.UserId;
            orderId = s.OrderId;
            var order = await seed.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            expectedSourceUpdatedAt = order.UpdatedAt ?? order.CreatedAt;
            sourceUpdatedAtBefore = order.UpdatedAt;
            sourceRowsBefore = await seed.SalesOrderDetails.CountAsync(d => d.SalesOrderId == orderId);
        }

        long requestId;
        await using (var db = new ErpDbContext(options))
            requestId = (await CreateAsync(Controller(db, userId), orderId)).Id;

        await using (var verify = new ErpDbContext(options))
        {
            // 行锁不改写来源订单（含 UpdatedAt）：快照 SourceUpdatedAt 保持登记当时的真实值
            var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            Assert.Equal(sourceUpdatedAtBefore, order.UpdatedAt);
            Assert.Equal(150m, order.TotalAmount);
            Assert.Equal(sourceRowsBefore, await verify.SalesOrderDetails
                .CountAsync(d => d.SalesOrderId == orderId && !d.IsDeleted));

            var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                .SingleAsync(r => r.Id == requestId);
            Assert.Equal(orderId, request.SalesOrderId);
            Assert.Equal(expectedSourceUpdatedAt, request.SourceUpdatedAt);
            Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, request.Status);

            var details = await verify.SalesOrderChangeRequestDetails.AsNoTracking()
                .Where(d => d.ChangeRequestId == requestId && !d.IsDeleted)
                .OrderBy(d => d.LineNo).ToListAsync();
            Assert.Equal(2, details.Count);
            Assert.All(details, d => Assert.True(d.HasSourceLine));
            Assert.Equal(150m, request.ProposedTotalAmount);
            Assert.Equal(45m, request.ProposedDepositAmount);
        }
    }

    [Fact]
    public async Task 登记_拟议明细非法_不落任何申请行()
    {
        var options = InMemoryOptions("erp415-create-fail-" + Guid.NewGuid().ToString("N"));
        long userId, orderId;
        await using (var seed = new ErpDbContext(options))
        {
            var s = await SeedAsync(seed);
            userId = s.UserId;
            orderId = s.OrderId;
        }

        await using (var db = new ErpDbContext(options))
        {
            var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => CreateAsync(
                Controller(db, userId), orderId, "非法拟议数量",
                new List<SalesOrderChangeRequestDetailSaveDto>
                {
                    Detail(101, "A 商品", 0m, 5m),
                    Detail(102, "B 商品", 4m, 25m)
                }));
            Assert.Contains("数量", ex.Message);
        }

        await using (var verify = new ErpDbContext(options))
        {
            Assert.Equal(0, await verify.SalesOrderChangeRequests.CountAsync());
            Assert.Equal(0, await verify.SalesOrderChangeRequestDetails.CountAsync());
        }
    }

    // ==================== 3. 编辑（整体替换拟议明细，来源快照与来源订单不变） ====================

    [Fact]
    public async Task 编辑草稿_整体替换拟议明细但来源快照列与来源订单不变()
    {
        var options = InMemoryOptions("erp415-update-" + Guid.NewGuid().ToString("N"));
        long userId, orderId, requestId;
        decimal firstSourceQty;
        DateTime? sourceUpdatedAt;

        await using (var seed = new ErpDbContext(options))
        {
            var s = await SeedAsync(seed);
            userId = s.UserId;
            orderId = s.OrderId;
            var order = await seed.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            sourceUpdatedAt = order.UpdatedAt;
            firstSourceQty = await seed.SalesOrderDetails.AsNoTracking()
                .Where(d => d.SalesOrderId == orderId).OrderBy(d => d.Id).Select(d => d.Quantity).FirstAsync();
            requestId = (await CreateAsync(Controller(seed, userId), orderId)).Id;
        }

        await using (var db = new ErpDbContext(options))
        {
            var updated = await UpdateAsync(Controller(db, userId), requestId,
                new SalesOrderChangeRequestSaveDto
                {
                    Reason = "改数量并新增一行",
                    Details = new List<SalesOrderChangeRequestDetailSaveDto>
                    {
                        Detail(101, "A 商品", 20m, 5m),
                        Detail(102, "B 商品", 4m, 25m),
                        Detail(103, "C 商品", 2m, 10m)   // 新增行
                    }
                });
            Assert.Equal(3, updated.Details.Count);
            Assert.Equal(20m * 5m + 4m * 25m + 2m * 10m, updated.ProposedTotalAmount);
        }

        await using (var verify = new ErpDbContext(options))
        {
            // 来源快照列（数量 / 更新时间）绝不被编辑覆盖
            var firstSourceRow = await verify.SalesOrderChangeRequestDetails.AsNoTracking()
                .Where(d => d.ChangeRequestId == requestId && d.HasSourceLine)
                .OrderBy(d => d.LineNo).FirstAsync();
            Assert.Equal(firstSourceQty, firstSourceRow.SourceQuantity);

            var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                .SingleAsync(r => r.Id == requestId);
            Assert.Equal(sourceUpdatedAt, request.SourceUpdatedAt);

            // 来源订单与明细零改写（含技术字段 UpdatedAt 不被加锁改写）
            var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            Assert.Equal(sourceUpdatedAt, order.UpdatedAt);
            Assert.Equal(150m, order.TotalAmount);
            Assert.Equal(firstSourceQty, await verify.SalesOrderDetails.AsNoTracking()
                .Where(d => d.SalesOrderId == orderId && !d.IsDeleted).OrderBy(d => d.Id)
                .Select(d => d.Quantity).FirstAsync());
        }
    }

    [Fact]
    public async Task 并发提交后的陈旧编辑_被状态门拒绝且不覆盖冻结快照()
    {
        var options = InMemoryOptions("erp415-edit-race-" + Guid.NewGuid().ToString("N"));
        long userId, requestId;
        await using (var seed = new ErpDbContext(options))
        {
            var s = await SeedAsync(seed);
            userId = s.UserId;
            requestId = (await CreateAsync(Controller(seed, userId), s.OrderId)).Id;
        }

        string frozenReason;
        int frozenLines;
        await using (var submitDb = new ErpDbContext(options))
        {
            var submitted = await SubmitAsync(Controller(submitDb, userId), requestId);
            frozenReason = submitted.Reason;
            frozenLines = submitted.Details.Count;
        }

        await using (var editDb = new ErpDbContext(options))
        {
            var stale = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => UpdateAsync(
                Controller(editDb, userId), requestId,
                new SalesOrderChangeRequestSaveDto
                {
                    Reason = "陈旧编辑",
                    Details = new List<SalesOrderChangeRequestDetailSaveDto>
                    {
                        Detail(101, "A 商品", 1m, 1m),
                        Detail(102, "B 商品", 1m, 1m)
                    }
                }));
            Assert.Contains("已提交", stale.Message);
        }

        await using (var verify = new ErpDbContext(options))
        {
            var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                .SingleAsync(r => r.Id == requestId);
            Assert.Equal(SalesOrderChangeRequestRules.StatusSubmitted, request.Status);
            Assert.Equal(frozenReason, request.Reason);
            Assert.Equal(frozenLines, await verify.SalesOrderChangeRequestDetails
                .CountAsync(d => d.ChangeRequestId == requestId && !d.IsDeleted));
            Assert.Equal(150m, request.ProposedTotalAmount);
        }
    }

    // ==================== 4. 提交（冻结完整正数快照，重复提交与并发输家拒绝） ====================

    [Fact]
    public async Task 提交_冻结完整正数快照并按既有权威算法重算()
    {
        var options = InMemoryOptions("erp415-submit-" + Guid.NewGuid().ToString("N"));
        long userId, orderId, requestId;
        await using (var seed = new ErpDbContext(options))
        {
            var s = await SeedAsync(seed);
            userId = s.UserId;
            orderId = s.OrderId;
            requestId = (await CreateAsync(Controller(seed, userId), orderId)).Id;
            await UpdateAsync(Controller(seed, userId), requestId, new SalesOrderChangeRequestSaveDto
            {
                Reason = "调整数量并新增一行",
                Details = new List<SalesOrderChangeRequestDetailSaveDto>
                {
                    Detail(101, "A 商品", 10m, 5m),
                    Detail(102, "B 商品", 4m, 25m),
                    Detail(103, "C 商品", 2m, 10m)
                }
            });
        }

        await using (var db = new ErpDbContext(options))
        {
            var submitted = await SubmitAsync(Controller(db, userId), requestId);
            Assert.Equal(SalesOrderChangeRequestRules.StatusSubmitted, submitted.Status);
            Assert.NotNull(submitted.SubmittedAt);
            Assert.Equal(3, submitted.Details.Count);
        }

        await using (var verify = new ErpDbContext(options))
        {
            var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                .SingleAsync(r => r.Id == requestId);
            const decimal expectedTotal = 10m * 5m + 4m * 25m + 2m * 10m;
            Assert.Equal(expectedTotal, request.ProposedTotalAmount);
            Assert.Equal(expectedTotal * request.ProposedDepositRatio / 100, request.ProposedDepositAmount);
            Assert.NotNull(request.SubmittedAt);

            var lines = await verify.SalesOrderChangeRequestDetails.AsNoTracking()
                .Where(d => d.ChangeRequestId == requestId && !d.IsDeleted)
                .OrderBy(d => d.LineNo).ToListAsync();
            Assert.Equal(3, lines.Count);
            Assert.All(lines, d =>
            {
                Assert.True(d.ProposedQuantity > 0);
                Assert.Equal(d.ProposedQuantity * d.ProposedUnitPrice, d.ProposedAmount);
            });
        }

        // 重复提交拒绝（本模块不提供「重新提交」）
        await using (var again = new ErpDbContext(options))
        {
            var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
                () => SubmitAsync(Controller(again, userId), requestId));
            Assert.Contains("不能重复提交", ex.Message);
        }
    }

    [Fact]
    public async Task 提交_拟议快照为空_拒绝且保持草稿()
    {
        var options = InMemoryOptions("erp415-submit-empty-" + Guid.NewGuid().ToString("N"));
        long userId, orderId, requestId;
        await using (var seed = new ErpDbContext(options))
        {
            var s = await SeedAsync(seed);
            userId = s.UserId;
            orderId = s.OrderId;
            requestId = (await CreateAsync(Controller(seed, userId), orderId)).Id;

            // 直接破坏拟议快照（模拟外部直改库）：全部来源行标记移除
            var rows = await seed.SalesOrderChangeRequestDetails
                .Where(d => d.ChangeRequestId == requestId).ToListAsync();
            foreach (var row in rows) row.ProposedRemoved = true;
            await seed.SaveChangesAsync();
        }

        await using (var db = new ErpDbContext(options))
        {
            var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
                () => SubmitAsync(Controller(db, userId), requestId));
            Assert.Contains("至少保留一行", ex.Message);
        }

        await using (var verify = new ErpDbContext(options))
        {
            var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                .SingleAsync(r => r.Id == requestId);
            Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, request.Status);
            Assert.Null(request.SubmittedAt);
        }
    }

    [Fact]
    public async Task 提交_重算被篡改的拟议合计与逐行金额()
    {
        var options = InMemoryOptions("erp415-submit-recalc-" + Guid.NewGuid().ToString("N"));
        long userId, orderId, requestId;
        await using (var seed = new ErpDbContext(options))
        {
            var s = await SeedAsync(seed);
            userId = s.UserId;
            orderId = s.OrderId;
            requestId = (await CreateAsync(Controller(seed, userId), orderId)).Id;

            var request = await seed.SalesOrderChangeRequests.SingleAsync(r => r.Id == requestId);
            request.ProposedTotalAmount = 999_999m;
            request.ProposedDepositAmount = 999_999m;
            var first = await seed.SalesOrderChangeRequestDetails
                .Where(d => d.ChangeRequestId == requestId).OrderBy(d => d.LineNo).FirstAsync();
            first.ProposedAmount = 1m;
            await seed.SaveChangesAsync();
        }

        await using (var db = new ErpDbContext(options))
            await SubmitAsync(Controller(db, userId), requestId);

        await using (var verify = new ErpDbContext(options))
        {
            var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                .SingleAsync(r => r.Id == requestId);
            Assert.Equal(150m, request.ProposedTotalAmount);
            Assert.Equal(45m, request.ProposedDepositAmount);
            var lines = await verify.SalesOrderChangeRequestDetails.AsNoTracking()
                .Where(d => d.ChangeRequestId == requestId && !d.IsDeleted).ToListAsync();
            Assert.All(lines, d => Assert.Equal(d.ProposedQuantity * d.ProposedUnitPrice, d.ProposedAmount));
        }
    }

    [Fact]
    public async Task 提交_拟议数量非正_校验失败且保持草稿()
    {
        var options = InMemoryOptions("erp415-submit-invalid-" + Guid.NewGuid().ToString("N"));
        long userId, orderId, requestId;
        await using (var seed = new ErpDbContext(options))
        {
            var s = await SeedAsync(seed);
            userId = s.UserId;
            orderId = s.OrderId;
            requestId = (await CreateAsync(Controller(seed, userId), orderId)).Id;

            var first = await seed.SalesOrderChangeRequestDetails
                .Where(d => d.ChangeRequestId == requestId).OrderBy(d => d.LineNo).FirstAsync();
            first.ProposedQuantity = 0m;
            await seed.SaveChangesAsync();
        }

        await using (var db = new ErpDbContext(options))
        {
            var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
                () => SubmitAsync(Controller(db, userId), requestId));
            Assert.Contains("数量", ex.Message);
        }

        await using (var verify = new ErpDbContext(options))
        {
            var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                .SingleAsync(r => r.Id == requestId);
            Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, request.Status);
            Assert.Null(request.SubmittedAt);
        }
    }

    // ==================== 5. 取消（保留首次原因与时间，输家不可替换） ====================

    [Fact]
    public async Task 取消_保留首次原因与时间_重复取消拒绝且不可替换()
    {
        var options = InMemoryOptions("erp415-cancel-" + Guid.NewGuid().ToString("N"));
        long userId, orderId, requestId;
        await using (var seed = new ErpDbContext(options))
        {
            var s = await SeedAsync(seed);
            userId = s.UserId;
            orderId = s.OrderId;
            requestId = (await CreateAsync(Controller(seed, userId), orderId)).Id;
        }

        DateTime firstAt;
        await using (var db = new ErpDbContext(options))
        {
            var cancelled = await CancelAsync(Controller(db, userId), requestId, "第一次取消原因");
            Assert.Equal(SalesOrderChangeRequestRules.StatusCancelled, cancelled.Status);
            Assert.Equal("第一次取消原因", cancelled.CancelledReason);
            Assert.NotNull(cancelled.CancelledAt);
            firstAt = cancelled.CancelledAt!.Value;
        }

        await using (var loser = new ErpDbContext(options))
        {
            var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
                () => CancelAsync(Controller(loser, userId), requestId, "第二次取消原因"));
            Assert.Contains("不能重复取消", ex.Message);
        }

        await using (var verify = new ErpDbContext(options))
        {
            var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                .SingleAsync(r => r.Id == requestId);
            Assert.Equal(SalesOrderChangeRequestRules.StatusCancelled, request.Status);
            Assert.Equal("第一次取消原因", request.CancelledReason);   // 输家绝不覆盖原始原因
            Assert.Equal(firstAt, request.CancelledAt);                // 也绝不覆盖原始时间
        }
    }

    [Fact]
    public async Task 取消已提交申请_保留冻结拟议快照()
    {
        var options = InMemoryOptions("erp415-cancel-submitted-" + Guid.NewGuid().ToString("N"));
        long userId, orderId, requestId;
        await using (var seed = new ErpDbContext(options))
        {
            var s = await SeedAsync(seed);
            userId = s.UserId;
            orderId = s.OrderId;
            requestId = (await CreateAsync(Controller(seed, userId), orderId)).Id;
            await SubmitAsync(Controller(seed, userId), requestId);
            await CancelAsync(Controller(seed, userId), requestId, "客户撤回已提交申请");
        }

        await using (var verify = new ErpDbContext(options))
        {
            var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                .SingleAsync(r => r.Id == requestId);
            Assert.Equal(SalesOrderChangeRequestRules.StatusCancelled, request.Status);
            Assert.Equal("客户撤回已提交申请", request.CancelledReason);
            Assert.NotNull(request.SubmittedAt);
            Assert.NotNull(request.CancelledAt);
            Assert.Equal(150m, request.ProposedTotalAmount);
            Assert.Equal(2, await verify.SalesOrderChangeRequestDetails
                .CountAsync(d => d.ChangeRequestId == requestId && !d.IsDeleted));
        }
    }

    // ==================== 6. 写入失败（受控业务冲突 / 零部分写入） ====================

    [Fact]
    public async Task 登记写入失败_SQL异常受控为业务冲突且零写入()
    {
        var options = InMemoryOptions("erp415-write-fail-" + Guid.NewGuid().ToString("N"));
        long userId, orderId;
        await using (var seed = new ErpDbContext(options))
        {
            var s = await SeedAsync(seed);
            userId = s.UserId;
            orderId = s.OrderId;
        }

        await using (var db = new InjectOnProposalWriteDbContext(options)
        {
            NextWriteException = new DbUpdateException(
                "Cannot insert duplicate key row 2601 (UNIQUE) SqlException Microsoft.Data.SqlClient")
        })
        {
            var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
                () => CreateAsync(Controller(db, userId), orderId));
            Assert.Equal(SalesOrderChangeRequestMutationRules.WriteConflictText, ex.Message);
            Assert.DoesNotContain("2601", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SqlException", ex.Message, StringComparison.Ordinal);
        }

        await using (var verify = new ErpDbContext(options))
        {
            Assert.Equal(0, await verify.SalesOrderChangeRequests.CountAsync());
            Assert.Equal(0, await verify.SalesOrderChangeRequestDetails.CountAsync());
        }
    }

    [Fact]
    public async Task 编辑写入失败_整体回滚零部分写入()
    {
        var options = InMemoryOptions("erp415-edit-fail-" + Guid.NewGuid().ToString("N"));
        long userId, orderId, requestId;
        string originalReason;
        int originalLines;
        await using (var seed = new ErpDbContext(options))
        {
            var s = await SeedAsync(seed);
            userId = s.UserId;
            orderId = s.OrderId;
            var created = await CreateAsync(Controller(seed, userId), orderId);
            requestId = created.Id;
            originalReason = created.Reason;
            originalLines = created.Details.Count;
        }

        await using (var db = new InjectOnProposalWriteDbContext(options)
        {
            NextWriteException = new InvalidOperationException("ERP-415 注入的明细替换失败（非数据库异常）")
        })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync(
                Controller(db, userId), requestId,
                new SalesOrderChangeRequestSaveDto
                {
                    Reason = "不应落库的新原因",
                    Details = new List<SalesOrderChangeRequestDetailSaveDto>
                    {
                        Detail(101, "A 商品", 99m, 9m),
                        Detail(102, "B 商品", 99m, 9m)
                    }
                }));
        }

        await using (var verify = new ErpDbContext(options))
        {
            var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                .SingleAsync(r => r.Id == requestId);
            Assert.Equal(originalReason, request.Reason);              // 半成品写入被丢弃
            Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, request.Status);
            Assert.Equal(150m, request.ProposedTotalAmount);           // 拟议合计保持原值
            var lines = await verify.SalesOrderChangeRequestDetails.AsNoTracking()
                .Where(d => d.ChangeRequestId == requestId && !d.IsDeleted).ToListAsync();
            Assert.Equal(originalLines, lines.Count);
            Assert.DoesNotContain(lines, d => d.ProposedQuantity == 99m);
        }
    }

    // ==================== 7. 锁内重读实时权限（提交前菜单撤销 fail closed） ====================

    [Fact]
    public async Task 锁内重读实时权限_提交前菜单撤销_fail_closed且零写入()
    {
        var options = InMemoryOptions("erp415-live-auth-" + Guid.NewGuid().ToString("N"));
        long userId, roleId, requestId;
        await using (var seed = new ErpDbContext(options))
        {
            var s = await SeedAsync(seed);
            userId = s.UserId;
            roleId = s.RoleId;
            requestId = (await CreateAsync(Controller(seed, userId), s.OrderId)).Id;
        }

        await using (var revoke = new ErpDbContext(options))
            await RevokeMenuAsync(revoke, roleId);

        // 直接用**加锁前的陈旧特权范围**调用服务：锁内必须重新解析实时权限并 fail closed
        var staleScope = new SalespersonDataScope { IsPrivileged = true, AllowedCustomerIds = null };
        await using (var db = new ErpDbContext(options))
        {
            var ex = await AssertBusinessAsync(ErrorCodes.Forbidden,
                () => SalesOrderChangeRequestService.SubmitAsync(db, requestId, staleScope, userId));
            Assert.Contains("销售订单", ex.Message);
        }

        await using (var verify = new ErpDbContext(options))
        {
            var request = await verify.SalesOrderChangeRequests.AsNoTracking()
                .SingleAsync(r => r.Id == requestId);
            Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, request.Status);   // 零写入
            Assert.Null(request.SubmittedAt);
        }
    }

    [Fact]
    public async Task 锁内实时授权_无实时身份时沿用调用方范围()
    {
        using var db = TestDbFactory.Create();
        var privileged = new SalespersonDataScope { IsPrivileged = true, AllowedCustomerIds = null };
        var resolved = await SalesOrderChangeRequestMutationRules.ResolveLiveScopeAsync(db, null, privileged);
        Assert.Same(privileged, resolved);
    }

    // ==================== 8. 来源身份解析（确定性锁序用） ====================

    [Fact]
    public async Task 来源身份解析_返回不可变来源Id_缺失或已删除拒绝()
    {
        var options = InMemoryOptions("erp415-source-id-" + Guid.NewGuid().ToString("N"));
        long userId, orderId, requestId;
        await using (var seed = new ErpDbContext(options))
        {
            var s = await SeedAsync(seed);
            userId = s.UserId;
            orderId = s.OrderId;
            requestId = (await CreateAsync(Controller(seed, userId), orderId)).Id;
        }

        await using (var db = new ErpDbContext(options))
        {
            Assert.Equal(orderId,
                await SalesOrderChangeRequestService.ResolveStoredSourceOrderIdAsync(db, requestId));

            var missing = await AssertBusinessAsync(ErrorCodes.NotFound,
                () => SalesOrderChangeRequestService.ResolveStoredSourceOrderIdAsync(db, 9_415_999L));
            Assert.Equal(SalesOrderChangeRequestMutationRules.RequestUnavailableText, missing.Message);
        }
    }
}
