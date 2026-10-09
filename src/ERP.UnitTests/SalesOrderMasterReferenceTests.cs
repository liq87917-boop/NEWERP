using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-423 规范销售订单「实时主数据引用」护栏单元测试（纯内存 + 直接实例化控制器，不连接 SQL Server、
/// 不执行任何 DDL / 部署脚本）：
/// <list type="number">
/// <item><b>唯一规则</b>：必填客户（存在 / 未删除 / 启用）、每条有效明细的必填商品、可选业务员（在职员工）、
/// 可选目的港（Port 港口字典项）与既有有效单位口径（基础单位 / 合法装箱单位，绝不臆造换算）；</item>
/// <item><b>控制器行为</b>：新增 / 修改在单号预约与字段 / 明细赋值之前拒绝失效引用且零写入 / 零单号；
/// 提交 / 审核在订单行锁内重查引用，来源失效（客户 / 商品被删除 / 停用）时原子拒绝、状态不变；
/// 合法手工订单完整生命周期（新增 → 提交 → 审核 → 取消）仍可用；历史引用失效订单的读取 / 打印完全只读、不被回填；</item>
/// <item><b>授权前置与非披露</b>：写入授权先于主数据校验，缺菜单的受限账号即便提交无效引用也返回同一受控权限错误。</item>
/// </list>
/// <para>安全口径：全部使用内存库 <see cref="TestDbFactory"/>，不读取 appsettings / .env / 生产凭据，
/// 不执行 drop / reset，也不使用生产数据。</para>
/// </summary>
public class SalesOrderMasterReferenceTests
{
    // ==================== 1. 必填客户 ====================

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task 客户_非正整数_按参数错误拒绝(long customerId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsureCustomerAvailableAsync(db, customerId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderMasterReferenceRules.CustomerRequiredText, ex.Message);
    }

    [Fact]
    public async Task 客户_不存在_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsureCustomerAvailableAsync(db, 424242L));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("不存在或已删除", ex.Message);
    }

    [Fact]
    public async Task 客户_已删除_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, deleted: true);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsureCustomerAvailableAsync(db, customer.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task 客户_已停用_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsureCustomerAvailableAsync(db, customer.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
    }

    [Fact]
    public async Task 客户_合法_返回实时实体()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);

        var resolved = await SalesOrderMasterReferenceRules.EnsureCustomerAvailableAsync(db, customer.Id);

        Assert.Equal(customer.Id, resolved.Id);
        Assert.Equal(customer.CustomerName, resolved.CustomerName);
    }

    // ==================== 2. 可选业务员 ====================

    [Fact]
    public async Task 业务员_未填写_跳过校验()
    {
        using var db = TestDbFactory.Create();

        Assert.Null(await SalesOrderMasterReferenceRules.EnsureSalesmanAvailableAsync(db, null));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public async Task 业务员_显式非正整数_按参数错误拒绝(long salesmanId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsureSalesmanAvailableAsync(db, salesmanId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderMasterReferenceRules.SalesmanRequiredText, ex.Message);
    }

    [Fact]
    public async Task 业务员_不存在或已删除_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedEmployee(db, deleted: true);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsureSalesmanAvailableAsync(db, 777777L));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var removed = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsureSalesmanAvailableAsync(db, deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, removed.Code);
    }

    [Fact]
    public async Task 业务员_已离职停用_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var employee = SeedEmployee(db, status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsureSalesmanAvailableAsync(db, employee.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已离职", ex.Message);
    }

    [Fact]
    public async Task 业务员_合法在职_返回实时实体()
    {
        using var db = TestDbFactory.Create();
        var employee = SeedEmployee(db);

        var resolved = await SalesOrderMasterReferenceRules.EnsureSalesmanAvailableAsync(db, employee.Id);

        Assert.NotNull(resolved);
        Assert.Equal(employee.Id, resolved!.Id);
    }

    // ==================== 3. 可选目的港 ====================

    [Fact]
    public async Task 目的港_未填写_跳过校验()
    {
        using var db = TestDbFactory.Create();

        Assert.Null(await SalesOrderMasterReferenceRules.EnsurePortAvailableAsync(db, null));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-3L)]
    public async Task 目的港_显式非正整数_按参数错误拒绝(long portId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsurePortAvailableAsync(db, portId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderMasterReferenceRules.PortRequiredText, ex.Message);
    }

    [Fact]
    public async Task 目的港_不存在或已删除_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedPort(db, deleted: true);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsurePortAvailableAsync(db, 888888L));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var removed = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsurePortAvailableAsync(db, deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, removed.Code);
    }

    [Fact]
    public async Task 目的港_非港口字典项_按参数错误拒绝()
    {
        using var db = TestDbFactory.Create();
        var other = new BaseOtherInfo { InfoType = "Currency", InfoCode = "USD", InfoName = "美元", Status = 1 };
        db.BaseOtherInfos.Add(other);
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsurePortAvailableAsync(db, other.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains(SalesOrderMasterReferenceRules.PortInfoType, ex.Message);
    }

    [Fact]
    public async Task 目的港_已停用_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var port = SeedPort(db, status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsurePortAvailableAsync(db, port.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
    }

    [Fact]
    public async Task 目的港_合法启用_返回实时实体()
    {
        using var db = TestDbFactory.Create();
        var port = SeedPort(db);

        var resolved = await SalesOrderMasterReferenceRules.EnsurePortAvailableAsync(db, port.Id);

        Assert.NotNull(resolved);
        Assert.Equal(port.Id, resolved!.Id);
    }

    // ==================== 4. 必填商品（每条有效明细） ====================

    [Fact]
    public async Task 商品_明细ProductId非正整数_按参数错误拒绝()
    {
        using var db = TestDbFactory.Create();
        var details = new[] { new SalesOrderDetail { ProductId = 0, Unit = "PCS" } };

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsureDetailProductsAsync(db, details));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderMasterReferenceRules.ProductRequiredText, ex.Message);
    }

    [Fact]
    public async Task 商品_不存在或已删除_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedProduct(db, deleted: true, unit: "PCS");

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsureDetailProductsAsync(db,
                new[] { new SalesOrderDetail { ProductId = 515151L, Unit = "PCS" } }));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var removed = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsureDetailProductsAsync(db,
                new[] { new SalesOrderDetail { ProductId = deleted.Id, Unit = "PCS" } }));
        Assert.Equal(ErrorCodes.NotFound, removed.Code);
    }

    [Fact]
    public async Task 商品_已停用_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, status: 0, unit: "PCS");

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsureDetailProductsAsync(db,
                new[] { new SalesOrderDetail { ProductId = product.Id, Unit = "PCS" } }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
    }

    [Fact]
    public async Task 商品_已软删除明细行_不参与校验()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, unit: "PCS");
        var details = new[]
        {
            new SalesOrderDetail { ProductId = product.Id, Unit = "PCS" },
            new SalesOrderDetail { ProductId = 999999L, Unit = "PCS", IsDeleted = true }
        };

        await SalesOrderMasterReferenceRules.EnsureDetailProductsAsync(db, details);
    }

    [Fact]
    public async Task 商品_全部有效明细合法_放行且不产生变更跟踪()
    {
        using var db = TestDbFactory.Create();
        var productA = SeedProduct(db, unit: "PCS");
        var productB = SeedProduct(db, unit: "SET");

        await SalesOrderMasterReferenceRules.EnsureDetailProductsAsync(db, new[]
        {
            new SalesOrderDetail { ProductId = productA.Id, Unit = "PCS" },
            new SalesOrderDetail { ProductId = productB.Id, Unit = "set" }
        });

        // 只读投影查询：不落库、不产生任何被跟踪的变更。
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    // ==================== 5. 既有有效单位口径 ====================

    [Fact]
    public void 单位_基础单位与合法装箱单位均被支持()
    {
        var product = new BaseProduct
        {
            ProductName = "多单位商品", Unit = "PCS", PackageUnit = "BOX", UnitsPerPackage = 12, Status = 1
        };

        Assert.True(SalesOrderMasterReferenceRules.IsSupportedUnit(product, "PCS"));
        Assert.True(SalesOrderMasterReferenceRules.IsSupportedUnit(product, "pcs"));
        Assert.True(SalesOrderMasterReferenceRules.IsSupportedUnit(product, " BOX "));
        Assert.Equal(2, SalesOrderMasterReferenceRules.SupportedUnits(product).Count);
    }

    [Fact]
    public void 单位_装箱数非正时装箱单位不被支持()
    {
        var product = new BaseProduct
        {
            ProductName = "无效装箱商品", Unit = "PCS", PackageUnit = "BOX", UnitsPerPackage = 0, Status = 1
        };

        Assert.True(SalesOrderMasterReferenceRules.IsSupportedUnit(product, "PCS"));
        Assert.False(SalesOrderMasterReferenceRules.IsSupportedUnit(product, "BOX"));
    }

    [Fact]
    public void 单位_非法单位_不被支持且错误文案列出既有口径()
    {
        var product = new BaseProduct { ProductName = "单单位商品", Unit = "PCS", Status = 1 };

        Assert.False(SalesOrderMasterReferenceRules.IsSupportedUnit(product, "SET"));
        Assert.False(SalesOrderMasterReferenceRules.IsSupportedUnit(product, string.Empty));
        Assert.False(SalesOrderMasterReferenceRules.IsSupportedUnit(product, null));

        var text = SalesOrderMasterReferenceRules.UnsupportedUnitText(product, "SET");
        Assert.Contains("PCS", text);
        Assert.Contains("拒绝臆造单位换算", text);
    }

    [Fact]
    public void 单位_商品未维护任何单位时不产生单位判定()
    {
        var product = new BaseProduct { ProductName = "历史商品", Unit = string.Empty, Status = 1 };

        Assert.True(SalesOrderMasterReferenceRules.IsSupportedUnit(product, "SET"));
        Assert.True(SalesOrderMasterReferenceRules.IsSupportedUnit(product, string.Empty));
        Assert.Empty(SalesOrderMasterReferenceRules.SupportedUnits(product));
    }

    [Fact]
    public async Task 单位_明细使用未支持单位_写入规则拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, unit: "PCS");

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderMasterReferenceRules.EnsureDetailProductsAsync(db,
                new[] { new SalesOrderDetail { ProductId = product.Id, Unit = "SET" } }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不在既有有效单位口径内", ex.Message);
    }

    // ==================== 6. 口径与边界文案 ====================

    [Fact]
    public void 口径文案_覆盖客户_商品_可选引用与单位()
    {
        Assert.Equal("Port", SalesOrderMasterReferenceRules.PortInfoType);
        Assert.Equal(1, SalesOrderMasterReferenceRules.EnabledStatus);

        var rule = SalesOrderMasterReferenceRules.RuleText;
        Assert.Contains("CustomerId", rule);
        Assert.Contains("ProductId", rule);
        Assert.Contains("SalesmanId", rule);
        Assert.Contains("PortId", rule);
        Assert.Contains("单位", rule);

        var boundary = SalesOrderMasterReferenceRules.BoundaryText;
        Assert.Contains("不落库", boundary);
        Assert.Contains("既有血缘规则", boundary);
        Assert.Contains("不被自动回填或修正", boundary);
    }

    // ==================== 7. 控制器：新增 / 修改 ====================

    [Fact]
    public async Task 新增_客户缺失_受控拒绝且零写入零单号()
    {
        using var db = TestDbFactory.Create();
        var ctl = PrivilegedController(db);
        var before = await SnapshotAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrder(987654L, new[] { Detail(1L, "PCS") })));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("客户", ex.Message);
        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task 新增_客户已停用_受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, status: 0);
        var ctl = PrivilegedController(db);
        var before = await SnapshotAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrder(customer.Id, new[] { Detail(1L, "PCS") })));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task 新增_商品缺失或停用_受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var disabled = SeedProduct(db, status: 0, unit: "PCS");
        var ctl = PrivilegedController(db);
        var before = await SnapshotAsync(db);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrder(customer.Id, new[] { Detail(987654L, "PCS") })));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var stopped = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrder(customer.Id, new[] { Detail(disabled.Id, "PCS") })));
        Assert.Equal(ErrorCodes.RuleConflict, stopped.Code);

        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task 新增_商品单位不支持_受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, unit: "PCS");
        var ctl = PrivilegedController(db);
        var before = await SnapshotAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrder(customer.Id, new[] { Detail(product.Id, "SET") })));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("单位", ex.Message);
        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task 新增_可选业务员或目的港非法_受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, unit: "PCS");
        var ctl = PrivilegedController(db);
        var before = await SnapshotAsync(db);

        var salesman = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrder(customer.Id, new[] { Detail(product.Id, "PCS") }, salesmanId: 765432L)));
        Assert.Equal(ErrorCodes.NotFound, salesman.Code);
        Assert.Contains("业务员", salesman.Message);

        var port = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrder(customer.Id, new[] { Detail(product.Id, "PCS") }, portId: 765433L)));
        Assert.Equal(ErrorCodes.NotFound, port.Code);
        Assert.Contains("目的港", port.Message);

        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task 新增_手工订单_合法主数据_完整生命周期可用()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var employee = SeedEmployee(db);
        var port = SeedPort(db);
        var product = SeedProduct(db, unit: "PCS");
        var ctl = PrivilegedController(db);

        // 可选业务员 / 目的港留空或填合法值时均放行。
        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrder(customer.Id, new[] { Detail(product.Id, "PCS") })));
        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrder(customer.Id,
            new[] { Detail(product.Id, "PCS") }, salesmanId: employee.Id, portId: port.Id)));

        var first = await db.SalesOrders.AsNoTracking().OrderBy(o => o.Id).FirstAsync();
        Assert.IsType<OkObjectResult>(await ctl.Submit(first.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(first.Id));
        Assert.Equal(DocumentStatus.Approved,
            (await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == first.Id)).Status);
        Assert.IsType<OkObjectResult>(await ctl.Cancel(first.Id));
        Assert.Equal(DocumentStatus.Cancelled,
            (await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == first.Id)).Status);
    }

    [Fact]
    public async Task 修改_客户失效_受控拒绝且原始表头明细不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, unit: "PCS");
        var order = SeedOrder(db, customer.Id, product.Id, DocumentStatus.Pending);
        customer.IsDeleted = true;
        await db.SaveChangesAsync();
        var ctl = PrivilegedController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(order.Id, NewOrder(customer.Id, new[] { Detail(product.Id, "PCS") })));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        var persisted = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(order.TotalAmount, persisted.TotalAmount);
        Assert.Single(await db.SalesOrderDetails.AsNoTracking()
            .Where(d => d.SalesOrderId == order.Id).ToListAsync());
    }

    // ==================== 8. 控制器：提交 / 审核重查与历史只读 ====================

    [Fact]
    public async Task 提交审核_合法主数据_放行至已审核()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, unit: "PCS");
        var order = SeedOrder(db, customer.Id, product.Id, DocumentStatus.Pending);
        var ctl = PrivilegedController(db);

        Assert.IsType<OkObjectResult>(await ctl.Submit(order.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(order.Id));
        Assert.Equal(DocumentStatus.Approved,
            (await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task 提交_客户提交前被删除_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, unit: "PCS");
        var order = SeedOrder(db, customer.Id, product.Id, DocumentStatus.Pending);
        customer.IsDeleted = true;
        await db.SaveChangesAsync();
        var ctl = PrivilegedController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(order.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(DocumentStatus.Pending,
            (await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task 审核_商品提交前被停用_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, unit: "PCS");
        var order = SeedOrder(db, customer.Id, product.Id, DocumentStatus.Submitted);
        product.Status = 0;
        await db.SaveChangesAsync();
        var ctl = PrivilegedController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(order.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Submitted,
            (await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task 历史订单_引用失效主数据_读取打印仍可读且不被回填()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, unit: "PCS");
        var order = SeedOrder(db, customer.Id, product.Id, DocumentStatus.Approved);
        customer.IsDeleted = true;
        product.IsDeleted = true;
        await db.SaveChangesAsync();
        var ctl = PrivilegedController(db);

        var detail = Assert.IsType<ApiResponse<SalesOrder>>(
            Assert.IsType<OkObjectResult>(await ctl.GetById(order.Id)).Value).Data!;
        Assert.Equal(customer.Id, detail.CustomerId);
        Assert.Equal(product.Id, Assert.Single(detail.Details).ProductId);

        var print = Assert.IsType<ApiResponse<SalesOrder>>(
            Assert.IsType<OkObjectResult>(await ctl.GetPrint(order.Id)).Value).Data!;
        Assert.Equal(product.Id, Assert.Single(print.Details).ProductId);

        // 读取 / 打印完全只读：历史引用不被回填或改写。
        var persisted = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(customer.Id, persisted.CustomerId);
    }

    [Fact]
    public async Task 受限账号_缺菜单_即便引用无效也返回受控权限错误()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedRestrictedUser(db);
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrder(987654L, new[] { Detail(1L, "PCS") })));

        // 授权先于主数据校验：绝不因「客户是否存在」而差异化响应（非披露）。
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(SalesOrderMutationAuthorizationRules.MenuDeniedText, ex.Message);
        Assert.Empty(db.SalesOrders);
    }

    // ==================== 工厂与种子数据 ====================

    private sealed record Counts(int Orders, int Details, int NumberRules);

    private static async Task<Counts> SnapshotAsync(ErpDbContext db)
        => new(await db.SalesOrders.CountAsync(), await db.SalesOrderDetails.CountAsync(),
            await db.SysDocumentNumberRules.CountAsync());

    private static async Task AssertUnchangedAsync(ErpDbContext db, Counts before)
    {
        var after = await SnapshotAsync(db);
        Assert.Equal(before.Orders, after.Orders);
        Assert.Equal(before.Details, after.Details);
        Assert.Equal(before.NumberRules, after.NumberRules);
    }

    private static SalesOrderController NewController(ErpDbContext db, long? userId)
    {
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static SalesOrderController PrivilegedController(ErpDbContext db)
        => NewController(db, TestAuth.SeedPrivilegedUser(db));

    private static SalesOrder NewOrder(long customerId, IEnumerable<SalesOrderDetail> details,
        long? salesmanId = null, long? portId = null)
        => new()
        {
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            SalesmanId = salesmanId,
            PortId = portId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            Details = details.ToList()
        };

    private static SalesOrderDetail Detail(long productId, string unit)
        => new()
        {
            ProductId = productId, ProductName = $"商品 {productId}", Unit = unit, Quantity = 10m, UnitPrice = 5m
        };

    private static SalesOrder SeedOrder(ErpDbContext db, long customerId, long productId, DocumentStatus status)
    {
        var order = new SalesOrder
        {
            OrderNo = $"SO-MR-{Guid.NewGuid():N}"[..18],
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            Status = status,
            Details = new List<SalesOrderDetail> { Detail(productId, "PCS") }
        };
        SalesOrderAmountRules.ApplyDetailAmounts(order);
        SalesOrderAmountRules.Calculate(order);
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-MR-{Guid.NewGuid():N}",
            CustomerName = "ERP423 客户",
            Status = status,
            IsDeleted = deleted,
            DepositRatio = 30m
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = $"E-MR-{Guid.NewGuid():N}",
            EmployeeName = "ERP423 业务员",
            IsSalesman = true,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, int status = 1, bool deleted = false, string? unit = null)
    {
        var product = new BaseProduct
        {
            ProductCode = $"P-MR-{Guid.NewGuid():N}",
            ProductName = "ERP423 商品",
            Unit = unit ?? string.Empty,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static BaseOtherInfo SeedPort(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var port = new BaseOtherInfo
        {
            InfoType = SalesOrderMasterReferenceRules.PortInfoType,
            InfoCode = $"P-MR-{Guid.NewGuid():N}",
            InfoName = "ERP423 港口",
            Status = status,
            IsDeleted = deleted
        };
        db.BaseOtherInfos.Add(port);
        db.SaveChanges();
        return port;
    }

    private static long SeedRestrictedUser(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"mr-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "ERP423 受限账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleCode = $"MR-{Guid.NewGuid():N}", RoleName = "ERP423 受限角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }
}

