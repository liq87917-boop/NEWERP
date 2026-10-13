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
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-485 规范预装柜单（<c>api/container/pre-loadings</c>）与规范装柜清单（<c>api/container/loading-lists</c>）
/// 的「省略单号」主子表 HTTP 表单录入契约单元测试（内存库 <see cref="TestDbFactory"/>，不连接 SQL Server、不启动 API）。
/// <para>覆盖：<list type="bullet">
/// <item>实体层：<see cref="ContainerPreLoading.PreLoadingNo"/> 与 <see cref="ContainerLoadingList.LoadingListNo"/>
/// 不再显式 <c>[Required]</c>（省略 / 空串照常绑定），长度约束保留；</item>
/// <item>新增入口：省略 / 空串 / 伪造单号一律由服务端权威单号覆盖，主键 / 状态（待提交草稿）/ 删除态 /
/// 时间戳与明细身份（主键 / 归属单据 / 删除态 / 时间戳）全部由服务端覆盖；</item>
/// <item>主子表往返：主表 + 明细一起保存 / 重开 / 修改原样往返；主表单独更新或携带明细的普通编辑
/// <b>不得静默清空</b>行上的显式需求来源 / 出运证据链接；</item>
/// <item>拒绝先于编号与持久化：无身份 / 非法来源 / 无效出运证据一律零写入且<b>零占号</b>；</item>
/// <item>边界：有效保存 / 状态流转绝不创建库存 / 库存流水 / 财务 / 单证记录；</item>
/// <item>接线契约：规范模块声明主子表、表单不提交单号、crud.js 随行回传持久化证据链接。</item>
/// </list></para>
/// <para>真实隔离 SQL / HTTP 的端到端验收见
/// <c>ERP.IntegrationTests/PreLoadingLoadingCanonicalEntrySqlServerTests.cs</c>。</para>
/// </summary>
public class PreLoadingLoadingCanonicalEntryTests
{
    private const long CustomerA = 985001L;
    private const long ProductA = 985101L;
    private const long ProductB = 985102L;

    // ==================== 0. 脚手架 ====================

    private sealed class CountingDocumentNumberService : IDocumentNumberService
    {
        private readonly DocumentType _expected;
        private readonly string _prefix;
        private int _seq;

        public CountingDocumentNumberService(DocumentType expected, string prefix)
        {
            _expected = expected;
            _prefix = prefix;
        }

        /// <summary>已被请求生成的单号次数（用于证明被拒请求绝不消耗单据号）。</summary>
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
        {
            Calls++;
            Assert.Equal(_expected, documentType);
            return Task.FromResult($"{_prefix}{DateTime.Now:yyyyMMdd}{++_seq:D4}");
        }
    }

    private static ContainerPreLoadingController NewPreLoadingController(
        ErpDbContext db, long? userId, IDocumentNumberService? noService = null)
    {
        var ctl = new ContainerPreLoadingController(db, noService ?? new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static ContainerLoadingListController NewLoadingController(
        ErpDbContext db, long? userId, IDocumentNumberService? noService = null)
    {
        var ctl = new ContainerLoadingListController(db, noService ?? new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static object? Anon(object data, string name) => data.GetType().GetProperty(name)?.GetValue(data);

    private static void SeedCustomer(ErpDbContext db, long id, string name)
    {
        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = id, CustomerCode = $"BC-485-{id}", CustomerName = name, Status = 1
        });
        db.SaveChanges();
    }

    private static void SeedProduct(ErpDbContext db, long id, string name, string unit = "PCS")
    {
        db.BaseProducts.Add(new BaseProduct
        {
            Id = id, ProductCode = $"P-485-{id}", ProductName = name, Spec = "规格A", Unit = unit
        });
        db.SaveChanges();
    }

    private static ContainerBooking SeedBooking(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var booking = new ContainerBooking
        {
            BookingNo = no, BookingDate = DateTime.Today, CustomerId = customerId, Status = status
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = no, OrderDate = DateTime.Today, CustomerId = customerId, Status = status
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SalesOrderDetail AddOrderDetail(ErpDbContext db, long salesOrderId, long productId,
        decimal quantity, string unit = "PCS")
    {
        var detail = new SalesOrderDetail
        {
            SalesOrderId = salesOrderId, ProductId = productId, ProductName = $"商品{productId}",
            Unit = unit, Quantity = quantity, UnitPrice = 10m, Amount = quantity * 10m
        };
        db.SalesOrderDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static StockOut SeedStockOut(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Approved, long? salesOrderId = null)
    {
        var stockOut = new StockOut
        {
            StockOutNo = no, StockOutDate = DateTime.Today, CustomerId = customerId,
            Status = status, SalesOrderId = salesOrderId
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        return stockOut;
    }

    private static StockOutDetail AddStockOutDetail(ErpDbContext db, long stockOutId, long productId,
        decimal quantity, string unit = "PCS")
    {
        var detail = new StockOutDetail
        {
            StockOutId = stockOutId, ProductId = productId, ProductName = $"商品{productId}",
            Unit = unit, Quantity = quantity
        };
        db.StockOutDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static ContainerPreLoading NewPreLoading(long? bookingId = null,
        params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
    {
        var entity = new ContainerPreLoading
        {
            LoadingDate = DateTime.Today,
            BookingId = bookingId,
            ContainerNo = "CTN-485",
            SealNo = "SEAL-485",
            Remark = "ERP-485_TEST"
        };
        foreach (var (productId, quantity, sourceDetailId) in lines)
        {
            entity.Details.Add(new ContainerPreLoadingDetail
            {
                ProductId = productId, ProductName = $"商品{productId}", Quantity = quantity,
                Cartons = 1m, Weight = 10m, Volume = 0.5m, SourceSalesOrderDetailId = sourceDetailId
            });
        }
        return entity;
    }

    private static ContainerLoadingList NewLoadingList(long customerId, long? preLoadingId = null,
        params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
    {
        var entity = new ContainerLoadingList
        {
            LoadingDate = DateTime.Today,
            CustomerId = customerId,
            PreLoadingId = preLoadingId,
            ContainerNo = "CTN-485",
            Remark = "ERP-485_TEST"
        };
        foreach (var (productId, quantity, sourceDetailId) in lines)
        {
            entity.Details.Add(new ContainerLoadingDetail
            {
                ProductId = productId, ProductName = $"商品{productId}", Quantity = quantity,
                Cartons = 1m, Weight = 10m, Volume = 0.5m, SourceStockOutDetailId = sourceDetailId
            });
        }
        return entity;
    }

    private static async Task<ContainerPreLoading> ReloadPreLoadingAsync(ErpDbContext db, long id)
    {
        db.ChangeTracker.Clear();
        return await db.ContainerPreLoadings.Include(o => o.Details).AsNoTracking().SingleAsync(o => o.Id == id);
    }

    private static async Task<ContainerLoadingList> ReloadLoadingAsync(ErpDbContext db, long id)
    {
        db.ChangeTracker.Clear();
        return await db.ContainerLoadingLists.Include(o => o.Details).AsNoTracking().SingleAsync(o => o.Id == id);
    }

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await action());
        Assert.Equal(expected, ex.Code);
    }

    private static ApiResponse<T> AssertOkResponse<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var resp = AssertOkResponse<T>(result);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>截取 <c>modules-doc2.js</c> 中某个模块的文本块（从模块键到下一个两空格缩进的 <c>},</c>）。</summary>
    private static string ModuleBlock(string source, string moduleKey)
    {
        var start = source.IndexOf(moduleKey, StringComparison.Ordinal);
        Assert.True(start >= 0, $"缺少模块 {moduleKey}");
        var end = source.IndexOf("\n  },", start, StringComparison.Ordinal);
        Assert.True(end > start, $"模块 {moduleKey} 未闭合");
        return source[start..end];
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }

    // ==================== 1. 实体契约：省略单号 ====================

    [Fact]
    public void Entity_两个规范单据号都不再显式必填_长度约束保留()
    {
        foreach (var property in new[]
                 {
                     typeof(ContainerPreLoading).GetProperty(nameof(ContainerPreLoading.PreLoadingNo))!,
                     typeof(ContainerLoadingList).GetProperty(nameof(ContainerLoadingList.LoadingListNo))!,
                 })
        {
            Assert.Equal(typeof(string), property.PropertyType);
            Assert.False(Attribute.IsDefined(property, typeof(RequiredAttribute), true),
                $"{property.Name} 不得显式必填（否则省略单号会在模型绑定阶段被拒为 HTTP 400）");
            var maxLength = Assert.Single(property
                .GetCustomAttributes(typeof(MaxLengthAttribute), true)
                .Cast<MaxLengthAttribute>());
            Assert.Equal(50, maxLength.Length);
        }
    }

    // ==================== 2. 省略 / 空串 / 伪造单号：服务端权威覆盖 ====================

    [Fact]
    public async Task PreLoading_省略单号_服务端权威单号草稿身份与明细身份()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "本人客户");
        SeedProduct(db, ProductA, "商品A");
        var userId = TestAuth.SeedPrivilegedUser(db);
        var no = new CountingDocumentNumberService(DocumentType.PreLoading, "YZ");
        var ctl = NewPreLoadingController(db, userId, no);

        // 客户端注入伪造值：省略的单号（默认空串）+ 伪造主键 / 状态 / 删除态 / 时间戳 / 明细身份
        var request = NewPreLoading(null, (ProductA, 6m, null));
        request.Id = 987654;
        request.Status = DocumentStatus.Approved;
        request.IsDeleted = true;
        request.UpdatedAt = new DateTime(2000, 1, 1);
        request.CreatedAt = new DateTime(2000, 1, 1);
        request.Details[0].Id = 555;                 // 伪造明细主键
        request.Details[0].IsDeleted = true;         // 伪造明细删除态
        request.Details[0].PreLoadingId = 987654;    // 伪造明细归属
        request.Details[0].UpdatedAt = new DateTime(2000, 1, 1);

        var ok = Assert.IsType<OkObjectResult>(await ctl.Create(request));
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);

        Assert.Equal(1, no.Calls);
        var stored = await ReloadPreLoadingAsync(db, db.ContainerPreLoadings.Single().Id);
        Assert.True(stored.Id > 0);
        Assert.Equal(stored.Id, Assert.IsType<long>(Anon(resp.Data!, "Id")));
        Assert.Equal(stored.PreLoadingNo, Assert.IsType<string>(Anon(resp.Data!, "PreLoadingNo")));
        Assert.StartsWith("YZ", stored.PreLoadingNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(DocumentStatus.Pending, Assert.IsType<DocumentStatus>(Anon(resp.Data!, "Status")));
        Assert.False(stored.IsDeleted);
        Assert.NotEqual(new DateTime(2000, 1, 1), stored.UpdatedAt);
        Assert.True(stored.CreatedAt >= DateTime.Today);

        var detail = Assert.Single(stored.Details);
        Assert.True(detail.Id > 0);
        Assert.NotEqual(555, detail.Id);            // 伪造明细主键被服务端覆盖
        Assert.False(detail.IsDeleted);             // 伪造明细删除态被服务端覆盖
        Assert.Equal(stored.Id, detail.PreLoadingId);
        Assert.Equal(6m, detail.Quantity);
        // 主表汇总按明细权威重算（客户端注入的 totalCartons/totalWeight/totalVolume 不参与持久化）
        Assert.Equal(1m, stored.TotalCartons);
        Assert.Equal(10m, stored.TotalWeight);
        Assert.Equal(0.5m, stored.TotalVolume);
    }

    [Fact]
    public async Task PreLoading_空串与伪造单号_服务端权威覆盖且不落客户端值()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "本人客户");
        SeedProduct(db, ProductA, "商品A");
        var userId = TestAuth.SeedPrivilegedUser(db);
        var no = new CountingDocumentNumberService(DocumentType.PreLoading, "YZ");
        var ctl = NewPreLoadingController(db, userId, no);

        var blank = NewPreLoading(null, (ProductA, 1m, null));
        blank.PreLoadingNo = "   ";
        var forged = NewPreLoading(null, (ProductA, 2m, null));
        forged.PreLoadingNo = "YZ-FORGED-0001";

        Assert.IsType<OkObjectResult>(await ctl.Create(blank));
        Assert.IsType<OkObjectResult>(await ctl.Create(forged));

        Assert.Equal(2, no.Calls);
        var stored = db.ContainerPreLoadings.AsNoTracking().OrderBy(p => p.Id).ToList();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, p => Assert.StartsWith("YZ", p.PreLoadingNo));
        Assert.All(stored, p => Assert.NotEqual("YZ-FORGED-0001", p.PreLoadingNo));
        Assert.Equal(2, stored.Select(p => p.PreLoadingNo).Distinct().Count());
    }

    [Fact]
    public async Task LoadingList_省略单号_服务端权威单号与草稿身份()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "本人客户");
        SeedProduct(db, ProductA, "商品A");
        var userId = TestAuth.SeedPrivilegedUser(db);
        var no = new CountingDocumentNumberService(DocumentType.LoadingList, "ZQ");
        var ctl = NewLoadingController(db, userId, no);

        var request = NewLoadingList(CustomerA, null, (ProductA, 4m, null));
        request.Id = 123456;                          // 伪造主键
        request.Status = DocumentStatus.Cancelled;    // 伪造状态
        request.IsDeleted = true;                     // 伪造删除态
        request.UpdatedAt = new DateTime(2000, 1, 1);
        request.Details[0].Id = 444;                  // 伪造明细主键
        request.Details[0].IsDeleted = true;          // 伪造明细删除态
        request.Details[0].LoadingListId = 123456;    // 伪造明细归属

        var ok = Assert.IsType<OkObjectResult>(await ctl.Create(request));
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);

        Assert.Equal(1, no.Calls);
        var stored = await ReloadLoadingAsync(db, db.ContainerLoadingLists.Single().Id);
        Assert.True(stored.Id > 0);
        Assert.NotEqual(123456, stored.Id);
        Assert.Equal(stored.Id, Assert.IsType<long>(Anon(resp.Data!, "Id")));
        Assert.Equal(stored.LoadingListNo, Assert.IsType<string>(Anon(resp.Data!, "LoadingListNo")));
        Assert.StartsWith("ZQ", stored.LoadingListNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(DocumentStatus.Pending, Assert.IsType<DocumentStatus>(Anon(resp.Data!, "Status")));
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.PreLoadingId);

        var detail = Assert.Single(stored.Details);
        Assert.True(detail.Id > 0);
        Assert.NotEqual(444, detail.Id);
        Assert.False(detail.IsDeleted);
        Assert.Equal(stored.Id, detail.LoadingListId);
        Assert.Null(detail.SourceStockOutDetailId);   // 未链接 = 显式事实，绝不臆造
    }

    // ==================== 3. 拒绝先于编号与持久化 ====================

    [Fact]
    public async Task LoadingList_无效来源链接_拒绝先于编号且零写入()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "本人客户");
        SeedProduct(db, ProductA, "商品A");
        var userId = TestAuth.SeedPrivilegedUser(db);
        var no = new CountingDocumentNumberService(DocumentType.LoadingList, "ZQ");
        var ctl = NewLoadingController(db, userId, no);

        // 非正预装柜来源 Id：显式非法引用，必须在单号预约 / 落库之前 fail closed
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewLoadingList(CustomerA, 0L, (ProductA, 4m, null))));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        Assert.Equal(0, no.Calls);                                  // 被拒请求绝不消耗装柜清单号
        Assert.Empty(db.ContainerLoadingLists);
        Assert.Empty(db.ContainerLoadingDetails);
    }

    [Fact]
    public async Task PreLoading_无效订柜链接_拒绝先于编号且零写入()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "本人客户");
        SeedProduct(db, ProductA, "商品A");
        var userId = TestAuth.SeedPrivilegedUser(db);
        var no = new CountingDocumentNumberService(DocumentType.PreLoading, "YZ");
        var ctl = NewPreLoadingController(db, userId, no);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPreLoading(999999L, (ProductA, 4m, null))));
        Assert.Contains(ex.Code, new[] { ErrorCodes.RuleConflict, ErrorCodes.InvalidParameter, ErrorCodes.Forbidden });

        Assert.Equal(0, no.Calls);                                  // 被拒请求绝不消耗预装柜单号
        Assert.Empty(db.ContainerPreLoadings);
        Assert.Empty(db.ContainerPreLoadingDetails);
    }

    [Fact]
    public async Task Create_无身份_未授权且零占号零写入()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "本人客户");
        SeedProduct(db, ProductA, "商品A");
        var no = new CountingDocumentNumberService(DocumentType.PreLoading, "YZ");

        await AssertCode(ErrorCodes.Unauthorized, () =>
            NewPreLoadingController(db, null, no).Create(NewPreLoading(null, (ProductA, 4m, null))));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.ContainerPreLoadings);
    }

    // ==================== 4. 主子表往返与显式证据保留 ====================

    [Fact]
    public async Task PreLoading_主子表新增重开修改_需求来源随行保留且行身份由服务端拥有()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "本人客户");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-485-01", CustomerA);
        var order = SeedSalesOrder(db, "SO-485-01", CustomerA);
        var sourceDetail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var userId = TestAuth.SeedPrivilegedUser(db);
        var ctl = NewPreLoadingController(db, userId);

        // 主子表一起新增（含显式需求来源证据）：服务端校验后权威编号与落库
        var created = Assert.IsType<OkObjectResult>(
            await ctl.Create(NewPreLoading(booking.Id, (ProductA, 4m, sourceDetail.Id))));
        Assert.Equal(ErrorCodes.Success, Assert.IsType<ApiResponse<object>>(created.Value).Code);
        var stored = await ReloadPreLoadingAsync(db, db.ContainerPreLoadings.Single().Id);
        Assert.Equal(sourceDetail.Id, Assert.Single(stored.Details).SourceSalesOrderDetailId);
        Assert.Equal(booking.Id, stored.BookingId);
        Assert.Equal(0.5m, stored.TotalVolume);   // 汇总由服务端按明细权威重算

        // 主表单独更新（未携带明细）：既有明细与需求来源证据原样保留
        var headerOnly = await ctl.Update(stored.Id, new ContainerPreLoading
        {
            LoadingDate = DateTime.Today,
            BookingId = booking.Id,
            ContainerNo = stored.ContainerNo,
            SealNo = "SEAL-485-UPD",
            Remark = "仅更新主表",
            Details = new List<ContainerPreLoadingDetail>()
        });
        Assert.IsType<OkObjectResult>(headerOnly);
        var afterHeader = await ReloadPreLoadingAsync(db, stored.Id);
        Assert.Equal("SEAL-485-UPD", afterHeader.SealNo);
        Assert.Equal(sourceDetail.Id, Assert.Single(afterHeader.Details).SourceSalesOrderDetailId);

        // 携带明细的普通编辑：随行保留同一需求来源证据，伪造明细身份被服务端覆盖
        var detailEdit = await ctl.Update(stored.Id, new ContainerPreLoading
        {
            LoadingDate = DateTime.Today,
            BookingId = booking.Id,
            ContainerNo = stored.ContainerNo,
            SealNo = "SEAL-485-UPD",
            Details = new List<ContainerPreLoadingDetail>
            {
                new()
                {
                    Id = 777,                       // 伪造明细主键
                    IsDeleted = true,               // 伪造明细删除态
                    ProductId = ProductA, ProductName = "商品A", Quantity = 6m,
                    Cartons = 2m, Weight = 20m, Volume = 1m,
                    SourceSalesOrderDetailId = sourceDetail.Id
                }
            }
        });
        Assert.IsType<OkObjectResult>(detailEdit);
        var afterEdit = await ReloadPreLoadingAsync(db, stored.Id);
        var edited = Assert.Single(afterEdit.Details);
        Assert.NotEqual(777, edited.Id);
        Assert.False(edited.IsDeleted);
        Assert.Equal(stored.Id, edited.PreLoadingId);
        Assert.Equal(6m, edited.Quantity);
        Assert.Equal(sourceDetail.Id, edited.SourceSalesOrderDetailId);

        // 保存后的规范 Id 直接可达既有「需求来源」证据工作台，且证据仍有效
        var line = Assert.Single(AssertOk<List<PreLoadingSalesOrderLinkLineDto>>(
            await ctl.GetSalesOrderLinks(stored.Id)));
        Assert.Equal(sourceDetail.Id, line.SourceSalesOrderDetailId);
        Assert.True(line.SourceAvailable);
        Assert.Equal(order.OrderNo, line.OrderNo);
    }

    [Fact]
    public async Task LoadingList_主子表新增重开修改_出运证据随行保留且行身份由服务端拥有()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "本人客户");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-485-01", CustomerA);
        var sourceDetail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var userId = TestAuth.SeedPrivilegedUser(db);
        var ctl = NewLoadingController(db, userId);

        var created = Assert.IsType<OkObjectResult>(
            await ctl.Create(NewLoadingList(CustomerA, null, (ProductA, 4m, sourceDetail.Id))));
        Assert.Equal(ErrorCodes.Success, Assert.IsType<ApiResponse<object>>(created.Value).Code);
        var stored = await ReloadLoadingAsync(db, db.ContainerLoadingLists.Single().Id);
        Assert.Equal(sourceDetail.Id, Assert.Single(stored.Details).SourceStockOutDetailId);

        var detailEdit = await ctl.Update(stored.Id, new ContainerLoadingList
        {
            LoadingDate = DateTime.Today,
            CustomerId = CustomerA,
            ContainerNo = stored.ContainerNo,
            ShippingMark = "唛头-485",
            Details = new List<ContainerLoadingDetail>
            {
                new()
                {
                    Id = 888,                       // 伪造明细主键
                    IsDeleted = true,               // 伪造明细删除态
                    ProductId = ProductA, ProductName = "商品A", Quantity = 5m,
                    Cartons = 2m, Weight = 20m, Volume = 1m,
                    SourceStockOutDetailId = sourceDetail.Id
                }
            }
        });
        Assert.IsType<OkObjectResult>(detailEdit);
        var edited = Assert.Single((await ReloadLoadingAsync(db, stored.Id)).Details);
        Assert.NotEqual(888, edited.Id);
        Assert.False(edited.IsDeleted);
        Assert.Equal(5m, edited.Quantity);
        Assert.Equal(sourceDetail.Id, edited.SourceStockOutDetailId);

        var line = Assert.Single(AssertOk<List<LoadingStockOutLinkLineDto>>(
            await ctl.GetStockOutLinks(stored.Id)));
        Assert.Equal(sourceDetail.Id, line.SourceStockOutDetailId);
        Assert.True(line.SourceAvailable);
        Assert.Equal(stockOut.Id, line.StockOutId);
    }

    // ==================== 5. 有效生命周期：零下游副作用 ====================

    [Fact]
    public async Task 有效生命周期_提交审核取消_绝不创建库存财务与单证记录()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "本人客户");
        SeedProduct(db, ProductA, "商品A");
        var userId = TestAuth.SeedPrivilegedUser(db);

        var preCtl = NewPreLoadingController(db, userId);
        Assert.IsType<OkObjectResult>(await preCtl.Create(NewPreLoading(null, (ProductA, 4m, null))));
        var preId = db.ContainerPreLoadings.Single().Id;

        Assert.IsType<OkObjectResult>(await preCtl.Submit(preId));
        Assert.Equal(DocumentStatus.Submitted, (await ReloadPreLoadingAsync(db, preId)).Status);
        Assert.IsType<OkObjectResult>(await preCtl.Approve(preId));
        Assert.Equal(DocumentStatus.Approved, (await ReloadPreLoadingAsync(db, preId)).Status);
        Assert.IsType<OkObjectResult>(await preCtl.Cancel(preId));
        Assert.Equal(DocumentStatus.Cancelled, (await ReloadPreLoadingAsync(db, preId)).Status);

        var loadCtl = NewLoadingController(db, userId);
        Assert.IsType<OkObjectResult>(await loadCtl.Create(NewLoadingList(CustomerA, null, (ProductA, 4m, null))));
        var loadId = db.ContainerLoadingLists.Single().Id;

        Assert.IsType<OkObjectResult>(await loadCtl.Submit(loadId));
        Assert.Equal(DocumentStatus.Submitted, (await ReloadLoadingAsync(db, loadId)).Status);
        Assert.IsType<OkObjectResult>(await loadCtl.Approve(loadId));
        Assert.Equal(DocumentStatus.Approved, (await ReloadLoadingAsync(db, loadId)).Status);

        // 装柜单据始终只是物流证据：不创建库存 / 库存流水 / 财务 / 单证记录
        Assert.Empty(db.StockIns);
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.FinanceReceipts);
        Assert.Empty(db.FinanceExpenses);
        Assert.Empty(db.TradeDocuments);
    }

    // ==================== 6. 接线契约：模块 / 表单 / 控制器顺序 ====================

    [Fact]
    public void 接线_两个规范模块声明主子表_表单不提交单号_可空引用不回落为0()
    {
        var modules = ReadSource("src/ERP.Api/wwwroot/js/modules-doc2.js");

        var preLoading = ModuleBlock(modules, "  'pre-loading': {");
        Assert.Contains("api: '/api/container/pre-loadings'", preLoading);
        Assert.Contains("canSubmit: true", preLoading);
        Assert.Contains("detailKey: 'details'", preLoading);
        Assert.Contains("detailFields: PRE_LOADING_DETAIL_FIELDS", preLoading);
        var preFields = preLoading[preLoading.IndexOf("fields: [", StringComparison.Ordinal)..];
        Assert.DoesNotContain("key: 'preLoadingNo'", preFields);     // 表单不提交预装柜单号
        Assert.Contains("type: 'ref', ref: 'booking'", preLoading);   // 可选订柜引用（留空提交 null）

        var loadingList = ModuleBlock(modules, "  'loading-list': {");
        Assert.Contains("api: '/api/container/loading-lists'", loadingList);
        Assert.Contains("canSubmit: true", loadingList);
        Assert.Contains("detailKey: 'details'", loadingList);
        Assert.Contains("detailFields: LOADING_LIST_DETAIL_FIELDS", loadingList);
        var listFields = loadingList[loadingList.IndexOf("fields: [", StringComparison.Ordinal)..];
        Assert.DoesNotContain("key: 'loadingListNo'", listFields);    // 表单不提交装柜清单号
        Assert.Contains("type: 'ref', ref: 'customer'", loadingList);
        Assert.Contains("type: 'ref', ref: 'pre-loading'", loadingList);

        // 明细列只暴露业务列：显式证据链接不在列里（只能由工作台维护），也绝不写死为 0
        Assert.Contains("const PRE_LOADING_DETAIL_FIELDS", modules);
        Assert.Contains("const LOADING_LIST_DETAIL_FIELDS", modules);
        Assert.DoesNotContain("sourceSalesOrderDetailId", preLoading);
        Assert.DoesNotContain("sourceStockOutDetailId", loadingList);
        Assert.DoesNotMatch(new Regex(@"/api/v2/bills"), modules);    // 绝不调用旧存储过程族
    }

    [Fact]
    public void 接线_控制器校验严格先于单号预约且服务端拥有明细身份()
    {
        var preLoading = ReadSource("src/ERP.Api/Controllers/ContainerPreLoadingController.cs");
        var preValidate = preLoading.IndexOf("PreLoadingSalesOrderLinkRules.ValidateLinksAsync(Db, entity, CurrentUserId());", StringComparison.Ordinal);
        var preNumber = preLoading.IndexOf("GenerateAsync(DocumentType.PreLoading)", StringComparison.Ordinal);
        Assert.True(preValidate >= 0 && preNumber > preValidate,
            "预装柜单号必须在全部校验之后才预约");
        Assert.Contains("ApplyServerOwnedDetailIdentity(entity.Details, 0, now);", preLoading);

        var loading = ReadSource("src/ERP.Api/Controllers/ContainerLoadingListController.cs");
        var listValidate = loading.IndexOf("LoadingStockOutLinkRules.ValidateLinksAsync(Db, entity, CurrentUserId());", StringComparison.Ordinal);
        var listNumber = loading.IndexOf("GenerateAsync(DocumentType.LoadingList)", StringComparison.Ordinal);
        Assert.True(listValidate >= 0 && listNumber > listValidate,
            "装柜清单号必须在全部校验（含显式出运证据链接）之后才预约");
        Assert.Contains("ApplyServerOwnedDetailIdentity(entity.Details, 0, now);", loading);
    }
}
