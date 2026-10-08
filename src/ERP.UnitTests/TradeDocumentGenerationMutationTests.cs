using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-397「来源单据 → 单证」生成的**确定性来源行锁 + 原子事务 + 锁内权威复核**单元测试。
/// <list type="number">
/// <item><b>纯判定</b>：确定性来源行锁键 / 单证生成键、来源行锁语句（与既有取消 / 写路由同一把锁）、
/// 来源作废 / 身份不一致 / 陈旧金额 / 陈旧数量 / 单位不符的 fail closed 判定、权威数量与单位集合事实。</item>
/// <item><b>内存库行为</b>：控制器生成入口走 <c>GenerateAtomicAsync</c>（锁内权威重读 → 重复检测 →
/// 编号预约 → 表头与明细行写入）；来源已作废 / 已删除 / 重复生成 / 越界拒绝都不落任何单证与明细行；
/// 编号冲突确定性回退；不相关来源仍可生成；失败注入下异常向上传播。</item>
/// <item><b>接线契约</b>：两个来源控制器都调用原子生成入口并在锁内重读来源；生成护栏只做锁定 / 事务 / 判定，
/// 不新增菜单 / 角色 / 用户授权，不匿名放行，不改 schema。</item>
/// </list>
/// <para>口径：全部使用内存库（<see cref="TestDbFactory"/>），不连接 SQL Server、不执行任何 SQL / 部署脚本；
/// 真实来源行锁竞态由 <c>ERP.IntegrationTests/TradeDocumentGenerationMutationSqlServerTests.cs</c>
/// 在受控 localdb 上验证。</para>
/// </summary>
public class TradeDocumentGenerationMutationTests
{
    private const string 商业发票 = "商业发票";
    private const string 装箱单 = "装箱单";

    // ==================== 0. 脚手架 ====================

    private static SalesOrderController NewSalesOrderController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static ContainerLoadingListController NewLoadingListController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static BaseCustomer SeedCustomer(ErpDbContext db, string currency = "EUR")
    {
        var customer = new BaseCustomer
        {
            CustomerCode = "C-E397-1", CustomerName = "义乌客户（ERP-397）", Currency = currency,
            DestinationPort = "ROTTERDAM", Status = ContainerLoadingParticipantRules.ActiveStatus,
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, string code, string unit = "PCS")
    {
        var product = new BaseProduct { ProductCode = code, ProductName = code, Unit = unit };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    /// <summary>销售订单：金额 200、一行明细（数量 10 / 单价 20 / 单位 箱），状态默认已审核。</summary>
    private static SalesOrder SeedSalesOrder(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Approved, long productId = 0)
    {
        var order = new SalesOrder
        {
            OrderNo = no, OrderDate = DateTime.Today, CustomerId = customerId,
            Currency = Currency.USD, TotalAmount = 200m, Status = status,
            DestinationPort = "HAMBURG",
            Details = new List<SalesOrderDetail>
            {
                new()
                {
                    ProductId = productId, ProductName = "毛巾", Spec = "70x140", Unit = "箱",
                    Quantity = 10m, UnitPrice = 20m, Amount = 200m,
                }
            }
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static ContainerLoadingList SeedLoadingList(ErpDbContext db, string no, long customerId,
        string containerNo = "CTN-E397", DocumentStatus status = DocumentStatus.Approved, long productId = 0)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no, LoadingDate = DateTime.Today, ContainerNo = containerNo,
            CustomerId = customerId, Status = status, TotalCartons = 12m, TotalWeight = 100.5m,
            Details = new List<ContainerLoadingDetail>
            {
                new()
                {
                    ProductId = productId, ProductName = "毛巾", Quantity = 10m,
                    Cartons = 12m, Weight = 100.5m,
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
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        Assert.NotNull(response.Data);
        return response.Data!;
    }

    private static async Task<BusinessException> CodeOfAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    /// <summary>构造锁内权威快照（用于直接驱动 <c>GenerateAtomicAsync</c> 的判定分支）。</summary>
    private static TradeDocumentGenerationSource Source(string sourceType, long sourceId, string sourceNo,
        long? customerId, DocumentStatus status, decimal? expectedAmount = null,
        Func<string, TradeDocument>? buildDraft = null,
        Func<string, TradeDocumentLineSnapshotResult>? buildLines = null)
        => new()
        {
            SourceType = sourceType, SourceId = sourceId, SourceNo = sourceNo,
            SalesOrderNo = sourceType == TradeDocumentGenerationMutationRules.SalesOrderSourceType ? sourceNo : null,
            LoadingListNo = sourceType == TradeDocumentGenerationMutationRules.LoadingListSourceType ? sourceNo : null,
            CustomerId = customerId, Status = status, ExpectedAmount = expectedAmount,
            BuildDraft = buildDraft ?? (docType => new TradeDocument
            {
                DocNo = $"X-{sourceNo}", DocType = docType, CustomerId = customerId is > 0 ? customerId : null,
                Amount = expectedAmount ?? 0m,
            }),
            BuildLines = buildLines,
        };

    // ==================== 1. 确定性锁定键与锁语句 ====================

    [Fact]
    public void 确定性锁定键_来源与类型归一化后恒等()
    {
        Assert.Equal("SalesOrder#7",
            TradeDocumentGenerationMutationRules.SourceLockKey(" SalesOrder ", 7));
        Assert.Equal("SalesOrder#7#商业发票",
            TradeDocumentGenerationMutationRules.GenerationKey("SalesOrder", 7, " 商业发票 "));
        Assert.Equal("#7", TradeDocumentGenerationMutationRules.SourceLockKey(null, 7));
        Assert.NotEqual(
            TradeDocumentGenerationMutationRules.GenerationKey("SalesOrder", 7, 商业发票),
            TradeDocumentGenerationMutationRules.GenerationKey("SalesOrder", 7, 装箱单));
    }

    [Fact]
    public void 来源行锁语句_与既有取消与装柜写路由同一把锁()
    {
        // 销售订单来源：与销售订单取消 / 出库审核 / 预装柜流程同一常量（同一把来源行锁）
        Assert.Equal(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql,
            TradeDocumentGenerationMutationRules.SalesOrderRowLockSql);
        Assert.Equal(TradeDocumentGenerationMutationRules.SalesOrderRowLockSql,
            TradeDocumentGenerationMutationRules.SourceRowLockSql("SalesOrder"));

        // 装柜清单来源：与装柜清单写路由同一把清单行锁
        Assert.Contains("db_owner.ContainerLoadingLists", TradeDocumentGenerationMutationRules.LoadingListRowLockSql);
        Assert.Contains("UPDLOCK, HOLDLOCK", TradeDocumentGenerationMutationRules.LoadingListRowLockSql);

        // 未知来源类型在任何写入之前 fail closed
        var ex = Assert.Throws<BusinessException>(
            () => TradeDocumentGenerationMutationRules.SourceRowLockSql("Unknown"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void 来源类型常量_与既有生成口径同源()
    {
        Assert.Equal(TradeDocumentGeneration.SalesOrderSourceType,
            TradeDocumentGenerationMutationRules.SalesOrderSourceType);
        Assert.Equal(TradeDocumentGeneration.LoadingListSourceType,
            TradeDocumentGenerationMutationRules.LoadingListSourceType);
        Assert.Equal(8, TradeDocumentMutationRules.RowLockRetryAttempts);
    }

    [Fact]
    public void 来源作废文案_与既有生成守卫口径一致()
    {
        Assert.Equal("已作废的销售订单不能生成单证",
            TradeDocumentGenerationMutationRules.SourceCancelledText(
                TradeDocumentGenerationMutationRules.SalesOrderSourceType));
        Assert.Equal("已作废的装柜清单不能生成单证",
            TradeDocumentGenerationMutationRules.SourceCancelledText(
                TradeDocumentGenerationMutationRules.LoadingListSourceType));
    }

    // ==================== 2. 锁内权威复核（纯判定） ====================

    [Fact]
    public void 锁内复核_来源身份不一致或已作废都fail_closed()
    {
        var source = Source(TradeDocumentGenerationMutationRules.SalesOrderSourceType, 5, "SO-X", 1L,
            DocumentStatus.Approved);

        // 身份一致 + 未作废：放行
        TradeDocumentGenerationMutationRules.EnsureSourceStable(source, "SalesOrder", 5);

        // 来源 Id 不一致（绝不用另一个来源的权威快照生成）
        var mismatchId = Assert.Throws<BusinessException>(
            () => TradeDocumentGenerationMutationRules.EnsureSourceStable(source, "SalesOrder", 6));
        Assert.Equal(ErrorCodes.NotFound, mismatchId.Code);
        Assert.Contains("不一致", mismatchId.Message);

        var mismatchType = Assert.Throws<BusinessException>(
            () => TradeDocumentGenerationMutationRules.EnsureSourceStable(source, "LoadingList", 5));
        Assert.Equal(ErrorCodes.NotFound, mismatchType.Code);

        // 锁内权威状态为已作废：拒绝（与预检同文案）
        var cancelled = Source(TradeDocumentGenerationMutationRules.SalesOrderSourceType, 5, "SO-X", 1L,
            DocumentStatus.Cancelled);
        var ex = Assert.Throws<BusinessException>(
            () => TradeDocumentGenerationMutationRules.EnsureSourceStable(cancelled, "SalesOrder", 5));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已作废的销售订单", ex.Message);
    }

    [Fact]
    public void 锁内复核_来源客户越界fail_closed且空范围保持内部口径()
    {
        var source = Source(TradeDocumentGenerationMutationRules.SalesOrderSourceType, 5, "SO-X", 9L,
            DocumentStatus.Approved);

        // 进程内调用（null 范围）：保持既有内部口径
        TradeDocumentGenerationMutationRules.EnsureSourceScopeAllowed(null, source);

        var foreign = new SalespersonDataScope
        {
            IsPrivileged = false, SalesmanId = 1, AllowedCustomerIds = new HashSet<long> { 100L },
        };
        var ex = Assert.Throws<BusinessException>(
            () => TradeDocumentGenerationMutationRules.EnsureSourceScopeAllowed(foreign, source));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        var own = new SalespersonDataScope
        {
            IsPrivileged = false, SalesmanId = 1, AllowedCustomerIds = new HashSet<long> { 9L },
        };
        TradeDocumentGenerationMutationRules.EnsureSourceScopeAllowed(own, source);
    }

    [Fact]
    public void 锁内复核_表头客户与金额必须来自权威重读()
    {
        var source = Source(TradeDocumentGenerationMutationRules.SalesOrderSourceType, 5, "SO-X", 9L,
            DocumentStatus.Approved, expectedAmount: 200m);

        TradeDocumentGenerationMutationRules.EnsureDraftMatchesSource(source,
            new TradeDocument { CustomerId = 9L, Amount = 200m });

        var staleCustomer = Assert.Throws<BusinessException>(() =>
            TradeDocumentGenerationMutationRules.EnsureDraftMatchesSource(source,
                new TradeDocument { CustomerId = 8L, Amount = 200m }));
        Assert.Equal(ErrorCodes.RuleConflict, staleCustomer.Code);
        Assert.Contains("并发改写", staleCustomer.Message);

        var staleAmount = Assert.Throws<BusinessException>(() =>
            TradeDocumentGenerationMutationRules.EnsureDraftMatchesSource(source,
                new TradeDocument { CustomerId = 9L, Amount = 199m }));
        Assert.Equal(ErrorCodes.RuleConflict, staleAmount.Code);
    }

    [Fact]
    public void 锁内复核_明细行数量与单位必须来自权威重读()
    {
        var source = new TradeDocumentGenerationSource
        {
            SourceType = TradeDocumentGenerationMutationRules.SalesOrderSourceType,
            SourceId = 5, SourceNo = "SO-X", CustomerId = 9L, Status = DocumentStatus.Approved,
            ExpectedLineQuantityTotal = 10m,
            AuthoritativeUnits = new HashSet<string>(StringComparer.Ordinal) { "箱", "PCS" },
        };

        TradeDocumentGenerationMutationRules.EnsureLinesMatchSource(source,
            new TradeDocumentLineSnapshotResult
            {
                DocType = 商业发票,
                Lines = new List<TradeDocumentItem> { new() { Quantity = 10m, Unit = "箱" } },
            });

        var staleQuantity = new TradeDocumentLineSnapshotResult
        {
            DocType = 商业发票,
            Lines = new List<TradeDocumentItem> { new() { Quantity = 9m, Unit = "箱" } },
        };
        Assert.Equal(ErrorCodes.RuleConflict, Assert.Throws<BusinessException>(
            () => TradeDocumentGenerationMutationRules.EnsureLinesMatchSource(source, staleQuantity)).Code);

        var foreignUnit = new TradeDocumentLineSnapshotResult
        {
            DocType = 商业发票,
            Lines = new List<TradeDocumentItem> { new() { Quantity = 10m, Unit = "KG" } },
        };
        Assert.Equal(ErrorCodes.RuleConflict, Assert.Throws<BusinessException>(
            () => TradeDocumentGenerationMutationRules.EnsureLinesMatchSource(source, foreignUnit)).Code);

        // 不带明细行的类型（空行集合）：不做数量 / 单位复核
        TradeDocumentGenerationMutationRules.EnsureLinesMatchSource(source,
            new TradeDocumentLineSnapshotResult { DocType = "提单" });
    }

    [Fact]
    public void 权威事实_数量合计与单位集合与明细行快照同口径()
    {
        Assert.Equal(30m, TradeDocumentGenerationMutationRules.LineQuantityTotal(new[] { 10m, 20m }));
        Assert.Equal(0m, TradeDocumentGenerationMutationRules.LineQuantityTotal(new[] { 0m, -5m }));
        Assert.Equal(0m, TradeDocumentGenerationMutationRules.LineQuantityTotal(null));

        var products = new Dictionary<long, BaseProduct>
        {
            [3] = new() { Id = 3, ProductCode = "P3", Unit = "PCS" },
        };
        var units = TradeDocumentGenerationMutationRules.LineUnits(
            new[] { (3L, (string?)"箱"), (0L, (string?)null), (5L, (string?)null) }, products);
        Assert.Equal(2, units.Count);
        Assert.Contains("箱", units);          // 明细自身单位
        Assert.Contains("PCS", units);         // 引用商品资料单位
    }


    // ==================== 3. 原子生成行为（内存库；真实行锁竞态见 SQL 集成测试） ====================

    [Fact]
    public async Task 销售订单原子生成_控制器走锁内重读_表头与明细行完整()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-E397-1", "PCS");
        var order = SeedSalesOrder(db, "SO-E397-1", customer.Id, productId: product.Id);

        var result = AssertOk<TradeDocGenerateResult>(
            await NewSalesOrderController(db).GenerateTradeDocuments(order.Id, null));

        Assert.Equal(new[] { 商业发票, 装箱单 }, result.Documents.Select(d => d.DocType).ToArray());
        Assert.Equal("CI-SO-E397-1", result.Documents[0].DocNo);
        Assert.Equal("PL-SO-E397-1", result.Documents[1].DocNo);
        Assert.Equal(2, result.TotalLineCount);

        var invoice = db.TradeDocuments.Single(d => d.DocType == 商业发票);
        Assert.Equal(customer.Id, invoice.CustomerId);          // 权威客户来自锁内重读
        Assert.Equal(200m, invoice.Amount);                     // 权威金额来自锁内重读
        Assert.Equal("待制作", invoice.Status);
        Assert.Equal("SO-E397-1", invoice.SalesOrderNo);
        Assert.Equal(1, db.TradeDocumentItems.Count(i => i.TradeDocumentId == invoice.Id));
    }

    [Fact]
    public async Task 装柜清单原子生成_控制器走锁内重读_装箱单行完整()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-E397-2", "PCS");
        var list = SeedLoadingList(db, "ZQ-E397-1", customer.Id, productId: product.Id);

        var result = AssertOk<TradeDocGenerateResult>(
            await NewLoadingListController(db).GenerateTradeDocuments(list.Id, null));

        Assert.Equal(new[] { 装箱单 }, result.Documents.Select(d => d.DocType).ToArray());
        Assert.Equal("PL-ZQ-E397-1", result.Documents[0].DocNo);

        var packing = db.TradeDocuments.Single();
        Assert.Equal("CTN-E397", packing.RefNo);                // 来源柜号留痕
        Assert.Equal(customer.Id, packing.CustomerId);
        Assert.Equal(0m, packing.Amount);                       // 装柜阶段金额未定
        Assert.Equal(1, db.TradeDocumentItems.Count(i => i.TradeDocumentId == packing.Id));
    }

    [Fact]
    public async Task 原子生成_未知单证类型_在加锁与写入之前拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-E397-T", customer.Id);

        var ex = await CodeOfAsync(ErrorCodes.InvalidParameter, () =>
            TradeDocumentGeneration.GenerateAtomicAsync(db,
                TradeDocumentGeneration.SalesOrderSourceType, order.Id, new List<string> { "空运单" },
                ct => Task.FromResult<TradeDocumentGenerationSource?>(Source(
                    TradeDocumentGenerationMutationRules.SalesOrderSourceType, order.Id, order.OrderNo,
                    customer.Id, DocumentStatus.Approved))));

        Assert.Contains("不支持的单证类型", ex.Message);
        Assert.Empty(db.TradeDocuments);
    }

    [Fact]
    public async Task 原子生成_锁内重读发现来源已作废_拒绝且零落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-E397-C", customer.Id, DocumentStatus.Cancelled);

        var ex = await CodeOfAsync(ErrorCodes.RuleConflict, () =>
            TradeDocumentGeneration.GenerateAtomicAsync(db,
                TradeDocumentGeneration.SalesOrderSourceType, order.Id, new List<string> { 商业发票 },
                ct => Task.FromResult<TradeDocumentGenerationSource?>(Source(
                    TradeDocumentGenerationMutationRules.SalesOrderSourceType, order.Id, order.OrderNo,
                    customer.Id, DocumentStatus.Cancelled))));

        Assert.Contains("已作废的销售订单", ex.Message);
        Assert.Empty(db.TradeDocuments);
        Assert.Empty(db.TradeDocumentItems);
    }

    [Fact]
    public async Task 原子生成_锁内重读发现来源已删除_拒绝且零落库()
    {
        using var db = TestDbFactory.Create();

        var ex = await CodeOfAsync(ErrorCodes.NotFound, () =>
            TradeDocumentGeneration.GenerateAtomicAsync(db,
                TradeDocumentGeneration.SalesOrderSourceType, 999L, new List<string> { 商业发票 },
                ct => Task.FromResult<TradeDocumentGenerationSource?>(null)));

        Assert.Contains("不存在或已删除", ex.Message);
        Assert.Empty(db.TradeDocuments);
    }

    [Fact]
    public async Task 原子生成_来源客户越界_拒绝且零落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-E397-F", customer.Id);
        var foreign = new SalespersonDataScope
        {
            IsPrivileged = false, SalesmanId = 1,
            AllowedCustomerIds = new HashSet<long> { customer.Id + 1000 },
        };

        var ex = await CodeOfAsync(ErrorCodes.Forbidden, () =>
            TradeDocumentGeneration.GenerateAtomicAsync(db,
                TradeDocumentGeneration.SalesOrderSourceType, order.Id, new List<string> { 商业发票 },
                ct => Task.FromResult<TradeDocumentGenerationSource?>(Source(
                    TradeDocumentGenerationMutationRules.SalesOrderSourceType, order.Id, order.OrderNo,
                    customer.Id, DocumentStatus.Approved, 200m)),
                targetScope: foreign));

        Assert.Contains("客户数据范围", ex.Message);
        Assert.Empty(db.TradeDocuments);
    }


    [Fact]
    public async Task 原子生成_重复生成_第二次拒绝且不新增()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-E397-D", customer.Id);
        var controller = NewSalesOrderController(db);
        var request = new TradeDocGenerateRequest { DocTypes = new List<string> { 商业发票 } };

        await controller.GenerateTradeDocuments(order.Id, request);

        var ex = await CodeOfAsync(ErrorCodes.RuleConflict,
            () => controller.GenerateTradeDocuments(order.Id, request));
        Assert.Contains("不能重复生成", ex.Message);
        Assert.Contains("CI-SO-E397-D", ex.Message);
        Assert.Single(db.TradeDocuments);

        // 部分重复（已生成 + 未生成）同样整体拒绝，绝不产生半成品数据
        var mixed = new TradeDocGenerateRequest { DocTypes = new List<string> { 商业发票, 装箱单 } };
        await CodeOfAsync(ErrorCodes.RuleConflict, () => controller.GenerateTradeDocuments(order.Id, mixed));
        Assert.Single(db.TradeDocuments);
    }

    [Fact]
    public async Task 原子生成_编号冲突_锁内预约确定性回退且不覆盖既有单证()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-E397-N", customer.Id);
        // 历史人工单证占用了确定性基础编号（来源字段与本单无关 → 不触发重复生成守卫）
        db.TradeDocuments.Add(new TradeDocument
        {
            DocNo = "CI-SO-E397-N", DocType = 商业发票, SalesOrderNo = "OTHER-SO", Status = "待制作",
        });
        db.SaveChanges();

        var result = AssertOk<TradeDocGenerateResult>(await NewSalesOrderController(db)
            .GenerateTradeDocuments(order.Id,
                new TradeDocGenerateRequest { DocTypes = new List<string> { 商业发票 } }));

        Assert.Equal("CI-SO-E397-N-2", result.Documents[0].DocNo);
        Assert.Equal(2, db.TradeDocuments.Count());
        Assert.Single(db.TradeDocuments, d => d.DocNo == "CI-SO-E397-N");
    }

    [Fact]
    public async Task 原子生成_不相关来源仍可正常生成()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var first = SeedSalesOrder(db, "SO-E397-U1", customer.Id);
        var second = SeedSalesOrder(db, "SO-E397-U2", customer.Id);
        var controller = NewSalesOrderController(db);

        await controller.GenerateTradeDocuments(first.Id, null);
        await controller.GenerateTradeDocuments(second.Id, null);

        Assert.Equal(4, db.TradeDocuments.Count());
        Assert.Equal(2, db.TradeDocuments.Count(d => d.SalesOrderNo == "SO-E397-U1"));
        Assert.Equal(2, db.TradeDocuments.Count(d => d.SalesOrderNo == "SO-E397-U2"));
    }

    [Fact]
    public async Task 原子生成_写入失败时异常向上传播且不返回结果()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-E397-W", customer.Id);
        var (proxy, counter) = FailingContext.Wrap(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TradeDocumentGeneration.GenerateAtomicAsync(proxy,
                TradeDocumentGeneration.SalesOrderSourceType, order.Id, new List<string> { 商业发票 },
                ct => Task.FromResult<TradeDocumentGenerationSource?>(Source(
                    TradeDocumentGenerationMutationRules.SalesOrderSourceType, order.Id, order.OrderNo,
                    customer.Id, DocumentStatus.Approved, 200m))));

        Assert.Equal(FailingContext.SaveFailureText, ex.Message);
        Assert.Equal(1, counter.SaveCalls);
        // 内存库无回滚能力（见 TestDbFactory）：失败的写入绝不返回结果，也绝不留下已保存的表头
        Assert.Empty(db.TradeDocuments);
    }


    // ==================== 4. 接线契约（源码级） ====================

    [Fact]
    public void 生成入口_在锁内权威重读后才重复检测编号预约与写入()
    {
        var source = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "TradeDocumentGeneration.cs"));

        Assert.Contains("BeginGenerationTransactionAsync", source);   // 复用既有原子事务（不嵌套）
        Assert.Contains("LockSourceRowAsync", source);                // 先取来源行锁
        Assert.Contains("EnsureSourceStable(source", source);         // 锁内复核来源身份 / 状态
        Assert.Contains("EnsureSourceScopeAllowed(targetScope, source)", source);
        Assert.Contains("EnsureDraftMatchesSource(source", source);   // 表头客户 / 金额必须在锁内复核
        Assert.Contains("EnsureLinesMatchSource(source", source);     // 明细行数量 / 单位必须在锁内复核
        Assert.Contains("DiscardTrackedChanges(db)", source);         // 失败回滚后丢弃半成品变更
        Assert.Contains("await WriteDocumentsAsync(db, created, lineResults, ct)", source);

        // 顺序契约（限定在原子生成入口内）：加锁 → 权威重读 → 重复检测 → 构造 / 编号预约 → 写入
        var methodIndex = source.IndexOf(
            "public static async Task<TradeDocGenerateResult> GenerateAtomicAsync", StringComparison.Ordinal);
        Assert.True(methodIndex >= 0, "必须存在原子生成入口 GenerateAtomicAsync。");
        var atomic = source[methodIndex..];

        var lockIndex = atomic.IndexOf("LockSourceRowAsync", StringComparison.Ordinal);
        var reloadIndex = atomic.IndexOf("await reloadSource(ct)", StringComparison.Ordinal);
        var conflictIndex = atomic.IndexOf("await EnsureNoConflictsAsync", StringComparison.Ordinal);
        var buildIndex = atomic.IndexOf("await BuildDocumentAsync", StringComparison.Ordinal);
        var writeIndex = atomic.IndexOf(
            "await WriteDocumentsAsync(db, created, lineResults, ct)", StringComparison.Ordinal);
        Assert.True(lockIndex >= 0 && reloadIndex > lockIndex, "来源行锁必须先于锁内权威重读。");
        Assert.True(conflictIndex > reloadIndex, "重复生成检测必须在锁内权威重读之后。");
        Assert.True(buildIndex > conflictIndex, "表头 / 明细行构造与编号预约必须在重复检测之后。");
        Assert.True(writeIndex > buildIndex, "写入必须在锁内构造完成之后。");

        // 不新增权限模型、不匿名放行、不改 schema
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("SysRoleMenu", source);
        Assert.DoesNotContain("CREATE TABLE", source);
    }

    [Fact]
    public void 来源控制器_都调用原子生成入口并在锁内重读来源()
    {
        var sales = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "SalesOrderController.cs"));
        Assert.Contains("TradeDocumentGeneration.GenerateAtomicAsync", sales);
        Assert.Contains("ReloadSalesOrderSourceAsync", sales);
        Assert.Contains("TradeDocumentGenerationMutationRules.SourceCancelledText", sales);
        Assert.Contains("EnsureSourceScopeAllowed(scope, order.CustomerId)", sales);

        var loading = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "ContainerLoadingListController.cs"));
        Assert.Contains("TradeDocumentGeneration.GenerateAtomicAsync", loading);
        Assert.Contains("ReloadLoadingListSourceAsync", loading);
        // 同一把清单行锁：控制器内联锁语句必须与规则常量逐字一致（防漂移）
        Assert.Contains($"\"{TradeDocumentGenerationMutationRules.LoadingListRowLockSql}\"", loading);
        Assert.Contains("EnsureSourceScopeAllowed(scope, list.CustomerId)", loading);
    }

    [Fact]
    public void 生成护栏_只做锁定事务与判定_不新增授权不改schema()
    {
        var source = File.ReadAllText(
            RepoFile("src", "ERP.Application", "Services", "TradeDocumentGenerationMutationRules.cs"));

        Assert.Contains("UpdatedAt = DateTime.Now", source);          // 行锁 = 审计时间戳刷新 UPDATE（X 锁）
        Assert.Contains("DbUpdateConcurrencyException", source);      // 并发令牌过期后有界重试
        Assert.Contains("IsRelationalProvider", source);              // 内存库跳过行锁
        Assert.Contains("RowLockRetryAttempts", source);              // 与 ERP-395 同口径的有界重试
        Assert.Contains("LockSalesOrderRowSql", source);              // 与取消共用同一把来源订单行锁
        Assert.Contains("ContainerLoadingLists WITH (UPDLOCK, HOLDLOCK)", source);

        Assert.DoesNotContain("SysRoleMenu", source);
        Assert.DoesNotContain("SysUserRole", source);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("CREATE TABLE", source);
        Assert.DoesNotContain("ALTER TABLE", source);
        Assert.DoesNotContain("SchemaUpgrader", source);
    }

    /// <summary>写入失败注入上下文（每次 <c>SaveChangesAsync</c> 都抛出，用于验证失败向上传播）。</summary>
    private class FailingContext : DispatchProxy
    {
        public const string SaveFailureText = "injected trade document write failure (ERP-397)";

        private IErpDbContext _inner = null!;

        /// <summary><c>SaveChangesAsync</c> 调用次数</summary>
        public int SaveCalls { get; private set; }

        public static (IErpDbContext Proxy, FailingContext Counter) Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, FailingContext>();
            var counter = (FailingContext)(object)proxy;
            counter._inner = inner;
            return (proxy, counter);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                SaveCalls++;
                throw new InvalidOperationException(SaveFailureText);
            }

            return targetMethod.Invoke(_inner, args);
        }
    }

}
