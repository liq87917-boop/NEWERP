using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-518 / ERP-535 单证明细行与单证表头金额的**实际存储精度**边界单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item>实际 EF 列精度与规则常量同源：明细数量 / 单价 / 净重 / 毛重 <c>decimal(18,4)</c>、行金额
/// <c>decimal(18,2)</c>、单证表头金额显式 <c>decimal(18,4)</c>（ERP-535：与权威升级脚本宣告列同源；
/// 仅以 SQL Server 提供程序<b>仅构建模型</b>，不连接数据库）；</item>
/// <item>明细行**手动新增 / 修改**在数量 / 单价 / 净重 / 毛重（18,4）、行金额（18,2）越界或乘法溢出时受控拒绝，
/// 且零写入、零改写（合法边界值原样落库）；</item>
/// <item>行金额服务端计算复用既有币种精度取整（含零小数币种），仅追加「受控乘法 + 实际列容量」判定；</item>
/// <item>单证表头**新增 / 修改**金额按宣告 4 位小数列逐位原样判定：精确 4 位小数上限与 3 / 4 位小数放行并原样
/// 读回，第 5 位小数、量级越界与「半进位取整后恰好落到上限」受控拒绝且零单证 / 逐字段不变；</item>
/// <item>来源快照生成（销售订单 / 装柜清单）使用同一套实际精度规则：明细越界或表头金额越界时**整批原子拒绝**，
/// 零单证 / 零明细 / 零编号占用，来源与商品资料不被改写；保留可空重量与装箱单零价格口径；</item>
/// <item>清单读取是**只读聚合展示**：行金额合计与重量合计刻意不施加单列存储上界（可合法超过单个列容量）；
/// 全局 <see cref="CurrencyAmountRules"/> 不被施加单列上限；</item>
/// <item>控制器 / 规则源码契约：数值护栏先于编号预约与任何写入。</item>
/// </list>
/// <para>全部使用内存库（<see cref="TestDbFactory"/>）；真实 SQL Server 列类型 / 边界往返 / 越界零写入由
/// <c>ERP.IntegrationTests/TradeDocumentPersistedNumericBoundsSqlServerTests.cs</c> 在受控 localdb 上验证。</para>
/// </summary>
public class TradeDocumentPersistedNumericBoundsTests
{
    /// <summary>数量 / 单价 / 净重 / 毛重的实际列上界（<c>DECIMAL(18,4)</c>）。</summary>
    private const decimal Max4 = FinancePersistedAmountRules.MaxDecimal18Scale4;

    /// <summary>行金额 / 表头金额的实际列上界（<c>DECIMAL(18,2)</c>）。</summary>
    private const decimal Max2 = FinancePersistedAmountRules.MaxDecimal18Scale2;

    // ==================== 0. 测试脚手架（真实启用身份 + 真实主数据） ====================

    /// <summary>
    /// ERP-497：绑定隔离测试数据里的<b>真实启用身份</b>（既有系统内置角色 → 特权数据范围）的单证中心控制器；
    /// 单证路由的实时授权由动作内部无条件执行，不依赖请求管线过滤器或空范围旁路。
    /// </summary>
    private static TradeDocumentController BuildItemController(ErpDbContext db)
    {
        var controller = new TradeDocumentController(new GenericService<TradeDocument>(db), db);
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    /// <summary>ERP-497：同上，销售订单控制器（来源生成入口）。</summary>
    private static SalesOrderController BuildSalesOrderController(ErpDbContext db)
    {
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    /// <summary>ERP-497：同上，装柜清单控制器（来源生成入口）。</summary>
    private static ContainerLoadingListController BuildLoadingListController(ErpDbContext db)
    {
        var controller = new ContainerLoadingListController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    /// <summary>真实客户主数据（启用状态 + 币种 + 目的港）。</summary>
    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string currency = "USD")
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = "义乌客户（ERP-518）", Currency = currency,
            DestinationPort = "ROTTERDAM", Status = ContainerLoadingParticipantRules.ActiveStatus,
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    /// <summary>真实商品主数据（启用状态）。</summary>
    private static BaseProduct SeedProduct(ErpDbContext db, string code)
    {
        var product = new BaseProduct
        {
            ProductCode = code, ProductName = code, Spec = "70x140", Unit = "箱", Status = 1,
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static TradeDocument SeedDocument(ErpDbContext db, string docNo, string docType,
        string status = "待制作", decimal amount = 0m, string currency = "USD")
    {
        var document = new TradeDocument
        {
            DocNo = docNo, DocType = docType, Status = status, Amount = amount,
            Currency = currency, IssueDate = new DateTime(2026, 9, 20),
            CustomerName = "义乌客户（ERP-518）", Copies = 3,
        };
        db.TradeDocuments.Add(document);
        db.SaveChanges();
        return document;
    }

    private static TradeDocumentItemSaveDto Item(
        decimal quantity = 10m, decimal unitPrice = 2.5m, int? packageCount = null,
        decimal? netWeight = null, decimal? grossWeight = null, string remark = "")
        => new()
        {
            ProductId = 0, ProductCode = "P001", ProductNameCn = "毛巾", Unit = "箱",
            Quantity = quantity, UnitPrice = unitPrice, PackageCount = packageCount,
            NetWeight = netWeight, GrossWeight = grossWeight, Remark = remark,
        };

    /// <summary>以既有权威单证为蓝本构造「拟议」表头（只改金额，其余保持原值）。</summary>
    private static TradeDocument Edit(TradeDocument stored, decimal amount)
        => new()
        {
            Id = stored.Id, DocNo = stored.DocNo, DocType = stored.DocType, Status = stored.Status,
            Amount = amount, Currency = stored.Currency, IssueDate = stored.IssueDate,
            CustomerId = stored.CustomerId, CustomerName = stored.CustomerName, Copies = stored.Copies,
            DeparturePort = stored.DeparturePort, DestinationPort = stored.DestinationPort,
            IssuedBy = stored.IssuedBy, SalesOrderNo = stored.SalesOrderNo, RefNo = stored.RefNo,
            DeclareNo = stored.DeclareNo, FileNote = stored.FileNote, Remark = stored.Remark,
        };

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string no, long customerId, long productId,
        decimal totalAmount, params (decimal Quantity, decimal UnitPrice)[] details)
    {
        var order = new SalesOrder
        {
            OrderNo = no, OrderDate = DateTime.Today, CustomerId = customerId,
            Currency = Currency.USD, TotalAmount = totalAmount, Status = DocumentStatus.Approved,
            DestinationPort = "HAMBURG",
            Details = details.Select(line => new SalesOrderDetail
            {
                ProductId = productId, ProductName = "毛巾", Spec = "70x140", Unit = "箱",
                Quantity = line.Quantity, UnitPrice = line.UnitPrice,
                Amount = line.Quantity * line.UnitPrice,
            }).ToList(),
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static ContainerLoadingList SeedLoadingList(ErpDbContext db, string no, long customerId,
        long productId, decimal quantity, decimal cartons, decimal weight)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no, LoadingDate = DateTime.Today, ContainerNo = "CTN-518",
            CustomerId = customerId, Status = DocumentStatus.Approved,
            TotalCartons = cartons, TotalWeight = weight,
            Details = new List<ContainerLoadingDetail>
            {
                new()
                {
                    ProductId = productId, ProductName = "毛巾", Quantity = quantity,
                    Cartons = cartons, Weight = weight,
                }
            }
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static string RepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // ==================== 1. 实际 EF 列精度与规则常量同源（仅构建模型） ====================

    [Fact]
    public void 实际EF列精度与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP518_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        // 明细行：数量 / 单价 / 净重 / 毛重显式 decimal(18,4)，行金额显式 decimal(18,2)。
        AssertDecimalColumn(db.Model, typeof(TradeDocumentItem), nameof(TradeDocumentItem.Quantity), 18, 4);
        AssertDecimalColumn(db.Model, typeof(TradeDocumentItem), nameof(TradeDocumentItem.UnitPrice), 18, 4);
        AssertDecimalColumn(db.Model, typeof(TradeDocumentItem), nameof(TradeDocumentItem.NetWeight), 18, 4);
        AssertDecimalColumn(db.Model, typeof(TradeDocumentItem), nameof(TradeDocumentItem.GrossWeight), 18, 4);
        AssertDecimalColumn(db.Model, typeof(TradeDocumentItem), nameof(TradeDocumentItem.LineAmount), 18, 2);

        // 单证表头金额显式声明 decimal(18,4)（ERP-535：与权威升级脚本 SchemaUpgrader 第 16 段的
        // db_owner.TradeDocuments.Amount DECIMAL(18,4) 宣告列同源）；未声明时 EF 会按默认 decimal(18,2)
        // 静默取整 3 / 4 位小数，并让宣告列可原样存储的值被误判为越界。
        AssertDecimalColumn(db.Model, typeof(TradeDocument), nameof(TradeDocument.Amount), 18, 4);

        // 规则常量与真实列精度同源（表头金额按宣告列 4 位小数，与固定 2 位小数的明细行金额分开）。
        Assert.Equal(18, TradeDocumentItemRules.ItemScalarPrecision);
        Assert.Equal(4, TradeDocumentItemRules.ItemScalarScale);
        Assert.Equal(18, TradeDocumentItemRules.LineAmountPrecision);
        Assert.Equal(2, TradeDocumentItemRules.LineAmountScale);
        Assert.Equal(18, TradeDocumentItemRules.HeaderAmountPrecision);
        Assert.Equal(4, TradeDocumentItemRules.HeaderAmountScale);
        Assert.Equal(FinancePersistedAmountRules.DecimalScale4, TradeDocumentItemRules.ItemScalarScale);
        Assert.Equal(FinancePersistedAmountRules.DecimalScale4, TradeDocumentItemRules.HeaderAmountScale);
        Assert.Equal(FinancePersistedAmountRules.DecimalScale, TradeDocumentItemRules.LineAmountScale);
    }

    private static void AssertDecimalColumn(IModel model, Type entityType, string propertyName,
        int precision, int scale)
    {
        var property = model.FindEntityType(entityType)!.FindProperty(propertyName)!;
        Assert.Equal(precision, property.GetPrecision());
        Assert.Equal(scale, property.GetScale());
        Assert.Equal($"decimal({precision},{scale})", property.GetColumnType());
    }

    // ==================== 2. 精确合法边界与小数位口径 ====================

    [Fact]
    public void 实际精度判定_精确合法边界_超精度与越界在规则入口拒绝()
    {
        Assert.True(TradeDocumentItemRules.IsItemScalarRepresentable(Max4));
        Assert.False(TradeDocumentItemRules.IsItemScalarRepresentable(Max4 + 1m));
        Assert.True(TradeDocumentItemRules.IsItemScalarRepresentable(-Max4));
        Assert.False(TradeDocumentItemRules.IsItemScalarRepresentable(-(Max4 + 1m)));

        Assert.True(TradeDocumentItemRules.IsLineAmountRepresentable(Max2));
        Assert.False(TradeDocumentItemRules.IsLineAmountRepresentable(Max2 + 1m));

        // 单证表头金额按宣告列 4 位小数（ERP-535）：精确上限与 4 位小数放行；第 5 位小数与
        // 「半进位取整后恰好落到上限」的过小数位原值仍拒绝（绝不采信「取整之后才可存储」）；量级越界拒绝。
        Assert.True(TradeDocumentItemRules.IsHeaderAmountRepresentable(Max4));
        Assert.True(TradeDocumentItemRules.IsHeaderAmountRepresentable(-Max4));
        Assert.True(TradeDocumentItemRules.IsHeaderAmountRepresentable(1234.5678m));
        Assert.False(TradeDocumentItemRules.IsHeaderAmountRepresentable(Max4 + 1m));
        Assert.False(TradeDocumentItemRules.IsHeaderAmountRepresentable(-(Max4 + 1m)));
        Assert.False(TradeDocumentItemRules.IsHeaderAmountRepresentable(Max2 + 1m));
        Assert.False(TradeDocumentItemRules.IsHeaderAmountRepresentable(1.00001m));          // 第 5 位小数
        Assert.False(TradeDocumentItemRules.IsHeaderAmountRepresentable(Max4 - 0.00005m));  // 半进位取整后恰好 = 上限
        Assert.False(TradeDocumentItemRules.IsHeaderAmountRepresentable(Max4 + 0.00005m));  // 半进位取整后越过上限

        // 超精度（5 位小数）在规则入口被拒绝（不静默取整）。
        Assert.Equal(ErrorCodes.InvalidParameter, Assert.Throws<BusinessException>(
            () => TradeDocumentItemRules.NormalizeQuantity(1.00001m)).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, Assert.Throws<BusinessException>(
            () => TradeDocumentItemRules.NormalizeUnitPrice(1.00001m, pricingAllowed: true)).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, Assert.Throws<BusinessException>(
            () => TradeDocumentItemRules.NormalizeWeight(1.00001m, "净重")).Code);

        // 越界（超过 DECIMAL(18,4) 上界）在规则入口被拒绝，错误文案给出实际列精度。
        var over = Assert.Throws<BusinessException>(() => TradeDocumentItemRules.NormalizeQuantity(Max4 + 1m));
        Assert.Equal(ErrorCodes.InvalidParameter, over.Code);
        Assert.Contains("DECIMAL(18,4)", over.Message, StringComparison.Ordinal);
        Assert.Throws<BusinessException>(() => TradeDocumentItemRules.NormalizeUnitPrice(Max4 + 1m, true));
        Assert.Throws<BusinessException>(() => TradeDocumentItemRules.NormalizeWeight(Max4 + 1m, "毛重"));

        // 合法边界值原样返回（绝不取整 / 截断）。
        Assert.Equal(Max4, TradeDocumentItemRules.NormalizeQuantity(Max4));
        Assert.Equal(Max4, TradeDocumentItemRules.NormalizeUnitPrice(Max4, true));
        Assert.Equal(Max4, TradeDocumentItemRules.NormalizeWeight(Max4, "净重"));
        Assert.Null(TradeDocumentItemRules.NormalizeWeight(null, "净重"));
        Assert.Equal(0m, TradeDocumentItemRules.NormalizeUnitPrice(0m, pricingAllowed: false));
    }


    // ==================== 3. 行金额：受控乘法 + 既有币种取整 + 实际列容量 ====================

    [Fact]
    public void 行金额_受控乘法_币种取整口径保持_溢出与越界受控拒绝()
    {
        // 既有币种精度取整口径原样保留（0.5 进位；零小数币种为 0 位）。
        Assert.Equal(25m, TradeDocumentItemRules.ComputeLineAmount(10m, 2.5m, "USD"));
        Assert.Equal(3.02m, TradeDocumentItemRules.ComputeLineAmount(3m, 1.005m, "USD"));
        Assert.Equal(3m, TradeDocumentItemRules.ComputeLineAmount(3m, 1.005m, "JPY"));
        Assert.Equal(0m, TradeDocumentItemRules.ComputeLineAmount(10m, 0m, "USD"));

        // 恰好等于行金额列上界：放行并原样返回。
        Assert.Equal(Max2, TradeDocumentItemRules.ComputeLineAmount(Max4, 100m, "CNY"));

        // CLR 十进制乘法溢出 → 受控业务错误（不是未处理异常）。
        var clr = Assert.Throws<BusinessException>(
            () => TradeDocumentItemRules.ComputeLineAmount(decimal.MaxValue, 2m, "CNY"));
        Assert.Equal(ErrorCodes.InvalidParameter, clr.Code);
        Assert.Contains("溢出", clr.Message, StringComparison.Ordinal);

        // 乘积不溢出但超出行金额列容量（DECIMAL(18,2)）：受控拒绝。
        var over = Assert.Throws<BusinessException>(
            () => TradeDocumentItemRules.ComputeLineAmount(Max4, 101m, "CNY"));
        Assert.Equal(ErrorCodes.InvalidParameter, over.Code);
        Assert.Contains("DECIMAL(18,2)", over.Message, StringComparison.Ordinal);

        // 全局币种取整与只读求和**没有**单列上限（只读聚合可合法超过单个存储列）。
        var aggregate = Max2 + Max2;
        Assert.True(aggregate > Max2);
        Assert.Equal(aggregate, CurrencyAmountRules.RoundAmount(aggregate, "CNY"));
    }

    // ==================== 4. 明细行新增 / 修改：越界拒绝且零写入 / 零改写 ====================

    [Fact]
    public async Task 明细行新增_合法边界原样落库_越界与溢出拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-518-BOUND", "商业发票", currency: "USD");
        var controller = BuildItemController(db);

        // 数量 = DECIMAL(18,4) 上界、单价 100 → 行金额恰好 = DECIMAL(18,2) 上界，原样落库。
        var created = AssertOk<TradeDocumentItemDto>(await controller.CreateItem(document.Id,
            Item(quantity: Max4, unitPrice: 100m)));
        Assert.Equal(Max4, created.Quantity);
        Assert.Equal(100m, created.UnitPrice);
        Assert.Equal(Max2, created.LineAmount);

        var stored = await db.TradeDocumentItems.AsNoTracking().SingleAsync();
        Assert.Equal(Max2, stored.LineAmount);
        var documentBefore = await db.TradeDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id);

        foreach (var bad in new[]
        {
            Item(quantity: Max4 + 1m, unitPrice: 1m),           // 数量越界（18,4）
            Item(quantity: 1.00001m, unitPrice: 1m),            // 数量超精度
            Item(quantity: 1m, unitPrice: Max4 + 1m),           // 单价越界
            Item(quantity: Max4, unitPrice: 101m),              // 行金额越界（18,2）
            Item(quantity: decimal.MaxValue, unitPrice: 2m),    // CLR 乘法溢出
        })
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => controller.CreateItem(document.Id, bad));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        }

        Assert.Equal(1, await db.TradeDocumentItems.CountAsync());
        var documentAfter = await db.TradeDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        Assert.Equal(documentBefore.Amount, documentAfter.Amount);
        Assert.Equal(documentBefore.UpdatedAt, documentAfter.UpdatedAt);
    }

    [Fact]
    public async Task 明细行修改_越界拒绝_既有行逐字段不变()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-518-UPD", "商业发票", currency: "EUR");
        var controller = BuildItemController(db);

        var created = AssertOk<TradeDocumentItemDto>(await controller.CreateItem(document.Id,
            Item(quantity: 2m, unitPrice: 3.5m, remark: "原备注")));
        var before = await db.TradeDocumentItems.AsNoTracking().SingleAsync(i => i.Id == created.Id);

        var bad = Item(quantity: Max4 + 1m, unitPrice: 3.5m, remark: "SHOULD_NOT_BE_SAVED");
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.UpdateItem(created.Id, bad));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        var after = await db.TradeDocumentItems.AsNoTracking().SingleAsync(i => i.Id == created.Id);
        Assert.Equal(before.LineNo, after.LineNo);
        Assert.Equal(before.Quantity, after.Quantity);
        Assert.Equal(before.UnitPrice, after.UnitPrice);
        Assert.Equal(before.LineAmount, after.LineAmount);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }


    // ==================== 5. 装箱单：零价格口径与可空重量语义 ====================

    [Fact]
    public async Task 装箱单_零单价与可空重量语义保持_越界重量与单价拒绝()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "PL-518-BOUND", "装箱单", currency: "USD");
        var controller = BuildItemController(db);

        var full = AssertOk<TradeDocumentItemDto>(await controller.CreateItem(document.Id,
            Item(quantity: 12m, unitPrice: 0m, packageCount: 5, netWeight: 1.25m, grossWeight: 1.5m)));
        Assert.Equal(0m, full.UnitPrice);
        Assert.Equal(0m, full.LineAmount);
        Assert.Equal(5, full.PackageCount);
        Assert.Equal(1.25m, full.NetWeight);
        Assert.Equal(1.5m, full.GrossWeight);

        // 未登记的可选箱数 / 净重 / 毛重保持 null（不臆造为 0）。
        var bare = AssertOk<TradeDocumentItemDto>(await controller.CreateItem(document.Id,
            Item(quantity: 3m, unitPrice: 0m)));
        Assert.Equal(0m, bare.UnitPrice);
        Assert.Null(bare.PackageCount);
        Assert.Null(bare.NetWeight);
        Assert.Null(bare.GrossWeight);

        // 非零单价（装箱单不含价格口径）与越界 / 超精度重量一律受控拒绝且零写入。
        foreach (var bad in new[]
        {
            Item(unitPrice: 1m),                                       // 装箱单不含价格口径
            Item(unitPrice: 0m, netWeight: Max4 + 1m),                 // 净重越界（18,4）
            Item(unitPrice: 0m, grossWeight: Max4 + 1m),               // 毛重越界（18,4）
            Item(unitPrice: 0m, netWeight: 1.00001m),                  // 净重超精度
        })
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => controller.CreateItem(document.Id, bad));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        }

        Assert.Equal(2, await db.TradeDocumentItems.CountAsync());
    }

    // ==================== 6. 单证表头金额：按宣告 4 位小数列判定 ====================

    [Fact]
    public async Task 单证表头新增修改_金额按宣告列4位小数判定_拒绝零写入_零改写()
    {
        using var db = TestDbFactory.Create();
        var controller = BuildItemController(db);

        var headerSeq = 0;
        TradeDocument Header(decimal amount) => new()
        {
            DocNo = $"CI-535-HEAD-{++headerSeq:D2}", DocType = "商业发票", Status = "待制作",
            Amount = amount, Currency = "USD", IssueDate = new DateTime(2026, 9, 20),
            CustomerName = "义乌客户（ERP-535）", Copies = 3,
        };

        // 新增：量级越界 / 第 5 位小数 / 「半进位取整后恰好落到上限」一律受控拒绝，零单证写入。
        foreach (var bad in new[] { Max4 + 1m, Max2 + 1m, 1.00001m, Max4 - 0.00005m })
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(Header(bad)));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
            Assert.Contains("DECIMAL(18,4)", ex.Message, StringComparison.Ordinal);
            Assert.Empty(await db.TradeDocuments.AsNoTracking().ToListAsync());
        }

        // 精确 4 位小数上限与 3 / 4 位小数金额放行，并按原值逐位落库 / 读回（不取整、不截断）。
        var boundary = AssertOk<TradeDocument>(await controller.Create(Header(Max4)));
        Assert.Equal(Max4, boundary.Amount);
        Assert.Equal(Max4, (await db.TradeDocuments.AsNoTracking()
            .SingleAsync(d => d.Id == boundary.Id)).Amount);

        var decimals = AssertOk<TradeDocument>(await controller.Create(Header(1234.5678m)));
        Assert.Equal(1234.5678m, decimals.Amount);
        Assert.Equal(1234.5678m, (await db.TradeDocuments.AsNoTracking()
            .SingleAsync(d => d.Id == decimals.Id)).Amount);

        // 修改：量级越界金额受控拒绝，既有单证逐字段不变（含审计时间戳）。
        var before = await db.TradeDocuments.AsNoTracking().SingleAsync(d => d.Id == boundary.Id);
        var edit = Edit(before, Max4 + 1m);
        edit.Remark = "SHOULD_NOT_BE_SAVED";
        var updateEx = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Update(boundary.Id, edit));
        Assert.Equal(ErrorCodes.InvalidParameter, updateEx.Code);
        Assert.Contains("DECIMAL(18,4)", updateEx.Message, StringComparison.Ordinal);

        var after = await db.TradeDocuments.AsNoTracking().SingleAsync(d => d.Id == boundary.Id);
        Assert.Equal(before.Amount, after.Amount);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);

        // 4 位小数金额修改放行（既有生命周期判定与审计口径不变）。
        var allowed = Edit(before, Max4 - 0.1111m);
        allowed.Remark = "4 位小数金额修改";
        var updated = AssertOk<TradeDocument>(await controller.Update(boundary.Id, allowed));
        Assert.Equal(Max4 - 0.1111m, updated.Amount);
        Assert.Equal("4 位小数金额修改", updated.Remark);
    }

    // ==================== 7. 只读聚合展示：不施加单列存储上界 ====================

    [Fact]
    public async Task 清单读取_只读合计不被单列上界限制()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-518-SUM", "商业发票", currency: "CNY", amount: Max2);
        var controller = BuildItemController(db);

        // 两行各自恰好等于单列上界（合法），合计合法地超过单个 DECIMAL(18,2) 列容量。
        db.TradeDocumentItems.Add(new TradeDocumentItem
        {
            TradeDocumentId = document.Id, LineNo = 1, ProductCode = "P1", ProductNameCn = "毛巾",
            Quantity = 1m, UnitPrice = Max2, LineAmount = Max2, Currency = "CNY",
            NetWeight = Max4, GrossWeight = Max4,
        });
        db.TradeDocumentItems.Add(new TradeDocumentItem
        {
            TradeDocumentId = document.Id, LineNo = 2, ProductCode = "P2", ProductNameCn = "浴巾",
            Quantity = 1m, UnitPrice = Max2, LineAmount = Max2, Currency = "CNY",
            NetWeight = Max4, GrossWeight = Max4,
        });
        db.SaveChanges();

        var list = AssertOk<TradeDocumentItemListDto>(await controller.GetItems(document.Id));
        Assert.Equal(2, list.LineCount);
        Assert.Equal(Max2 + Max2, list.LineAmountTotal);
        Assert.Equal(Max4 + Max4, list.NetWeightTotal);
        Assert.Equal(Max4 + Max4, list.GrossWeightTotal);
        Assert.False(list.Truncated);
    }


    // ==================== 8. 来源快照精度规则（与明细行同源，fail closed） ====================

    [Fact]
    public void 来源快照精度规则_与明细行同源_越界返回空由调用方拒绝()
    {
        Assert.Equal(Max4, TradeDocumentLineSnapshotRules.NormalizeQuantity(Max4));
        Assert.Null(TradeDocumentLineSnapshotRules.NormalizeQuantity(Max4 + 1m));   // 越界：不截断，交调用方拒绝
        Assert.Equal(Max4, TradeDocumentLineSnapshotRules.NormalizeUnitPrice(Max4));
        Assert.Null(TradeDocumentLineSnapshotRules.NormalizeUnitPrice(Max4 + 1m));
        Assert.Equal(0m, TradeDocumentLineSnapshotRules.NormalizeUnitPrice(0m));     // 装箱单零价格口径

        Assert.True(TradeDocumentLineSnapshotRules.IsGrossWeightRepresentable(null));    // 未登记 = 留空，可表示
        Assert.True(TradeDocumentLineSnapshotRules.IsGrossWeightRepresentable(Max4));
        Assert.False(TradeDocumentLineSnapshotRules.IsGrossWeightRepresentable(Max4 + 1m));
        Assert.Equal(Max4, TradeDocumentLineSnapshotRules.GrossWeightOf(Max4));
        Assert.Null(TradeDocumentLineSnapshotRules.GrossWeightOf(0m));                   // 来源 0 = 未登记

        // 已构造行快照的持久化标量整批判定（可空重量保持 null）。
        TradeDocumentItemRules.EnsurePersistedLineRepresentable(new TradeDocumentItem
        {
            Quantity = 1m, UnitPrice = 0m, LineAmount = 0m, NetWeight = null, GrossWeight = null,
        });
        var ex = Assert.Throws<BusinessException>(() => TradeDocumentItemRules.EnsurePersistedLineRepresentable(
            new TradeDocumentItem { Quantity = 1m, UnitPrice = 0m, LineAmount = Max2 + 1m }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 9. 来源生成：整批原子拒绝 ====================

    [Fact]
    public async Task 销售订单生成_来源明细越界_整批原子拒绝且零单证零明细()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-518-A");
        var product = SeedProduct(db, "P-518-A");
        var order = SeedSalesOrder(db, "SO-518-A", customer.Id, product.Id, totalAmount: 200m,
            details: new[] { (1m, 1m), (Max4 + 1m, 10m) });
        var controller = BuildSalesOrderController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.GenerateTradeDocuments(order.Id, null));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        Assert.Empty(await db.TradeDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await db.TradeDocumentItems.AsNoTracking().ToListAsync());
        // 来源单据与明细未被改写。
        var storedOrder = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(200m, storedOrder.TotalAmount);
        Assert.Equal(2, await db.SalesOrderDetails.AsNoTracking().CountAsync(d => d.SalesOrderId == order.Id));
    }

    [Fact]
    public async Task 销售订单生成_表头金额越界_整批拒绝且零单证()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-518-B");
        var product = SeedProduct(db, "P-518-B");
        var order = SeedSalesOrder(db, "SO-518-B", customer.Id, product.Id, totalAmount: Max4 + 1m,
            details: new[] { (1m, 1m) });
        var controller = BuildSalesOrderController(db);

        // 量级越界（超过宣告列 DECIMAL(18,4) 上界）：整批拒绝，错误文案给出宣告列精度。
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.GenerateTradeDocuments(order.Id, null));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,4)", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await db.TradeDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await db.TradeDocumentItems.AsNoTracking().ToListAsync());

        // 第 5 位小数的表头金额（宣告列只有 4 位小数）同样整批拒绝且零写入。
        var order5 = SeedSalesOrder(db, "SO-535-B5", customer.Id, product.Id, totalAmount: 1.00001m,
            details: new[] { (1m, 1m) });
        var ex5 = await Assert.ThrowsAsync<BusinessException>(
            () => controller.GenerateTradeDocuments(order5.Id, null));
        Assert.Equal(ErrorCodes.InvalidParameter, ex5.Code);
        Assert.Contains("DECIMAL(18,4)", ex5.Message, StringComparison.Ordinal);
        Assert.Empty(await db.TradeDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await db.TradeDocumentItems.AsNoTracking().ToListAsync());
    }


    [Fact]
    public async Task 装柜清单生成_来源毛重越界_整批拒绝且零单证()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-518-C");
        var product = SeedProduct(db, "P-518-C");
        var list = SeedLoadingList(db, "LL-518-C", customer.Id, product.Id,
            quantity: 10m, cartons: 12m, weight: Max4 + 1m);
        var controller = BuildLoadingListController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.GenerateTradeDocuments(list.Id, null));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("毛重", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await db.TradeDocuments.AsNoTracking().ToListAsync());
        Assert.Empty(await db.TradeDocumentItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 销售订单生成_合法来源_明细与表头金额原样落库_来源链接保持()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-518-D");
        var product = SeedProduct(db, "P-518-D");
        var order = SeedSalesOrder(db, "SO-518-D", customer.Id, product.Id, totalAmount: 7.5m,
            details: new[] { (3m, 2.5m) });
        var controller = BuildSalesOrderController(db);

        var result = AssertOk<TradeDocGenerateResult>(
            await controller.GenerateTradeDocuments(order.Id, null));
        Assert.Equal(new[] { "商业发票", "装箱单" }, result.Documents.Select(d => d.DocType).ToArray());
        Assert.Equal(2, result.TotalLineCount);

        var ci = await db.TradeDocuments.AsNoTracking().SingleAsync(d => d.DocType == "商业发票");
        Assert.Equal(order.OrderNo, ci.SalesOrderNo);        // 显式来源链接保持
        Assert.Equal(7.5m, ci.Amount);
        var ciLine = await db.TradeDocumentItems.AsNoTracking()
            .SingleAsync(i => i.TradeDocumentId == ci.Id);
        Assert.Equal(3m, ciLine.Quantity);
        Assert.Equal(2.5m, ciLine.UnitPrice);
        Assert.Equal(7.5m, ciLine.LineAmount);

        var pl = await db.TradeDocuments.AsNoTracking().SingleAsync(d => d.DocType == "装箱单");
        var plLine = await db.TradeDocumentItems.AsNoTracking()
            .SingleAsync(i => i.TradeDocumentId == pl.Id);
        Assert.Equal(0m, plLine.UnitPrice);                  // 装箱单零价格口径
        Assert.Equal(0m, plLine.LineAmount);
        Assert.Null(plLine.PackageCount);                    // 来源未提供：留空
        Assert.Null(plLine.NetWeight);
        Assert.Null(plLine.GrossWeight);
    }

    // ==================== 10. 源码 / 文档契约 ====================

    [Fact]
    public void 源码契约_数值护栏先于编号预约与任何写入()
    {
        var generation = ReadSource("ERP.Api/Controllers/TradeDocumentGeneration.cs");
        Assert.Contains("EnsurePersistedBatchRepresentable", generation);

        var buildStart = generation.IndexOf(
            "private static async Task<(TradeDocument Document, TradeDocumentLineSnapshotResult Lines)> BuildDocumentAsync",
            StringComparison.Ordinal);
        var buildEnd = generation.IndexOf("private static void EnsureLineBound", StringComparison.Ordinal);
        Assert.True(buildStart >= 0 && buildEnd > buildStart);
        var build = generation[buildStart..buildEnd];
        Assert.True(build.IndexOf("EnsurePersistedBatchRepresentable", StringComparison.Ordinal)
                    < build.IndexOf("EnsureUniqueDocNoAsync", StringComparison.Ordinal));   // 校验先于编号预约
        Assert.True(build.IndexOf("EnsureUniqueDocNoAsync", StringComparison.Ordinal)
                    < build.IndexOf("db.TradeDocuments.Add", StringComparison.Ordinal));     // 编号先于任何写入

        var controller = ReadSource("ERP.Api/Controllers/TradeDocumentController.cs");
        var createStart = controller.IndexOf(
            "public override async Task<IActionResult> Create", StringComparison.Ordinal);
        var updateStart = controller.IndexOf(
            "public override async Task<IActionResult> Update", StringComparison.Ordinal);
        Assert.True(createStart >= 0 && updateStart > createStart);
        var create = controller[createStart..updateStart];
        Assert.True(create.IndexOf("EnsureHeaderAmountRepresentable", StringComparison.Ordinal)
                    < create.IndexOf("Service.CreateAsync", StringComparison.Ordinal));

        var rules = ReadSource("ERP.Application/Services/TradeDocumentItemRules.cs");
        Assert.Contains("EnsureItemScalarRepresentable", rules);
        Assert.Contains("EnsureLineAmountRepresentable", rules);
        Assert.Contains("EnsureHeaderAmountRepresentable", rules);
        Assert.Contains("SafeMultiply", rules);              // 复用既有受控乘法（不新增第二套）
        Assert.DoesNotContain("SaveChanges", rules);         // 纯判定与计算，不落库
    }

    [Fact]
    public void 规则与文档同源_实际精度口径齐备()
    {
        Assert.Contains("DECIMAL(18,4)", TradeDocumentItemRules.RuleText, StringComparison.Ordinal);
        Assert.Contains("DECIMAL(18,2)", TradeDocumentItemRules.RuleText, StringComparison.Ordinal);

        var doc = File.ReadAllText(
            Path.Combine(RepoRoot(), "docs", "trade-document-persisted-numeric-bounds.md"));
        Assert.Contains("DECIMAL(18,4)", doc, StringComparison.Ordinal);
        Assert.Contains("DECIMAL(18,2)", doc, StringComparison.Ordinal);
        Assert.Contains("EnsurePersistedBatchRepresentable", doc, StringComparison.Ordinal);
        Assert.Contains("CurrencyAmountRules", doc, StringComparison.Ordinal);
    }

}

