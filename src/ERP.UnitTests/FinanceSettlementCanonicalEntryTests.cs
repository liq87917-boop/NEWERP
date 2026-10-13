using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-481 装柜结算单 / 散货结算单规范入口与「省略结算单号」手工录入契约单元测试
/// （内存库 <see cref="TestDbFactory"/>，不连接 SQL Server、不启动 API）。
/// <para>覆盖：<list type="bullet">
/// <item>实体层：<see cref="FinanceContainerSettlement.SettlementNo"/> / <see cref="FinanceBulkSettlement.SettlementNo"/>
/// 不再显式 <c>[Required]</c>（省略 / 空串照常绑定），长度约束保留，且两类结算单都没有币种 / 汇率字段；</item>
/// <item>新增入口：省略 / 空串 / 伪造结算单号一律由服务端权威单号覆盖，主键 / 状态 / 删除态 / 时间戳
/// 全部由服务端覆盖（客户端注入值不参与持久化）；</item>
/// <item>拒绝先于编号与持久化：无身份 / 缺菜单 / 账号禁用 / 客户不可用 / 越范围客户 / 非法金额 /
/// 来源清单悬空 · 已删除 · 已取消 · 跨客户一律零写入且零占号；</item>
/// <item>来源显式关联 / 清空：装柜结算单的来源装柜清单可显式关联、显式断开（提交 <c>null</c> 保留历史未关联语义），
/// 而显式非正数 Id（0 / 负数）按「非法引用」fail closed 拒绝且零写入；</item>
/// <item>有效生命周期：提交 → 审核 → 取消，非法流转（待提交直接审核、重复取消）与仅待提交可删拒绝且零状态变化；</item>
/// <item>接线契约：菜单路由不再落到旧存储过程族、规范模块与既有来源选择器接线、校验先于单号预约、
/// 交付说明记录口径。</item>
/// </list></para>
/// <para>真实浏览器 + 真实隔离 SQL / HTTP 的端到端验收见
/// <c>ERP.IntegrationTests/FinanceSettlementCanonicalEntrySqlServerTests.cs</c>。</para>
/// </summary>
public class FinanceSettlementCanonicalEntryTests
{
    // ==================== 0. 测试脚手架 ====================

    private sealed class FakeDocumentNumberService : IDocumentNumberService
    {
        private int _seq;

        /// <summary>已被请求生成的单号次数（用于证明被拒请求绝不消耗结算单号）。</summary>
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
        {
            Calls++;
            var prefix = documentType == DocumentType.BulkSettlement ? "SJ" : "ZGS";
            return Task.FromResult($"{prefix}{DateTime.Now:yyyyMMdd}{++_seq:D4}");
        }
    }

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, long? empId = null, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static ContainerLoadingList SeedLoadingList(
        ErpDbContext db, string listNo, string containerNo, long customerId,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = listNo,
            LoadingDate = new DateTime(2026, 9, 25),
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = status,
            IsDeleted = deleted
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    private static FinanceContainerSettlement SeedContainerSettlement(
        ErpDbContext db, string settlementNo, long? loadingListId, long customerId,
        decimal totalAmount = 1000m, decimal freightCost = 0m, decimal otherCost = 0m,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var settlement = new FinanceContainerSettlement
        {
            SettlementNo = settlementNo,
            SettlementDate = new DateTime(2026, 9, 25),
            LoadingListId = loadingListId,
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            OtherCost = otherCost,
            Remark = "原始备注",
            Status = status,
            UpdatedAt = new DateTime(2026, 9, 26, 8, 0, 0),
            IsDeleted = deleted
        };
        db.FinanceContainerSettlements.Add(settlement);
        db.SaveChanges();
        return settlement;
    }

    private static FinanceBulkSettlement SeedBulkSettlement(
        ErpDbContext db, string settlementNo, long customerId,
        decimal totalAmount = 1000m, decimal freightCost = 0m,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var settlement = new FinanceBulkSettlement
        {
            SettlementNo = settlementNo,
            SettlementDate = new DateTime(2026, 9, 25),
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            Remark = "原始备注",
            Status = status,
            UpdatedAt = new DateTime(2026, 9, 26, 8, 0, 0),
            IsDeleted = deleted
        };
        db.FinanceBulkSettlements.Add(settlement);
        db.SaveChanges();
        return settlement;
    }

    private static void GrantMenu(ErpDbContext db, long roleId, string code, string name, string path)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = code,
            MenuName = name,
            Path = path,
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }

    /// <summary>
    /// 播种一个操作员：<paramref name="privileged"/> 为 <c>true</c> 时复用既有「系统内置角色」口径（可见全部客户，
    /// 且无需菜单授权）；为 <c>false</c> 时按既有业务员数据范围口径限制客户范围，并显式授予既有结算单菜单。
    /// </summary>
    private static (long UserId, long RoleId, string UserName) SeedOperator(
        ErpDbContext db, bool privileged = true, bool withContainerMenu = true, bool withBulkMenu = true,
        UserStatus status = UserStatus.Enabled)
    {
        var role = new SysRole
        {
            RoleName = "结算单规范入口操作角色",
            RoleCode = $"SettlementCanonicalOp-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var userName = $"settlement-canonical-op-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "结算单规范入口操作员",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (withContainerMenu)
            GrantMenu(db, role.Id, FinanceContainerSettlementLifecycleRules.RequiredMenuCode,
                FinanceContainerSettlementLifecycleRules.RequiredMenuText, "/finance/container-settlement");
        if (withBulkMenu)
            GrantMenu(db, role.Id, FinanceBulkSettlementLifecycleRules.RequiredMenuCode,
                FinanceBulkSettlementLifecycleRules.RequiredMenuText, "/finance/bulk-settlement");

        return (user.Id, role.Id, userName);
    }

    private static void MapOperatorToEmployee(ErpDbContext db, string userName)
    {
        db.BaseEmployees.Add(new BaseEmployee
        {
            EmployeeCode = userName,
            EmployeeName = userName,
            IsSalesman = true,
            Status = 1
        });
        db.SaveChanges();
    }

    private static void RevokeMenus(ErpDbContext db, long roleId)
    {
        foreach (var grant in db.SysRoleMenus.Where(g => g.RoleId == roleId && !g.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static void DisableUser(ErpDbContext db, long userId)
    {
        db.SysUsers.Single(u => u.Id == userId).Status = UserStatus.Disabled;
        db.SaveChanges();
    }

    private static async Task<BusinessException> AssertBusinessCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static FinanceContainerSettlementController BuildContainerController(
        ErpDbContext db, FakeDocumentNumberService no) => new(db, no);

    private static FinanceBulkSettlementController BuildBulkController(
        ErpDbContext db, FakeDocumentNumberService no) => new(db, no);

    private static FinanceContainerSettlement NewContainerRequest(
        long customerId, long? loadingListId = null, decimal totalAmount = 1000m,
        decimal freightCost = 0m, decimal otherCost = 0m, string settlementNo = "")
        => new()
        {
            SettlementNo = settlementNo,
            SettlementDate = new DateTime(2026, 9, 27),
            LoadingListId = loadingListId,
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            OtherCost = otherCost,
            Remark = "请求备注"
        };

    private static FinanceBulkSettlement NewBulkRequest(
        long customerId, decimal totalAmount = 1000m, decimal freightCost = 0m, string settlementNo = "")
        => new()
        {
            SettlementNo = settlementNo,
            SettlementDate = new DateTime(2026, 9, 27),
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            Remark = "请求备注"
        };

    /// <summary>读取新增响应里的服务端权威主键（匿名对象 Id）。</summary>
    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!;
    }

    /// <summary>读取新增响应里的服务端权威结算单号（匿名对象 SettlementNo）。</summary>
    private static string CreatedNo(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (string)payload.GetType().GetProperty("SettlementNo")!.GetValue(payload)!;
    }

    private static FinanceContainerSettlement StoredContainer(ErpDbContext db, long id)
        => db.FinanceContainerSettlements.AsNoTracking().Single(s => s.Id == id);

    private static FinanceBulkSettlement StoredBulk(ErpDbContext db, long id)
        => db.FinanceBulkSettlements.AsNoTracking().Single(s => s.Id == id);

    /// <summary>读取仓库内源文件（按解决方案根定位，避免依赖输出目录层级）。</summary>
    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>剥离块注释与行注释（用于只对「生效中的代码」做断言，说明性迁移记录不参与判定）。</summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty);
    }

    // ==================== 1. 实体契约：结算单号不再显式必填、长度保留、无币种字段 ====================

    [Fact]
    public void 结算单号_不再显式必填_长度约束保留()
    {
        foreach (var type in new[] { typeof(FinanceContainerSettlement), typeof(FinanceBulkSettlement) })
        {
            var property = type.GetProperty(nameof(FinanceContainerSettlement.SettlementNo))!;
            Assert.Equal(typeof(string), property.PropertyType);
            Assert.False(
                Attribute.IsDefined(property, typeof(System.ComponentModel.DataAnnotations.RequiredAttribute), true),
                $"{type.Name}.SettlementNo 不得显式必填（否则省略结算单号会在模型绑定阶段被拒为 HTTP 400）");

            var maxLength = Assert.Single(property
                .GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.MaxLengthAttribute), true)
                .Cast<System.ComponentModel.DataAnnotations.MaxLengthAttribute>());
            Assert.Equal(50, maxLength.Length);

            // 默认空串：省略字段时绑定层留下的就是空串（隐式必填 AllowEmptyStrings = true 允许空串）。
            Assert.Equal(string.Empty, property.GetValue(Activator.CreateInstance(type)));
        }
    }

    [Fact]
    public void 两类结算单_没有币种与汇率字段_绝不跨币种聚合()
    {
        var entities = ReadSource("src", "ERP.Domain", "Entities", "Finance2.cs");
        var bulkStart = entities.IndexOf("public class FinanceBulkSettlement", StringComparison.Ordinal);
        var bulkEnd = entities.IndexOf("public class FinanceReceipt", StringComparison.Ordinal);
        Assert.True(bulkStart > 0 && bulkEnd > bulkStart);
        foreach (var region in new[] { entities[..bulkStart], entities[bulkStart..bulkEnd] })
        {
            Assert.DoesNotContain("Currency", region);
            Assert.DoesNotContain("ExchangeRate", region);
        }

        var modules = ReadSource("src", "ERP.Api", "wwwroot", "js", "modules-finance.js");
        var containerStart = modules.IndexOf("'container-settlement': {", StringComparison.Ordinal);
        var bulkModuleStart = modules.IndexOf("'bulk-settlement': {", StringComparison.Ordinal);
        var receiptStart = modules.IndexOf("receipt: {", StringComparison.Ordinal);
        Assert.True(containerStart >= 0 && bulkModuleStart > containerStart && receiptStart > bulkModuleStart);

        var containerModule = modules[containerStart..bulkModuleStart];
        var bulkModule = modules[bulkModuleStart..receiptStart];
        Assert.DoesNotContain("currency", containerModule, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exchangeRate", containerModule, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("currency", bulkModule, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exchangeRate", bulkModule, StringComparison.OrdinalIgnoreCase);

        // 散货结算单没有来源单据字段：保持手工 / 未关联录入，绝不推断来源（不声明任何来源选择器）。
        Assert.DoesNotContain("selector:", bulkModule);
        Assert.Contains("selector: 'container-settlement-loading-source'", containerModule);
    }

    // ==================== 2. 省略结算单号：服务端权威单号与主键 / 状态 ====================

    [Fact]
    public async Task 省略结算单号_装柜结算单创建_服务端权威单号与默认状态()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C-481", "客户481");
        var no = new FakeDocumentNumberService();
        var controller = BuildContainerController(db, no);
        TestAuth.SetUser(controller, userId);

        var request = NewContainerRequest(customer.Id, totalAmount: 1200.5m);
        request.Id = 987654321L;                        // 伪造主键
        request.Status = DocumentStatus.Approved;       // 伪造状态
        request.IsDeleted = true;                       // 伪造删除态
        request.UpdatedAt = new DateTime(2000, 1, 1);   // 伪造时间戳
        request.CreatedAt = new DateTime(2000, 1, 1);

        var id = CreatedId(await controller.Create(request));
        Assert.Equal(1, no.Calls);

        Assert.True(id > 0);
        var stored = StoredContainer(db, id);
        Assert.StartsWith("ZGS", stored.SettlementNo, StringComparison.Ordinal);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
        // 审计字段由服务端覆盖：客户端伪造的时间戳不参与持久化。
        Assert.True(stored.UpdatedAt > new DateTime(2020, 1, 1));
        Assert.True(stored.CreatedAt > new DateTime(2020, 1, 1));
        Assert.Equal(1200.5m, stored.TotalAmount);
        Assert.Null(stored.LoadingListId);              // 未关联来源 = 历史未链接语义
    }

    [Fact]
    public async Task 省略结算单号_散货结算单创建_服务端权威单号与默认状态()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C-481-B", "客户481B");
        var no = new FakeDocumentNumberService();
        var controller = BuildBulkController(db, no);
        TestAuth.SetUser(controller, userId);

        var request = NewBulkRequest(customer.Id, totalAmount: 800m);
        request.Id = 55555L;
        request.Status = DocumentStatus.Cancelled;
        request.IsDeleted = true;
        request.UpdatedAt = new DateTime(2000, 1, 1);

        var id = CreatedId(await controller.Create(request));
        Assert.Equal(1, no.Calls);

        var stored = StoredBulk(db, id);
        Assert.StartsWith("SJ", stored.SettlementNo, StringComparison.Ordinal);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
        // 审计字段由服务端覆盖：客户端伪造的时间戳不参与持久化。
        Assert.True(stored.UpdatedAt > new DateTime(2020, 1, 1));
        Assert.Equal(800m, stored.TotalAmount);
        Assert.Equal(customer.Id, stored.CustomerId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HACK-ZGS-481")]
    public async Task 空串或伪造结算单号_装柜结算单创建_持久化号码由服务端权威生成(string settlementNo)
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C-481-C", "客户481C");
        var controller = BuildContainerController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var id = CreatedId(await controller.Create(
            NewContainerRequest(customer.Id, settlementNo: settlementNo)));
        var stored = StoredContainer(db, id);
        Assert.StartsWith("ZGS", stored.SettlementNo, StringComparison.Ordinal);
        Assert.DoesNotContain("HACK", stored.SettlementNo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HACK-SJ-481")]
    public async Task 空串或伪造结算单号_散货结算单创建_持久化号码由服务端权威生成(string settlementNo)
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C-481-D", "客户481D");
        var controller = BuildBulkController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var id = CreatedId(await controller.Create(
            NewBulkRequest(customer.Id, settlementNo: settlementNo)));
        var stored = StoredBulk(db, id);
        Assert.StartsWith("SJ", stored.SettlementNo, StringComparison.Ordinal);
        Assert.DoesNotContain("HACK", stored.SettlementNo, StringComparison.Ordinal);
    }

    // ==================== 3. 保存 / 回读 / 修改与来源显式选择 / 清空 ====================

    [Fact]
    public async Task 装柜结算单_保存回读修改_规范Id往返且来源可显式关联与清空()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C-481-E", "客户481E");
        var list = SeedLoadingList(db, "LL-481", "CONT-481", customer.Id);
        var controller = BuildContainerController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var id = CreatedId(await controller.Create(
            NewContainerRequest(customer.Id, totalAmount: 1500m, otherCost: 30m)));
        var created = StoredContainer(db, id);
        Assert.Equal("请求备注", created.Remark);

        // 回读（详情）：规范 Id 往返，服务端字段原样返回。
        var detail = Assert.IsType<ApiResponse<FinanceContainerSettlement>>(
            Assert.IsType<OkObjectResult>(await controller.GetById(id)).Value).Data!;
        Assert.Equal(created.SettlementNo, detail.SettlementNo);
        Assert.Equal(1500m, detail.TotalAmount);
        Assert.Equal("请求备注", detail.Remark);

        // 列表按单号关键字可检索（菜单打开后的默认列表口径）。
        var page = Assert.IsType<ApiResponse<PagedResult<FinanceContainerSettlement>>>(
            Assert.IsType<OkObjectResult>(await controller.GetPaged(
                new PageQuery { Keyword = created.SettlementNo }, null)).Value).Data!;
        Assert.Equal(id, Assert.Single(page.Items).Id);

        // 修改：显式关联来源装柜清单 + 更新金额 / 备注；单号 / 状态保持服务端值。
        var update = NewContainerRequest(customer.Id, list.Id, totalAmount: 900m,
            otherCost: 30m, settlementNo: "HACK-481");
        update.Remark = "更新备注";
        update.Status = DocumentStatus.Approved;
        Assert.IsType<OkObjectResult>(await controller.Update(id, update));

        var linked = StoredContainer(db, id);
        Assert.Equal(created.SettlementNo, linked.SettlementNo);
        Assert.Equal(DocumentStatus.Pending, linked.Status);
        Assert.Equal(list.Id, linked.LoadingListId);
        Assert.Equal(900m, linked.TotalAmount);
        Assert.Equal(30m, linked.OtherCost);
        Assert.Equal("更新备注", linked.Remark);

        // 已存储来源只读展示按规范 Id 可达（重开口径）。
        var source = Assert.IsType<ApiResponse<ContainerSettlementLoadingSourceViewDto>>(
            Assert.IsType<OkObjectResult>(await controller.GetLoadingListSource(id)).Value).Data!;
        Assert.True(source.Linked);
        Assert.Equal(list.Id, source.LoadingListId);
        Assert.Equal("LL-481", source.LoadingListNo);

        // 显式清空来源：单号 / 状态不变，来源归一为未关联（历史语义）。
        var clear = NewContainerRequest(customer.Id, null, totalAmount: 900m, otherCost: 30m);
        clear.Remark = "更新备注";
        Assert.IsType<OkObjectResult>(await controller.Update(id, clear));
        var cleared = StoredContainer(db, id);
        Assert.Null(cleared.LoadingListId);
        Assert.Equal(created.SettlementNo, cleared.SettlementNo);
        Assert.Equal(DocumentStatus.Pending, cleared.Status);

        // 显式非正数来源 Id（0）按「非法引用」fail closed 拒绝：零写入、已存来源与金额不被静默改写。
        var invalid = NewContainerRequest(customer.Id, 0L, totalAmount: 700m, otherCost: 30m);
        invalid.Remark = "不应写入";
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Update(id, invalid));
        var afterInvalid = StoredContainer(db, id);
        Assert.Null(afterInvalid.LoadingListId);
        Assert.Equal(created.SettlementNo, afterInvalid.SettlementNo);
        Assert.Equal(900m, afterInvalid.TotalAmount);
        Assert.Equal("更新备注", afterInvalid.Remark);
    }

    [Fact]
    public async Task 散货结算单_保存回读修改_规范Id往返且手工未关联语义保留()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C-481-F", "客户481F");
        var controller = BuildBulkController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var id = CreatedId(await controller.Create(
            NewBulkRequest(customer.Id, totalAmount: 700m)));
        var created = StoredBulk(db, id);

        var detail = Assert.IsType<ApiResponse<FinanceBulkSettlement>>(
            Assert.IsType<OkObjectResult>(await controller.GetById(id)).Value).Data!;
        Assert.Equal(created.SettlementNo, detail.SettlementNo);
        Assert.Equal(700m, detail.TotalAmount);

        var update = NewBulkRequest(customer.Id, totalAmount: 600m, settlementNo: "HACK-481");
        update.Remark = "更新备注";
        update.Status = DocumentStatus.Cancelled;
        Assert.IsType<OkObjectResult>(await controller.Update(id, update));

        var stored = StoredBulk(db, id);
        Assert.Equal(created.SettlementNo, stored.SettlementNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(600m, stored.TotalAmount);
        Assert.Equal("更新备注", stored.Remark);
    }

    // ==================== 4. 拒绝先于编号与持久化：零结算单 / 零来源 / 零状态 / 零占号 ====================

    [Fact]
    public async Task 未认证_结算单创建与读取拒绝_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-481-G", "客户481G");
        var container = SeedContainerSettlement(db, "ZGS-SEED", null, customer.Id);
        var bulk = SeedBulkSettlement(db, "SJ-SEED", customer.Id);
        var no = new FakeDocumentNumberService();
        var containerController = BuildContainerController(db, no);
        var bulkController = BuildBulkController(db, no);
        TestAuth.SetUser(containerController, null);
        TestAuth.SetUser(bulkController, null);

        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized,
            () => containerController.Create(NewContainerRequest(customer.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized,
            () => bulkController.Create(NewBulkRequest(customer.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized,
            () => containerController.GetById(container.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized,
            () => bulkController.GetById(bulk.Id));

        Assert.Equal(0, no.Calls);
        Assert.Single(db.FinanceContainerSettlements.AsNoTracking().ToList());
        Assert.Single(db.FinanceBulkSettlements.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 缺菜单授权与账号停用_结算单创建拒绝_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        var no = new FakeDocumentNumberService();
        var customer = SeedCustomer(db, "C-481-H", "客户481H");

        // 非特权账号且未授予既有结算单菜单 → 权限不足（fail closed，不降级为管理员）。
        var (noMenuUserId, _, _) = SeedOperator(db, privileged: false,
            withContainerMenu: false, withBulkMenu: false);
        var noMenuContainer = BuildContainerController(db, no);
        TestAuth.SetUser(noMenuContainer, noMenuUserId);
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => noMenuContainer.Create(NewContainerRequest(customer.Id)));

        // 实时撤销授权后下一次请求立即收敛。
        var (revokedUserId, revokedRoleId, _) = SeedOperator(db, privileged: false);
        RevokeMenus(db, revokedRoleId);
        var revokedBulk = BuildBulkController(db, no);
        TestAuth.SetUser(revokedBulk, revokedUserId);
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => revokedBulk.Create(NewBulkRequest(customer.Id)));

        // 账号停用 → 权限不足。
        var (disabledUserId, _, _) = SeedOperator(db);
        DisableUser(db, disabledUserId);
        var disabledContainer = BuildContainerController(db, no);
        TestAuth.SetUser(disabledContainer, disabledUserId);
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => disabledContainer.Create(NewContainerRequest(customer.Id)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceContainerSettlements.AsNoTracking().ToList());
        Assert.Empty(db.FinanceBulkSettlements.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 客户不存在已删除停用或缺失_结算单创建拒绝_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var deleted = SeedCustomer(db, "C-DEL", "已删除客户", deleted: true);
        var disabled = SeedCustomer(db, "C-DIS", "已停用客户", status: 0);
        var no = new FakeDocumentNumberService();
        var containerController = BuildContainerController(db, no);
        var bulkController = BuildBulkController(db, no);
        TestAuth.SetUser(containerController, userId);
        TestAuth.SetUser(bulkController, userId);

        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => containerController.Create(NewContainerRequest(deleted.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => containerController.Create(NewContainerRequest(disabled.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => containerController.Create(NewContainerRequest(999_999L)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => containerController.Create(NewContainerRequest(0L)));

        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => bulkController.Create(NewBulkRequest(deleted.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => bulkController.Create(NewBulkRequest(disabled.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => bulkController.Create(NewBulkRequest(999_999L)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceContainerSettlements.AsNoTracking().ToList());
        Assert.Empty(db.FinanceBulkSettlements.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 越范围客户_受限账号结算单创建拒绝_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var employee = db.BaseEmployees.Single(e => e.EmployeeCode == userName);
        var own = SeedCustomer(db, "C-OWN-481", "本人客户", empId: employee.Id);
        var foreign = SeedCustomer(db, "C-FOREIGN-481", "范围外客户", empId: 9999L);
        var no = new FakeDocumentNumberService();
        var containerController = BuildContainerController(db, no);
        var bulkController = BuildBulkController(db, no);
        TestAuth.SetUser(containerController, userId);
        TestAuth.SetUser(bulkController, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => containerController.Create(NewContainerRequest(foreign.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => bulkController.Create(NewBulkRequest(foreign.Id)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceContainerSettlements.AsNoTracking().ToList());
        Assert.Empty(db.FinanceBulkSettlements.AsNoTracking().ToList());

        // 本人客户仍可创建（既有授权范围不被整体阻断）。
        Assert.True(CreatedId(await containerController.Create(NewContainerRequest(own.Id))) > 0);
        Assert.True(CreatedId(await bulkController.Create(NewBulkRequest(own.Id))) > 0);
    }

    [Theory]
    [InlineData("zeroTotal")]
    [InlineData("negativeTotal")]
    [InlineData("negativeFreight")]
    [InlineData("negativeOtherCost")]
    public async Task 非法金额_结算单创建拒绝_零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C-481-I", "客户481I");
        var no = new FakeDocumentNumberService();
        var containerController = BuildContainerController(db, no);
        TestAuth.SetUser(containerController, userId);

        var request = NewContainerRequest(customer.Id);
        switch (scenario)
        {
            case "zeroTotal": request.TotalAmount = 0m; break;
            case "negativeTotal": request.TotalAmount = -1m; break;
            case "negativeFreight": request.FreightCost = -0.01m; break;
            default: request.OtherCost = -1m; break;
        }
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => containerController.Create(request));

        var bulkController = BuildBulkController(db, no);
        TestAuth.SetUser(bulkController, userId);
        var bulkRequest = NewBulkRequest(customer.Id);
        if (scenario is "zeroTotal" or "negativeTotal")
            bulkRequest.TotalAmount = request.TotalAmount;
        else
            bulkRequest.FreightCost = -1m;
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => bulkController.Create(bulkRequest));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceContainerSettlements.AsNoTracking().ToList());
        Assert.Empty(db.FinanceBulkSettlements.AsNoTracking().ToList());
    }

    [Theory]
    [InlineData("dangling")]
    [InlineData("deleted")]
    [InlineData("cancelled")]
    [InlineData("crossCustomer")]
    public async Task 来源装柜清单不可用_装柜结算单创建拒绝_零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C-481-J", "客户481J");
        var otherCustomer = SeedCustomer(db, "C-481-K", "客户481K");
        long loadingListId = scenario switch
        {
            "dangling" => 999_999L,
            "deleted" => SeedLoadingList(db, "LL-DEL", "CONT-DEL", customer.Id, deleted: true).Id,
            "cancelled" => SeedLoadingList(db, "LL-CAN", "CONT-CAN", customer.Id,
                status: DocumentStatus.Cancelled).Id,
            _ => SeedLoadingList(db, "LL-FOREIGN", "CONT-FOREIGN", otherCustomer.Id).Id
        };
        var probe = scenario == "dangling"
            ? null
            : db.ContainerLoadingLists.AsNoTracking().Single(l => l.Id == loadingListId);

        var no = new FakeDocumentNumberService();
        var controller = BuildContainerController(db, no);
        TestAuth.SetUser(controller, userId);

        var expected = scenario is "dangling" or "deleted" ? ErrorCodes.NotFound : ErrorCodes.RuleConflict;
        await AssertBusinessCodeAsync(expected, () => controller.Create(
            NewContainerRequest(customer.Id, loadingListId, totalAmount: 1000m)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceContainerSettlements.AsNoTracking().ToList());
        if (probe is not null)
        {
            // 被拒绝的请求绝不改写来源清单状态 / 删除态。
            var reloaded = db.ContainerLoadingLists.AsNoTracking().Single(l => l.Id == probe.Id);
            Assert.Equal(probe.Status, reloaded.Status);
            Assert.Equal(probe.IsDeleted, reloaded.IsDeleted);
        }
    }

    // ==================== 5. 有效生命周期与非法流转（零状态变化） ====================

    [Fact]
    public async Task 装柜结算单_提交审核取消_有效流转与非法流转零状态变化()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C-481-L", "客户481L");
        var list = SeedLoadingList(db, "LL-481-L", "CONT-481-L", customer.Id);
        var settlement = SeedContainerSettlement(db, "ZGS-LIFE", list.Id, customer.Id);
        var controller = BuildContainerController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        // 待提交直接审核 → 规则冲突，零状态变化。
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Approve(settlement.Id));
        Assert.Equal(DocumentStatus.Pending, StoredContainer(db, settlement.Id).Status);

        Assert.IsType<OkObjectResult>(await controller.Submit(settlement.Id));
        Assert.Equal(DocumentStatus.Submitted, StoredContainer(db, settlement.Id).Status);
        Assert.IsType<OkObjectResult>(await controller.Approve(settlement.Id));
        Assert.Equal(DocumentStatus.Approved, StoredContainer(db, settlement.Id).Status);

        // 状态不符的重复流转 → 规则冲突，零状态变化。
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Submit(settlement.Id));
        Assert.Equal(DocumentStatus.Approved, StoredContainer(db, settlement.Id).Status);

        Assert.IsType<OkObjectResult>(await controller.Cancel(settlement.Id));
        Assert.Equal(DocumentStatus.Cancelled, StoredContainer(db, settlement.Id).Status);
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(settlement.Id));
        Assert.Equal(DocumentStatus.Cancelled, StoredContainer(db, settlement.Id).Status);

        // 仅待提交可删：已取消拒绝软删除，来源清单状态与结算单单号 / 金额不变。
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(settlement.Id));
        var stored = StoredContainer(db, settlement.Id);
        Assert.False(stored.IsDeleted);
        Assert.Equal("ZGS-LIFE", stored.SettlementNo);
        Assert.Equal(1000m, stored.TotalAmount);
        Assert.Equal(DocumentStatus.Pending,
            db.ContainerLoadingLists.AsNoTracking().Single(l => l.Id == list.Id).Status);
    }

    [Fact]
    public async Task 散货结算单_提交审核取消_有效流转与非法流转零状态变化()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C-481-M", "客户481M");
        var settlement = SeedBulkSettlement(db, "SJ-LIFE", customer.Id);
        var controller = BuildBulkController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Approve(settlement.Id));
        Assert.Equal(DocumentStatus.Pending, StoredBulk(db, settlement.Id).Status);

        Assert.IsType<OkObjectResult>(await controller.Submit(settlement.Id));
        Assert.IsType<OkObjectResult>(await controller.Approve(settlement.Id));
        Assert.Equal(DocumentStatus.Approved, StoredBulk(db, settlement.Id).Status);

        Assert.IsType<OkObjectResult>(await controller.Cancel(settlement.Id));
        Assert.Equal(DocumentStatus.Cancelled, StoredBulk(db, settlement.Id).Status);
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(settlement.Id));

        var stored = StoredBulk(db, settlement.Id);
        Assert.False(stored.IsDeleted);
        Assert.Equal("SJ-LIFE", stored.SettlementNo);
        Assert.Equal(DocumentStatus.Cancelled, stored.Status);
    }

    // ==================== 6. 接线契约：菜单路由 / 规范模块 / 校验先于单号预约 ====================

    [Fact]
    public void 菜单路由_结算单走规范模块_不再落到旧存储过程族()
    {
        // ERP-481：菜单必须打开规范 EF 模块（api/finance/container-settlements、api/finance/bulk-settlements，
        // 规范 Id），不得再经 BILL_CODE_MAP 落到旧存储过程族（其写路径已被 ERP-404 门禁整体停用）。
        var billV2 = ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-v2.js");
        var mapStart = billV2.IndexOf("const BILL_CODE_MAP = {", StringComparison.Ordinal);
        var mapEnd = billV2.IndexOf("};", mapStart, StringComparison.Ordinal);
        Assert.True(mapStart >= 0 && mapEnd > mapStart);
        var billCodeMap = billV2[mapStart..mapEnd];

        // 说明性注释保留了原字面量（迁移记录），因此这里先剥离注释，只对**生效中的映射键**断言。
        var activeMap = StripComments(billCodeMap);
        Assert.DoesNotMatch(@"'container-settlement'\s*:", activeMap);
        Assert.DoesNotMatch(@"'bulk-settlement'\s*:", activeMap);
        Assert.DoesNotContain("container-settlement", activeMap);
        Assert.DoesNotContain("bulk-settlement", activeMap);
        Assert.Contains("ERP-481", billV2);

        // 其它旧族键不受影响（本次只改装柜结算单 / 散货结算单路由）。
        Assert.Contains("complaint: 'complaint'", activeMap);
        Assert.Contains("'receiving-plan': 'receiving-plan'", activeMap);

        // app.js 先判 BILL_CODE_MAP 再判 MODULES：移除映射后菜单必然落到规范模块。
        var app = ReadSource("src", "ERP.Api", "wwwroot", "js", "app.js");
        var billMapCheck = app.IndexOf("else if (BILL_CODE_MAP[code]) renderBillV2", StringComparison.Ordinal);
        var modulesLookup = app.IndexOf("const mod = MODULES[code]", StringComparison.Ordinal);
        Assert.True(billMapCheck >= 0 && modulesLookup > billMapCheck,
            "app.js 在 MODULES 之前判定 BILL_CODE_MAP：移除映射即落到规范结算单模块");

        // 规范模块声明：既有 api/finance 规范端点 + 显式可空来源字段 + 既有来源选择器。
        var modules = ReadSource("src", "ERP.Api", "wwwroot", "js", "modules-finance.js");
        Assert.Contains("title: '装柜结算单', api: '/api/finance/container-settlements'", modules);
        Assert.Contains("title: '散货结算单', api: '/api/finance/bulk-settlements'", modules);
        Assert.Contains("type: 'number', nullable: true, selector: 'container-settlement-loading-source'", modules);
        Assert.DoesNotContain("/api/v2/bills", modules);

        // crud.js 规范路径绝不调用旧存储过程族，且声明式可空引用留空提交 null（而不是哨兵值 0）。
        var crud = ReadSource("src", "ERP.Api", "wwwroot", "js", "crud.js");
        Assert.DoesNotContain("/api/v2/bills", crud);
        Assert.Contains("f.nullable === true ? null : 0", crud);
        Assert.Contains("await api(`${mod.api}/${id}`, 'PUT', body)", crud);
        Assert.Contains("await api(mod.api, 'POST', body)", crud);
    }

    [Fact]
    public void 旧族读侧保留_规范Id与旧Oid之间不存在映射_交付说明记录口径()
    {
        var billConfig = ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-config3.js");
        Assert.Contains("'container-settlement'", billConfig);
        Assert.Contains("'bulk-settlement'", billConfig);

        var billV2 = ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-v2.js");
        var mapStart = billV2.IndexOf("const BILL_CODE_MAP = {", StringComparison.Ordinal);
        var mapEnd = billV2.IndexOf("};", mapStart, StringComparison.Ordinal);
        var activeMap = StripComments(billV2[mapStart..mapEnd]);
        Assert.DoesNotContain("'container-settlement': 'container-settlement'", activeMap);
        Assert.DoesNotContain("'bulk-settlement': 'bulk-settlement'", activeMap);

        // 交付说明存在且记录规范端点与「真实浏览器 / 不声称整阶段验收」口径。
        var doc = ReadSource("docs", "finance-settlement-canonical-entry.md");
        Assert.Contains("ERP-481", doc);
        Assert.Contains("/api/finance/container-settlements", doc);
        Assert.Contains("/api/finance/bulk-settlements", doc);
        Assert.Contains("真实浏览器", doc);
    }

    [Fact]
    public void Controllers_校验先于单号预约_身份状态由服务端覆盖()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "FinanceSettlementControllers.cs");
        var bulkClass = controller.IndexOf("class FinanceBulkSettlementController", StringComparison.Ordinal);
        Assert.True(bulkClass > 0);
        var container = controller[..bulkClass];

        var containerAuthorize = container.IndexOf(
            "FinanceContainerSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId())",
            StringComparison.Ordinal);
        var containerSource = container.IndexOf(
            "FinanceContainerSettlementLifecycleRules.ResolveAndAuthorizeLoadingListAsync(",
            StringComparison.Ordinal);
        var containerGenerate = container.IndexOf(
            "entity.SettlementNo = await _noService.GenerateAsync(DocumentType.ContainerSettlement)",
            StringComparison.Ordinal);
        Assert.True(containerAuthorize >= 0 && containerSource > containerAuthorize
            && containerGenerate > containerSource,
            "装柜结算单授权 / 客户 · 金额 / 来源清单资格校验必须先于单号预约（被拒方零写入、零占号）");

        // 主键 / 状态 / 删除态 / 时间戳一律服务端覆盖（客户端注入值不参与持久化）。
        Assert.Contains("entity.Id = 0;", container);
        Assert.Contains("entity.Status = DocumentStatus.Pending;", container);
        Assert.Contains("entity.IsDeleted = false;", container);
        Assert.Contains("entity.UpdatedAt = null;", container);

        var bulk = controller[bulkClass..];
        var bulkAuthorize = bulk.IndexOf(
            "FinanceBulkSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId())",
            StringComparison.Ordinal);
        var bulkCustomer = bulk.IndexOf(
            "FinanceBulkSettlementLifecycleRules.EnsureCustomerAvailableAsync(Db, entity.CustomerId)",
            StringComparison.Ordinal);
        var bulkGenerate = bulk.IndexOf(
            "entity.SettlementNo = await _noService.GenerateAsync(DocumentType.BulkSettlement)",
            StringComparison.Ordinal);
        Assert.True(bulkAuthorize >= 0 && bulkCustomer > bulkAuthorize && bulkGenerate > bulkCustomer,
            "散货结算单授权 / 客户 · 金额校验必须先于单号预约（被拒方零写入、零占号）");
        Assert.Contains("entity.Id = 0;", bulk);
        Assert.Contains("entity.Status = DocumentStatus.Pending;", bulk);
        Assert.Contains("entity.IsDeleted = false;", bulk);
        Assert.Contains("entity.UpdatedAt = null;", bulk);

        // 不新增任何权限 / 会计凭证 / 旧族写适配器 / 任意 SQL。
        Assert.DoesNotContain("/api/v2/bills", controller);
        Assert.DoesNotContain("FromSql", controller);
        Assert.DoesNotContain("ExecuteSql", controller);
        Assert.DoesNotContain("JournalEntry", controller);
        Assert.DoesNotContain("AccountingEntry", controller);
        Assert.DoesNotContain("Voucher", controller);
    }

    [Fact]
    public void 装柜结算单来源选择器_既有显式选择器接线_可显式断开()
    {
        var picker = ReadSource("src", "ERP.Api", "wwwroot", "js", "container-settlement-loading-source.js");
        Assert.Contains("field.selector !== 'container-settlement-loading-source'", picker);
        Assert.Contains("function cslsSourceButtonHtml()", picker);
        Assert.Contains("cslsFormFill", picker);
        Assert.Contains("cslsUnlinkFill", picker);
        Assert.Contains("cslsUnlinkSource", picker);
        Assert.Contains("cslsShouldAcceptResponse", picker);
        Assert.Contains("CSLS_EDITABLE_STATUS", picker);
        Assert.DoesNotContain("/api/v2/bills", picker);
        Assert.DoesNotContain("FromSql", picker);
        Assert.DoesNotContain("ExecuteSql", picker);

        var index = ReadSource("src", "ERP.Api", "wwwroot", "index.html");
        Assert.Contains("/js/container-settlement-loading-source.js", index);
        Assert.Contains("/js/modules-finance.js", index);
    }
}
