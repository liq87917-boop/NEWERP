using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 供应商采购发票登记单元测试（ERP-043）。覆盖：普票 / 专票与发票代码要求、金额等式与币种精度、
/// 供应商存在 / 删除 / 停用校验、重复身份（含规范化）拒绝、草稿编辑与冻结、
/// 采购订单关联（同供应商 + 同币种、非取消、重复订单、超含税总额、金额必须为正）、
/// 部分 / 全额关联与未关联金额、整体替换、登记 / 作废与历史保留、登记前关联复核、
/// 非变更边界（采购订单 / 库存 / 库存流水 / 退税 / 收付款）、台账过滤与分页、
/// 候选订单派生金额，以及模型 / 幂等结构与前端接线契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class PurchaseInvoiceTests
{
    // ==================== 0. 测试脚手架 ====================

    private static PurchaseInvoiceController BuildController(ErpDbContext db) => new(db);

    private static BaseSupplier SeedSupplier(
        ErpDbContext db, string code, string name, int status = 1, bool deleted = false)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = code,
            SupplierName = name,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static PurchaseOrder SeedOrder(
        ErpDbContext db, string orderNo, long supplierId, Currency currency = Currency.CNY,
        DocumentStatus status = DocumentStatus.Approved, decimal totalAmount = 1000m, bool deleted = false)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            SupplierId = supplierId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            IsDeleted = deleted,
            ArrivalProgress = "未到货",
            SettlementProgress = "未结算"
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static PurchaseInvoiceSaveDto InvoiceDto(
        long supplierId, string invoiceType = "普票", string? code = null, string number = "0001",
        decimal net = 100m, decimal tax = 13m, decimal gross = 113m,
        string currency = "CNY", DateTime? date = null, string remark = "")
        => new()
        {
            InvoiceType = invoiceType,
            InvoiceCode = code,
            InvoiceNumber = number,
            InvoiceDate = date ?? new DateTime(2026, 9, 20),
            SupplierId = supplierId,
            Currency = currency,
            NetAmount = net,
            TaxAmount = tax,
            GrossAmount = gross,
            Remark = remark
        };

    private static PurchaseInvoiceAllocationSaveRequest Lines(params (long OrderId, decimal Amount)[] lines)
        => new()
        {
            Lines = lines.Select(l => new PurchaseInvoiceAllocationSaveDto
            {
                PurchaseOrderId = l.OrderId,
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

    private static async Task<PurchaseInvoiceDto> CreateInvoiceAsync(
        PurchaseInvoiceController controller, PurchaseInvoiceSaveDto dto)
        => AssertOk<PurchaseInvoiceDto>(await controller.Create(dto));

    // ==================== 1. 发票类型、发票代码与金额等式 ====================

    [Fact]
    public async Task 普票_可不填发票代码_保存草稿并写入供应商快照()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var controller = BuildController(db);

        var created = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "12345678"));

        Assert.Equal(PurchaseInvoiceRules.StatusDraft, created.Status);
        Assert.Equal("草稿", created.StatusText);
        Assert.True(created.IsDraft);
        Assert.False(created.IsRecorded);
        Assert.False(created.IsVoided);
        Assert.Equal("S001", created.SupplierCode);
        Assert.Equal("义乌档口", created.SupplierName);
        Assert.True(created.SupplierAvailable);
        Assert.Equal("", created.InvoiceCode);
        Assert.Contains("12345678", created.IdentityText);
        Assert.Equal(100m, created.NetAmount);
        Assert.Equal(13m, created.TaxAmount);
        Assert.Equal(113m, created.GrossAmount);
        Assert.Equal(0m, created.LinkedAmount);
        Assert.Equal(113m, created.UnlinkedAmount);
        Assert.Equal(PurchaseInvoiceRules.LinkageUnlinked, created.LinkageStatus);
        Assert.Contains("未关联", created.LinkageText);
        Assert.Empty(created.Allocations);
    }

    [Fact]
    public async Task 专票_缺少发票代码被拒绝_填写后成功()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(InvoiceDto(supplier.Id, "专票", null, "999")));
        Assert.Contains("专票必须填写发票代码", ex.Message);

        var created = await CreateInvoiceAsync(
            controller, InvoiceDto(supplier.Id, "专票", "044031900111", "999"));
        Assert.Equal("专票", created.InvoiceType);
        Assert.Equal("044031900111", created.InvoiceCode);
        Assert.Contains("044031900111", created.IdentityText);
        Assert.Contains("999", created.IdentityText);
    }

    [Fact]
    public async Task 未知发票类型_被拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(InvoiceDto(supplier.Id, "电子专票")));
        Assert.Contains("不受支持", ex.Message);
    }

    [Theory]
    [InlineData(100, 13, 114)]   // 含税总额 ≠ 净额 + 税额（偏大）
    [InlineData(100, 13, 112)]   // 含税总额 ≠ 净额 + 税额（偏小）
    [InlineData(-1, 13, 12)]     // 净额为负
    [InlineData(100, -13, 87)]   // 税额为负
    [InlineData(0, 0, 0)]        // 含税总额必须大于 0
    public async Task 金额等式或取值不合法_被拒绝(decimal net, decimal tax, decimal gross)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(InvoiceDto(supplier.Id, net: net, tax: tax, gross: gross)));

        Assert.Empty(await db.PurchaseInvoices.ToListAsync());
    }

    [Fact]
    public async Task 币种精度_金额按币种精度取整后校验等式()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var controller = BuildController(db);

        // CNY：2 位小数四舍五入后等式成立
        var cny = await CreateInvoiceAsync(controller,
            InvoiceDto(supplier.Id, number: "CNY-1", net: 100.40m, tax: 0.10m, gross: 100.50m));
        Assert.Equal(100.40m, cny.NetAmount);
        Assert.Equal(0.10m, cny.TaxAmount);
        Assert.Equal(100.50m, cny.GrossAmount);
        Assert.Equal(2, cny.AmountDecimals);

        // JPY：0 位小数 → 100.4 / 0.1 / 100.5 取整为 100 / 0 / 101，等式不成立 → 拒绝
        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(
            InvoiceDto(supplier.Id, number: "JPY-1", net: 100.40m, tax: 0.10m, gross: 100.50m, currency: "JPY")));
        Assert.Contains("金额等式不成立", ex.Message);

        // JPY：整数金额合法（精度 0 位）
        var jpy = await CreateInvoiceAsync(controller,
            InvoiceDto(supplier.Id, number: "JPY-2", net: 1000m, tax: 100m, gross: 1100m, currency: "JPY"));
        Assert.Equal(0, jpy.AmountDecimals);
        Assert.Equal(1100m, jpy.GrossAmount);
    }

    [Fact]
    public async Task 不支持的币种_被拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(InvoiceDto(supplier.Id, currency: "RUB")));
        Assert.Contains("不受支持", ex.Message);
    }

    [Fact]
    public async Task 供应商不存在或已删除_被拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A", deleted: true);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(InvoiceDto(supplier.Id)));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(InvoiceDto(9999)));
    }

    [Fact]
    public async Task 供应商已停用_被拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A", status: 0);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(InvoiceDto(supplier.Id)));
        Assert.Contains("已停用", ex.Message);
        Assert.Empty(await db.PurchaseInvoices.ToListAsync());
    }

    // ==================== 2. 有效身份唯一与草稿编辑 ====================

    [Fact]
    public async Task 同一供应商同类型同代码号码_重复登记被拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var controller = BuildController(db);

        await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "专票", "044031900111", "8888"));

        var ex = await AssertBusinessAsync(ErrorCodes.Duplicate, () => controller.Create(
            InvoiceDto(supplier.Id, "专票", "044031900111", "8888")));
        Assert.Contains("重复", ex.Message);
        Assert.Single(await db.PurchaseInvoices.ToListAsync());
    }

    [Fact]
    public async Task 身份规范化_空格连字符视为同一身份()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var controller = BuildController(db);

        await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", "0440-3190", "8800 1"));

        await AssertBusinessAsync(ErrorCodes.Duplicate, () => controller.Create(
            InvoiceDto(supplier.Id, "普票", " 04403190 ", "88001")));
        Assert.Equal(1, await db.PurchaseInvoices.CountAsync());
    }

    [Fact]
    public async Task 不同供应商或不同类型_同号码不算重复()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var other = SeedSupplier(db, "S002", "工厂B");
        var controller = BuildController(db);

        await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "6666"));
        await CreateInvoiceAsync(controller, InvoiceDto(other.Id, "普票", null, "6666"));
        await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "专票", "0440", "6666"));

        Assert.Equal(3, await db.PurchaseInvoices.CountAsync());
    }

    [Fact]
    public async Task 作废后同一身份可重新登记且历史保留()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var controller = BuildController(db);

        var first = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "7777"));
        AssertOk<PurchaseInvoiceDto>(await controller.Void(
            first.Id, new PurchaseInvoiceVoidRequest { Reason = "发票号码录入错误" }));

        var second = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "7777"));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await db.PurchaseInvoices.CountAsync());
        var history = AssertOk<PurchaseInvoiceDto>(await controller.GetById(first.Id));
        Assert.True(history.IsVoided);
        Assert.Equal("发票号码录入错误", history.VoidReason);
    }

    [Fact]
    public async Task 修改草稿_更新金额与备注并保留供应商快照()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var controller = BuildController(db);

        var created = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "1111"));
        supplier.SupplierName = "工厂A（改名前）";
        await db.SaveChangesAsync();

        var updated = AssertOk<PurchaseInvoiceDto>(await controller.Update(created.Id,
            InvoiceDto(supplier.Id, "普票", null, "1111", net: 200m, tax: 26m, gross: 226m, remark: "补充说明")));

        Assert.Equal(226m, updated.GrossAmount);
        Assert.Equal("补充说明", updated.Remark);
        // 供应商快照在修改时由服务端按当前主数据写入；历史快照不被回填改写
        Assert.Equal("工厂A（改名前）", updated.SupplierName);
    }

    [Fact]
    public async Task 修改草稿_已登记或已作废被拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var controller = BuildController(db);

        var recorded = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "2001"));
        AssertOk<PurchaseInvoiceDto>(await controller.Record(recorded.Id));
        var exRecorded = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Update(recorded.Id, InvoiceDto(supplier.Id, "普票", null, "2001", net: 1m, tax: 0m, gross: 1m)));
        Assert.Contains("已登记", exRecorded.Message);

        var voided = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "2002"));
        AssertOk<PurchaseInvoiceDto>(await controller.Void(voided.Id, new PurchaseInvoiceVoidRequest { Reason = "重开" }));
        var exVoided = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Update(voided.Id, InvoiceDto(supplier.Id, "普票", null, "2002", net: 1m, tax: 0m, gross: 1m)));
        Assert.Contains("已作废", exVoided.Message);
    }

    [Fact]
    public async Task 修改草稿_已有采购订单关联时不允许更换供应商或币种()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var other = SeedSupplier(db, "S002", "工厂B");
        var order = SeedOrder(db, "PO-1", supplier.Id);
        var controller = BuildController(db);

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "3001"));
        AssertOk<PurchaseInvoiceDto>(await controller.SaveAllocations(invoice.Id, Lines((order.Id, 50m))));

        var exSupplier = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Update(
            invoice.Id, InvoiceDto(other.Id, "普票", null, "3001")));
        Assert.Contains("更换供应商或币种", exSupplier.Message);

        var exCurrency = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Update(
            invoice.Id, InvoiceDto(supplier.Id, "普票", null, "3001", currency: "USD")));
        Assert.Contains("更换供应商或币种", exCurrency.Message);
    }

    // ==================== 3. 采购订单关联（权威一致性） ====================

    [Fact]
    public async Task 关联_同供应商同币种订单_记录快照与已关联未关联金额()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var order = SeedOrder(db, "PO-1", supplier.Id, Currency.USD, totalAmount: 500m);
        var controller = BuildController(db);

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(
            supplier.Id, "专票", "0440", "4001", net: 800m, tax: 0m, gross: 800m, currency: "USD"));
        var saved = AssertOk<PurchaseInvoiceDto>(
            await controller.SaveAllocations(invoice.Id, Lines((order.Id, 300m))));

        var line = Assert.Single(saved.Allocations);
        Assert.Equal(order.Id, line.PurchaseOrderId);
        Assert.Equal("PO-1", line.OrderNo);
        Assert.Equal(new DateTime(2026, 9, 1), line.OrderDate);
        Assert.Equal("USD", line.Currency);
        Assert.Equal("S001", line.SupplierCode);
        Assert.Equal("工厂A", line.SupplierName);
        Assert.Equal(300m, line.AllocatedAmount);
        Assert.True(line.OrderAvailable);
        Assert.Contains("可用", line.OrderAvailabilityText);

        Assert.Equal(300m, saved.LinkedAmount);
        Assert.Equal(500m, saved.UnlinkedAmount);
        Assert.Equal(PurchaseInvoiceRules.LinkagePartial, saved.LinkageStatus);
        Assert.Contains("部分关联", saved.LinkageText);
        Assert.Equal(1, saved.AllocationCount);
    }

    [Fact]
    public async Task 关联_多订单全额关联()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var first = SeedOrder(db, "PO-1", supplier.Id);
        var second = SeedOrder(db, "PO-2", supplier.Id);
        var controller = BuildController(db);

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "4101"));
        var saved = AssertOk<PurchaseInvoiceDto>(await controller.SaveAllocations(
            invoice.Id, Lines((first.Id, 100m), (second.Id, 13m))));

        Assert.Equal(113m, saved.LinkedAmount);
        Assert.Equal(0m, saved.UnlinkedAmount);
        Assert.Equal(PurchaseInvoiceRules.LinkageLinked, saved.LinkageStatus);
        Assert.Equal(2, saved.AllocationCount);
        Assert.Equal(new[] { first.Id, second.Id }, saved.Allocations.Select(a => a.PurchaseOrderId).ToArray());
    }

    [Theory]
    [InlineData("supplier")]   // 供应商不一致
    [InlineData("currency")]   // 币种不一致
    [InlineData("cancelled")]  // 已取消订单
    public async Task 关联_供应商或币种不一致或订单已取消_被拒绝(string scenario)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var other = SeedSupplier(db, "S002", "工厂B");
        var controller = BuildController(db);

        var order = scenario switch
        {
            "supplier" => SeedOrder(db, "PO-X", other.Id),
            "currency" => SeedOrder(db, "PO-X", supplier.Id, Currency.USD),
            _ => SeedOrder(db, "PO-X", supplier.Id, Currency.CNY, DocumentStatus.Cancelled)
        };

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "5001"));
        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.SaveAllocations(invoice.Id, Lines((order.Id, 10m))));

        Assert.Contains("不能关联", ex.Message);
        Assert.Empty(await db.PurchaseInvoiceAllocations.ToListAsync());
    }

    [Fact]
    public async Task 关联_订单不存在或已删除_被拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var deleted = SeedOrder(db, "PO-DEL", supplier.Id, deleted: true);
        var controller = BuildController(db);

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "5101"));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.SaveAllocations(invoice.Id, Lines((deleted.Id, 10m))));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.SaveAllocations(invoice.Id, Lines((987654, 10m))));
    }

    [Fact]
    public async Task 关联_同一订单重复提交被拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var order = SeedOrder(db, "PO-1", supplier.Id);
        var controller = BuildController(db);

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "5201"));
        var ex = await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => controller.SaveAllocations(invoice.Id, Lines((order.Id, 10m), (order.Id, 20m))));

        Assert.Contains("重复", ex.Message);
        Assert.Empty(await db.PurchaseInvoiceAllocations.ToListAsync());
    }

    [Fact]
    public async Task 关联_金额合计超过含税总额被拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var first = SeedOrder(db, "PO-1", supplier.Id);
        var second = SeedOrder(db, "PO-2", supplier.Id);
        var controller = BuildController(db);

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "5301"));
        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.SaveAllocations(
            invoice.Id, Lines((first.Id, 113m), (second.Id, 0.01m))));

        Assert.Contains("超过发票含税总额", ex.Message);
        Assert.Empty(await db.PurchaseInvoiceAllocations.ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task 关联_金额为0或负数被拒绝(decimal amount)
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var order = SeedOrder(db, "PO-1", supplier.Id);
        var controller = BuildController(db);

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "5401"));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.SaveAllocations(invoice.Id, Lines((order.Id, amount))));
    }

    [Fact]
    public async Task 关联_清空后发票保持未关联()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var order = SeedOrder(db, "PO-1", supplier.Id);
        var controller = BuildController(db);

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "5501"));
        AssertOk<PurchaseInvoiceDto>(await controller.SaveAllocations(invoice.Id, Lines((order.Id, 13m))));

        var cleared = AssertOk<PurchaseInvoiceDto>(await controller.SaveAllocations(
            invoice.Id, new PurchaseInvoiceAllocationSaveRequest()));

        Assert.Equal(0m, cleared.LinkedAmount);
        Assert.Equal(113m, cleared.UnlinkedAmount);
        Assert.Equal(PurchaseInvoiceRules.LinkageUnlinked, cleared.LinkageStatus);
        Assert.Empty(await db.PurchaseInvoiceAllocations.ToListAsync());
    }

    [Fact]
    public async Task 关联_整体替换不残留旧关联行()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var first = SeedOrder(db, "PO-1", supplier.Id);
        var second = SeedOrder(db, "PO-2", supplier.Id);
        var controller = BuildController(db);

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "5601"));
        AssertOk<PurchaseInvoiceDto>(await controller.SaveAllocations(invoice.Id, Lines((first.Id, 13m))));

        var replaced = AssertOk<PurchaseInvoiceDto>(
            await controller.SaveAllocations(invoice.Id, Lines((second.Id, 113m))));

        Assert.Equal(second.Id, Assert.Single(replaced.Allocations).PurchaseOrderId);
        var rows = await db.PurchaseInvoiceAllocations.ToListAsync();
        Assert.Equal(second.Id, Assert.Single(rows).PurchaseOrderId);
    }

    [Fact]
    public async Task 关联预览_只读并给出保存后的已关联未关联金额()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var order = SeedOrder(db, "PO-1", supplier.Id, Currency.CNY, totalAmount: 900m);
        var controller = BuildController(db);

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "5701"));
        var preview = AssertOk<PurchaseInvoiceAllocationPreviewDto>(
            await controller.PreviewAllocations(invoice.Id, Lines((order.Id, 40m))));

        Assert.Equal(113m, preview.GrossAmount);
        Assert.Equal(0m, preview.PersistedLinkedAmount);
        Assert.Equal(40m, preview.ProposedTotal);
        Assert.Equal(73m, preview.UnlinkedAfterSave);
        Assert.Equal(PurchaseInvoiceRules.LinkagePartial, preview.LinkageStatus);
        Assert.Equal(900m, Assert.Single(preview.Lines).OrderTotalAmount);
        Assert.True(Assert.Single(preview.Lines).Eligible);
        Assert.Contains("关联", preview.RuleText);

        // 预览不写库
        Assert.Empty(await db.PurchaseInvoiceAllocations.ToListAsync());

        // 非法行同样在预览阶段被拒绝
        var other = SeedSupplier(db, "S002", "工厂B");
        var foreign = SeedOrder(db, "PO-2", other.Id);
        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.PreviewAllocations(invoice.Id, Lines((foreign.Id, 10m))));
    }

    [Fact]
    public async Task 候选订单_只返回同供应商同币种并派生剩余金额()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var other = SeedSupplier(db, "S002", "工厂B");
        var target = SeedOrder(db, "PO-CNY", supplier.Id, Currency.CNY, totalAmount: 1000m);
        SeedOrder(db, "PO-USD", supplier.Id, Currency.USD, totalAmount: 500m);
        SeedOrder(db, "PO-OTHER", other.Id, Currency.CNY, totalAmount: 800m);
        SeedOrder(db, "PO-CANCELLED", supplier.Id, Currency.CNY, DocumentStatus.Cancelled);
        var controller = BuildController(db);

        // 另一张有效发票（含税 200）已占用该订单 200 → 体现「其他有效发票已关联」
        var otherInvoice = await CreateInvoiceAsync(controller, InvoiceDto(
            supplier.Id, "普票", null, "5801", net: 200m, tax: 0m, gross: 200m));
        AssertOk<PurchaseInvoiceDto>(await controller.SaveAllocations(otherInvoice.Id, Lines((target.Id, 200m))));

        // 本发票已关联 100
        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "5802"));
        AssertOk<PurchaseInvoiceDto>(await controller.SaveAllocations(invoice.Id, Lines((target.Id, 100m))));

        var candidates = AssertOk<List<PurchaseInvoiceOrderCandidateDto>>(
            await controller.OrderCandidates(invoice.Id, null, PurchaseInvoiceRules.MaxOrderCandidates));

        Assert.DoesNotContain(candidates, c => c.OrderNo == "PO-USD");
        Assert.DoesNotContain(candidates, c => c.OrderNo == "PO-OTHER");

        var row = Assert.Single(candidates, c => c.OrderNo == "PO-CNY");
        Assert.Equal(1000m, row.OrderedAmount);
        Assert.Equal(100m, row.LinkedByThisInvoice);
        Assert.Equal(200m, row.LinkedByOtherInvoices);
        Assert.Equal(700m, row.RemainingUnallocatedAmount);
        Assert.Equal("CNY", row.Currency);
        Assert.True(row.Eligible);
        Assert.Contains("可关联", row.EligibilityText);

        var cancelledRow = Assert.Single(candidates, c => c.OrderNo == "PO-CANCELLED");
        Assert.True(cancelledRow.Cancelled);
        Assert.False(cancelledRow.Eligible);
        Assert.Contains("已取消", cancelledRow.EligibilityText);

        // 关键字命中采购单号；其他供应商发票的候选为空
        Assert.Equal("PO-CANCELLED", Assert.Single(AssertOk<List<PurchaseInvoiceOrderCandidateDto>>(
            await controller.OrderCandidates(invoice.Id, "CANCELLED", 10))).OrderNo);
        Assert.Empty(AssertOk<List<PurchaseInvoiceOrderCandidateDto>>(
            await controller.OrderCandidates(otherInvoice.Id, "PO-OTHER", 10)));
    }

    [Fact]
    public async Task 非草稿_关联维护被拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var order = SeedOrder(db, "PO-1", supplier.Id);
        var controller = BuildController(db);

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "5901"));
        AssertOk<PurchaseInvoiceDto>(await controller.Record(invoice.Id));

        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.SaveAllocations(invoice.Id, Lines((order.Id, 10m))));
        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.PreviewAllocations(invoice.Id, Lines((order.Id, 10m))));

        AssertOk<PurchaseInvoiceDto>(await controller.Void(invoice.Id,
            new PurchaseInvoiceVoidRequest { Reason = "重开" }));
        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.SaveAllocations(invoice.Id, Lines((order.Id, 10m))));
    }

    // ==================== 4. 登记、作废与非变更边界 ====================

    [Fact]
    public async Task 登记_冻结证据并保留关联与金额()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var order = SeedOrder(db, "PO-1", supplier.Id);
        var controller = BuildController(db);

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "6001"));
        AssertOk<PurchaseInvoiceDto>(await controller.SaveAllocations(invoice.Id, Lines((order.Id, 13m))));

        var recorded = AssertOk<PurchaseInvoiceDto>(await controller.Record(invoice.Id));

        Assert.True(recorded.IsRecorded);
        Assert.Equal("已登记", recorded.StatusText);
        Assert.NotNull(recorded.RecordedAt);
        Assert.Equal(13m, recorded.LinkedAmount);
        Assert.Equal(100m, recorded.UnlinkedAmount);
        Assert.Equal(113m, recorded.GrossAmount);
        Assert.Equal(order.Id, Assert.Single(recorded.Allocations).PurchaseOrderId);

        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Record(invoice.Id));
    }

    [Fact]
    public async Task 作废_原因必填且保留身份金额与关联()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var order = SeedOrder(db, "PO-1", supplier.Id);
        var controller = BuildController(db);

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "6101"));
        AssertOk<PurchaseInvoiceDto>(await controller.SaveAllocations(invoice.Id, Lines((order.Id, 13m))));
        AssertOk<PurchaseInvoiceDto>(await controller.Record(invoice.Id));

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(invoice.Id, new PurchaseInvoiceVoidRequest { Reason = "  " }));

        var voided = AssertOk<PurchaseInvoiceDto>(await controller.Void(
            invoice.Id, new PurchaseInvoiceVoidRequest { Reason = "供应商作废重开" }));

        Assert.True(voided.IsVoided);
        Assert.Equal("已作废", voided.StatusText);
        Assert.Equal("供应商作废重开", voided.VoidReason);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal(113m, voided.GrossAmount);
        Assert.Equal(13m, voided.LinkedAmount);
        Assert.Equal(order.Id, Assert.Single(voided.Allocations).PurchaseOrderId);

        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Void(invoice.Id, new PurchaseInvoiceVoidRequest { Reason = "重复作废" }));
    }

    [Fact]
    public async Task 登记_关联订单被取消后拒绝登记()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var order = SeedOrder(db, "PO-1", supplier.Id);
        var controller = BuildController(db);

        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "6201"));
        AssertOk<PurchaseInvoiceDto>(await controller.SaveAllocations(invoice.Id, Lines((order.Id, 13m))));

        order.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Record(invoice.Id));
        Assert.Contains("已取消", ex.Message);
        Assert.Equal(PurchaseInvoiceRules.StatusDraft,
            await db.PurchaseInvoices.Where(x => x.Id == invoice.Id).Select(x => x.Status).FirstAsync());
    }

    [Fact]
    public async Task 没有发票数据时_既有供应商与采购订单读取不受影响且不回填()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var order = SeedOrder(db, "PO-1", supplier.Id);
        var controller = BuildController(db);

        var page = AssertOk<PagedResult<PurchaseInvoiceDto>>(await controller.GetPaged(new PurchaseInvoiceQuery()));

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
        Assert.Empty(await db.PurchaseInvoices.ToListAsync());
        Assert.Empty(await db.PurchaseInvoiceAllocations.ToListAsync());

        var orderAfter = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(order.OrderNo, orderAfter.OrderNo);
        Assert.Equal(supplier.Id, orderAfter.SupplierId);
        Assert.Equal(1, (await db.BaseSuppliers.AsNoTracking().FirstAsync(s => s.Id == supplier.Id)).Status);
    }

    [Fact]
    public async Task 登记与作废_不改写采购订单库存退税与收付款记录()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var order = SeedOrder(db, "PO-1", supplier.Id, totalAmount: 1000m);

        var stock = new Stock
        {
            WarehouseId = 1, ProductId = 1, Quantity = 10m, AvailableQuantity = 10m,
            AverageCost = 5m, TotalCost = 50m
        };
        var movement = new StockMovement
        {
            MovementDate = new DateTime(2026, 9, 2), MovementType = InventoryMovementType.PurchaseIn,
            SourceDocType = "StockIn", SourceDocNo = "SI-1", WarehouseId = 1, ProductId = 1,
            Quantity = 10m, Amount = 50m
        };
        var taxRefund = new BaseTaxRefund
        {
            RefundNo = "TR-1", RefundPeriod = "2026-08", ExportAmount = 1000m,
            RefundableAmount = 130m, RefundedAmount = 0m, Status = "待申报"
        };
        var payment = new FinancePayment
        {
            PaymentNo = "PAY-1", PaymentDate = new DateTime(2026, 9, 3), SupplierId = supplier.Id,
            Amount = 300m, Currency = Currency.CNY, Status = DocumentStatus.Approved
        };
        db.Stocks.Add(stock);
        db.StockMovements.Add(movement);
        db.BaseTaxRefunds.Add(taxRefund);
        db.FinancePayments.Add(payment);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var invoice = await CreateInvoiceAsync(controller, InvoiceDto(supplier.Id, "普票", null, "6301"));
        AssertOk<PurchaseInvoiceDto>(await controller.SaveAllocations(invoice.Id, Lines((order.Id, 13m))));
        AssertOk<PurchaseInvoiceDto>(await controller.Record(invoice.Id));
        AssertOk<PurchaseInvoiceDto>(await controller.Void(
            invoice.Id, new PurchaseInvoiceVoidRequest { Reason = "以票换票" }));

        var orderAfter = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Approved, orderAfter.Status);
        Assert.Equal("未到货", orderAfter.ArrivalProgress);
        Assert.Equal("未结算", orderAfter.SettlementProgress);
        Assert.Equal(1000m, orderAfter.TotalAmount);
        Assert.False(orderAfter.IsDeleted);

        var stockAfter = await db.Stocks.AsNoTracking().FirstAsync(s => s.Id == stock.Id);
        Assert.Equal(10m, stockAfter.Quantity);
        Assert.Equal(10m, stockAfter.AvailableQuantity);
        Assert.Equal(50m, stockAfter.TotalCost);

        var movementAfter = await db.StockMovements.AsNoTracking().FirstAsync(m => m.Id == movement.Id);
        Assert.Equal(10m, movementAfter.Quantity);
        Assert.Equal(50m, movementAfter.Amount);

        var refundAfter = await db.BaseTaxRefunds.AsNoTracking().FirstAsync(t => t.Id == taxRefund.Id);
        Assert.Equal(0m, refundAfter.RefundedAmount);
        Assert.Equal("待申报", refundAfter.Status);

        var paymentAfter = await db.FinancePayments.AsNoTracking().FirstAsync(p => p.Id == payment.Id);
        Assert.Equal(300m, paymentAfter.Amount);
        Assert.Equal(DocumentStatus.Approved, paymentAfter.Status);

        // 未新增任何库存流水 / 收付款 / 退税记录（登记与作废都不产生财务单据）
        Assert.Equal(1, await db.StockMovements.CountAsync());
        Assert.Equal(1, await db.FinancePayments.CountAsync());
        Assert.Equal(1, await db.BaseTaxRefunds.CountAsync());
        Assert.Equal(0, await db.FinanceReceipts.CountAsync());
        Assert.Equal(0, await db.FinanceExpenses.CountAsync());
    }

    // ==================== 5. 台账过滤与分页 ====================

    [Fact]
    public async Task 台账_支持供应商类型状态币种日期关键字与关联状态过滤()
    {
        using var db = TestDbFactory.Create();
        var supplierA = SeedSupplier(db, "S001", "工厂A");
        var supplierB = SeedSupplier(db, "S002", "工厂B");
        var usdOrder = SeedOrder(db, "PO-USD", supplierA.Id, Currency.USD, totalAmount: 900m);
        var controller = BuildController(db);

        var draftCny = await CreateInvoiceAsync(controller, InvoiceDto(
            supplierA.Id, "普票", null, "7001", date: new DateTime(2026, 9, 1)));
        var usd = await CreateInvoiceAsync(controller, InvoiceDto(
            supplierA.Id, "专票", "0440", "7002", net: 500m, tax: 0m, gross: 500m,
            currency: "USD", date: new DateTime(2026, 9, 15)));
        AssertOk<PurchaseInvoiceDto>(await controller.SaveAllocations(usd.Id, Lines((usdOrder.Id, 500m))));

        var voided = await CreateInvoiceAsync(controller, InvoiceDto(
            supplierB.Id, "普票", null, "7003", date: new DateTime(2026, 10, 1)));
        AssertOk<PurchaseInvoiceDto>(await controller.Void(voided.Id,
            new PurchaseInvoiceVoidRequest { Reason = "重复登记" }));

        // 默认包含已作废历史
        Assert.Equal(3, AssertOk<PagedResult<PurchaseInvoiceDto>>(
            await controller.GetPaged(new PurchaseInvoiceQuery())).Total);

        // 供应商 / 类型 / 状态 / 币种 / 日期区间 / 关键字
        Assert.Equal(2, AssertOk<PagedResult<PurchaseInvoiceDto>>(await controller.GetPaged(
            new PurchaseInvoiceQuery { SupplierId = supplierA.Id })).Total);
        Assert.Equal(1, AssertOk<PagedResult<PurchaseInvoiceDto>>(await controller.GetPaged(
            new PurchaseInvoiceQuery { InvoiceType = "专票" })).Total);
        Assert.Equal(2, AssertOk<PagedResult<PurchaseInvoiceDto>>(await controller.GetPaged(
            new PurchaseInvoiceQuery { Status = PurchaseInvoiceRules.StatusDraft })).Total);
        Assert.Equal(1, AssertOk<PagedResult<PurchaseInvoiceDto>>(await controller.GetPaged(
            new PurchaseInvoiceQuery { Status = PurchaseInvoiceRules.StatusVoided })).Total);
        Assert.Equal(1, AssertOk<PagedResult<PurchaseInvoiceDto>>(await controller.GetPaged(
            new PurchaseInvoiceQuery { Currency = "usd" })).Total);
        Assert.Equal(1, AssertOk<PagedResult<PurchaseInvoiceDto>>(await controller.GetPaged(
            new PurchaseInvoiceQuery
            {
                InvoiceDateFrom = new DateTime(2026, 9, 10),
                InvoiceDateTo = new DateTime(2026, 9, 30)
            })).Total);
        Assert.Equal(1, AssertOk<PagedResult<PurchaseInvoiceDto>>(await controller.GetPaged(
            new PurchaseInvoiceQuery { Keyword = "7002" })).Total);
        Assert.Equal(2, AssertOk<PagedResult<PurchaseInvoiceDto>>(await controller.GetPaged(
            new PurchaseInvoiceQuery { Keyword = "工厂A" })).Total);

        // 关联状态过滤（未关联 / 已全额关联 / 部分关联）
        Assert.Equal(2, AssertOk<PagedResult<PurchaseInvoiceDto>>(await controller.GetPaged(
            new PurchaseInvoiceQuery { LinkageStatus = PurchaseInvoiceRules.LinkageUnlinked })).Total);
        Assert.Equal(1, AssertOk<PagedResult<PurchaseInvoiceDto>>(await controller.GetPaged(
            new PurchaseInvoiceQuery { LinkageStatus = PurchaseInvoiceRules.LinkageLinked })).Total);
        Assert.Equal(0, AssertOk<PagedResult<PurchaseInvoiceDto>>(await controller.GetPaged(
            new PurchaseInvoiceQuery { LinkageStatus = PurchaseInvoiceRules.LinkagePartial })).Total);

        // 未知发票类型 / 关联状态 / 关键字超长一律拒绝，不静默忽略筛选
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new PurchaseInvoiceQuery { InvoiceType = "电子票" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new PurchaseInvoiceQuery { LinkageStatus = "paid" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new PurchaseInvoiceQuery
            {
                Keyword = new string('x', PurchaseInvoiceService.MaxKeywordLength + 1)
            }));

        // 详情：关联行与订单可用性只读标注
        var detail = AssertOk<PurchaseInvoiceDto>(await controller.GetById(usd.Id));
        Assert.Equal(PurchaseInvoiceRules.LinkageLinked, detail.LinkageStatus);
        Assert.True(Assert.Single(detail.Allocations).OrderAvailable);
        Assert.Equal(draftCny.Id, AssertOk<PurchaseInvoiceDto>(
            await controller.GetById(draftCny.Id)).Id);
    }

    [Fact]
    public async Task 台账_分页参数被钳制且分页稳定()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "工厂A");
        var controller = BuildController(db);

        for (var i = 1; i <= 3; i++)
        {
            await CreateInvoiceAsync(controller, InvoiceDto(
                supplier.Id, "普票", null, "8" + i.ToString("000"),
                date: new DateTime(2026, 9, i)));
        }

        var query = new PurchaseInvoiceQuery { Page = 0, PageSize = 100000 };
        query.Normalize();
        Assert.Equal(1, query.Page);
        Assert.Equal(PurchaseInvoiceQuery.MaxPageSize, query.PageSize);

        var firstPage = AssertOk<PagedResult<PurchaseInvoiceDto>>(
            await controller.GetPaged(new PurchaseInvoiceQuery { PageSize = 2 }));
        Assert.Equal(3, firstPage.Total);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(new DateTime(2026, 9, 3), firstPage.Items[0].InvoiceDate);
        Assert.Equal(new DateTime(2026, 9, 2), firstPage.Items[1].InvoiceDate);

        var secondPage = AssertOk<PagedResult<PurchaseInvoiceDto>>(
            await controller.GetPaged(new PurchaseInvoiceQuery { PageSize = 2, Page = 2 }));
        Assert.Equal(new DateTime(2026, 9, 1), Assert.Single(secondPage.Items).InvoiceDate);
    }

    // ==================== 6. 规则、模型、幂等结构与前端接线契约 ====================

    [Fact]
    public void 规则_发票类型身份金额状态与关联判定的纯函数契约()
    {
        Assert.Equal("普票", PurchaseInvoiceRules.NormalizeInvoiceType(" 普票 "));
        Assert.Throws<BusinessException>(() => PurchaseInvoiceRules.NormalizeInvoiceType(""));
        Assert.True(PurchaseInvoiceRules.RequiresInvoiceCode("专票"));
        Assert.False(PurchaseInvoiceRules.RequiresInvoiceCode("普票"));
        Assert.Throws<BusinessException>(() => PurchaseInvoiceRules.NormalizeInvoiceCode(null, "专票"));
        Assert.Equal("", PurchaseInvoiceRules.NormalizeInvoiceCode("   ", "普票"));
        Assert.Equal("ABC123", PurchaseInvoiceRules.NormalizeIdentityPart(" ab-c 123 "));
        Assert.Equal("", PurchaseInvoiceRules.NormalizeIdentityPart("  "));
        Assert.Contains("0001", PurchaseInvoiceRules.IdentityText("普票", "", "0001"));
        Assert.Contains("0440-0001", PurchaseInvoiceRules.IdentityText("专票", "0440", "0001"));
        Assert.Throws<BusinessException>(() => PurchaseInvoiceRules.NormalizeInvoiceNumber(" "));
        Assert.Equal("USD", PurchaseInvoiceRules.NormalizeCurrencyStrict(" usd "));
        Assert.Throws<BusinessException>(() => PurchaseInvoiceRules.NormalizeCurrencyStrict("RUB"));

        var (net, tax, gross) = PurchaseInvoiceRules.ValidateAmounts(100.4m, 0.1m, 100.5m, "CNY");
        Assert.Equal((100.40m, 0.10m, 100.50m), (net, tax, gross));
        Assert.Throws<BusinessException>(() => PurchaseInvoiceRules.ValidateAmounts(100m, 1m, 100m, "CNY"));
        Assert.Equal(1m, PurchaseInvoiceRules.NormalizeAllocationAmount(1m, "CNY"));
        Assert.Throws<BusinessException>(() => PurchaseInvoiceRules.NormalizeAllocationAmount(0.001m, "CNY"));

        Assert.Equal("草稿", PurchaseInvoiceRules.StatusText(PurchaseInvoiceRules.StatusDraft));
        Assert.Equal("已登记", PurchaseInvoiceRules.StatusText(PurchaseInvoiceRules.StatusRecorded));
        Assert.Equal("已作废", PurchaseInvoiceRules.StatusText(PurchaseInvoiceRules.StatusVoided));
        Assert.Throws<BusinessException>(() => PurchaseInvoiceRules.StatusText(9));

        Assert.Equal(PurchaseInvoiceRules.LinkageUnlinked, PurchaseInvoiceRules.LinkageStatusOf(100m, 0m));
        Assert.Equal(PurchaseInvoiceRules.LinkagePartial, PurchaseInvoiceRules.LinkageStatusOf(100m, 50m));
        Assert.Equal(PurchaseInvoiceRules.LinkageLinked, PurchaseInvoiceRules.LinkageStatusOf(100m, 100m));
        Assert.Contains("未关联", PurchaseInvoiceRules.LinkageText(100m, 0m, 0, "CNY"));
        Assert.Contains("部分关联", PurchaseInvoiceRules.LinkageText(100m, 50m, 1, "CNY"));
        Assert.Contains("已全额关联", PurchaseInvoiceRules.LinkageText(100m, 100m, 1, "CNY"));
        Assert.True(PurchaseInvoiceRules.IsSupportedLinkage(PurchaseInvoiceRules.LinkagePartial));
        Assert.False(PurchaseInvoiceRules.IsSupportedLinkage("paid"));
        Assert.Throws<BusinessException>(() => PurchaseInvoiceRules.NormalizeLinkageFilter("paid"));
        Assert.Throws<BusinessException>(() => PurchaseInvoiceRules.NormalizeVoidReason(" "));
        Assert.Throws<BusinessException>(() => PurchaseInvoiceRules.NormalizeRemark(
            new string('x', PurchaseInvoiceRules.MaxRemarkLength + 1)));

        Assert.Equal("供应商可用", PurchaseInvoiceRules.SupplierAvailabilityText(new BaseSupplier { Status = 1 }));
        Assert.Contains("已停用", PurchaseInvoiceRules.SupplierAvailabilityText(new BaseSupplier { Status = 0 }));
        Assert.Contains("已删除", PurchaseInvoiceRules.SupplierAvailabilityText(new BaseSupplier { IsDeleted = true }));
        Assert.True(PurchaseInvoiceRules.IsSupplierSelectable(new BaseSupplier { Status = 1 }));

        // 边界声明必须明确「不是应付账款台账 / 不是税务申报 / 不是付款授权」
        Assert.Contains("不是应付账款台账", PurchaseInvoiceRules.BoundaryText);
        Assert.Contains("不是税务申报系统", PurchaseInvoiceRules.BoundaryText);
        Assert.Contains("不是付款授权", PurchaseInvoiceRules.BoundaryText);
        Assert.Contains("含税总额", PurchaseInvoiceRules.AmountEquationText);
        Assert.Contains("币种都必须与发票一致", PurchaseInvoiceRules.LinkageRuleText);

        // 币种精度口径与装柜费用分摊（ERP-042）共用同一权威实现，不允许两套取整规则
        Assert.Equal(ContainerExpenseAllocationRules.PrecisionOf("jpy"), CurrencyAmountRules.PrecisionOf("JPY"));
        Assert.Equal(ContainerExpenseAllocationRules.RoundAmount(2.345m, "CNY"),
            CurrencyAmountRules.RoundAmount(2.345m, "CNY"));
        Assert.Equal(Enum.GetNames(typeof(Currency)), PurchaseInvoiceRules.SupportedCurrencies);
    }

    [Fact]
    public void 模型配置_精度长度索引过滤与唯一外键契约()
    {
        using var db = TestDbFactory.Create();

        var invoice = db.Model.FindEntityType(typeof(PurchaseInvoice));
        Assert.NotNull(invoice);
        Assert.Equal(2, invoice!.FindProperty(nameof(PurchaseInvoice.NetAmount))!.GetScale());
        Assert.Equal(2, invoice.FindProperty(nameof(PurchaseInvoice.TaxAmount))!.GetScale());
        Assert.Equal(2, invoice.FindProperty(nameof(PurchaseInvoice.GrossAmount))!.GetScale());
        Assert.Equal(50, invoice.FindProperty(nameof(PurchaseInvoice.InvoiceNumber))!.GetMaxLength());
        Assert.Equal(50, invoice.FindProperty(nameof(PurchaseInvoice.NormalizedInvoiceNumber))!.GetMaxLength());
        Assert.Equal(200, invoice.FindProperty(nameof(PurchaseInvoice.SupplierName))!.GetMaxLength());
        Assert.Equal(500, invoice.FindProperty(nameof(PurchaseInvoice.VoidReason))!.GetMaxLength());

        var identity = Assert.Single(invoice.GetIndexes(), i => i.IsUnique && i.Properties.Count == 4);
        Assert.Equal("UX_PurchaseInvoices_ActiveIdentity", identity.GetDatabaseName());
        Assert.Contains("IsDeleted = 0", identity.GetFilter());
        Assert.Contains("Status <> 2", identity.GetFilter());

        var allocation = db.Model.FindEntityType(typeof(PurchaseInvoiceAllocation));
        Assert.NotNull(allocation);
        Assert.Equal(2, allocation!.FindProperty(nameof(PurchaseInvoiceAllocation.AllocatedAmount))!.GetScale());
        Assert.Equal(50, allocation.FindProperty(nameof(PurchaseInvoiceAllocation.OrderNo))!.GetMaxLength());
        var invoiceOrder = Assert.Single(allocation.GetIndexes(), i => i.IsUnique);
        Assert.Equal("UX_PurchaseInvoiceAllocations_InvoiceOrder", invoiceOrder.GetDatabaseName());
        Assert.Contains("IsDeleted = 0", invoiceOrder.GetFilter());

        // 关联行唯一外键只有「关联行 → 发票」：刻意不建到采购订单 / 供应商的外键（历史必须可读）
        var foreignKeys = allocation.GetForeignKeys().ToList();
        Assert.Equal(typeof(PurchaseInvoice), Assert.Single(foreignKeys).PrincipalEntityType.ClrType);
        Assert.DoesNotContain(foreignKeys, fk => fk.PrincipalEntityType.ClrType == typeof(PurchaseOrder));
        Assert.DoesNotContain(foreignKeys, fk => fk.PrincipalEntityType.ClrType == typeof(BaseSupplier));

        // 采购订单侧不新增导航属性（历史订单结构与读取口径完全不变）
        var order = db.Model.FindEntityType(typeof(PurchaseOrder));
        Assert.NotNull(order);
        Assert.DoesNotContain(order!.GetNavigations(), n => n.ClrType == typeof(PurchaseInvoice));
        Assert.DoesNotContain(order.GetNavigations(), n => n.ClrType == typeof(PurchaseInvoiceAllocation));
    }

    [Fact]
    public void Schema_upgrade_幂等建表建索引且不含任何回填语句()
    {
        var script = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.PurchaseInvoices') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.PurchaseInvoices", script);
        Assert.Contains("GrossAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("Status INT NOT NULL DEFAULT 0", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_PurchaseInvoices_ActiveIdentity", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status <> 2;", script);
        Assert.Contains("IF OBJECT_ID('db_owner.PurchaseInvoiceAllocations') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.PurchaseInvoiceAllocations", script);
        Assert.Contains("AllocatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_PurchaseInvoiceAllocations_InvoiceOrder", script);
        Assert.Contains("FK_PurchaseInvoiceAllocations_Invoice", script);

        // 不建到采购订单 / 供应商的外键；本段 SQL 文本里也没有任何回填语句
        Assert.DoesNotContain("FK_PurchaseInvoiceAllocations_PurchaseOrder", script);
        var start = script.IndexOf("// 31. 供应商采购发票登记", StringComparison.Ordinal);
        Assert.True(start > 0);
        var segment = script[start..];
        Assert.DoesNotContain("UPDATE db_owner", segment);
        Assert.DoesNotContain("INSERT INTO db_owner", segment);
        Assert.DoesNotContain("DELETE FROM db_owner", segment);
    }

    [Fact]
    public void 前端与路由接线契约()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/supplier-purchase-invoices.js", index);

        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc.js"));
        Assert.Contains("openPurchaseInvoiceRegister", modules);

        var js = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "supplier-purchase-invoices.js"));
        Assert.Contains("async function openPurchaseInvoiceRegister", js);
        Assert.Contains("'/api/purchase-invoices'", js);
        Assert.Contains("'/api/purchase-invoices?'", js);
        Assert.Contains("/allocations/preview", js);
        Assert.Contains("pirSaveAllocations", js);
        Assert.Contains("/record", js);
        Assert.Contains("/void", js);
        Assert.Contains("/order-candidates?", js);

        var controller = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "PurchaseInvoiceController.cs"));
        Assert.Contains("[Route(\"api/purchase-invoices\")]", controller);
        Assert.Contains("{id:long}/order-candidates", controller);
        Assert.Contains("{id:long}/allocations/preview", controller);
        Assert.Contains("{id:long}/allocations", controller);
        Assert.Contains("{id:long}/record", controller);
        Assert.Contains("{id:long}/void", controller);
    }
}
