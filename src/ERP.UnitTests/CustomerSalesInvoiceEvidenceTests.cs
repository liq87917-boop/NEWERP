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
/// 客户销项发票证据登记单元测试（ERP-055）。覆盖：发票类型（普票 / 专票 / 出口发票）与发票代码要求、
/// 金额等式与币种精度、有效身份唯一（同一客户 + 类型 + 规范化代码 / 号码）、客户权威资格（存在 / 未删除 / 启用）、
/// 分摊权威资格（订单未删除 / 未取消 / 客户一致 / 币种一致）、分摊金额精度与上限、全额 / 部分 / 未分摊与文案、
/// 登记冻结与登记前复核、作废保留原始值与历史（重复作废拒绝、作废后可重新登记、作废不释放其他记录）、
/// 单证中心商业发票的显式交叉引用（只接受商业发票、只写快照、不读取金额、不改写单证）与分离口径、
/// 台账过滤与分页有界、候选派生（同客户 + 同币种、其他发票已分摊 / 剩余未覆盖）、
/// 非变更边界（销售订单、客户信用状态、收款单、收款引用行、库存、退税、费用、其它发票证据均不被改写），
/// 以及模型 / 幂等结构 / 前端接线契约。全部使用内存库（TestDbFactory），不连接 SQL Server、
/// 不执行任何 SQL / 部署脚本、不调用任何开票 / 税务服务、不做任何浏览器 / UI 验收。
/// </summary>
public class CustomerSalesInvoiceEvidenceTests
{
    // ==================== 0. 测试脚手架 ====================

    private static CustomerSalesInvoiceEvidenceController BuildController(ErpDbContext db) => new(db);

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency = Currency.USD,
        DocumentStatus status = DocumentStatus.Approved, decimal totalAmount = 1000m,
        string contractNo = "", bool deleted = false, int? rawCurrency = null)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Currency = rawCurrency is null ? currency : (Currency)rawCurrency.Value,
            TotalAmount = totalAmount,
            ContractNo = contractNo,
            Status = status,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static TradeDocument SeedTradeDoc(
        ErpDbContext db, string docNo, string docType = "商业发票", string salesOrderNo = "SO-A",
        long? customerId = null, bool deleted = false, decimal amount = 888m)
    {
        var document = new TradeDocument
        {
            DocNo = docNo,
            DocType = docType,
            IssueDate = new DateTime(2026, 9, 5),
            SalesOrderNo = salesOrderNo,
            CustomerId = customerId,
            CustomerName = "义乌进出口",
            Amount = amount,
            Currency = "USD",
            Status = "已制作",
            IsDeleted = deleted
        };
        db.TradeDocuments.Add(document);
        db.SaveChanges();
        return document;
    }

    private static CustomerSalesInvoiceEvidenceSaveDto SaveDto(
        long customerId, string invoiceNumber = "INV-001", string invoiceType = "普票",
        string invoiceCode = "", string currency = "USD", decimal net = 900m, decimal tax = 100m,
        decimal gross = 1000m, string remark = "", long? tradeDocumentId = null,
        string commercialInvoiceReference = "", DateTime? invoiceDate = null)
        => new()
        {
            InvoiceType = invoiceType,
            InvoiceCode = invoiceCode,
            InvoiceNumber = invoiceNumber,
            InvoiceDate = invoiceDate ?? new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Currency = currency,
            NetAmount = net,
            TaxAmount = tax,
            GrossAmount = gross,
            TradeDocumentId = tradeDocumentId,
            CommercialInvoiceReference = commercialInvoiceReference,
            Remark = remark
        };

    private static CustomerSalesInvoiceAllocationSaveRequest AllocationRequest(
        params (long OrderId, decimal Amount)[] lines)
        => new()
        {
            Lines = lines.Select(l => new CustomerSalesInvoiceAllocationSaveDto
            {
                SalesOrderId = l.OrderId,
                AllocatedAmount = l.Amount
            }).ToList()
        };

    /// <summary>断言成功响应并取出数据（业务码必须为 0）</summary>
    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>断言业务异常的错误码（避免只断言消息文案）</summary>
    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static async Task<CustomerSalesInvoiceEvidenceDto> CreateAsync(
        CustomerSalesInvoiceEvidenceController controller,
        CustomerSalesInvoiceEvidenceSaveDto dto)
        => AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.Create(dto));

    private static async Task<CustomerSalesInvoiceEvidenceDto> SaveAllocationsAsync(
        CustomerSalesInvoiceEvidenceController controller, long invoiceId,
        CustomerSalesInvoiceAllocationSaveRequest request)
        => AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.SaveAllocations(invoiceId, request));

    // ==================== 1. 登记：类型 / 身份 / 金额等式 / 客户资格 ====================

    [Fact]
    public async Task 三种发票类型均可登记_专票必须填写发票代码且出口发票可不填()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var ordinary = await CreateAsync(controller, SaveDto(customer.Id, "INV-001", "普票"));
        var special = await CreateAsync(controller,
            SaveDto(customer.Id, "INV-002", "专票", invoiceCode: "033002100211"));
        var export = await CreateAsync(controller, SaveDto(customer.Id, "INV-003", "出口发票"));
        Assert.Equal("普票", ordinary.InvoiceType);
        Assert.Equal("专票", special.InvoiceType);
        Assert.Equal("出口发票", export.InvoiceType);

        // 专票缺发票代码被拒绝
        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => CreateAsync(controller, SaveDto(customer.Id, "INV-004", "专票")));
        Assert.Contains("专票必须填写发票代码", ex.Message);

        // 未知类型被拒绝（不隐式兜底）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => CreateAsync(controller, SaveDto(customer.Id, "INV-005", "电子普票")));

        Assert.Equal(3, await db.CustomerSalesInvoiceEvidences.CountAsync());
    }

    [Fact]
    public async Task 金额等式_净额加税额必须等于含税总额且按币种精度取整()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        // 等式不成立：含税总额 ≠ 净额 + 税额
        var mismatch = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => CreateAsync(controller, SaveDto(customer.Id, "INV-001", net: 900m, tax: 100m, gross: 1001m)));
        Assert.Contains("金额等式不成立", mismatch.Message);

        // 负值拒绝
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => CreateAsync(controller, SaveDto(customer.Id, "INV-002", net: -1m, tax: 1001m, gross: 1000m)));

        // 含税总额必须大于 0（净额 0.004 + 税额 0 取整后含税总额 0）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => CreateAsync(controller, SaveDto(customer.Id, "INV-003", net: 0.004m, tax: 0m, gross: 0.004m)));

        // 0.5 进位（2 位小数）：900.005 → 900.01，100.005 → 100.01，含税总额 1000.02
        var rounded = await CreateAsync(controller,
            SaveDto(customer.Id, "INV-004", net: 900.005m, tax: 100.005m, gross: 1000.02m));
        Assert.Equal(900.01m, rounded.NetAmount);
        Assert.Equal(100.01m, rounded.TaxAmount);
        Assert.Equal(1000.02m, rounded.GrossAmount);
        Assert.Equal(2, rounded.AmountDecimals);

        // JPY（0 位小数）：净额 900 + 税额 100 = 1000
        var jpy = await CreateAsync(controller,
            SaveDto(customer.Id, "INV-005", currency: "JPY", net: 900m, tax: 100m, gross: 1000m));
        Assert.Equal(0, jpy.AmountDecimals);

        // 未知币种拒绝（不做汇率换算）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => CreateAsync(controller, SaveDto(customer.Id, "INV-006", currency: "BTC")));
    }

    [Fact]
    public async Task 客户必须存在且启用_停用或删除客户拒绝新建但历史快照仍可读()
    {
        using var db = TestDbFactory.Create();
        var active = SeedCustomer(db, "C001", "义乌进出口");
        var disabled = SeedCustomer(db, "C002", "停用客户", status: 0);
        var deleted = SeedCustomer(db, "C003", "已删客户", deleted: true);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => CreateAsync(controller, SaveDto(disabled.Id, "INV-101")));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => CreateAsync(controller, SaveDto(deleted.Id, "INV-102")));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => CreateAsync(controller, SaveDto(999999, "INV-103")));

        var invoice = await CreateAsync(controller, SaveDto(active.Id, "INV-104"));
        Assert.Equal("C001", invoice.CustomerCode);
        Assert.Equal("义乌进出口", invoice.CustomerName);
        Assert.True(invoice.CustomerAvailable);

        // 客户随后被停用 / 删除：历史证据照常可读，只是显式标注不可用
        var stored = await db.BaseCustomers.FirstAsync(c => c.Id == active.Id);
        stored.Status = 0;
        await db.SaveChangesAsync();

        var detail = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.GetById(invoice.Id));
        Assert.Equal("义乌进出口", detail.CustomerName);
        Assert.False(detail.CustomerAvailable);
        Assert.Contains("客户已停用", detail.CustomerAvailabilityText);

        stored.IsDeleted = true;
        await db.SaveChangesAsync();
        var afterDelete = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.GetById(invoice.Id));
        Assert.Contains("客户已删除", afterDelete.CustomerAvailabilityText);
    }

    [Fact]
    public async Task 有效身份唯一_同一客户同类型同代码号码重复拒绝_作废后释放身份()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "义乌进出口");
        var customerB = SeedCustomer(db, "C002", "宁波贸易");
        var controller = BuildController(db);

        var first = await CreateAsync(controller,
            SaveDto(customerA.Id, "INV-001", "专票", invoiceCode: "0330021"));

        // 去掉连字符 / 空白后同一身份 → 拒绝（不静默合并）
        var duplicate = await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => CreateAsync(controller, SaveDto(customerA.Id, "INV - 001", "专票", invoiceCode: "0330021")));
        Assert.Contains("已存在同一身份的未作废发票", duplicate.Message);

        // 不同客户同一号码：允许（身份包含客户）
        var otherCustomer = await CreateAsync(controller,
            SaveDto(customerB.Id, "INV-001", "专票", invoiceCode: "0330021"));
        Assert.Equal(customerB.Id, otherCustomer.CustomerId);

        // 同一客户不同发票类型同一号码：允许（身份包含类型）
        var otherType = await CreateAsync(controller, SaveDto(customerA.Id, "INV-001", "普票"));
        Assert.Equal("普票", otherType.InvoiceType);

        // 作废第一条后身份被释放：可重新登记同一身份
        AssertOk<CustomerSalesInvoiceEvidenceDto>(
            await controller.Void(first.Id, new CustomerSalesInvoiceVoidRequest { Reason = "号码录错重登" }));
        var recreated = await CreateAsync(controller,
            SaveDto(customerA.Id, "INV-001", "专票", invoiceCode: "0330021"));
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusDraft, recreated.Status);

        // 作废记录仍保留可读（身份 / 金额 / 历史不重写）
        var voided = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.GetById(first.Id));
        Assert.True(voided.IsVoided);
        Assert.Equal("号码录错重登", voided.VoidReason);
        Assert.Equal(1000m, voided.GrossAmount);
    }

    // ==================== 2. 分摊：部分 / 全额 / 未分摊与快照 ====================

    [Fact]
    public async Task 部分分摊两张订单_保留服务端快照且不改写客户与销售订单()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var orderA = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 600m);
        var orderB = SeedOrder(db, "SO-B", customer.Id, Currency.USD, DocumentStatus.Approved, 500m);
        var controller = BuildController(db);

        var invoice = await CreateAsync(controller,
            SaveDto(customer.Id, "INV-001", currency: "USD", net: 900m, tax: 100m, gross: 1000m));
        var linked = await SaveAllocationsAsync(controller, invoice.Id,
            AllocationRequest((orderA.Id, 300m), (orderB.Id, 200.005m)));

        Assert.Equal(2, linked.AllocationCount);
        Assert.Equal(500.01m, linked.LinkedAmount);
        Assert.Equal(499.99m, linked.UnlinkedAmount);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.LinkagePartial, linked.LinkageStatus);
        Assert.Contains("部分分摊", linked.LinkageText);
        Assert.Contains("未分摊", linked.LinkageText);

        var firstRow = linked.Allocations.Single(a => a.SalesOrderId == orderA.Id);
        Assert.Equal("SO-A", firstRow.OrderNo);
        Assert.Equal(new DateTime(2026, 9, 1), firstRow.OrderDate);
        Assert.Equal("已审核", firstRow.OrderStatusText);
        Assert.Equal("USD", firstRow.OrderCurrency);
        Assert.Equal("C001", firstRow.CustomerCode);
        Assert.Equal("义乌进出口", firstRow.CustomerName);
        Assert.Equal(300m, firstRow.AllocatedAmount);
        Assert.True(firstRow.OrderAvailable);
        Assert.Equal(1, firstRow.SortOrder);

        // 金额按币种精度取整（0.5 进位，2 位小数）
        Assert.Equal(200.01m, linked.Allocations.Single(a => a.SalesOrderId == orderB.Id).AllocatedAmount);

        // 客户与销售订单的既有字段完全不被改写
        var storedCustomer = await db.BaseCustomers.AsNoTracking().FirstAsync(c => c.Id == customer.Id);
        Assert.Equal("C001", storedCustomer.CustomerCode);
        Assert.Equal("正常", storedCustomer.CreditStatus);
        Assert.Equal(100000m, storedCustomer.CreditLimit);
        Assert.Equal(30, storedCustomer.CreditDays);

        var storedOrderA = await db.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == orderA.Id);
        Assert.Equal(600m, storedOrderA.TotalAmount);
        Assert.Equal(DocumentStatus.Approved, storedOrderA.Status);
        Assert.Equal(0m, storedOrderA.DepositAmount);
        Assert.Equal(orderA.UpdatedAt, storedOrderA.UpdatedAt);

        // 分摊只写本登记册，不产生任何其它单据
        Assert.Equal(0, await db.FinanceReceipts.CountAsync());
        Assert.Equal(0, await db.CustomerReceiptAllocations.CountAsync());
        Assert.Equal(0, await db.PurchaseInvoices.CountAsync());
    }

    [Fact]
    public async Task 全额分摊后未分摊为0且状态为已全额分摊_未分摊发票显示未分摊金额()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 1500m);
        var controller = BuildController(db);

        var invoice = await CreateAsync(controller,
            SaveDto(customer.Id, "INV-001", currency: "USD", net: 900m, tax: 100m, gross: 1000m));

        // 尚未分摊：显示未分摊，且明确不猜测订单
        var empty = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.GetById(invoice.Id));
        Assert.Equal(0m, empty.LinkedAmount);
        Assert.Equal(1000m, empty.UnlinkedAmount);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.LinkageUnlinked, empty.LinkageStatus);
        Assert.Contains("未分摊", empty.LinkageText);
        Assert.Contains("不按单号", empty.LinkageText);

        var full = await SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((order.Id, 1000m)));
        Assert.Equal(1000m, full.LinkedAmount);
        Assert.Equal(0m, full.UnlinkedAmount);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.LinkageLinked, full.LinkageStatus);
        Assert.Contains("已全额分摊", full.LinkageText);

        // 草稿期可整体替换（清空后回到未分摊）
        var cleared = await SaveAllocationsAsync(controller, invoice.Id,
            new CustomerSalesInvoiceAllocationSaveRequest());
        Assert.Equal(0, cleared.AllocationCount);
        Assert.Equal(0m, cleared.LinkedAmount);
        Assert.Equal(0, await db.CustomerSalesInvoiceAllocations.CountAsync());
    }

    [Fact]
    public async Task 分摊金额合计不得超过含税总额_零值与负数拒绝_同一订单不得重复()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var orderA = SeedOrder(db, "SO-A", customer.Id);
        var orderB = SeedOrder(db, "SO-B", customer.Id);
        var controller = BuildController(db);

        var invoice = await CreateAsync(controller, SaveDto(customer.Id, "INV-001"));

        var over = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => SaveAllocationsAsync(controller, invoice.Id,
                AllocationRequest((orderA.Id, 700m), (orderB.Id, 400m))));
        Assert.Contains("超过发票含税总额", over.Message);

        // 零值分摊（按币种精度取整后为 0）拒绝
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((orderA.Id, 0.001m))));

        // 负数拒绝
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((orderA.Id, -5m))));

        // 同一订单重复分摊：拒绝而不是静默合并
        var duplicate = await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => SaveAllocationsAsync(controller, invoice.Id,
                AllocationRequest((orderA.Id, 100m), (orderA.Id, 200m))));
        Assert.Contains("只能分摊一次", duplicate.Message);

        // 未选择订单
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((0, 100m))));

        // 校验失败不落任何分摊行
        Assert.Equal(0, await db.CustomerSalesInvoiceAllocations.CountAsync());
    }

    [Fact]
    public async Task 客户或币种不一致的订单被拒绝且绝不换算或改派()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "义乌进出口");
        var customerB = SeedCustomer(db, "C002", "宁波贸易");
        var sameCustomer = SeedOrder(db, "SO-A", customerA.Id, Currency.USD);
        var otherCustomer = SeedOrder(db, "SO-B", customerB.Id, Currency.USD);
        var otherCurrency = SeedOrder(db, "SO-C", customerA.Id, Currency.EUR);
        var controller = BuildController(db);

        var invoice = await CreateAsync(controller,
            SaveDto(customerA.Id, "INV-001", currency: "USD", net: 900m, tax: 100m, gross: 1000m));

        var customerMismatch = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((otherCustomer.Id, 100m))));
        Assert.Contains("客户", customerMismatch.Message);
        Assert.Contains("不一致", customerMismatch.Message);

        var currencyMismatch = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((otherCurrency.Id, 100m))));
        Assert.Contains("币种", currencyMismatch.Message);
        Assert.Contains("不做汇率换算", currencyMismatch.Message);

        // 同客户 + 同币种可分摊
        var ok = await SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((sameCustomer.Id, 100m)));
        Assert.Equal(1, ok.AllocationCount);

        // 不存在的订单 → 数据不存在
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((999999, 100m))));
    }

    [Fact]
    public async Task 已取消或已删除订单被拒绝_候选只列同客户同币种并派生金额()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var orderOk = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 1000m);
        var orderCancelled = SeedOrder(db, "SO-B", customer.Id, Currency.USD, DocumentStatus.Cancelled, 800m);
        var orderDeleted = SeedOrder(db, "SO-C", customer.Id, Currency.USD, DocumentStatus.Approved, 700m, deleted: true);
        SeedOrder(db, "SO-D", customer.Id, Currency.EUR, DocumentStatus.Approved, 600m);
        var controller = BuildController(db);

        var invoice = await CreateAsync(controller,
            SaveDto(customer.Id, "INV-001", currency: "USD", net: 900m, tax: 100m, gross: 1000m));

        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((orderCancelled.Id, 100m))));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((orderDeleted.Id, 100m))));

        // 候选（只读有界）：只列出同客户 + 同币种的未删除订单（含已取消并标注不可分摊）
        var candidates = AssertOk<List<CustomerSalesInvoiceOrderCandidateDto>>(
            await controller.OrderCandidates(invoice.Id, null, 200));
        Assert.Equal(2, candidates.Count);
        Assert.DoesNotContain(candidates, c => c.OrderNo is "SO-C" or "SO-D");

        var cancelled = candidates.Single(c => c.OrderNo == "SO-B");
        Assert.True(cancelled.Cancelled);
        Assert.False(cancelled.Eligible);
        Assert.Contains("已取消", cancelled.EligibilityText);

        var available = candidates.Single(c => c.OrderNo == "SO-A");
        Assert.True(available.Eligible);
        Assert.Contains("可分摊", available.EligibilityText);
        Assert.Equal(1000m, available.OrderedAmount);
        Assert.Equal(1000m, available.RemainingUnallocatedAmount);

        // 其他有效发票已分摊金额参与「剩余未覆盖」派生（不是应收余额）
        var other = await CreateAsync(controller,
            SaveDto(customer.Id, "INV-002", currency: "USD", net: 180m, tax: 20m, gross: 200m));
        await SaveAllocationsAsync(controller, other.Id, AllocationRequest((orderOk.Id, 200m)));

        var afterOther = AssertOk<List<CustomerSalesInvoiceOrderCandidateDto>>(
            await controller.OrderCandidates(invoice.Id, null, 200));
        var row = afterOther.Single(c => c.OrderNo == "SO-A");
        Assert.Equal(200m, row.AllocatedByOtherInvoices);
        Assert.Equal(800m, row.RemainingUnallocatedAmount);
        Assert.Equal(0m, row.AllocatedByThisInvoice);
    }

    [Fact]
    public async Task 订单或客户软删除取消后历史分摊仍可读并标注不可用()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 1000m);
        var controller = BuildController(db);

        var invoice = await CreateAsync(controller, SaveDto(customer.Id, "INV-001", currency: "USD"));
        await SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((order.Id, 300m)));
        AssertOk<CustomerSalesInvoiceEvidenceDto>(
            await controller.Record(invoice.Id));

        // 订单随后被取消 → 历史分摊仍可读，标注不再可用
        var storedOrder = await db.SalesOrders.FirstAsync(o => o.Id == order.Id);
        storedOrder.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var detail = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.GetById(invoice.Id));
        var row = detail.Allocations.Single();
        Assert.Equal(300m, row.AllocatedAmount);
        Assert.True(row.OrderAvailable);
        Assert.Contains("已取消", row.OrderAvailabilityText);

        // 订单被软删除 → 历史分摊仍可读，只是标注不可用
        storedOrder.IsDeleted = true;
        await db.SaveChangesAsync();
        var afterDelete = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.GetById(invoice.Id));
        Assert.False(afterDelete.Allocations.Single().OrderAvailable);
        Assert.Contains("已删除", afterDelete.Allocations.Single().OrderAvailabilityText);
        Assert.Equal(300m, afterDelete.LinkedAmount);
    }

    // ==================== 3. 登记冻结、登记前复核与作废保留 ====================

    [Fact]
    public async Task 登记后冻结_修改与分摊被拒绝_重复登记与重复作废被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 1000m);
        var controller = BuildController(db);

        var invoice = await CreateAsync(controller, SaveDto(customer.Id, "INV-001"));
        await SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((order.Id, 400m)));

        var recorded = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.Record(invoice.Id));
        Assert.True(recorded.IsRecorded);
        Assert.Equal("已登记", recorded.StatusText);
        Assert.NotNull(recorded.RecordedAt);
        Assert.Equal(400m, recorded.LinkedAmount);

        // 已登记禁止修改
        var editBlocked = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Update(invoice.Id, SaveDto(customer.Id, "INV-001", remark: "改一下")));
        Assert.Contains("已登记", editBlocked.Message);

        // 已登记禁止再改分摊
        var allocateBlocked = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((order.Id, 500m))));
        Assert.Contains("已登记", allocateBlocked.Message);

        // 重复登记拒绝
        var recordAgain = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Record(invoice.Id));
        Assert.Contains("不能重复登记", recordAgain.Message);

        // 作废保留分摊与金额；重复作废拒绝
        var voided = AssertOk<CustomerSalesInvoiceEvidenceDto>(
            await controller.Void(invoice.Id, new CustomerSalesInvoiceVoidRequest { Reason = "客户要求重开" }));
        Assert.True(voided.IsVoided);
        Assert.Equal("客户要求重开", voided.VoidReason);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal(400m, voided.LinkedAmount);
        Assert.Single(voided.Allocations);

        var voidAgain = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Void(invoice.Id, new CustomerSalesInvoiceVoidRequest { Reason = "再作废一次" }));
        Assert.Contains("不能重复作废", voidAgain.Message);

        // 作废证据不可修改、不可登记
        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Update(invoice.Id, SaveDto(customer.Id, "INV-001")));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Record(invoice.Id));
    }

    [Fact]
    public async Task 作废必须填原因且长度有界_草稿也可作废一次()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var draft = await CreateAsync(controller, SaveDto(customer.Id, "INV-001"));

        var blank = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(draft.Id, new CustomerSalesInvoiceVoidRequest { Reason = "   " }));
        Assert.Contains("请填写作废原因", blank.Message);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(draft.Id,
                new CustomerSalesInvoiceVoidRequest { Reason = new string('X', 501) }));

        // 草稿也可以作废一次（保留历史）
        var voided = AssertOk<CustomerSalesInvoiceEvidenceDto>(
            await controller.Void(draft.Id, new CustomerSalesInvoiceVoidRequest { Reason = "草稿录错" }));
        Assert.True(voided.IsVoided);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusVoided, voided.Status);
    }

    [Fact]
    public async Task 登记前复核_已持久化分摊订单被取消时拒绝登记()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 1000m);
        var controller = BuildController(db);

        var invoice = await CreateAsync(controller, SaveDto(customer.Id, "INV-001"));
        await SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((order.Id, 300m)));

        // 登记前订单被取消：登记被拒绝，且不改写已持久化分摊行
        var storedOrder = await db.SalesOrders.FirstAsync(o => o.Id == order.Id);
        storedOrder.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var blocked = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Record(invoice.Id));
        Assert.Contains("已取消", blocked.Message);

        var stillDraft = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.GetById(invoice.Id));
        Assert.True(stillDraft.IsDraft);
        Assert.Equal(300m, stillDraft.LinkedAmount);
        Assert.Equal(1, await db.CustomerSalesInvoiceAllocations.CountAsync());

        // 订单恢复后可以登记
        storedOrder.Status = DocumentStatus.Approved;
        await db.SaveChangesAsync();
        AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.Record(invoice.Id));
    }

    [Fact]
    public async Task 编辑草稿_已有分摊时不允许更换客户或币种_其他字段可改()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "义乌进出口");
        var customerB = SeedCustomer(db, "C002", "宁波贸易");
        var order = SeedOrder(db, "SO-A", customerA.Id, Currency.USD, DocumentStatus.Approved, 1000m);
        var controller = BuildController(db);

        var invoice = await CreateAsync(controller, SaveDto(customerA.Id, "INV-001", currency: "USD"));
        await SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((order.Id, 300m)));

        var switchCustomer = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Update(invoice.Id, SaveDto(customerB.Id, "INV-001", currency: "USD")));
        Assert.Contains("不能更换客户", switchCustomer.Message);

        var switchCurrency = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Update(invoice.Id, SaveDto(customerA.Id, "INV-001", currency: "EUR")));
        Assert.Contains("不能更换币种", switchCurrency.Message);

        // 其他字段可以改（金额、备注、号码）
        var updated = AssertOk<CustomerSalesInvoiceEvidenceDto>(
            await controller.Update(invoice.Id,
                SaveDto(customerA.Id, "INV-001-B", currency: "USD", net: 1800m, tax: 200m, gross: 2000m,
                    remark: "更正金额")));
        Assert.Equal("INV-001-B", updated.InvoiceNumber);
        Assert.Equal(2000m, updated.GrossAmount);
        Assert.Equal("更正金额", updated.Remark);
        Assert.Equal(300m, updated.LinkedAmount);
    }

    // ==================== 4. 与单证中心商业发票的显式交叉引用 ====================

    [Fact]
    public async Task 显式交叉引用_只接受商业发票_写入快照且不读取金额不改写单证()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var commercial = SeedTradeDoc(db, "CI-2026-001", "商业发票", customerId: customer.Id, amount: 888m);
        var packingList = SeedTradeDoc(db, "PL-2026-001", "装箱单");
        var deleted = SeedTradeDoc(db, "CI-2026-002", "商业发票", deleted: true);
        var controller = BuildController(db);
        var documentBefore = await db.TradeDocuments.AsNoTracking().FirstAsync(d => d.Id == commercial.Id);

        // 引用商业发票：允许，写入单证号 / 类型快照
        var invoice = await CreateAsync(controller,
            SaveDto(customer.Id, "INV-001", tradeDocumentId: commercial.Id,
                commercialInvoiceReference: "商业发票号 CI-2026-001"));

        Assert.Equal(commercial.Id, invoice.TradeDocumentId);
        Assert.Equal("CI-2026-001", invoice.TradeDocumentNo);
        Assert.Equal("商业发票", invoice.TradeDocumentDocType);
        Assert.Equal("商业发票号 CI-2026-001", invoice.CommercialInvoiceReference);
        Assert.True(invoice.TradeDocumentAvailable);
        Assert.Contains("商业发票", invoice.TradeDocumentSeparationText);
        Assert.Contains("刻意分离", invoice.TradeDocumentSeparationText);

        // 引用装箱单：拒绝（只接受商业发票）
        var wrongType = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => CreateAsync(controller, SaveDto(customer.Id, "INV-002", tradeDocumentId: packingList.Id)));
        Assert.Contains("不是商业发票", wrongType.Message);

        // 引用已删除单证：数据不存在
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => CreateAsync(controller, SaveDto(customer.Id, "INV-003", tradeDocumentId: deleted.Id)));

        // 引用说明长度有界
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => CreateAsync(controller, SaveDto(customer.Id, "INV-004",
                commercialInvoiceReference: new string('R', 101))));

        // 单证本体完全未被改写（金额 / 状态 / 日期 / 更新时间）
        var stored = await db.TradeDocuments.AsNoTracking().FirstAsync(d => d.Id == commercial.Id);
        Assert.Equal(888m, stored.Amount);
        Assert.Equal("已制作", stored.Status);
        Assert.Equal(new DateTime(2026, 9, 5), stored.IssueDate);
        Assert.Equal(documentBefore.UpdatedAt, stored.UpdatedAt);
        Assert.Equal(documentBefore.CustomerName, stored.CustomerName);
        Assert.Equal(0, await db.TradeDocumentItems.CountAsync());

        // 单证随后被删除：历史引用快照仍可读，只是标注不可用
        var tracked = await db.TradeDocuments.FirstAsync(d => d.Id == commercial.Id);
        tracked.IsDeleted = true;
        await db.SaveChangesAsync();
        var detail = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.GetById(invoice.Id));
        Assert.Equal("CI-2026-001", detail.TradeDocumentNo);
        Assert.False(detail.TradeDocumentAvailable);
        Assert.Contains("已删除", detail.TradeDocumentAvailabilityText);
    }

    [Fact]
    public async Task 商业发票候选只列未删除商业发票_且不读取单证金额参与登记册金额()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        SeedTradeDoc(db, "CI-2026-001", "商业发票", "SO-A");
        SeedTradeDoc(db, "PL-2026-001", "装箱单", "SO-A");
        SeedTradeDoc(db, "CI-2026-002", "商业发票", "SO-B", deleted: true);
        SeedTradeDoc(db, "CI-2026-003", "商业发票", "SO-C");
        var controller = BuildController(db);

        var all = AssertOk<List<CustomerSalesInvoiceTradeDocumentCandidateDto>>(
            await controller.CommercialInvoiceCandidates(null, 200));
        Assert.Equal(2, all.Count);
        Assert.All(all, d => Assert.Equal("商业发票", d.DocType));
        Assert.All(all, d => Assert.True(d.Eligible));

        var filtered = AssertOk<List<CustomerSalesInvoiceTradeDocumentCandidateDto>>(
            await controller.CommercialInvoiceCandidates("SO-C", 200));
        Assert.Single(filtered);
        Assert.Equal("CI-2026-003", filtered[0].DocNo);

        // 单证金额只作为候选展示字段；登记册金额完全来自客户端提交并受金额等式校验
        var invoice = await CreateAsync(controller,
            SaveDto(customer.Id, "INV-001", tradeDocumentId: all[0].TradeDocumentId));
        Assert.Equal(1000m, invoice.GrossAmount);
        Assert.NotEqual(all.First(d => d.TradeDocumentId == invoice.TradeDocumentId).Amount, invoice.GrossAmount);
    }

    // ==================== 5. 台账过滤、分页有界与非变更边界 ====================

    [Fact]
    public async Task 台账按客户类型状态币种分摊状态与关键字过滤_分页有界且未知筛选取值拒绝()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "义乌进出口");
        var customerB = SeedCustomer(db, "C002", "宁波贸易");
        var orderA = SeedOrder(db, "SO-A", customerA.Id, Currency.USD, DocumentStatus.Approved, 5000m);
        var controller = BuildController(db);

        var linkedInv = await CreateAsync(controller,
            SaveDto(customerA.Id, "INV-001", currency: "USD", net: 900m, tax: 100m, gross: 1000m));
        await SaveAllocationsAsync(controller, linkedInv.Id, AllocationRequest((orderA.Id, 1000m)));

        var partialInv = await CreateAsync(controller,
            SaveDto(customerA.Id, "INV-002", "专票", invoiceCode: "033", currency: "USD",
                net: 450m, tax: 50m, gross: 500m));
        await SaveAllocationsAsync(controller, partialInv.Id, AllocationRequest((orderA.Id, 200m)));

        await CreateAsync(controller,
            SaveDto(customerA.Id, "INV-003", "出口发票", currency: "USD", net: 90m, tax: 10m, gross: 100m));
        var voidedInv = await CreateAsync(controller,
            SaveDto(customerB.Id, "INV-004", currency: "EUR", net: 90m, tax: 10m, gross: 100m));
        AssertOk<CustomerSalesInvoiceEvidenceDto>(
            await controller.Void(voidedInv.Id, new CustomerSalesInvoiceVoidRequest { Reason = "客户要求作废" }));

        // 全部（含已作废历史）
        var all = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery()));
        Assert.Equal(4, all.Total);

        var active = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery { Status = 2 }));
        Assert.Single(active.Items);

        var byCustomer = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery { CustomerId = customerA.Id }));
        Assert.Equal(3, byCustomer.Total);

        var byType = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery { InvoiceType = "专票" }));
        Assert.Single(byType.Items);

        var unlinked = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery
            { LinkageStatus = CustomerSalesInvoiceEvidenceRules.LinkageUnlinked }));
        Assert.Equal(2, unlinked.Total);

        var fully = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery
            { LinkageStatus = CustomerSalesInvoiceEvidenceRules.LinkageLinked }));
        Assert.Single(fully.Items);

        var byOrder = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery { SalesOrderId = orderA.Id }));
        Assert.Equal(2, byOrder.Total);

        var byKeyword = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery { Keyword = "宁波" }));
        Assert.Single(byKeyword.Items);

        var byCurrency = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery { Currency = "EUR" }));
        Assert.Single(byCurrency.Items);

        var byDate = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery
            { InvoiceDateFrom = new DateTime(2026, 9, 10), InvoiceDateTo = new DateTime(2026, 9, 10) }));
        Assert.Equal(4, byDate.Total);

        // 分页有界：页码越界返回空、页大小被上限截断
        var paged = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery { Page = 2, PageSize = 2 }));
        Assert.Equal(4, paged.Total);
        Assert.Equal(2, paged.Items.Count);
        Assert.Equal(2, paged.PageSize);

        var oversized = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery { PageSize = 100000 }));
        Assert.Equal(CustomerSalesInvoiceEvidenceQuery.MaxPageSize, oversized.PageSize);

        // 未知筛选取值一律拒绝（不静默忽略筛选条件）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery { Status = 9 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery { InvoiceType = "电子普票" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery { LinkageStatus = "guessed" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery { Currency = "BTC" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery
            { Keyword = new string('K', 101) }));

        // 不存在的发票 → 数据不存在
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(999999));
    }

    [Fact]
    public async Task 分摊预览不写库_且逐行给出租格与派生金额()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var orderA = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 1000m);
        var orderCancelled = SeedOrder(db, "SO-B", customer.Id, Currency.USD, DocumentStatus.Cancelled, 500m);
        var controller = BuildController(db);

        var invoice = await CreateAsync(controller,
            SaveDto(customer.Id, "INV-001", currency: "USD", net: 900m, tax: 100m, gross: 1000m));

        var preview = AssertOk<CustomerSalesInvoiceAllocationPreviewDto>(
            await controller.PreviewAllocations(invoice.Id,
                AllocationRequest((orderA.Id, 600m))));

        Assert.Equal(600m, preview.ProposedLinkedAmount);
        Assert.Equal(400m, preview.UnlinkedAmount);
        Assert.Equal(0m, preview.PersistedLinkedAmount);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.LinkagePartial, preview.LinkageStatus);
        Assert.Single(preview.Lines);
        Assert.Equal(1000m, preview.Lines[0].OrderedAmount);
        Assert.Contains("未分摊部分", preview.LinkageText);
        Assert.Contains("不会按单号相似度", preview.LinkageRuleText);

        // 预览不写库
        Assert.Equal(0, await db.CustomerSalesInvoiceAllocations.CountAsync());

        // 预览同样做资格校验（已取消订单被拒绝）
        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.PreviewAllocations(invoice.Id, AllocationRequest((orderCancelled.Id, 100m))));
        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.PreviewAllocations(invoice.Id, AllocationRequest((orderA.Id, 1200m))));
        Assert.Equal(0, await db.CustomerSalesInvoiceAllocations.CountAsync());
    }

    [Fact]
    public async Task 登记与作废不改写客户信用状态订单进度与其它单据且无请求期回填()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 1000m,
            contractNo: "HT-2026-001");
        var commercial = SeedTradeDoc(db, "CI-2026-001", "商业发票", customerId: customer.Id);
        var controller = BuildController(db);

        var customerBefore = await db.BaseCustomers.AsNoTracking().SingleAsync();
        var orderBefore = await db.SalesOrders.AsNoTracking().SingleAsync();
        var documentBefore = await db.TradeDocuments.AsNoTracking().SingleAsync();

        var invoice = await CreateAsync(controller,
            SaveDto(customer.Id, "INV-001", currency: "USD", net: 900m, tax: 100m, gross: 1000m,
                tradeDocumentId: commercial.Id, commercialInvoiceReference: "CI-2026-001"));
        await SaveAllocationsAsync(controller, invoice.Id, AllocationRequest((order.Id, 250m)));
        AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.Record(invoice.Id));
        AssertOk<CustomerSalesInvoiceEvidenceDto>(
            await controller.Void(invoice.Id, new CustomerSalesInvoiceVoidRequest { Reason = "发票号码录错" }));

        // 客户：名称 / 状态 / 信用状态与额度 / 账期 / 备注均未被改写
        var customerAfter = await db.BaseCustomers.AsNoTracking().SingleAsync();
        Assert.Equal(customerBefore.CustomerName, customerAfter.CustomerName);
        Assert.Equal(customerBefore.Status, customerAfter.Status);
        Assert.Equal(customerBefore.CreditStatus, customerAfter.CreditStatus);
        Assert.Equal(customerBefore.CreditLimit, customerAfter.CreditLimit);
        Assert.Equal(customerBefore.CreditDays, customerAfter.CreditDays);
        Assert.Equal(customerBefore.Remark, customerAfter.Remark);
        Assert.Equal(customerBefore.IsDeleted, customerAfter.IsDeleted);

        // 销售订单：金额 / 状态 / 定金 / 合同 / 交期均未被改写
        var orderAfter = await db.SalesOrders.AsNoTracking().SingleAsync();
        Assert.Equal(orderBefore.TotalAmount, orderAfter.TotalAmount);
        Assert.Equal(orderBefore.Status, orderAfter.Status);
        Assert.Equal(orderBefore.DepositAmount, orderAfter.DepositAmount);
        Assert.Equal(orderBefore.ContractNo, orderAfter.ContractNo);
        Assert.Equal(orderBefore.DeliveryDate, orderAfter.DeliveryDate);
        Assert.Equal(orderBefore.PaymentTerms, orderAfter.PaymentTerms);
        Assert.Equal(orderBefore.UpdatedAt, orderAfter.UpdatedAt);

        // 单证中心单证：金额 / 状态 / 日期均未被改写
        var documentAfter = await db.TradeDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(documentBefore.Amount, documentAfter.Amount);
        Assert.Equal(documentBefore.Status, documentAfter.Status);
        Assert.Equal(documentBefore.IssueDate, documentAfter.IssueDate);
        Assert.Equal(documentBefore.UpdatedAt, documentAfter.UpdatedAt);

        // 其它单据一张都没有被创建（没有请求期回填、没有开票、没有记账、没有收款核销）
        Assert.Equal(0, await db.FinanceReceipts.CountAsync());
        Assert.Equal(0, await db.CustomerReceiptAllocations.CountAsync());
        Assert.Equal(0, await db.PurchaseInvoices.CountAsync());
        Assert.Equal(0, await db.BaseTaxRefunds.CountAsync());
        Assert.Equal(0, await db.FinanceExpenses.CountAsync());
        Assert.Equal(0, await db.StockMovements.CountAsync());
        Assert.Equal(0, await db.SalesOrderChangeRequests.CountAsync());
        Assert.Equal(0, await db.DocumentAttachmentReferences.CountAsync());
        Assert.Equal(0, await db.SupplierPaymentAllocations.CountAsync());

        // 本登记册保留作废证据（作废不是删除），且没有为历史单据生成任何发票证据
        Assert.Single(await db.CustomerSalesInvoiceEvidences.AsNoTracking().ToListAsync());
        Assert.Single(await db.CustomerSalesInvoiceAllocations.AsNoTracking().ToListAsync());
    }

    // ==================== 6. 纯规则、模型与接线契约 ====================

    [Fact]
    public void 纯规则_类型身份金额精度状态分摊与文案()
    {
        // 发票类型与发票代码要求
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.InvoiceTypeSpecial,
            CustomerSalesInvoiceEvidenceRules.NormalizeInvoiceType(" 专票 "));
        Assert.True(CustomerSalesInvoiceEvidenceRules.RequiresInvoiceCode("专票"));
        Assert.False(CustomerSalesInvoiceEvidenceRules.RequiresInvoiceCode("普票"));
        Assert.False(CustomerSalesInvoiceEvidenceRules.RequiresInvoiceCode("出口发票"));
        Assert.Throws<BusinessException>(() => CustomerSalesInvoiceEvidenceRules.NormalizeInvoiceType(""));

        // 身份规范化：去空白 / 连字符 / 下划线 + 大写（精确判定，不做模糊匹配）
        Assert.Equal("INV001", CustomerSalesInvoiceEvidenceRules.NormalizeIdentityPart(" inv-001 "));
        Assert.Equal("0360021", CustomerSalesInvoiceEvidenceRules.NormalizeIdentityPart("036-0021"));
        Assert.Equal("专票 036-0021", CustomerSalesInvoiceEvidenceRules.IdentityText("专票", "036", "0021"));
        Assert.Equal("普票 0021", CustomerSalesInvoiceEvidenceRules.IdentityText("普票", "", "0021"));

        // 金额精度：JPY 0 位、其余 2 位、未知币种按 2 位
        Assert.Equal(0, CurrencyAmountRules.PrecisionOf("JPY"));
        Assert.Equal(2, CurrencyAmountRules.PrecisionOf("CNY"));
        Assert.Equal(2, CurrencyAmountRules.PrecisionOf(null));

        // 金额等式：取整后必须严格相等
        var ok = CustomerSalesInvoiceEvidenceRules.ValidateAmounts(900m, 100m, 1000m, "CNY");
        Assert.Equal(1000m, ok.Gross);
        Assert.Throws<BusinessException>(
            () => CustomerSalesInvoiceEvidenceRules.ValidateAmounts(900m, 100m, 1001m, "CNY"));
        Assert.Throws<BusinessException>(
            () => CustomerSalesInvoiceEvidenceRules.ValidateAmounts(0m, 0m, 0m, "CNY"));

        // 分摊金额：按币种精度取整且必须大于 0
        Assert.Equal(500.01m, CustomerSalesInvoiceEvidenceRules.NormalizeAllocationAmount(500.005m, "USD"));
        Assert.Equal(1000m, CustomerSalesInvoiceEvidenceRules.NormalizeAllocationAmount(1000.4m, "JPY"));
        Assert.Throws<BusinessException>(
            () => CustomerSalesInvoiceEvidenceRules.NormalizeAllocationAmount(0.001m, "USD"));
        Assert.Throws<BusinessException>(
            () => CustomerSalesInvoiceEvidenceRules.NormalizeAllocationAmount(-1m, "USD"));

        // 状态机与文案
        Assert.Equal("草稿", CustomerSalesInvoiceEvidenceRules.StatusText(0));
        Assert.Equal("已登记", CustomerSalesInvoiceEvidenceRules.StatusText(1));
        Assert.Equal("已作废", CustomerSalesInvoiceEvidenceRules.StatusText(2));
        Assert.Throws<BusinessException>(() => CustomerSalesInvoiceEvidenceRules.StatusText(9));
        Assert.Throws<BusinessException>(
            () => CustomerSalesInvoiceEvidenceRules.EnsureEditable(1, "普票 001"));
        Assert.Throws<BusinessException>(
            () => CustomerSalesInvoiceEvidenceRules.EnsureRecordable(1, "普票 001"));
        Assert.Throws<BusinessException>(
            () => CustomerSalesInvoiceEvidenceRules.EnsureVoidable(2, "普票 001"));
        Assert.Throws<BusinessException>(
            () => CustomerSalesInvoiceEvidenceRules.NormalizeVoidReason("  "));

        // 分摊状态与文案
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.LinkageUnlinked,
            CustomerSalesInvoiceEvidenceRules.LinkageStatusOf(1000m, 0m));
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.LinkagePartial,
            CustomerSalesInvoiceEvidenceRules.LinkageStatusOf(1000m, 400m));
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.LinkageLinked,
            CustomerSalesInvoiceEvidenceRules.LinkageStatusOf(1000m, 1000m));
        Assert.Contains("未分摊", CustomerSalesInvoiceEvidenceRules.LinkageText(1000m, 0m, 0, "USD"));
        Assert.Contains("已全额分摊", CustomerSalesInvoiceEvidenceRules.LinkageText(1000m, 1000m, 2, "USD"));

        // 边界文案：明确不是开票系统 / 税务申报 / 应收账款台账 / 收款核销
        var boundary = CustomerSalesInvoiceEvidenceRules.BoundaryText;
        Assert.Contains("不是发票开具系统", boundary);
        Assert.Contains("不是税务申报", boundary);
        Assert.Contains("不是应收账款台账或余额", boundary);
        Assert.Contains("不调用任何开票服务", boundary);

        // 与单证中心商业发票的分离口径文案
        var separation = CustomerSalesInvoiceEvidenceRules.TradeDocumentSeparationText;
        Assert.Contains("刻意分离", separation);
        Assert.Contains("不会互相转换、替换或自动链接", separation);

        // 单证交叉引用资格：只接受商业发票（其余类型拒绝）
        var commercial = new TradeDocument { DocNo = "CI-1", DocType = "商业发票" };
        Assert.True(CustomerSalesInvoiceEvidenceRules.EvaluateTradeDocumentEligibility(commercial).Eligible);
        var packing = new TradeDocument { DocNo = "PL-1", DocType = "装箱单" };
        Assert.False(CustomerSalesInvoiceEvidenceRules.EvaluateTradeDocumentEligibility(packing).Eligible);
        Assert.Contains("不是商业发票",
            CustomerSalesInvoiceEvidenceRules.EvaluateTradeDocumentEligibility(packing).Text);
        Assert.Throws<BusinessException>(
            () => CustomerSalesInvoiceEvidenceRules.EnsureTradeDocumentReferenceable(null));
    }

    [Fact]
    public void 模型配置契约_精度长度过滤唯一索引与刻意不建外键()
    {
        using var db = TestDbFactory.Create();

        var entityType = db.Model.FindEntityType(typeof(CustomerSalesInvoiceEvidence));
        Assert.NotNull(entityType);

        Assert.Equal(20, entityType!.FindProperty(nameof(CustomerSalesInvoiceEvidence.InvoiceType))!.GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(CustomerSalesInvoiceEvidence.InvoiceNumber))!.GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(CustomerSalesInvoiceEvidence.NormalizedInvoiceNumber))!.GetMaxLength());
        Assert.Equal(200, entityType.FindProperty(nameof(CustomerSalesInvoiceEvidence.CustomerName))!.GetMaxLength());
        Assert.Equal(20, entityType.FindProperty(nameof(CustomerSalesInvoiceEvidence.Currency))!.GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(CustomerSalesInvoiceEvidence.TradeDocumentNo))!.GetMaxLength());
        Assert.Equal(30, entityType.FindProperty(nameof(CustomerSalesInvoiceEvidence.TradeDocumentDocType))!.GetMaxLength());
        Assert.Equal(100, entityType.FindProperty(nameof(CustomerSalesInvoiceEvidence.CommercialInvoiceReference))!.GetMaxLength());
        Assert.Equal(500, entityType.FindProperty(nameof(CustomerSalesInvoiceEvidence.VoidReason))!.GetMaxLength());
        Assert.Equal(500, entityType.FindProperty(nameof(CustomerSalesInvoiceEvidence.Remark))!.GetMaxLength());
        Assert.Equal(18, entityType.FindProperty(nameof(CustomerSalesInvoiceEvidence.GrossAmount))!.GetPrecision());
        Assert.Equal(2, entityType.FindProperty(nameof(CustomerSalesInvoiceEvidence.GrossAmount))!.GetScale());
        Assert.Equal(18, entityType.FindProperty(nameof(CustomerSalesInvoiceEvidence.TaxAmount))!.GetPrecision());
        Assert.Equal(18, entityType.FindProperty(nameof(CustomerSalesInvoiceEvidence.NetAmount))!.GetPrecision());

        var uniqueIdentity = entityType.GetIndexes().Single(i =>
            i.GetDatabaseName() == "UX_CustomerSalesInvoiceEvidences_ActiveIdentity");
        Assert.True(uniqueIdentity.IsUnique);
        Assert.Equal("IsDeleted = 0 AND Status <> 2", uniqueIdentity.GetFilter());
        Assert.Equal(4, uniqueIdentity.Properties.Count);

        Assert.Contains(entityType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_CustomerSalesInvoiceEvidences_CustomerId_Status");
        Assert.Contains(entityType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_CustomerSalesInvoiceEvidences_Status_InvoiceDate");
        Assert.Contains(entityType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_CustomerSalesInvoiceEvidences_NormalizedInvoiceNumber");

        // 读取侧标注是非持久化列（不落库）
        Assert.DoesNotContain(entityType.GetProperties(),
            p => p.Name is nameof(CustomerSalesInvoiceEvidence.LinkedAmount)
                or nameof(CustomerSalesInvoiceEvidence.UnlinkedAmount)
                or nameof(CustomerSalesInvoiceEvidence.LinkageStatus)
                or nameof(CustomerSalesInvoiceEvidence.AllocationCount)
                or nameof(CustomerSalesInvoiceEvidence.CustomerAvailable)
                or nameof(CustomerSalesInvoiceEvidence.TradeDocumentAvailable)
                or nameof(CustomerSalesInvoiceEvidence.BoundaryText));

        // 发票证据刻意不建到客户 / 销售订单 / 单证的外键
        Assert.Empty(entityType.GetForeignKeys());

        var allocationType = db.Model.FindEntityType(typeof(CustomerSalesInvoiceAllocation));
        Assert.NotNull(allocationType);
        Assert.Equal(50, allocationType!.FindProperty(nameof(CustomerSalesInvoiceAllocation.OrderNo))!.GetMaxLength());
        Assert.Equal(20, allocationType.FindProperty(nameof(CustomerSalesInvoiceAllocation.OrderCurrency))!.GetMaxLength());
        Assert.Equal(18, allocationType.FindProperty(nameof(CustomerSalesInvoiceAllocation.AllocatedAmount))!.GetPrecision());

        var uniqueAllocation = allocationType.GetIndexes().Single(i =>
            i.GetDatabaseName() == "UX_CustomerSalesInvoiceAllocations_InvoiceOrder");
        Assert.True(uniqueAllocation.IsUnique);
        Assert.Equal("IsDeleted = 0", uniqueAllocation.GetFilter());
        Assert.Contains(allocationType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_CustomerSalesInvoiceAllocations_SalesOrderId");

        // 分摊行刻意不建任何外键（与 ERP-049 / ERP-053 一致），也没有导航属性
        Assert.Empty(allocationType.GetForeignKeys());
        Assert.Empty(allocationType.GetNavigations());

        // 销售订单 / 客户 / 单证上没有被加上本模块的导航属性
        var orderType = db.Model.FindEntityType(typeof(SalesOrder));
        Assert.DoesNotContain(orderType!.GetNavigations(),
            n => n.ClrType == typeof(CustomerSalesInvoiceEvidence)
                || n.ClrType == typeof(CustomerSalesInvoiceAllocation));
        var customerType = db.Model.FindEntityType(typeof(BaseCustomer));
        Assert.DoesNotContain(customerType!.GetNavigations(),
            n => n.ClrType == typeof(CustomerSalesInvoiceEvidence));
        var documentType = db.Model.FindEntityType(typeof(TradeDocument));
        Assert.DoesNotContain(documentType!.GetNavigations(),
            n => n.ClrType == typeof(CustomerSalesInvoiceEvidence));
    }

    [Fact]
    public void Schema_upgrade_幂等建表建索引且不含任何回填或开票语句()
    {
        var script = File.ReadAllText(
            RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.CustomerSalesInvoiceEvidences') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.CustomerSalesInvoiceEvidences", script);
        Assert.Contains("IF OBJECT_ID('db_owner.CustomerSalesInvoiceAllocations') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.CustomerSalesInvoiceAllocations", script);
        Assert.Contains("NetAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("TaxAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("GrossAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("AllocatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_CustomerSalesInvoiceEvidences_ActiveIdentity", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_CustomerSalesInvoiceAllocations_InvoiceOrder", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status <> 2;", script);

        // 刻意不建任何外键（与 ERP-049 / ERP-053 一致）：本段只有建表 + 过滤索引，不含 ALTER TABLE
        Assert.DoesNotContain("FK_CustomerSalesInvoice", script);

        var start = script.IndexOf("// 37. 客户销项发票证据登记", StringComparison.Ordinal);
        Assert.True(start > 0);
        var segment = script[start..];
        // 本模块段落只建表 + 过滤索引：不改写任何既有表结构，也不做任何回填、开票、报税或记账语句
        Assert.DoesNotContain("ALTER TABLE", segment);
        Assert.DoesNotContain("UPDATE db_owner", segment);
        Assert.DoesNotContain("INSERT INTO db_owner", segment);
        Assert.DoesNotContain("DELETE FROM db_owner", segment);
        Assert.DoesNotContain("EXEC ", segment);
    }

    [Fact]
    public void 前端与路由接线契约()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/customer-sales-invoices.js", index);

        var docModules = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc.js"));
        // 工具栏入口必须带括号（extraActions / toolbar 直接注入 onclick 属性），行操作只写函数名（渲染时注入行 Id）
        Assert.Contains("onclick: 'openCustomerSalesInvoiceRegister()'", docModules);
        Assert.Contains("onclick: 'openCustomerSalesInvoiceRegister'", docModules);

        var js = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "customer-sales-invoices.js"));
        Assert.Contains("async function openCustomerSalesInvoiceRegister", js);
        Assert.Contains("'/api/customer-sales-invoices'", js);
        Assert.Contains("'/api/customer-sales-invoices?'", js);
        Assert.Contains("/commercial-invoice-candidates?", js);
        Assert.Contains("/order-candidates?", js);
        Assert.Contains("/allocations/preview", js);
        Assert.Contains("/allocations'", js);
        Assert.Contains("/record", js);
        Assert.Contains("/void", js);
        Assert.Contains("csiSaveForm", js);
        Assert.Contains("csiSaveAllocations", js);
        Assert.Contains("csiConfirmVoid", js);
        Assert.Contains("不会开具", js);
        Assert.Contains("刻意分离", js);

        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "CustomerSalesInvoiceEvidenceController.cs"));
        Assert.Contains("[Route(\"api/customer-sales-invoices\")]", controller);
        Assert.Contains("commercial-invoice-candidates", controller);
        Assert.Contains("{id:long}/order-candidates", controller);
        Assert.Contains("{id:long}/allocations/preview", controller);
        Assert.Contains("{id:long}/allocations", controller);
        Assert.Contains("{id:long}/record", controller);
        Assert.Contains("{id:long}/void", controller);
    }
}











