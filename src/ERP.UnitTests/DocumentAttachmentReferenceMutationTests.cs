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
/// 业务单据附件引用「登记 / 作废」原子性与确定性行锁单元测试（ERP-412）。
/// 覆盖：登记在原子事务 + 权威父单据行锁内重新读取实时身份 / 菜单 / 客户范围与权威父单据之后才快照与写入；
/// 父单据已删除 → 零写入拒绝；归属越界 → 零写入拒绝；加锁只刷新技术字段 <c>UpdatedAt</c>（父单据商业字段不变）；
/// 并发登记输家被既有过滤唯一索引拒绝 → 映射为稳定业务冲突（Duplicate）且不泄露 SqlException / 内部路径；
/// 元数据写入失败 → 整体回滚零部分写入；重复 / 竞态作废 → 拒绝且绝不覆盖原始作废原因与时间戳；
/// 作废后显式重新登记生效且历史不可变；非关系型提供程序等价无事务无行锁。
/// <para>全部使用内存库（<c>TestDbFactory</c> / 测试内共享内存库与失败注入上下文）；
/// 真实 SQL 两个独立连接竞态见 <c>ERP.IntegrationTests.DocumentAttachmentReferenceMutationSqlServerTests</c>。
/// 不连接 SQL Server、不访问对象存储、不做浏览器 / UI 验收。</para>
/// </summary>
public class DocumentAttachmentReferenceMutationTests
{
    // ==================== 0. 脚手架 ====================

    private static DocumentAttachmentReferenceController Controller(ErpDbContext db, long? userId)
    {
        var http = new DefaultHttpContext();
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        return new DocumentAttachmentReferenceController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static DocumentAttachmentReferenceSaveDto DarDto(
        long parentId, string referenceId, string displayName = "原子性合同扫描件",
        string parentType = DocumentAttachmentReferenceRules.ParentTypeSalesOrder)
        => new()
        {
            ParentType = parentType,
            ParentId = parentId,
            Category = DocumentAttachmentReferenceRules.CategoryContract,
            DisplayName = displayName,
            ReferenceId = referenceId,
            SourceAuthorizationAcknowledged = true,
            SourceAuthorizationNote = "ERP-412 单元测试来源授权留痕",
            AuthorizedBy = "ERP-412 登记人"
        };

    private static DbContextOptions<ErpDbContext> InMemoryOptions()
        => new DbContextOptionsBuilder<ErpDbContext>()
            .UseInMemoryDatabase("erp412-" + Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    // ==================== 0.1 种子（既有菜单 / 既有业务员数据范围，不新增权限模型） ====================

    /// <summary>
    /// 播种受限业务员账号（登录名 = 员工编码，ERP-097 权威映射）并按需授予既有模块菜单；
    /// 返回账号、角色与**唯一可见客户**（其名下销售订单为「本人」，其它客户为「越界」）。
    /// </summary>
    private static async Task<(SysUser User, SysRole Role, long CustomerId)> SeedRestrictedSalesmanAsync(
        ErpDbContext db, params string[] menuCodes)
    {
        var code = $"E412_EMP_{Guid.NewGuid():N}";
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

        var role = new SysRole
        {
            RoleName = $"E412_ROLE_{code}", RoleCode = $"E412_{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });

        foreach (var menuCode in menuCodes)
        {
            var menu = db.SysMenus.FirstOrDefault(m => !m.IsDeleted && m.MenuCode == menuCode);
            if (menu is null)
            {
                menu = new SysMenu
                {
                    ParentId = 0, MenuName = $"菜单 {menuCode}", MenuCode = menuCode,
                    Path = $"/{menuCode}", Icon = "test", SortOrder = 1, MenuType = MenuType.Menu
                };
                db.SysMenus.Add(menu);
                await db.SaveChangesAsync();
            }

            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }

        await db.SaveChangesAsync();

        var customer = new BaseCustomer
        {
            CustomerCode = $"E412_C_{code}", CustomerName = code, Status = 1,
            CreditStatus = "正常", Currency = "USD", EmpId = employee.Id
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();

        return (user, role, customer.Id);
    }

    private static async Task<BaseCustomer> SeedForeignCustomerAsync(ErpDbContext db)
    {
        var code = $"E412_FOREIGN_{Guid.NewGuid():N}";
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常",
            Currency = "USD", EmpId = null
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<SalesOrder> SeedSalesOrderAsync(
        ErpDbContext db, long customerId, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = $"E412_SO_{Guid.NewGuid():N}", OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customerId, Currency = Currency.USD, TotalAmount = 4321.00m,
            DepositAmount = 100m, Status = DocumentStatus.Approved, IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    /// <summary>Grant/revoke 既有菜单（不改写任何权限模型：只改既有「角色 → 菜单」授权行的软删除标记）。</summary>
    private static async Task RevokeMenusAsync(ErpDbContext db, long roleId)
    {
        var grants = await db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    // ==================== 0.2 失败注入上下文（内存库） ====================

    /// <summary>内存库上模拟「元数据写入失败 / 并发唯一索引冲突」：仅在插入新附件引用时注入一次。</summary>
    private sealed class InjectOnReferenceInsertDbContext : ErpDbContext
    {
        public InjectOnReferenceInsertDbContext(DbContextOptions<ErpDbContext> options) : base(options)
        {
        }

        /// <summary>下一次「插入新附件引用」的保存抛出的异常（触发后自动清零）；null = 正常保存。</summary>
        public Exception? NextReferenceInsertException { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var pending = NextReferenceInsertException;
            if (pending is not null
                && ChangeTracker.Entries<DocumentAttachmentReference>().Any(e => e.State == EntityState.Added))
            {
                NextReferenceInsertException = null;
                throw pending;
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    // ==================== 1. 护栏契约与确定性辅助 ====================

    [Fact]
    public void 护栏契约_锁定SQL与文案声明不改写父单据商业字段且不泄露内部路径()
    {
        Assert.Contains("UPDLOCK, HOLDLOCK", DocumentAttachmentReferenceMutationRules.ParentRowLockSqlTemplate);
        Assert.Contains("UPDLOCK, HOLDLOCK", DocumentAttachmentReferenceMutationRules.ReferenceRowLockSql);
        Assert.Contains("db_owner.DocumentAttachmentReferences", DocumentAttachmentReferenceMutationRules.ReferenceRowLockSql);
        Assert.Contains("UpdatedAt", DocumentAttachmentReferenceMutationRules.LockOrderText);
        Assert.Contains("不改写", DocumentAttachmentReferenceMutationRules.BoundaryText);
        Assert.Contains("UPDLOCK, HOLDLOCK", DocumentAttachmentReferenceMutationRules.LockOrderText);
        Assert.Equal(8, DocumentAttachmentReferenceMutationRules.RowLockRetryAttempts);

        // 并发输家文案声明「唯一身份口径」且绝不内联表名 / 索引名 / 错误码 / 驱动名
        var conflict = DocumentAttachmentReferenceMutationRules.ActiveIdentityConflictText;
        Assert.Contains("唯一身份口径", conflict, StringComparison.Ordinal);
        Assert.DoesNotContain("2601", conflict, StringComparison.Ordinal);
        Assert.DoesNotContain("2627", conflict, StringComparison.Ordinal);
        Assert.DoesNotContain("UX_", conflict, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlException", conflict, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Data.SqlClient", conflict, StringComparison.Ordinal);

        var rules = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "DocumentAttachmentReferenceMutationRules.cs"));
        Assert.Contains("BeginMutationTransactionAsync", rules);
        Assert.Contains("LockParentRowAsync", rules);
        Assert.Contains("LockReferenceRowAsync", rules);
        Assert.Contains("SaveReferenceAsync", rules);
        Assert.Contains("LooksLikeUniqueViolation", rules);
        Assert.Contains("UX_DocumentAttachmentReferences_ActiveIdentity", rules);
        // 不引用驱动类型（仅文档注释可提及驱动名），也就不会泄露 SqlException；护栏文件不得出现任何 DDL
        Assert.DoesNotContain("using Microsoft.Data.SqlClient", rules);
        Assert.DoesNotContain("using System.Data.SqlClient", rules);
        Assert.DoesNotContain("catch (SqlException", rules);
        Assert.DoesNotContain("throw new SqlException", rules);
        Assert.DoesNotContain("CREATE TABLE", rules, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE UNIQUE INDEX", rules, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", rules, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 服务契约_登记先锁父单据行且锁内重读实时授权与权威父单据()
    {
        var service = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "DocumentAttachmentReferenceService.cs"));

        // 原子事务 + 确定性行锁（登记锁父单据行，作废锁引用行）
        Assert.Contains("DocumentAttachmentReferenceMutationRules.BeginMutationTransactionAsync", service);
        Assert.Contains("DocumentAttachmentReferenceMutationRules.LockParentRowAsync", service);
        Assert.Contains("DocumentAttachmentReferenceMutationRules.LockReferenceRowAsync", service);
        Assert.Contains("DocumentAttachmentReferenceMutationRules.SaveReferenceAsync", service);

        // 登记：加锁 → 锁内实时重解析授权 → 锁内重读权威父单据 → 锁内复核范围 → 锁内判定唯一 → 写入
        var lockAt = service.IndexOf(
            "DocumentAttachmentReferenceMutationRules.LockParentRowAsync", StringComparison.Ordinal);
        var liveAt = service.IndexOf("ResolveDocumentReferenceAccessAsync", StringComparison.Ordinal);
        var rereadAt = service.IndexOf("ResolveParentAsync(db, parentType, dto.ParentId)", StringComparison.Ordinal);
        var scopeAt = service.IndexOf("EnsureReferenceParentScopeAsync", StringComparison.Ordinal);
        var identityAt = service.IndexOf("EnsureIdentityAvailableAsync", StringComparison.Ordinal);
        var writeAt = service.IndexOf(
            "DocumentAttachmentReferenceMutationRules.SaveReferenceAsync", StringComparison.Ordinal);

        Assert.True(lockAt > 0 && lockAt < liveAt, "登记必须先在锁内重解析实时授权");
        Assert.True(lockAt < rereadAt, "登记必须在加锁之后重读权威父单据");
        Assert.True(lockAt < scopeAt, "登记必须在加锁之后复核权威归属范围");
        Assert.True(lockAt < identityAt, "登记必须在加锁之后判定有效身份唯一");
        Assert.True(lockAt < writeAt, "登记必须在加锁之后才写入");

        // 作废：加锁 → 锁内重解析实时授权 → 锁内判定可作废 → 写入
        var voidLockAt = service.LastIndexOf(
            "DocumentAttachmentReferenceMutationRules.LockReferenceRowAsync", StringComparison.Ordinal);
        var voidableAt = service.IndexOf("EnsureVoidable", StringComparison.Ordinal);
        Assert.True(voidLockAt > 0 && voidLockAt < voidableAt, "作废必须在加锁之后重读引用并判定可作废");

        // 失败整体回滚（zero partial write）
        Assert.Contains("TryRollbackAsync", service);
    }

    // ==================== 2. 登记（原子事务 + 权威父单据行锁） ====================

    [Fact]
    public async Task 非关系型提供程序_事务与行锁等价无操作_冲突判定与锁键序稳定()
    {
        using var db = TestDbFactory.Create();
        var order = new SalesOrder
        {
            OrderNo = $"E412_LOCK_{Guid.NewGuid():N}", OrderDate = new DateTime(2026, 9, 1),
            CustomerId = 7, Currency = Currency.USD, TotalAmount = 4321.00m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();

        Assert.False(DocumentAttachmentReferenceMutationRules.IsRelationalProvider(db));
        Assert.Null(await DocumentAttachmentReferenceMutationRules.BeginMutationTransactionAsync(db));

        Assert.True(await DocumentAttachmentReferenceMutationRules.LockParentRowAsync(
            db, DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id));
        Assert.False(await DocumentAttachmentReferenceMutationRules.LockParentRowAsync(
            db, "Quotation", order.Id));
        Assert.False(await DocumentAttachmentReferenceMutationRules.LockParentRowAsync(
            db, DocumentAttachmentReferenceRules.ParentTypeSalesOrder, 0));
        Assert.Null(await DocumentAttachmentReferenceMutationRules.LockReferenceRowAsync(db, 0));
        Assert.Null(await DocumentAttachmentReferenceMutationRules.LockReferenceRowAsync(db, 987_654));

        Assert.Equal(new long[] { 2, 5, 9 },
            DocumentAttachmentReferenceMutationRules.MergeLockIds(new long[] { 5, 0, -3, 2, 5, 9, 2 }).ToArray());
        Assert.Empty(DocumentAttachmentReferenceMutationRules.MergeLockIds(null));

        Assert.True(DocumentAttachmentReferenceMutationRules.LooksLikeUniqueViolation(
            new DbUpdateException("Cannot insert duplicate key row ... 2601 ...")));
        Assert.True(DocumentAttachmentReferenceMutationRules.LooksLikeUniqueViolation(
            new DbUpdateException("Violation of UNIQUE KEY constraint")));
        Assert.False(DocumentAttachmentReferenceMutationRules.LooksLikeUniqueViolation(
            new DbUpdateException("Invalid object name 'db_owner.DocumentAttachmentReferences'")));

        // 非关系型：加锁等价无操作，绝不改写父单据商业字段（存在性仍由锁内重读判定）
        var tracked = await db.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(4321.00m, tracked.TotalAmount);
        Assert.Equal(DocumentStatus.Approved, tracked.Status);
    }

    [Fact]
    public async Task 登记_锁内重读权威父单据_写入服务端快照且父单据商业字段不变()
    {
        var options = InMemoryOptions();
        long userId, orderId, customerId;
        await using (var seed = new ErpDbContext(options))
        {
            var (user, _, customer) = await SeedRestrictedSalesmanAsync(
                seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            customerId = customer;
            orderId = (await SeedSalesOrderAsync(seed, customer)).Id;
        }

        await using (var db = new ErpDbContext(options))
        {
            var result = await Controller(db, userId).Create(DarDto(orderId, "e412-create-ok"));
            var dto = Assert.IsType<ApiResponse<DocumentAttachmentReferenceDto>>(
                Assert.IsType<OkObjectResult>(result).Value).Data!;

            Assert.Equal(DocumentAttachmentReferenceRules.StatusActive, dto.Status);
            Assert.Equal(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, dto.ParentType);
            Assert.Equal(orderId, dto.ParentId);
            Assert.Equal("ERP-412 单元测试来源授权留痕", dto.SourceAuthorizationNote);
        }

        await using (var verify = new ErpDbContext(options))
        {
            var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            Assert.Equal(customerId, order.CustomerId);
            Assert.Equal(4321.00m, order.TotalAmount);
            Assert.Equal(100m, order.DepositAmount);
            Assert.Equal(DocumentStatus.Approved, order.Status);
            Assert.False(order.IsDeleted);

            var stored = await verify.DocumentAttachmentReferences.AsNoTracking().SingleAsync();
            Assert.Equal(order.OrderNo, stored.ParentNo);
            Assert.Equal(
                DocumentAttachmentReferenceRules.ParentTypeText(DocumentAttachmentReferenceRules.ParentTypeSalesOrder),
                stored.ParentTypeText);
            Assert.Equal(DocumentAttachmentReferenceRules.StatusActive, stored.Status);

            // 元数据引用册以外的任何数据零写入（不生成库存移动，不写附件内容证据）
            Assert.Equal(0, await verify.Stocks.CountAsync());
            Assert.Equal(0, await verify.StockMovements.CountAsync());
            Assert.Equal(0, await verify.AttachmentEvidences.CountAsync());
        }
    }

    [Fact]
    public async Task 登记_父单据已删除_锁内重读拒绝且零写入()
    {
        var options = InMemoryOptions();
        long userId, orderId;
        await using (var seed = new ErpDbContext(options))
        {
            var (user, _, customer) = await SeedRestrictedSalesmanAsync(
                seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            orderId = (await SeedSalesOrderAsync(seed, customer, deleted: true)).Id;
        }

        await using (var db = new ErpDbContext(options))
        {
            var ex = await AssertBusinessAsync(ErrorCodes.NotFound,
                () => Controller(db, userId).Create(DarDto(orderId, "e412-deleted-parent")));
            Assert.Contains("不存在或已删除", ex.Message, StringComparison.Ordinal);
        }

        await using (var verify = new ErpDbContext(options))
        {
            Assert.Equal(0, await verify.DocumentAttachmentReferences.CountAsync());
        }
    }

    [Fact]
    public async Task 登记_归属越界_锁内实时范围复核拒绝且零写入()
    {
        var options = InMemoryOptions();
        long userId, ownOrderId, foreignOrderId;
        await using (var seed = new ErpDbContext(options))
        {
            var (user, _, ownCustomer) = await SeedRestrictedSalesmanAsync(
                seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            ownOrderId = (await SeedSalesOrderAsync(seed, ownCustomer)).Id;
            var foreign = await SeedForeignCustomerAsync(seed);
            foreignOrderId = (await SeedSalesOrderAsync(seed, foreign.Id)).Id;
        }

        await using (var db = new ErpDbContext(options))
        {
            var ex = await AssertBusinessAsync(ErrorCodes.Forbidden,
                () => Controller(db, userId).Create(DarDto(foreignOrderId, "e412-out-of-scope")));
            Assert.Contains("客户数据范围", ex.Message, StringComparison.Ordinal);
        }

        await using (var verify = new ErpDbContext(options))
        {
            Assert.Equal(0, await verify.DocumentAttachmentReferences.CountAsync());
            // 越界登记绝不改写任何父单据（含本人订单）
            var own = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == ownOrderId);
            Assert.Equal(DocumentStatus.Approved, own.Status);
            Assert.Equal(4321.00m, own.TotalAmount);
        }
    }

    [Fact]
    public async Task 登记_旧授权快照不能替代锁内实时重解析_菜单撤销后拒绝且零写入()
    {
        var options = InMemoryOptions();
        long userId, orderId, customerId, roleId;
        await using (var seed = new ErpDbContext(options))
        {
            var (user, role, customer) = await SeedRestrictedSalesmanAsync(
                seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            roleId = role.Id;
            customerId = customer;
            orderId = (await SeedSalesOrderAsync(seed, customer)).Id;
        }

        // 加锁前解析（已授权）→ 提交前撤销菜单 → 锁内实时重解析必须收敛（fail closed）
        await using (var revoke = new ErpDbContext(options))
            await RevokeMenusAsync(revoke, roleId);

        await using (var db = new ErpDbContext(options))
        {
            var stale = new DocumentReferenceAccessContext
            {
                UserId = userId,
                UserName = "e412-stale",
                Scope = new SalespersonDataScope
                {
                    IsPrivileged = false,
                    SalesmanId = 1,
                    AllowedCustomerIds = new HashSet<long> { customerId }
                },
                AuthorizedParentTypes = new List<string>
                {
                    DocumentAttachmentReferenceRules.ParentTypeSalesOrder
                }
            };

            var ex = await AssertBusinessAsync(ErrorCodes.Forbidden, () =>
                DocumentAttachmentReferenceService.CreateAsync(
                    db, DarDto(orderId, "e412-stale-snapshot"), stale));
            Assert.Contains("菜单", ex.Message, StringComparison.Ordinal);
        }

        await using (var verify = new ErpDbContext(options))
        {
            Assert.Equal(0, await verify.DocumentAttachmentReferences.CountAsync());
        }
    }

    [Fact]
    public async Task 登记_并发唯一索引冲突_映射稳定业务冲突且零写入_不泄露内部路径()
    {
        var options = InMemoryOptions();
        long userId, orderId;
        await using (var seed = new ErpDbContext(options))
        {
            var (user, _, customer) = await SeedRestrictedSalesmanAsync(
                seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            orderId = (await SeedSalesOrderAsync(seed, customer)).Id;
        }

        await using (var db = new InjectOnReferenceInsertDbContext(options)
        {
            NextReferenceInsertException = new DbUpdateException(
                "Cannot insert duplicate key row in object 'db_owner.DocumentAttachmentReferences' "
                + "with unique index 'UX_DocumentAttachmentReferences_ActiveIdentity'. 2601")
        })
        {
            var ex = await AssertBusinessAsync(ErrorCodes.Duplicate,
                () => Controller(db, userId).Create(DarDto(orderId, "e412-unique-conflict")));

            // 稳定的业务冲突文案：绝不暴露 SqlException / 错误码 / 索引名 / 驱动名
            Assert.Equal(DocumentAttachmentReferenceMutationRules.ActiveIdentityConflictText, ex.Message);
            Assert.DoesNotContain("2601", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("UX_", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SqlException", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Microsoft.Data.SqlClient", ex.Message, StringComparison.Ordinal);
        }

        await using (var verify = new ErpDbContext(options))
        {
            Assert.Equal(0, await verify.DocumentAttachmentReferences.CountAsync());
        }
    }

    [Fact]
    public async Task 登记_元数据写入失败_完整回滚零部分写入且父单据不变()
    {
        var options = InMemoryOptions();
        long userId, orderId, customerId;
        await using (var seed = new ErpDbContext(options))
        {
            var (user, _, customer) = await SeedRestrictedSalesmanAsync(
                seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            customerId = customer;
            orderId = (await SeedSalesOrderAsync(seed, customer)).Id;
        }

        await using (var db = new InjectOnReferenceInsertDbContext(options)
        {
            NextReferenceInsertException = new InvalidOperationException("ERP-412 注入的元数据写入失败（非唯一性）")
        })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Controller(db, userId).Create(DarDto(orderId, "e412-write-failure")));
        }

        await using (var verify = new ErpDbContext(options))
        {
            Assert.Equal(0, await verify.DocumentAttachmentReferences.CountAsync());

            var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            Assert.Equal(customerId, order.CustomerId);
            Assert.Equal(4321.00m, order.TotalAmount);
            Assert.Equal(DocumentStatus.Approved, order.Status);
            Assert.False(order.IsDeleted);

            Assert.Equal(0, await verify.Stocks.CountAsync());
            Assert.Equal(0, await verify.StockMovements.CountAsync());
        }
    }

    // ==================== 3. 作废（原子事务 + 附件引用行锁） ====================

    private static async Task<DocumentAttachmentReferenceDto> CreateOkAsync(
        DocumentAttachmentReferenceController controller, DocumentAttachmentReferenceSaveDto dto)
        => Assert.IsType<ApiResponse<DocumentAttachmentReferenceDto>>(
            Assert.IsType<OkObjectResult>(await controller.Create(dto)).Value).Data!;

    private static async Task<DocumentAttachmentReferenceDto> VoidOkAsync(
        DocumentAttachmentReferenceController controller, long id, string reason)
        => Assert.IsType<ApiResponse<DocumentAttachmentReferenceDto>>(
            Assert.IsType<OkObjectResult>(await controller.Void(
                id, new DocumentAttachmentReferenceVoidRequest { Reason = reason })).Value).Data!;

    [Fact]
    public async Task 作废_重复作废_拒绝且保留原始原因与时间戳_历史不可变()
    {
        var options = InMemoryOptions();
        long userId, orderId;
        await using (var seed = new ErpDbContext(options))
        {
            var (user, _, customer) = await SeedRestrictedSalesmanAsync(
                seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            orderId = (await SeedSalesOrderAsync(seed, customer)).Id;
        }

        long createdId;
        await using (var db = new ErpDbContext(options))
            createdId = (await CreateOkAsync(Controller(db, userId), DarDto(orderId, "e412-void-repeat"))).Id;

        DateTime? originalVoidedAt;
        await using (var first = new ErpDbContext(options))
        {
            var voided = await VoidOkAsync(Controller(first, userId), createdId, "原始作废原因");
            Assert.True(voided.IsVoided);
            Assert.Equal("原始作废原因", voided.VoidReason);
            originalVoidedAt = voided.VoidedAt;
        }

        await using (var second = new ErpDbContext(options))
        {
            var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
                () => Controller(second, userId).Void(
                    createdId, new DocumentAttachmentReferenceVoidRequest { Reason = "试图覆盖的原因" }));
            Assert.Contains("已作废", ex.Message, StringComparison.Ordinal);
        }

        await using (var verify = new ErpDbContext(options))
        {
            var stored = await verify.DocumentAttachmentReferences.AsNoTracking()
                .SingleAsync(r => r.Id == createdId);
            Assert.Equal(DocumentAttachmentReferenceRules.StatusVoided, stored.Status);
            Assert.Equal("原始作废原因", stored.VoidReason);
            Assert.Equal(originalVoidedAt, stored.VoidedAt);
            Assert.Equal("ERP-412 单元测试来源授权留痕", stored.SourceAuthorizationNote);
            Assert.True(stored.SourceAuthorizationAcknowledged);
        }
    }

    [Fact]
    public async Task 作废_两个独立上下文_输家不覆盖赢家原因_作废后可重新登记且历史不可变()
    {
        var options = InMemoryOptions();
        long userId, orderId;
        await using (var seed = new ErpDbContext(options))
        {
            var (user, _, customer) = await SeedRestrictedSalesmanAsync(
                seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            orderId = (await SeedSalesOrderAsync(seed, customer)).Id;
        }

        long createdId;
        await using (var db = new ErpDbContext(options))
            createdId = (await CreateOkAsync(Controller(db, userId), DarDto(orderId, "e412-void-race"))).Id;

        DateTime? winnerAt;
        await using (var winner = new ErpDbContext(options))
            winnerAt = (await VoidOkAsync(Controller(winner, userId), createdId, "赢家原因")).VoidedAt;

        await using (var loser = new ErpDbContext(options))
        {
            await AssertBusinessAsync(ErrorCodes.RuleConflict,
                () => Controller(loser, userId).Void(
                    createdId, new DocumentAttachmentReferenceVoidRequest { Reason = "输家原因" }));
        }

        // 作废后同一引用标识可重新登记（新身份）；历史记录保持不可变
        long reRegisteredId;
        await using (var reRegister = new ErpDbContext(options))
            reRegisteredId = (await CreateOkAsync(
                Controller(reRegister, userId), DarDto(orderId, "e412-void-race"))).Id;

        await using (var verify = new ErpDbContext(options))
        {
            Assert.NotEqual(createdId, reRegisteredId);

            var original = await verify.DocumentAttachmentReferences.AsNoTracking()
                .SingleAsync(r => r.Id == createdId);
            Assert.Equal(DocumentAttachmentReferenceRules.StatusVoided, original.Status);
            Assert.Equal("赢家原因", original.VoidReason);
            Assert.Equal(winnerAt, original.VoidedAt);

            var active = await verify.DocumentAttachmentReferences.AsNoTracking()
                .SingleAsync(r => r.Status == DocumentAttachmentReferenceRules.StatusActive);
            Assert.Equal(reRegisteredId, active.Id);

            // 恰好一条有效身份（唯一口径）+ 一条历史留痕
            Assert.Equal(1, await verify.DocumentAttachmentReferences.CountAsync(
                r => r.Status == DocumentAttachmentReferenceRules.StatusActive));
            Assert.Equal(2, await verify.DocumentAttachmentReferences.CountAsync());
        }
    }

    [Fact]
    public async Task 作废_Id非法或不存在_拒绝且零写入()
    {
        var options = InMemoryOptions();
        long userId, orderId;
        await using (var seed = new ErpDbContext(options))
        {
            var (user, _, customer) = await SeedRestrictedSalesmanAsync(
                seed, DocumentAttachmentReferenceRules.MenuCodeSalesOrder);
            userId = user.Id;
            orderId = (await SeedSalesOrderAsync(seed, customer)).Id;
        }

        await using (var db = new ErpDbContext(options))
        {
            var controller = Controller(db, userId);

            var invalid = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
                () => controller.Void(0, new DocumentAttachmentReferenceVoidRequest { Reason = "作废" }));
            Assert.Contains("Id", invalid.Message, StringComparison.Ordinal);

            var missing = await AssertBusinessAsync(ErrorCodes.NotFound,
                () => controller.Void(999_123, new DocumentAttachmentReferenceVoidRequest { Reason = "作废" }));
            Assert.Contains("不存在或已删除", missing.Message, StringComparison.Ordinal);
        }

        await using (var verify = new ErpDbContext(options))
        {
            Assert.Equal(0, await verify.DocumentAttachmentReferences.CountAsync());
            Assert.Equal(0, await verify.DocumentAttachmentReferences
                .CountAsync(r => r.Status == DocumentAttachmentReferenceRules.StatusVoided));
        }
    }
}



