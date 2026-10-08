using ERP.Api.Controllers;
using ERP.Application.Common;
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
/// ERP-397「来源单据 → 单证」生成的确定性来源行锁 / 原子事务 / 锁内权威复核协议的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>两个独立连接竞态</b>：① 同一来源 + 同一类型并发生成 → 唯一完整一套（另一侧合法冲突拒绝、无孤儿明细行）；
/// ② 来源取消 vs 生成 → 取消先行则零单证，生成先行则完整一套；③ 确定性编号被历史单证占用时并发生成 →
/// 锁内确定性回退（<c>-2</c>）且全局无重复编号；④ 来源编辑（改数量 / 金额）vs 生成 → 表头金额与明细行数量不撕裂；
/// ⑤ 装柜清单来源的同清单同类型并发生成 → 唯一完整一套。</item>
/// <item><b>多类型整体拒绝</b>：请求含已生成类型时整体拒绝，未生成类型一张也不落库。</item>
/// <item><b>真实授权</b>：使用既有 <c>doc-center</c> 菜单 + 既有业务员数据范围派生的实时范围（不新增授权、无匿名 / 管理员兜底）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只新增自己的证据行，不清理 / 不删除任何既有行。
/// 构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class TradeDocumentGenerationMutationSqlServerTests
    : IClassFixture<TradeDocumentGenerationMutationSqlServerFixture>
{
    private readonly TradeDocumentGenerationMutationSqlServerFixture _fixture;

    public TradeDocumentGenerationMutationSqlServerTests(TradeDocumentGenerationMutationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(TradeDocumentGenerationMutationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static SalespersonDataScope Restricted(long salesmanId, params long[] allowedCustomerIds) => new()
    {
        IsPrivileged = false, SalesmanId = salesmanId, AllowedCustomerIds = allowedCustomerIds.ToHashSet()
    };

    private static DefaultHttpContext HttpFor(SalespersonDataScope? scope, long? userId = null)
    {
        var http = new DefaultHttpContext();
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));
        if (scope is not null)
            http.Items[TradeDocumentRequestAuthorizationFilter.ScopeItemKey] = scope;
        return http;
    }

    /// <summary>
    /// 用一条独立连接以**真实既有授权**执行销售订单生成入口：先用既有 <c>doc-center</c> 菜单授权校验实时身份，
    /// 再以既有业务员数据范围派生的范围调用真实控制器（每次调用各自 DbContext / 连接 / 事务）。
    /// </summary>
    private async Task<(bool Success, string Error)> TryOrderGenerateAsync(
        SalespersonDataScope scope, long userId, long orderId, IReadOnlyList<string>? docTypes = null)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            // 真实授权：账号必须已启用、未被删除，且显式具备既有「单证中心」菜单授权。
            await TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, userId);
            var controller = new SalesOrderController(db, new DocumentNumberService(db))
            {
                ControllerContext = new ControllerContext { HttpContext = HttpFor(scope, userId) }
            };
            var result = await controller.GenerateTradeDocuments(orderId,
                docTypes is null ? null : new TradeDocGenerateRequest { DocTypes = docTypes.ToList() });
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>用一条独立连接以真实既有授权执行装柜清单生成入口。</summary>
    private async Task<(bool Success, string Error)> TryLoadingGenerateAsync(
        SalespersonDataScope scope, long userId, long loadingListId, IReadOnlyList<string>? docTypes = null)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, userId);
            var controller = new ContainerLoadingListController(db, new DocumentNumberService(db))
            {
                ControllerContext = new ControllerContext { HttpContext = HttpFor(scope, userId) }
            };
            var result = await controller.GenerateTradeDocuments(loadingListId,
                docTypes is null ? null : new TradeDocGenerateRequest { DocTypes = docTypes.ToList() });
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>用一条独立连接取消销售订单（真实控制器；与生成共用同一把来源订单行锁）。</summary>
    private async Task<(bool Success, string Error)> TryCancelOrderAsync(long orderId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var controller = new SalesOrderController(db, new DocumentNumberService(db))
            {
                ControllerContext = new ControllerContext { HttpContext = HttpFor(null, userId) }
            };
            var result = await controller.Cancel(orderId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>用一条独立连接修改销售订单明细（数量 / 单价 → 服务端重算金额；与生成共用同一把来源订单行锁）。</summary>
    private async Task<(bool Success, string Error)> TryUpdateOrderAsync(long orderId, decimal quantity, decimal unitPrice)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var stored = await db.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
            var body = new SalesOrder
            {
                OrderDate = stored.OrderDate, CustomerId = stored.CustomerId, Currency = stored.Currency,
                Status = stored.Status, DestinationPort = stored.DestinationPort,
                Details = new List<SalesOrderDetail>
                {
                    new()
                    {
                        ProductId = 0, ProductName = "毛巾", Spec = "70x140", Unit = "箱",
                        Quantity = quantity, UnitPrice = unitPrice,
                    }
                },
            };
            var result = await new SalesOrderController(db, new DocumentNumberService(db)).Update(orderId, body);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>两条独立连接以同一起跑线并发执行（门闩对齐），返回两侧结果。</summary>
    private static async Task<List<(bool Success, string Error)>> RaceAsync(
        Func<Task<(bool Success, string Error)>> first, Func<Task<(bool Success, string Error)>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(bool Success, string Error)> Run(Func<Task<(bool Success, string Error)>> action)
        {
            await gate.Task;
            return await action();
        }

        var left = Run(first);
        var right = Run(second);
        gate.SetResult();
        return (await Task.WhenAll(left, right)).ToList();
    }

    // ==================== 种子（既有菜单 / 既有业务员数据范围，不新增权限模型） ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常",
            Currency = "USD", EmpId = empId,
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    /// <summary>
    /// 播种受限业务员账号：登录名 = 员工编码（ERP-097 权威映射），并授予**既有**菜单授权
    /// （单证中心 <c>doc-center</c> + 销售订单 <c>sales-order</c>）：不新增菜单 / 角色 / 用户授权。
    /// </summary>
    private static async Task<(SysUser User, BaseEmployee Employee, SysRole Role)> SeedOperatorAsync(ErpDbContext db)
    {
        var code = $"INT_E397_EMP_{Tag()}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"INT_E397_ROLE_{code}", RoleCode = $"INT_E397_{Guid.NewGuid():N}", IsSystem = false,
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menus = await db.SysMenus.Where(m => !m.IsDeleted
            && (m.MenuCode == "doc-center" || m.MenuCode == "sales-order")).ToListAsync();
        Assert.Equal(2, menus.Count);
        foreach (var menu in menus)
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        return (user, employee, role);
    }

    private static async Task<SalesOrder> SeedOrderAsync(ErpDbContext db, string no, long customerId,
        decimal quantity = 10m, decimal unitPrice = 20m, DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = no, OrderDate = DateTime.Today, CustomerId = customerId, Currency = Currency.USD,
            TotalAmount = quantity * unitPrice, Status = status, DestinationPort = "HAMBURG",
            Details = new List<SalesOrderDetail>
            {
                new()
                {
                    ProductId = 0, ProductName = "毛巾", Spec = "70x140", Unit = "箱",
                    Quantity = quantity, UnitPrice = unitPrice,
                }
            },
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<ContainerLoadingList> SeedLoadingListAsync(ErpDbContext db, string no,
        string containerNo, long customerId, decimal quantity = 10m)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no, LoadingDate = DateTime.Today, ContainerNo = containerNo, CustomerId = customerId,
            Status = DocumentStatus.Approved, TotalCartons = 12m, TotalWeight = 100.5m,
            Details = new List<ContainerLoadingDetail>
            {
                new() { ProductId = 0, ProductName = "毛巾", Quantity = quantity, Cartons = 12m, Weight = 100.5m }
            },
        };
        db.ContainerLoadingLists.Add(list);
        await db.SaveChangesAsync();
        return list;
    }

    /// <summary>播种历史人工单证（占用确定性基础编号，但来源字段与本单无关 → 不触发重复生成守卫）。</summary>
    private static async Task<TradeDocument> SeedManualDocumentAsync(ErpDbContext db, string docNo,
        string docType, string refNo)
    {
        var document = new TradeDocument
        {
            DocNo = docNo, DocType = docType, Status = "待制作", Currency = "USD", Amount = 0m,
            IssueDate = DateTime.Today, RefNo = refNo, CustomerName = string.Empty, Copies = 1,
        };
        db.TradeDocuments.Add(document);
        await db.SaveChangesAsync();
        return document;
    }


    // ==================== 1. 两个独立连接：同一来源并发生成 ====================

    [Fact]
    public async Task 两个独立连接_销售订单同类型并发生成_唯一完整一套_另一侧合法冲突()
    {
        Guard();
        var tag = Tag();
        long orderId, userId, customerId;
        SalespersonDataScope scope;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            var customer = await SeedCustomerAsync(seed, $"INT_E397_C_{tag}", employee.Id);
            var order = await SeedOrderAsync(seed, $"INT_E397_SO_{tag}", customer.Id);
            orderId = order.Id;
            userId = user.Id;
            customerId = customer.Id;
            scope = Restricted(employee.Id, customer.Id);
        }

        var results = await RaceAsync(
            () => TryOrderGenerateAsync(scope, userId, orderId, new[] { "商业发票" }),
            () => TryOrderGenerateAsync(scope, userId, orderId, new[] { "商业发票" }));

        // 确定性赢家：恰好一侧成功（另一侧在锁内检测到重复生成 → 合法冲突拒绝）
        Assert.Equal(1, results.Count(r => r.Success));
        Assert.All(results.Where(r => !r.Success), r => Assert.False(string.IsNullOrWhiteSpace(r.Error)));

        await using var verify = _fixture.CreateDbContext();
        var documents = await verify.TradeDocuments.AsNoTracking()
            .Where(d => d.SalesOrderNo == $"INT_E397_SO_{tag}" && !d.IsDeleted).ToListAsync();
        Assert.Single(documents);                                   // 同一来源 + 同一类型只有一套
        Assert.Equal("商业发票", documents[0].DocType);
        Assert.Equal(customerId, documents[0].CustomerId);          // 权威客户来自锁内重读
        var lines = await verify.TradeDocumentItems.AsNoTracking()
            .Where(i => i.TradeDocumentId == documents[0].Id && !i.IsDeleted).ToListAsync();
        Assert.Single(lines);                                       // 完整一套：明细行随表头一起提交
        Assert.Equal(10m, lines[0].Quantity);

        // 全局无重复编号
        var docNos = await verify.TradeDocuments.AsNoTracking().Where(d => !d.IsDeleted)
            .Select(d => d.DocNo).ToListAsync();
        Assert.Equal(docNos.Count, docNos.Distinct().Count());
    }

    [Fact]
    public async Task 两个独立连接_装柜清单同类型并发生成_唯一完整一套()
    {
        Guard();
        var tag = Tag();
        long loadingListId, userId, customerId;
        SalespersonDataScope scope;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            var customer = await SeedCustomerAsync(seed, $"INT_E397_C_{tag}", employee.Id);
            var list = await SeedLoadingListAsync(seed, $"INT_E397_ZQ_{tag}", $"CTN{tag}", customer.Id);
            loadingListId = list.Id;
            userId = user.Id;
            customerId = customer.Id;
            scope = Restricted(employee.Id, customer.Id);
        }

        var results = await RaceAsync(
            () => TryLoadingGenerateAsync(scope, userId, loadingListId, new[] { "装箱单" }),
            () => TryLoadingGenerateAsync(scope, userId, loadingListId, new[] { "装箱单" }));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var documents = await verify.TradeDocuments.AsNoTracking()
            .Where(d => d.RefNo == $"CTN{tag}" && !d.IsDeleted).ToListAsync();
        Assert.Single(documents);
        Assert.Equal("装箱单", documents[0].DocType);
        Assert.Equal(customerId, documents[0].CustomerId);
        Assert.Equal(1, await verify.TradeDocumentItems.AsNoTracking()
            .CountAsync(i => i.TradeDocumentId == documents[0].Id && !i.IsDeleted));
    }


    // ==================== 2. 两个独立连接：来源取消 vs 生成 ====================

    [Fact]
    public async Task 两个独立连接_来源取消与生成并发_取消先行零单证_生成先行完整一套()
    {
        Guard();
        var tag = Tag();
        long orderId, userId;
        SalespersonDataScope scope;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            var customer = await SeedCustomerAsync(seed, $"INT_E397_C_{tag}", employee.Id);
            var order = await SeedOrderAsync(seed, $"INT_E397_SOC_{tag}", customer.Id);
            orderId = order.Id;
            userId = user.Id;
            scope = Restricted(employee.Id, customer.Id);
        }

        var results = await RaceAsync(
            () => TryCancelOrderAsync(orderId, userId),
            () => TryOrderGenerateAsync(scope, userId, orderId, new[] { "商业发票", "装箱单" }));

        var cancel = results[0];
        var generate = results[1];

        Assert.True(cancel.Success, $"取消销售订单应总能成功：{cancel.Error}");

        await using var verify = _fixture.CreateDbContext();
        var verifyOrder = await verify.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        Assert.Equal(DocumentStatus.Cancelled, verifyOrder.Status);

        var documents = await verify.TradeDocuments.AsNoTracking()
            .Where(d => d.SalesOrderNo == $"INT_E397_SOC_{tag}" && !d.IsDeleted).ToListAsync();

        if (generate.Success)
        {
            // 生成赢得来源行锁：完整一套（商业发票 + 装箱单），此后取消照常生效
            Assert.Equal(2, documents.Count);
            Assert.Equal(2, documents.Select(d => d.DocType).Distinct().Count());
            var totalLines = await verify.TradeDocumentItems.AsNoTracking()
                .CountAsync(i => documents.Select(d => d.Id).Contains(i.TradeDocumentId) && !i.IsDeleted);
            Assert.Equal(2, totalLines);
        }
        else
        {
            // 取消先行提交：生成在锁内权威重读发现来源已作废，零单证、零孤儿明细行
            Assert.Empty(documents);
            Assert.Contains("已作废", generate.Error);
        }
    }

    // ==================== 3. 两个独立连接：编号冲突下的确定性回退 ====================

    [Fact]
    public async Task 两个独立连接_编号被历史单证占用_并发生成确定性回退且无重复编号()
    {
        Guard();
        var tag = Tag();
        long loadingListId, userId;
        SalespersonDataScope scope;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            var customer = await SeedCustomerAsync(seed, $"INT_E397_C_{tag}", employee.Id);
            var list = await SeedLoadingListAsync(seed, $"INT_E397_ZQN_{tag}", $"CTNN{tag}", customer.Id);
            loadingListId = list.Id;
            userId = user.Id;
            scope = Restricted(employee.Id, customer.Id);

            // 历史人工单证占用了确定性基础编号（来源字段无关 → 不触发重复生成守卫）
            await SeedManualDocumentAsync(seed, $"PL-INT_E397_ZQN_{tag}", "装箱单", $"OTHER{tag}");
        }

        var results = await RaceAsync(
            () => TryLoadingGenerateAsync(scope, userId, loadingListId, new[] { "装箱单" }),
            () => TryLoadingGenerateAsync(scope, userId, loadingListId, new[] { "装箱单" }));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var generated = await verify.TradeDocuments.AsNoTracking()
            .Where(d => d.RefNo == $"CTNN{tag}" && !d.IsDeleted).ToListAsync();
        Assert.Single(generated);
        Assert.Equal($"PL-INT_E397_ZQN_{tag}-2", generated[0].DocNo);   // 锁内确定性回退，不覆盖历史单证

        var manual = await verify.TradeDocuments.AsNoTracking()
            .SingleAsync(d => d.DocNo == $"PL-INT_E397_ZQN_{tag}");
        Assert.Equal($"OTHER{tag}", manual.RefNo);                      // 历史单证原样保留

        var docNos = await verify.TradeDocuments.AsNoTracking().Where(d => !d.IsDeleted)
            .Select(d => d.DocNo).ToListAsync();
        Assert.Equal(docNos.Count, docNos.Distinct().Count());           // 全局无重复编号
    }


    // ==================== 4. 两个独立连接：来源编辑 vs 生成（不撕裂） ====================

    [Fact]
    public async Task 两个独立连接_来源编辑与生成并发_表头金额与明细行数量不撕裂()
    {
        Guard();
        var tag = Tag();
        long orderId, userId;
        SalespersonDataScope scope;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            var customer = await SeedCustomerAsync(seed, $"INT_E397_C_{tag}", employee.Id);
            // 编辑前：数量 10 × 单价 2 = 金额 20；编辑后：数量 30 × 单价 2 = 金额 60
            var order = await SeedOrderAsync(seed, $"INT_E397_SOE_{tag}", customer.Id,
                quantity: 10m, unitPrice: 2m, status: DocumentStatus.Pending);
            orderId = order.Id;
            userId = user.Id;
            scope = Restricted(employee.Id, customer.Id);
        }

        var results = await RaceAsync(
            () => TryUpdateOrderAsync(orderId, 30m, 2m),
            () => TryOrderGenerateAsync(scope, userId, orderId, new[] { "商业发票" }));

        Assert.True(results[0].Success || results[1].Success, "编辑与生成至少一侧应成功。");

        await using var verify = _fixture.CreateDbContext();
        var document = await verify.TradeDocuments.AsNoTracking()
            .SingleAsync(d => d.SalesOrderNo == $"INT_E397_SOE_{tag}" && !d.IsDeleted);
        var quantity = await verify.TradeDocumentItems.AsNoTracking()
            .Where(i => i.TradeDocumentId == document.Id && !i.IsDeleted).SumAsync(i => i.Quantity);

        // 表头金额与明细行数量必须来自**同一个**已提交版本（生成在来源行锁内权威重读），绝不撕裂
        var preEdit = document.Amount == 20m && quantity == 10m;
        var postEdit = document.Amount == 60m && quantity == 30m;
        Assert.True(preEdit || postEdit,
            $"表头金额 {document.Amount} 与明细行数量 {quantity} 必须同源（20/10 或 60/30）。");
    }

    // ==================== 5. 多类型整体拒绝（无部分落库） ====================

    [Fact]
    public async Task 多类型请求_含已生成类型_整体拒绝且未生成类型不落库()
    {
        Guard();
        var tag = Tag();
        long orderId, userId;
        SalespersonDataScope scope;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            var customer = await SeedCustomerAsync(seed, $"INT_E397_C_{tag}", employee.Id);
            var order = await SeedOrderAsync(seed, $"INT_E397_SOM_{tag}", customer.Id);
            orderId = order.Id;
            userId = user.Id;
            scope = Restricted(employee.Id, customer.Id);
        }

        var first = await TryOrderGenerateAsync(scope, userId, orderId, new[] { "商业发票" });
        Assert.True(first.Success, first.Error);

        // 混合请求（已生成 + 未生成）：整体拒绝，未生成类型一张也不落库
        var mixed = await TryOrderGenerateAsync(scope, userId, orderId, new[] { "商业发票", "装箱单" });
        Assert.False(mixed.Success);
        Assert.Contains("不能重复生成", mixed.Error);

        await using var verify = _fixture.CreateDbContext();
        var documents = await verify.TradeDocuments.AsNoTracking()
            .Where(d => d.SalesOrderNo == $"INT_E397_SOM_{tag}" && !d.IsDeleted).ToListAsync();
        Assert.Single(documents);
        Assert.Equal("商业发票", documents[0].DocType);
        Assert.DoesNotContain(documents, d => d.DocType == "装箱单");
    }
}

/// <summary>
/// ERP-397 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class TradeDocumentGenerationMutationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_TRADEDOCGEN_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-397] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};" +
           "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
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

        Console.WriteLine("[ERP-397] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class TradeDocumentGenerationMutationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => TradeDocumentGenerationMutationSqlServerFixture.AssertDedicatedTarget(connection));
}

