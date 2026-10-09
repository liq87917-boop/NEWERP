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
/// ERP-437 代理服务费对账单证据（<c>api/agency-service-fee-statements</c>）与收款分摊证据
/// （<c>api/agency-service-fee-collection-allocations</c>）**真实 SQL Server** 实时授权与权威客户范围集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item>每一条路由在读取 / 写入之前都要求实时启用身份 + 既有「客户资料」（customer）功能菜单（缺一即 fail closed）；</item>
/// <item>受限业务员只读 / 只写本人被分配客户的对账单、收款单与分摊行，范围在 <c>Count</c> / 分页之前下推；</item>
/// <item>越范围 / 已删除 / 不存在的对账单、收款单与分摊行返回同一条不披露存在性的错误，且被拒写入零行；</item>
/// <item>被许可的对账单与分摊行生命周期（登记 / 作废）在同一范围内正常完成。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class AgencyServiceFeeRegisterAuthorizationSqlServerTests
    : IClassFixture<AgencyServiceFeeRegisterAuthorizationSqlServerFixture>
{
    private readonly AgencyServiceFeeRegisterAuthorizationSqlServerFixture _fixture;

    public AgencyServiceFeeRegisterAuthorizationSqlServerTests(
        AgencyServiceFeeRegisterAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(AgencyServiceFeeRegisterAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 断言 / 控制器脚手架 ====================

    private static async Task<BusinessException> AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await action());
        Assert.Equal(expected, ex.Code);
        return ex;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static AgencyServiceFeeStatementController NewStatementController(ErpDbContext db, long? userId)
    {
        var ctl = new AgencyServiceFeeStatementController(db);
        SetUser(ctl, userId);
        return ctl;
    }

    private static AgencyServiceFeeCollectionAllocationController NewAllocationController(
        ErpDbContext db, long? userId)
    {
        var ctl = new AgencyServiceFeeCollectionAllocationController(db);
        SetUser(ctl, userId);
        return ctl;
    }

    private static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 种子数据（SQL 自增主键，不显式指定 Id） ====================

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"ASF-C-{Guid.NewGuid():N}"[..30], CustomerName = name, EmpId = empId, Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedEmployeeAsync(ErpDbContext db)
    {
        var code = $"asf-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        return employee.Id;
    }

    private static async Task<long> SeedRestrictedOperatorAsync(ErpDbContext db, string userName, bool grantMenu)
    {
        var user = new SysUser
        {
            UserName = userName, DisplayName = userName, PasswordHash = "hash", PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "代理服务费证据操作员", RoleCode = $"AsfOp-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        await GrantCustomerMenuIfAsync(db, role.Id, grantMenu);
        return user.Id;
    }

    private static async Task<long> SeedPrivilegedOperatorAsync(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"asf-priv-{Guid.NewGuid():N}", DisplayName = "代理服务费证据特权账号",
            PasswordHash = "hash", PasswordSalt = "salt", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "代理服务费证据特权角色", RoleCode = $"AsfPriv-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        await GrantCustomerMenuIfAsync(db, role.Id, true);
        return user.Id;
    }

    private static async Task GrantCustomerMenuIfAsync(ErpDbContext db, long roleId, bool grant)
    {
        if (!grant) return;
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == AgencyServiceFeeReconciliationRules.RequiredMenuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        await db.SaveChangesAsync();
    }

    private static async Task RevokeMenusAsync(ErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        var grants = await db.SysRoleMenus
            .Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task DisableUserAsync(ErpDbContext db, long userId)
    {
        var user = await db.SysUsers.SingleAsync(u => u.Id == userId);
        user.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();
    }

    private static async Task<long> SeedDeletedUserAsync(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"asf-deleted-{Guid.NewGuid():N}", DisplayName = "已删除账号",
            PasswordHash = "hash", PasswordSalt = "salt", Status = UserStatus.Enabled, IsDeleted = true
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<long> SeedAgreementAsync(ErpDbContext db, long customerId, string agreementNo)
    {
        var agreement = new AgencyServiceFeeAgreement
        {
            AgreementNo = agreementNo,
            NormalizedAgreementNo = AgencyServiceFeeAgreementRules.NormalizeAgreementNo(agreementNo),
            CustomerId = customerId,
            Currency = "USD",
            EffectiveFrom = new DateTime(2026, 1, 1),
            FeeMethod = AgencyServiceFeeAgreementRules.FeeMethodRate,
            RatePercent = 1.5m,
            FeeBasis = "按出口发票金额",
            Status = AgencyServiceFeeAgreementRules.StatusRecorded,
            RecordedAt = new DateTime(2026, 1, 2),
            RecordedBy = "集成测试"
        };
        db.AgencyServiceFeeAgreements.Add(agreement);
        await db.SaveChangesAsync();
        return agreement.Id;
    }

    private static async Task<long> SeedSalesOrderAsync(ErpDbContext db, long customerId, string orderNo)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo, OrderDate = new DateTime(2026, 8, 15), CustomerId = customerId,
            Currency = Currency.USD, TotalAmount = 1000m, Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private static async Task<long> SeedReceiptAsync(ErpDbContext db, long customerId, string receiptNo)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo, ReceiptDate = new DateTime(2026, 9, 25), CustomerId = customerId,
            Amount = 1000m, Currency = Currency.USD, PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "TEST-ACCOUNT", Status = DocumentStatus.Approved
        };
        db.FinanceReceipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt.Id;
    }

    private static async Task<AgencyServiceFeeStatement> SeedStatementAsync(
        ErpDbContext db, long customerId, string statementNo, decimal totalAmount,
        int status = AgencyServiceFeeStatementRules.StatusRecorded, bool deleted = false)
    {
        var statement = new AgencyServiceFeeStatement
        {
            StatementNo = statementNo,
            NormalizedStatementNo = AgencyServiceFeeStatementRules.NormalizeIdentityPart(statementNo),
            CustomerId = customerId,
            CustomerCode = "ASF-INT",
            CustomerName = "集成测试客户",
            Currency = "USD",
            StatementDate = new DateTime(2026, 9, 20),
            ServicePeriodFrom = new DateTime(2026, 8, 1),
            ServicePeriodTo = new DateTime(2026, 8, 31),
            AgreementId = 1,
            AgreementNo = "ASF-INT",
            AgreementCurrency = "USD",
            AgreementCustomerId = customerId,
            AgreementFeeMethod = AgencyServiceFeeAgreementRules.FeeMethodRate,
            AgreementTermsText = "比例费率",
            TotalAmount = totalAmount,
            Status = status,
            RecordedAt = status == AgencyServiceFeeStatementRules.StatusRecorded ? new DateTime(2026, 9, 21) : null,
            RecordedBy = status == AgencyServiceFeeStatementRules.StatusRecorded ? "集成测试" : string.Empty,
            IsDeleted = deleted
        };
        db.AgencyServiceFeeStatements.Add(statement);
        await db.SaveChangesAsync();
        return statement;
    }

    private static AgencyServiceFeeStatementSaveDto SalesOrderSave(
        long customerId, long agreementId, long orderId, string statementNo, decimal amount = 100m)
        => new()
        {
            StatementNo = statementNo,
            CustomerId = customerId,
            Currency = "USD",
            StatementDate = new DateTime(2026, 9, 20),
            ServicePeriodFrom = new DateTime(2026, 8, 1),
            ServicePeriodTo = new DateTime(2026, 8, 31),
            AgreementId = agreementId,
            Lines = new List<AgencyServiceFeeStatementLineSaveDto>
            {
                new()
                {
                    SourceType = AgencyServiceFeeStatementRules.SourceTypeSalesOrder,
                    SourceId = orderId,
                    Description = "代理服务费",
                    Amount = amount,
                    Remark = string.Empty
                }
            }
        };

    private static AgencyServiceFeeCollectionAllocationSaveDto AllocationSave(
        long statementId, long receiptId, decimal amount = 20m)
        => new() { StatementId = statementId, ReceiptId = receiptId, AllocatedAmount = amount, Remark = "集成测试" };

    // ==================== 1. 身份 / 菜单矩阵（每一条路由先于任何读取 / 写入） ====================

    [Fact]
    public async Task Live_register_routes_reject_missing_disabled_deleted_and_revoked_identities_with_zero_mutation()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db, "ERP-437 认证客户");
        var statement = await SeedStatementAsync(db, customer, $"ASFS-437-{Tag()}", 1000m);
        var receipt = await SeedReceiptAsync(db, customer, $"RC-437-{Tag()}");

        var disabledId = await SeedPrivilegedOperatorAsync(db);
        await DisableUserAsync(db, disabledId);
        var deletedId = await SeedDeletedUserAsync(db);
        var revokedId = await SeedPrivilegedOperatorAsync(db);
        await RevokeMenusAsync(db, revokedId);
        var noMenuId = await SeedRestrictedOperatorAsync(db, $"asf437-nomenu-{Guid.NewGuid():N}", grantMenu: false);

        var beforeStatements = await db.AgencyServiceFeeStatements.AsNoTracking().CountAsync();
        var beforeAllocations = await db.AgencyServiceFeeCollectionAllocations.AsNoTracking().CountAsync();

        // 缺失 / 非法 / 已删除身份 → 未认证
        foreach (long? userId in new long?[] { null, 0, deletedId })
        {
            var stmts = NewStatementController(db, userId);
            await AssertCode(ErrorCodes.Unauthorized, () => stmts.GetPaged(new AgencyServiceFeeStatementQuery()));
            await AssertCode(ErrorCodes.Unauthorized, () => stmts.SourceOptions(null, customer, "USD", null, 50));
            await AssertCode(ErrorCodes.Unauthorized, () => stmts.GetById(statement.Id));
            await AssertCode(ErrorCodes.Unauthorized, () => stmts.Create(SalesOrderSave(customer, 1, 1, "X")));
            await AssertCode(ErrorCodes.Unauthorized, () => stmts.Update(statement.Id, SalesOrderSave(customer, 1, 1, "X")));
            await AssertCode(ErrorCodes.Unauthorized, () => stmts.Record(statement.Id));
            await AssertCode(ErrorCodes.Unauthorized,
                () => stmts.Void(statement.Id, new AgencyServiceFeeStatementVoidRequest { Reason = "作废" }));

            var allocs = NewAllocationController(db, userId);
            await AssertCode(ErrorCodes.Unauthorized,
                () => allocs.GetPaged(new AgencyServiceFeeCollectionAllocationQuery()));
            await AssertCode(ErrorCodes.Unauthorized, () => allocs.ReceiptCandidates(customer, "USD", null, 50));
            await AssertCode(ErrorCodes.Unauthorized, () => allocs.StatementCandidates(customer, "USD", null, 50));
            await AssertCode(ErrorCodes.Unauthorized, () => allocs.ReceiptSummary(receipt));
            await AssertCode(ErrorCodes.Unauthorized, () => allocs.AllocationsForReceipt(receipt));
            await AssertCode(ErrorCodes.Unauthorized, () => allocs.StatementSummary(statement.Id));
            await AssertCode(ErrorCodes.Unauthorized, () => allocs.AllocationsForStatement(statement.Id));
            await AssertCode(ErrorCodes.Unauthorized, () => allocs.GetById(1));
            await AssertCode(ErrorCodes.Unauthorized, () => allocs.Create(AllocationSave(statement.Id, receipt)));
            await AssertCode(ErrorCodes.Unauthorized,
                () => allocs.Void(1, new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = "作废" }));
        }

        // 禁用 / 撤销菜单 / 无菜单 → 权限不足
        foreach (var userId in new[] { disabledId, revokedId, noMenuId })
        {
            var stmts = NewStatementController(db, userId);
            await AssertCode(ErrorCodes.Forbidden, () => stmts.GetPaged(new AgencyServiceFeeStatementQuery()));
            await AssertCode(ErrorCodes.Forbidden, () => stmts.SourceOptions(null, customer, "USD", null, 50));
            await AssertCode(ErrorCodes.Forbidden, () => stmts.GetById(statement.Id));
            await AssertCode(ErrorCodes.Forbidden, () => stmts.Create(SalesOrderSave(customer, 1, 1, "X")));
            await AssertCode(ErrorCodes.Forbidden, () => stmts.Record(statement.Id));
            await AssertCode(ErrorCodes.Forbidden,
                () => stmts.Void(statement.Id, new AgencyServiceFeeStatementVoidRequest { Reason = "作废" }));

            var allocs = NewAllocationController(db, userId);
            await AssertCode(ErrorCodes.Forbidden, () => allocs.GetPaged(new AgencyServiceFeeCollectionAllocationQuery()));
            await AssertCode(ErrorCodes.Forbidden, () => allocs.ReceiptCandidates(customer, "USD", null, 50));
            await AssertCode(ErrorCodes.Forbidden, () => allocs.ReceiptSummary(receipt));
            await AssertCode(ErrorCodes.Forbidden, () => allocs.Create(AllocationSave(statement.Id, receipt)));
            await AssertCode(ErrorCodes.Forbidden,
                () => allocs.Void(1, new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = "作废" }));
        }

        // 零变更：被拒请求既不落对账单也不落分摊行，既有对账单保持不变
        Assert.Equal(beforeStatements, await db.AgencyServiceFeeStatements.AsNoTracking().CountAsync());
        Assert.Equal(beforeAllocations, await db.AgencyServiceFeeCollectionAllocations.AsNoTracking().CountAsync());
        var stored = await db.AgencyServiceFeeStatements.AsNoTracking().SingleAsync(s => s.Id == statement.Id);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, stored.Status);
    }

    // ==================== 2. 客户数据范围收敛 + 被许可生命周期 + 零变更 ====================

    [Fact]
    public async Task Live_scope_isolates_own_foreign_and_deleted_evidence_and_zeroes_denied_writes()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userName = $"asf437-own-{Guid.NewGuid():N}";
        var employeeId = await SeedEmployeeAsync(db);
        (await db.BaseEmployees.SingleAsync(e => e.Id == employeeId)).EmployeeCode = userName;
        await db.SaveChangesAsync();
        var operatorId = await SeedRestrictedOperatorAsync(db, userName, grantMenu: true);

        var ownCustomer = await SeedCustomerAsync(db, "ERP-437 自有客户", employeeId);
        var foreignCustomer = await SeedCustomerAsync(db, "ERP-437 他人客户");
        var ownAgreement = await SeedAgreementAsync(db, ownCustomer, $"ASF-OWN-{Tag()}");
        var foreignAgreement = await SeedAgreementAsync(db, foreignCustomer, $"ASF-FGN-{Tag()}");
        var ownOrder = await SeedSalesOrderAsync(db, ownCustomer, $"SO-OWN-{Tag()}");
        var foreignOrder = await SeedSalesOrderAsync(db, foreignCustomer, $"SO-FGN-{Tag()}");
        var ownStatement = await SeedStatementAsync(db, ownCustomer, $"ASFS-OWN-{Tag()}", 1000m);
        var foreignStatement = await SeedStatementAsync(db, foreignCustomer, $"ASFS-FGN-{Tag()}", 1000m);
        var deletedStatement = await SeedStatementAsync(
            db, ownCustomer, $"ASFS-DEL-{Tag()}", 1000m, deleted: true);
        var ownReceipt = await SeedReceiptAsync(db, ownCustomer, $"RC-OWN-{Tag()}");
        var foreignReceipt = await SeedReceiptAsync(db, foreignCustomer, $"RC-FGN-{Tag()}");
        var deletedReceipt = await SeedReceiptAsync(db, ownCustomer, $"RC-DEL-{Tag()}");
        (await db.FinanceReceipts.SingleAsync(r => r.Id == deletedReceipt)).IsDeleted = true;
        await db.SaveChangesAsync();

        // 特权账号建立越范围分摊行（外客户对账单 + 外客户收款单）
        var privilegedId = await SeedPrivilegedOperatorAsync(db);
        var privileged = NewAllocationController(db, privilegedId);
        var foreignRow = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await privileged.Create(AllocationSave(foreignStatement.Id, foreignReceipt)));

        var ctl = NewAllocationController(db, operatorId);

        // 自有范围内：分摊行登记 + 作废被许可
        var ownRow = AssertOk<AgencyServiceFeeCollectionAllocationDto>(
            await ctl.Create(AllocationSave(ownStatement.Id, ownReceipt)));
        Assert.True(ownRow.IsActive);
        var voidedRow = AssertOk<AgencyServiceFeeCollectionAllocationDto>(await ctl.Void(
            ownRow.Id, new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = "录错" }));
        Assert.True(voidedRow.IsVoided);

        // 越范围 / 不存在：同一条不披露错误
        var foreignDenied = await AssertCode(ErrorCodes.NotFound, () => ctl.ReceiptSummary(foreignReceipt));
        var missingDenied = await AssertCode(ErrorCodes.NotFound, () => ctl.ReceiptSummary(987_654_321L));
        Assert.Equal(foreignDenied.Message, missingDenied.Message);
        Assert.Equal(AgencyServiceFeeReconciliationRules.RegisterNotFoundText, foreignDenied.Message);
        await AssertCode(ErrorCodes.NotFound, () => ctl.GetById(foreignRow.Id));
        await AssertCode(ErrorCodes.NotFound, () => ctl.StatementSummary(foreignStatement.Id));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(AllocationSave(foreignStatement.Id, ownReceipt)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(AllocationSave(ownStatement.Id, deletedReceipt)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Void(
            foreignRow.Id, new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = "越权作废" }));

        // 对账单侧：越范围 / 已删除 / 不存在同一条不披露错误
        var stmts = NewStatementController(db, operatorId);
        var foreignDetail = await AssertCode(ErrorCodes.NotFound, () => stmts.GetById(foreignStatement.Id));
        var deletedDetail = await AssertCode(ErrorCodes.NotFound, () => stmts.GetById(deletedStatement.Id));
        Assert.Equal(foreignDenied.Message, foreignDetail.Message);
        Assert.Equal(foreignDenied.Message, deletedDetail.Message);
        await AssertCode(ErrorCodes.NotFound, () => stmts.Record(foreignStatement.Id));
        await AssertCode(ErrorCodes.NotFound, () => stmts.Create(
            SalesOrderSave(foreignCustomer, foreignAgreement, foreignOrder, $"ASFS-FGN2-{Tag()}")));
        await AssertCode(ErrorCodes.NotFound, () => stmts.SourceOptions(
            AgencyServiceFeeStatementRules.SourceTypeSalesOrder, foreignCustomer, "USD", null, 50));

        // 自有范围内：对账单登记 / 登记 / 作废被许可
        var createdStatement = AssertOk<AgencyServiceFeeStatementDto>(await stmts.Create(
            SalesOrderSave(ownCustomer, ownAgreement, ownOrder, $"ASFS-OWN2-{Tag()}")));
        var recordedStatement = AssertOk<AgencyServiceFeeStatementDto>(await stmts.Record(createdStatement.Id));
        Assert.True(recordedStatement.IsRecorded);
        var voidedStatement = AssertOk<AgencyServiceFeeStatementDto>(await stmts.Void(
            recordedStatement.Id, new AgencyServiceFeeStatementVoidRequest { Reason = "客户争议" }));
        Assert.True(voidedStatement.IsVoided);

        // 台账按客户范围过滤：受限业务员只看到自有客户
        var statementLedger = AssertOk<PagedResult<AgencyServiceFeeStatementDto>>(
            await stmts.GetPaged(new AgencyServiceFeeStatementQuery()));
        Assert.NotEmpty(statementLedger.Items);
        Assert.All(statementLedger.Items, i => Assert.Equal(ownCustomer, i.CustomerId));
        var allocationLedger = AssertOk<PagedResult<AgencyServiceFeeCollectionAllocationDto>>(
            await ctl.GetPaged(new AgencyServiceFeeCollectionAllocationQuery()));
        Assert.NotEmpty(allocationLedger.Items);
        Assert.All(allocationLedger.Items, i => Assert.Equal(ownCustomer, i.CustomerId));

        // 零变更：越范围分摊行仍为有效、越范围对账单仍为已登记
        var storedForeignRow = await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .SingleAsync(a => a.Id == foreignRow.Id);
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.StatusActive, storedForeignRow.Status);
        Assert.Null(storedForeignRow.VoidedAt);
        var storedForeignStatement = await db.AgencyServiceFeeStatements.AsNoTracking()
            .SingleAsync(s => s.Id == foreignStatement.Id);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, storedForeignStatement.Status);
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-437）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 授权集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class AgencyServiceFeeRegisterAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ASFREGISTERAUTH_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-437] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await EnsureFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task EnsureFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何数据库访问之前再次护栏：绝不使用生产 / 非专用回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁已存在的 Fixture 库或其它调用方的数据库。
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

        Console.WriteLine("[ERP-437] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class AgencyServiceFeeRegisterAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => AgencyServiceFeeRegisterAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => AgencyServiceFeeRegisterAuthorizationSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
