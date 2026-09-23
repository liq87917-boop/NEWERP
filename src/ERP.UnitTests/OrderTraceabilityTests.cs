using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-008 订单追溯字段单元测试：
/// 1) 销售订单外贸合同 / 运输 / 来源追溯字段的创建、回读、修改与安全默认值；
/// 2) 采购订单归属客户 / 归属销售订单 / 采购执行与结算字段的创建、回读与修改；
/// 3) 校验规则（佣金比例、税率）、打印数据、Excel 导出路径与前端报文反序列化契约。
/// </summary>
public class OrderTraceabilityTests
{
    // ==================== 销售订单：新字段往返 ====================

    [Fact]
    public async Task 销售订单_新字段完整持久化_GetById可原样读回()
    {
        using var db = TestDbFactory.Create();
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));

        var so = new SalesOrder
        {
            OrderDate = DateTime.Today,
            CustomerId = 999999L,
            SalesmanId = 888888L,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            PaymentTerms = "T/T 30% deposit",
            DeliveryDate = DateTime.Today.AddDays(30),
            ShippingMethod = "海运",
            CustomerPoNo = "PO-UI-20260923",
            ContractNo = "SC-2026-0001",
            TradeTerms = "FOB",
            DestinationPort = "HAMBURG",
            Consignee = "ACME IMPORT GMBH",
            NotifyParty = "ACME LOGISTICS",
            ShippingMarks = "ACME\nHAMBURG\nC/NO.1-120",
            SourceQuotationId = 77L,
            SourceQuotationNo = "QT2609230001",
            SourcePiId = 88L,
            SourcePiNo = "PI2609230001",
            ExportMode = "0110",
            CommissionRatio = 3.5m,
            BusinessNature = "自营出口",
            SplitShipment = true,
            InspectionRequirement = "客户验货 SGS",
            PackagingRequirement = "12 pcs/箱 彩盒",
            Remark = "ORDER_TRACE_TEST",
            Details = new List<SalesOrderDetail>
            {
                new() { ProductId = 1, ProductName = "P1", Spec = "大", Unit = "PCS", Quantity = 10m, UnitPrice = 100m }
            }
        };

        var result = await ctl.Create(so);
        Assert.IsType<OkObjectResult>(result);

        var id = db.SalesOrders.Single().Id;
        var readResult = await ctl.GetById(id);
        var resp = Assert.IsType<ApiResponse<SalesOrder>>(Assert.IsType<OkObjectResult>(readResult).Value);
        var order = resp.Data!;

        Assert.Equal("PO-UI-20260923", order.CustomerPoNo);
        Assert.Equal("SC-2026-0001", order.ContractNo);
        Assert.Equal("FOB", order.TradeTerms);
        Assert.Equal("HAMBURG", order.DestinationPort);
        Assert.Equal("ACME IMPORT GMBH", order.Consignee);
        Assert.Equal("ACME LOGISTICS", order.NotifyParty);
        Assert.Contains("C/NO.1-120", order.ShippingMarks);
        Assert.Equal(77L, order.SourceQuotationId);
        Assert.Equal("QT2609230001", order.SourceQuotationNo);
        Assert.Equal(88L, order.SourcePiId);
        Assert.Equal("PI2609230001", order.SourcePiNo);
        Assert.Equal("0110", order.ExportMode);
        Assert.Equal(3.5m, order.CommissionRatio);
        Assert.Equal("自营出口", order.BusinessNature);
        Assert.True(order.SplitShipment);
        Assert.Equal("客户验货 SGS", order.InspectionRequirement);
        Assert.Equal("12 pcs/箱 彩盒", order.PackagingRequirement);
        Assert.Equal(1000m, order.TotalAmount);              // 明细金额一并重算
    }

    [Fact]
    public async Task 销售订单_历史单据_新字段安全默认值_可正常打开()
    {
        using var db = TestDbFactory.Create();
        // 模拟 ERP-008 之前的存量单据：只写老字段，新字段不赋值
        var legacy = new SalesOrder
        {
            OrderNo = "SO-LEGACY-1", OrderDate = DateTime.Today, CustomerId = 1,
            TotalAmount = 500m, DepositRatio = 30m, DepositAmount = 150m
        };
        db.SalesOrders.Add(legacy);
        db.SaveChanges();

        var ctl = new SalesOrderController(db, new DocumentNumberService(db));
        var readResult = await ctl.GetById(legacy.Id);
        var order = Assert.IsType<ApiResponse<SalesOrder>>(Assert.IsType<OkObjectResult>(readResult).Value).Data!;

        Assert.Equal(string.Empty, order.CustomerPoNo);
        Assert.Equal(string.Empty, order.ContractNo);
        Assert.Equal(string.Empty, order.DestinationPort);
        Assert.Equal(string.Empty, order.SourcePiNo);
        Assert.Null(order.SourceQuotationId);
        Assert.Equal(0m, order.CommissionRatio);
        Assert.False(order.SplitShipment);
    }

    [Fact]
    public async Task 销售订单_Update_可改写并清空新字段()
    {
        using var db = TestDbFactory.Create();
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));
        await ctl.Create(new SalesOrder
        {
            OrderDate = DateTime.Today, CustomerId = 1, CustomerPoNo = "PO-A", ContractNo = "SC-A",
            TradeTerms = "CIF", SplitShipment = true, CommissionRatio = 5m
        });
        var id = db.SalesOrders.Single().Id;

        await ctl.Update(id, new SalesOrder
        {
            OrderDate = DateTime.Today, CustomerId = 1,
            CustomerPoNo = "PO-B", ContractNo = "SC-B", TradeTerms = "EXW",
            DestinationPort = "JEDDAH", ExportMode = "1039", BusinessNature = "代理出口",
            SourceQuotationNo = "QT-2", SplitShipment = false, CommissionRatio = 0m
        });

        var saved = db.SalesOrders.Single(o => o.Id == id);
        Assert.Equal("PO-B", saved.CustomerPoNo);
        Assert.Equal("SC-B", saved.ContractNo);
        Assert.Equal("EXW", saved.TradeTerms);
        Assert.Equal("JEDDAH", saved.DestinationPort);
        Assert.Equal("1039", saved.ExportMode);
        Assert.Equal("代理出口", saved.BusinessNature);
        Assert.Equal("QT-2", saved.SourceQuotationNo);
        Assert.False(saved.SplitShipment);
        Assert.Equal(0m, saved.CommissionRatio);
    }

    [Fact]
    public async Task 销售订单_佣金比例越界_抛InvalidParameter_且未落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new SalesOrder
        {
            OrderDate = DateTime.Today, CustomerId = 1, CommissionRatio = 120m
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task 销售订单_列表关键字_可命中客户PO号与合同号()
    {
        using var db = TestDbFactory.Create();
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));
        await ctl.Create(new SalesOrder { OrderDate = DateTime.Today, CustomerId = 1, CustomerPoNo = "BUYERPO-77" });
        await ctl.Create(new SalesOrder { OrderDate = DateTime.Today, CustomerId = 1, ContractNo = "SC-KEY-9" });

        var byPo = Assert.IsType<ApiResponse<PagedResult<SalesOrder>>>(
            Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10, Keyword = "BUYERPO-77" }, null)).Value);
        var byContract = Assert.IsType<ApiResponse<PagedResult<SalesOrder>>>(
            Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10, Keyword = "SC-KEY-9" }, null)).Value);

        Assert.Equal(1, byPo.Data!.Total);
        Assert.Equal(1, byContract.Data!.Total);
    }

    [Fact]
    public async Task 销售订单_打印数据_返回主表新字段与明细()
    {
        using var db = TestDbFactory.Create();
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));
        await ctl.Create(new SalesOrder
        {
            OrderDate = DateTime.Today, CustomerId = 1, ContractNo = "SC-PRINT-1", ShippingMarks = "MARKS-PRINT",
            Details = new List<SalesOrderDetail> { new() { ProductId = 1, ProductName = "P1", Quantity = 2m, UnitPrice = 10m } }
        });
        var id = db.SalesOrders.Single().Id;

        var result = await ctl.GetPrint(id);
        var doc = Assert.IsType<ApiResponse<SalesOrder>>(Assert.IsType<OkObjectResult>(result).Value).Data!;

        Assert.Equal("SC-PRINT-1", doc.ContractNo);
        Assert.Equal("MARKS-PRINT", doc.ShippingMarks);
        Assert.Single(doc.Details);
        Assert.Equal(20m, doc.TotalAmount);
    }

    [Fact]
    public async Task 销售订单_Excel导出_返回xlsx字节流()
    {
        using var db = TestDbFactory.Create();
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));
        await ctl.Create(new SalesOrder
        {
            OrderDate = DateTime.Today, CustomerId = 1, CustomerPoNo = "PO-EXCEL", ContractNo = "SC-EXCEL"
        });

        var result = await ctl.ExportExcel(null, null, null, null);
        var file = Assert.IsType<FileContentResult>(result);

        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
        Assert.EndsWith(".xlsx", file.FileDownloadName);
        Assert.True(file.FileContents.Length > 1000, "xlsx 内容不应为空");
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(file.FileContents, 0, 2));   // zip 容器签名
    }

    // ==================== 采购订单：归属与执行字段 ====================

    [Fact]
    public async Task 采购订单_归属客户与采购执行字段_完整持久化并读回()
    {
        using var db = TestDbFactory.Create();
        var ctl = new PurchaseOrderController(db, new DocumentNumberService(db));

        await ctl.Create(new PurchaseOrder
        {
            OrderDate = DateTime.Today, SupplierId = 999999L, BuyerId = 1L,
            Currency = Currency.CNY, ExchangeRate = 1m, PaymentTerms = "月结 30 天",
            DeliveryDate = DateTime.Today.AddDays(20),
            ContractNo = "PC-2026-0001",
            OwningCustomerId = 555L, OwningCustomerName = "ACME IMPORT",
            OwningSalesOrderId = 666L, OwningSalesOrderNo = "SO2609230001",
            AdvanceOnBehalf = true,
            SupplierConfirmedDate = DateTime.Today.AddDays(18),
            TaxRate = 13m, TaxIncluded = true,
            ArrivalProgress = "部分到货", QcStatus = "合格", SettlementProgress = "未结算",
            Remark = "PO_TRACE_TEST",
            Details = new List<PurchaseOrderDetail>
            {
                new() { ProductId = 1, ProductName = "P1", Quantity = 20m, UnitPrice = 50m }
            }
        });

        var id = db.PurchaseOrders.Single().Id;
        var readResult = await ctl.GetById(id);
        var po = Assert.IsType<ApiResponse<PurchaseOrder>>(Assert.IsType<OkObjectResult>(readResult).Value).Data!;

        Assert.Equal("PC-2026-0001", po.ContractNo);
        Assert.Equal(555L, po.OwningCustomerId);
        Assert.Equal("ACME IMPORT", po.OwningCustomerName);
        Assert.Equal(666L, po.OwningSalesOrderId);
        Assert.Equal("SO2609230001", po.OwningSalesOrderNo);
        Assert.True(po.AdvanceOnBehalf);
        Assert.Equal(DateTime.Today.AddDays(18), po.SupplierConfirmedDate);
        Assert.Equal(13m, po.TaxRate);
        Assert.True(po.TaxIncluded);
        Assert.Equal("部分到货", po.ArrivalProgress);
        Assert.Equal("合格", po.QcStatus);
        Assert.Equal("未结算", po.SettlementProgress);
        Assert.Equal(1000m, po.TotalAmount);
    }

    [Fact]
    public async Task 采购订单_Update_可回填归属销售订单与进度()
    {
        using var db = TestDbFactory.Create();
        var ctl = new PurchaseOrderController(db, new DocumentNumberService(db));
        await ctl.Create(new PurchaseOrder { OrderDate = DateTime.Today, SupplierId = 1 });
        var id = db.PurchaseOrders.Single().Id;

        await ctl.Update(id, new PurchaseOrder
        {
            OrderDate = DateTime.Today, SupplierId = 1,
            OwningSalesOrderId = 9L, OwningSalesOrderNo = "SO-LINK-9",
            OwningCustomerName = "LINK CUSTOMER",
            ArrivalProgress = "已到货", QcStatus = "不合格", SettlementProgress = "部分结算",
            AdvanceOnBehalf = true, TaxIncluded = false, TaxRate = 6m
        });

        var saved = db.PurchaseOrders.Single(o => o.Id == id);
        Assert.Equal(9L, saved.OwningSalesOrderId);
        Assert.Equal("SO-LINK-9", saved.OwningSalesOrderNo);
        Assert.Equal("LINK CUSTOMER", saved.OwningCustomerName);
        Assert.Equal("已到货", saved.ArrivalProgress);
        Assert.Equal("不合格", saved.QcStatus);
        Assert.Equal("部分结算", saved.SettlementProgress);
        Assert.True(saved.AdvanceOnBehalf);
        Assert.Equal(6m, saved.TaxRate);
    }

    [Fact]
    public async Task 采购订单_税率越界_抛InvalidParameter_且未落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = new PurchaseOrderController(db, new DocumentNumberService(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new PurchaseOrder
        {
            OrderDate = DateTime.Today, SupplierId = 1, TaxRate = -1m
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task 采购订单_历史单据_新字段安全默认值()
    {
        using var db = TestDbFactory.Create();
        var legacy = new PurchaseOrder
        {
            OrderNo = "PO-LEGACY-1", OrderDate = DateTime.Today, SupplierId = 1, TotalAmount = 800m
        };
        db.PurchaseOrders.Add(legacy);
        db.SaveChanges();

        var ctl = new PurchaseOrderController(db, new DocumentNumberService(db));
        var po = Assert.IsType<ApiResponse<PurchaseOrder>>(
            Assert.IsType<OkObjectResult>(await ctl.GetById(legacy.Id)).Value).Data!;

        Assert.Null(po.OwningCustomerId);
        Assert.Null(po.OwningSalesOrderId);
        Assert.Equal(string.Empty, po.OwningSalesOrderNo);
        Assert.False(po.AdvanceOnBehalf);
        Assert.Null(po.SupplierConfirmedDate);
        Assert.Equal(0m, po.TaxRate);
        Assert.False(po.TaxIncluded);
        Assert.Equal(string.Empty, po.ArrivalProgress);
        Assert.Equal(string.Empty, po.SettlementProgress);
    }

    [Fact]
    public async Task 采购订单_列表关键字_可命中归属销售订单号与合同号()
    {
        using var db = TestDbFactory.Create();
        var ctl = new PurchaseOrderController(db, new DocumentNumberService(db));
        await ctl.Create(new PurchaseOrder { OrderDate = DateTime.Today, SupplierId = 1, OwningSalesOrderNo = "SO-KEY-8" });
        await ctl.Create(new PurchaseOrder { OrderDate = DateTime.Today, SupplierId = 1, ContractNo = "PC-KEY-8" });

        var bySalesOrder = Assert.IsType<ApiResponse<PagedResult<PurchaseOrder>>>(
            Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10, Keyword = "SO-KEY-8" }, null)).Value);
        var byContract = Assert.IsType<ApiResponse<PagedResult<PurchaseOrder>>>(
            Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10, Keyword = "PC-KEY-8" }, null)).Value);

        Assert.Equal(1, bySalesOrder.Data!.Total);
        Assert.Equal(1, byContract.Data!.Total);
    }

    [Fact]
    public async Task 采购订单_打印与Excel导出_均可用()
    {
        using var db = TestDbFactory.Create();
        var ctl = new PurchaseOrderController(db, new DocumentNumberService(db));
        await ctl.Create(new PurchaseOrder
        {
            OrderDate = DateTime.Today, SupplierId = 1, OwningSalesOrderNo = "SO-PRINT-1",
            Details = new List<PurchaseOrderDetail> { new() { ProductId = 1, ProductName = "P1", Quantity = 3m, UnitPrice = 10m } }
        });
        var id = db.PurchaseOrders.Single().Id;

        var print = await ctl.GetPrint(id);
        var doc = Assert.IsType<ApiResponse<PurchaseOrder>>(Assert.IsType<OkObjectResult>(print).Value).Data!;
        Assert.Equal("SO-PRINT-1", doc.OwningSalesOrderNo);
        Assert.Single(doc.Details);

        var file = Assert.IsType<FileContentResult>(await ctl.ExportExcel(null, null, null, null));
        Assert.True(file.FileContents.Length > 1000);
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(file.FileContents, 0, 2));
    }

    // ==================== 前端报文契约（modules-doc.js valueType: 'bool'） ====================

    [Fact]
    public void 订单新增报文_布尔与枚举名_可反序列化为实体()
    {
        // 与 Program.cs AddJsonOptions（JsonStringEnumConverter）保持一致的序列化配置
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());

        var salesJson = """
            {"orderDate":"2026-09-23T00:00:00","customerId":1,"currency":"USD","exchangeRate":7.2,
             "customerPoNo":"PO-JSON","splitShipment":true,"commissionRatio":3}
            """;
        var so = JsonSerializer.Deserialize<SalesOrder>(salesJson, options)!;
        Assert.Equal("PO-JSON", so.CustomerPoNo);
        Assert.True(so.SplitShipment);
        Assert.Equal(Currency.USD, so.Currency);
        Assert.Equal(3m, so.CommissionRatio);

        var purchaseJson = """
            {"orderDate":"2026-09-23T00:00:00","supplierId":1,"currency":"CNY",
             "advanceOnBehalf":true,"taxIncluded":false,"taxRate":13,"arrivalProgress":"已到货"}
            """;
        var po = JsonSerializer.Deserialize<PurchaseOrder>(purchaseJson, options)!;
        Assert.True(po.AdvanceOnBehalf);
        Assert.False(po.TaxIncluded);
        Assert.Equal(13m, po.TaxRate);
        Assert.Equal("已到货", po.ArrivalProgress);
    }
}
