using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 单证明细行快照单元测试（ERP-051）。覆盖：允许的单证类型白名单（商业发票 / 装箱单）与其余类型拒绝、
/// 可维护状态（待制作 / 已制作）与冻结状态（已提交客户 / 已使用 / 未知）、行金额服务端计算与币种精度、
/// 数量 / 单价 / 箱数 / 净重 / 毛重与文本校验、行序追加与重复拒绝、商品引用权威快照与
/// 「商品资料变化后历史行不被刷新」、冻结快照不可删改、行合计与表头金额差异提示（不回写表头）、
/// 老单证无明细可读、读取有界与截断、非变更边界（商品 / 订单 / 装柜 / 库存 / 发票 / 退税 / 费用 / 财务），
/// 以及数据模型、幂等升级结构与前端接线契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class TradeDocumentItemTests
{
    // ==================== 0. 测试脚手架 ====================

    private static TradeDocumentController BuildController(ErpDbContext db)
        => new(new GenericService<TradeDocument>(db), db);

    private static TradeDocument SeedDocument(ErpDbContext db, string docNo, string docType,
        string status = "待制作", decimal amount = 0m, string currency = "USD", string remark = "")
    {
        var document = new TradeDocument
        {
            DocNo = docNo,
            DocType = docType,
            Status = status,
            Amount = amount,
            Currency = currency,
            IssueDate = new DateTime(2026, 9, 20),
            CustomerName = "义乌客户",
            Copies = 3,
            Remark = remark,
        };
        db.TradeDocuments.Add(document);
        db.SaveChanges();
        return document;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, string code, string nameCn,
        string nameEn = "Cotton Towel", string spec = "70x140cm", string unit = "箱",
        int status = 1, bool deleted = false)
    {
        var product = new BaseProduct
        {
            ProductCode = code,
            ProductName = nameCn,
            EnglishName = nameEn,
            Spec = spec,
            Unit = unit,
            Status = status,
            IsDeleted = deleted,
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static TradeDocumentItemSaveDto Item(
        decimal quantity = 10m, decimal unitPrice = 2.5m, long productId = 0,
        string productCode = "P001", string productNameCn = "毛巾", string productNameEn = "",
        string spec = "", string unit = "箱", int? packageCount = null, decimal? netWeight = null,
        decimal? grossWeight = null, int? lineOrder = null, string remark = "")
        => new()
        {
            ProductId = productId,
            ProductCode = productCode,
            ProductNameCn = productNameCn,
            ProductNameEn = productNameEn,
            Spec = spec,
            Quantity = quantity,
            Unit = unit,
            UnitPrice = unitPrice,
            PackageCount = packageCount,
            NetWeight = netWeight,
            GrossWeight = grossWeight,
            LineOrder = lineOrder,
            Remark = remark,
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

    /// <summary>断言业务异常的错误码并返回异常（避免只断言消息文案）</summary>
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

    private static async Task<TradeDocumentItemDto> AddItemAsync(
        TradeDocumentController controller, long documentId, TradeDocumentItemSaveDto dto)
        => AssertOk<TradeDocumentItemDto>(await controller.CreateItem(documentId, dto));

    private static async Task<TradeDocumentItemListDto> ListAsync(
        TradeDocumentController controller, long documentId, int take = TradeDocumentItemRules.MaxLinesPerDocument)
        => AssertOk<TradeDocumentItemListDto>(await controller.GetItems(documentId, take));

    // ==================== 1. 商业发票：行金额服务端计算、行序追加、快照字段 ====================

    [Fact]
    public async Task 商业发票_新增两行_行金额服务端计算且行序自动追加()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-001", "商业发票", currency: "USD");
        var controller = BuildController(db);

        var first = await AddItemAsync(controller, document.Id,
            Item(quantity: 10m, unitPrice: 2.5m, productCode: "P001", productNameCn: "毛巾", unit: "箱"));
        var second = await AddItemAsync(controller, document.Id,
            Item(quantity: 3m, unitPrice: 1.005m, productCode: "P002", productNameCn: "浴巾"));

        Assert.Equal(1, first.LineNo);
        Assert.Equal(25m, first.LineAmount);
        Assert.Equal(10m, first.Quantity);
        Assert.Equal(2.5m, first.UnitPrice);
        Assert.Equal("USD", first.Currency);
        Assert.Equal(2, first.AmountDecimals);
        Assert.Equal("毛巾", first.ProductNameCn);
        Assert.Equal("P001", first.ProductCode);
        Assert.Equal("箱", first.Unit);
        Assert.Equal(0, first.ProductId);
        Assert.True(first.ProductAvailable);
        Assert.Contains("未引用商品资料", first.ProductAvailabilityText);

        Assert.Equal(2, second.LineNo);
        /* 3 × 1.005 = 3.015，按美元 2 位小数 0.5 进位 → 3.02（确定性，不使用银行家舍入） */
        Assert.Equal(3.02m, second.LineAmount);
    }

    [Fact]
    public async Task 清单_行合计与表头金额差异只提示_绝不改写单证表头()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-002", "商业发票", amount: 100m, currency: "USD");
        var controller = BuildController(db);
        await AddItemAsync(controller, document.Id, Item(quantity: 10m, unitPrice: 2.5m));
        await AddItemAsync(controller, document.Id, Item(quantity: 3m, unitPrice: 1.005m));

        var list = await ListAsync(controller, document.Id);

        Assert.Equal(2, list.LineCount);
        Assert.False(list.Truncated);
        Assert.Equal(28.02m, list.LineAmountTotal);
        Assert.True(list.HeaderAmountRecorded);
        Assert.True(list.AmountMismatch);
        Assert.Contains("不一致", list.AmountMismatchText);
        Assert.Contains("不会自动改写单证金额", list.AmountMismatchText);
        Assert.Equal("CI-002", list.DocNo);
        Assert.True(list.DocTypeSupported);
        Assert.True(list.Editable);
        Assert.Contains("快照", list.BoundaryText);

        /* 单证表头（金额 / 状态 / 备注）不被明细行维护改写 */
        var reloaded = await db.TradeDocuments.AsNoTracking().FirstAsync(d => d.Id == document.Id);
        Assert.Equal(100m, reloaded.Amount);
        Assert.Equal("待制作", reloaded.Status);
        Assert.Equal(string.Empty, reloaded.Remark);
    }

    [Fact]
    public async Task 装箱单_可选箱数与重量_未填写保持为空且不臆造为零()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "PL-001", "装箱单", status: "已制作", currency: "CNY");
        var controller = BuildController(db);

        var withEvidence = await AddItemAsync(controller, document.Id,
            Item(quantity: 12m, unitPrice: 0m, packageCount: 5, netWeight: 12.5m, grossWeight: 13.75m));
        var withoutEvidence = await AddItemAsync(controller, document.Id,
            Item(quantity: 4m, unitPrice: 0m, productCode: "P002", productNameCn: "浴巾"));

        Assert.Equal(0m, withEvidence.UnitPrice);
        Assert.Equal(0m, withEvidence.LineAmount);
        Assert.Equal(5, withEvidence.PackageCount);
        Assert.Equal(12.5m, withEvidence.NetWeight);
        Assert.Equal(13.75m, withEvidence.GrossWeight);

        Assert.Null(withoutEvidence.PackageCount);
        Assert.Null(withoutEvidence.NetWeight);
        Assert.Null(withoutEvidence.GrossWeight);

        var list = await ListAsync(controller, document.Id);
        Assert.Equal(0m, list.LineAmountTotal);
        Assert.False(list.AmountMismatch);
        Assert.Equal(1, list.PackageCountRecordedLines);
        Assert.Equal(5, list.PackageCountTotal);
        Assert.Equal(1, list.WeightRecordedLines);
        Assert.Equal(12.5m, list.NetWeightTotal);
        Assert.Equal(13.75m, list.GrossWeightTotal);
    }

    [Fact]
    public async Task 装箱单_填写非零单价被拒绝_不含价格口径()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "PL-002", "装箱单");
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 9.9m)));

        Assert.Contains("不含价格口径", ex.Message);
        Assert.Equal(0, await db.TradeDocumentItems.CountAsync());
    }

    // ==================== 2. 单证类型白名单与状态冻结 ====================

    [Theory]
    [InlineData("报关单")]
    [InlineData("形式发票")]
    [InlineData("产地证")]
    [InlineData("提单")]
    [InlineData("订舱确认")]
    [InlineData("外汇核销单")]
    [InlineData("其他")]
    [InlineData("")]
    public async Task 非白名单单证类型_拒绝明细行变更_但清单仍可读(string docType)
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "TD-001", docType);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AddItemAsync(controller, document.Id, Item()));
        Assert.Contains("不允许明细行", ex.Message);
        Assert.Equal(0, await db.TradeDocumentItems.CountAsync());

        var list = await ListAsync(controller, document.Id);
        Assert.False(list.DocTypeSupported);
        Assert.False(list.Editable);
        Assert.Empty(list.Items);
        Assert.Contains("不支持明细行", list.DocTypeText);
    }

    [Theory]
    [InlineData("已提交客户")]
    [InlineData("已使用")]
    [InlineData("草稿")]
    [InlineData("")]
    public async Task 非准备状态_新增修改删除一律拒绝_快照冻结(string status)
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-FROZEN", "商业发票", status: status, amount: 25m);
        var controller = BuildController(db);

        var item = new TradeDocumentItem
        {
            TradeDocumentId = document.Id, LineNo = 1, Quantity = 10m, UnitPrice = 2.5m,
            LineAmount = 25m, Currency = "USD", ProductCode = "P001", ProductNameCn = "毛巾",
        };
        db.TradeDocumentItems.Add(item);
        db.SaveChanges();

        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 1m)));
        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.UpdateItem(item.Id, Item(quantity: 20m, unitPrice: 1m)));
        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.DeleteItem(item.Id));

        var stored = await db.TradeDocumentItems.AsNoTracking().FirstAsync(i => i.Id == item.Id);
        Assert.False(stored.IsDeleted);
        Assert.Equal(10m, stored.Quantity);
        Assert.Equal(25m, stored.LineAmount);

        var list = await ListAsync(controller, document.Id);
        Assert.False(list.Editable);
        Assert.Single(list.Items);
        Assert.Contains("不是准备状态", list.EditabilityText);
    }

    [Theory]
    [InlineData("待制作")]
    [InlineData("已制作")]
    public async Task 准备状态_可新增修改删除明细行(string status)
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-EDIT", "商业发票", status: status);
        var controller = BuildController(db);

        var created = await AddItemAsync(controller, document.Id, Item(quantity: 2m, unitPrice: 3m));
        var updated = AssertOk<TradeDocumentItemDto>(
            await controller.UpdateItem(created.Id, Item(quantity: 4m, unitPrice: 3m)));

        Assert.Equal(4m, updated.Quantity);
        Assert.Equal(12m, updated.LineAmount);

        var deleted = AssertOk<TradeDocumentItemDto>(await controller.DeleteItem(created.Id));
        Assert.Equal(created.Id, deleted.Id);
        Assert.False(await db.TradeDocumentItems.AnyAsync(i => i.Id == created.Id && !i.IsDeleted));
        /* 软删除：行记录（含审计字段）仍保留，只是不再出现在清单中 */
        Assert.True(await db.TradeDocumentItems.AnyAsync(i => i.Id == created.Id));

        var list = await ListAsync(controller, document.Id);
        Assert.Empty(list.Items);
        Assert.Contains("没有明细行", list.AmountMismatchText);
    }

    // ==================== 3. 数量 / 单价 / 重量 / 箱数校验 ====================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10.00005)]
    public async Task 数量非法_一律拒绝(double quantity)
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-QTY", "商业发票");
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AddItemAsync(controller, document.Id, Item(quantity: (decimal)quantity)));
        Assert.Equal(0, await db.TradeDocumentItems.CountAsync());
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(2.00005)]
    public async Task 单价非法_一律拒绝(double unitPrice)
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-PRICE", "商业发票");
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: (decimal)unitPrice)));
    }

    [Fact]
    public async Task 箱数净重毛重非法_一律拒绝_毛重不得小于净重()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "PL-WEIGHT", "装箱单");
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 0m, packageCount: -1)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AddItemAsync(controller, document.Id,
                Item(quantity: 1m, unitPrice: 0m, packageCount: TradeDocumentItemRules.MaxPackageCount + 1)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 0m, netWeight: -0.5m)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 0m, grossWeight: 1.50005m)));

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AddItemAsync(controller, document.Id,
                Item(quantity: 1m, unitPrice: 0m, netWeight: 20m, grossWeight: 19.5m)));
        Assert.Contains("不能小于净重", ex.Message);
        Assert.Equal(0, await db.TradeDocumentItems.CountAsync());
    }

    [Fact]
    public async Task 文本超长或不安全_一律拒绝写入()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-TEXT", "商业发票");
        var controller = BuildController(db);

        var tooLong = new string('好', TradeDocumentItemRules.MaxProductTextLength + 1);
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 1m, productNameCn: tooLong)));

        var markup = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AddItemAsync(controller, document.Id,
                Item(quantity: 1m, unitPrice: 1m, productNameCn: "<script>alert(1)</script>")));
        Assert.Contains("HTML", markup.Message);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AddItemAsync(controller, document.Id,
                Item(quantity: 1m, unitPrice: 1m, remark: "异常\u0001控制字符")));

        Assert.Equal(0, await db.TradeDocumentItems.CountAsync());
    }

    [Fact]
    public async Task 未引用商品时_商品编码与中文名称不能同时为空()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-IDENTITY", "商业发票");
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AddItemAsync(controller, document.Id,
                Item(quantity: 1m, unitPrice: 1m, productCode: "  ", productNameCn: string.Empty)));
        Assert.Contains("商品编码或商品中文名称", ex.Message);
    }

    [Fact]
    public async Task 显式行序重复_一律拒绝_不静默重排()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-LINE", "商业发票");
        var controller = BuildController(db);
        var first = await AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 1m, lineOrder: 3));

        Assert.Equal(3, first.LineNo);

        var ex = await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 1m, lineOrder: 3)));
        Assert.Contains("已存在行序 3", ex.Message);

        var third = await AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 1m));
        Assert.Equal(4, third.LineNo);   // 追加 = 既有最大行序 + 1（不填补空洞、不静默重排）

        await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => controller.UpdateItem(third.Id, Item(quantity: 1m, unitPrice: 1m, lineOrder: 3)));
    }

    // ==================== 4. 商品引用：权威快照、不可用标注、不静默刷新 ====================

    [Fact]
    public async Task 引用商品资料_写入权威快照且忽略客户端提交的商品文本()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-PROD", "商业发票");
        var product = SeedProduct(db, "P100", "毛巾", nameEn: "Towel", spec: "70x140", unit: "箱");
        var controller = BuildController(db);

        var created = await AddItemAsync(controller, document.Id,
            Item(quantity: 2m, unitPrice: 5m, productId: product.Id,
                productCode: "CLIENT-CODE", productNameCn: "客户端名称", spec: "客户端规格", unit: "个"));

        Assert.Equal(product.Id, created.ProductId);
        Assert.Equal("P100", created.ProductCode);
        Assert.Equal("毛巾", created.ProductNameCn);
        Assert.Equal("Towel", created.ProductNameEn);
        Assert.Equal("70x140", created.Spec);
        Assert.Equal("箱", created.Unit);
        Assert.Equal(10m, created.LineAmount);
        Assert.True(created.ProductAvailable);
        Assert.Contains("写入当时的快照", created.ProductAvailabilityText);
    }

    [Fact]
    public async Task 引用不存在或已删除的商品资料_一律拒绝()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-PROD-MISS", "商业发票");
        var deleted = SeedProduct(db, "P200", "已删除毛巾", deleted: true);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 1m, productId: 99_999)));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 1m, productId: deleted.Id)));

        Assert.Equal(0, await db.TradeDocumentItems.CountAsync());
    }

    [Fact]
    public async Task 商品资料后续改名停用删除_历史行快照保持不变且只读标注()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-SNAP", "商业发票");
        var product = SeedProduct(db, "P300", "毛巾", nameEn: "Towel", spec: "70x140");
        var controller = BuildController(db);
        await AddItemAsync(controller, document.Id, Item(quantity: 2m, unitPrice: 5m, productId: product.Id));

        product.ProductName = "改名后毛巾";
        product.EnglishName = "Renamed Towel";
        product.Spec = "99x99";
        product.Status = 0;
        db.SaveChanges();

        var stopped = Assert.Single((await ListAsync(controller, document.Id)).Items);
        Assert.Equal("毛巾", stopped.ProductNameCn);
        Assert.Equal("Towel", stopped.ProductNameEn);
        Assert.Equal("70x140", stopped.Spec);
        Assert.True(stopped.ProductAvailable);
        Assert.Contains("已停用", stopped.ProductAvailabilityText);

        product.IsDeleted = true;
        db.SaveChanges();

        var removed = Assert.Single((await ListAsync(controller, document.Id)).Items);
        Assert.Equal("毛巾", removed.ProductNameCn);
        Assert.False(removed.ProductAvailable);
        Assert.Contains("不存在或已删除", removed.ProductAvailabilityText);
        Assert.Contains("不会自动刷新", removed.ProductAvailabilityText);
    }

    [Fact]
    public async Task 修改行时商品引用未变化_历史快照不被商品资料刷新()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-NOREFRESH", "商业发票");
        var product = SeedProduct(db, "P400", "毛巾", nameEn: "Towel");
        var controller = BuildController(db);
        var created = await AddItemAsync(controller, document.Id,
            Item(quantity: 2m, unitPrice: 5m, productId: product.Id));

        product.ProductName = "改名后毛巾";
        product.EnglishName = "Renamed Towel";
        db.SaveChanges();

        var updated = AssertOk<TradeDocumentItemDto>(await controller.UpdateItem(created.Id,
            Item(quantity: 3m, unitPrice: 5m, productId: product.Id, productNameCn: "客户端提交名称")));

        Assert.Equal("毛巾", updated.ProductNameCn);
        Assert.Equal("Towel", updated.ProductNameEn);
        Assert.Equal(3m, updated.Quantity);
        Assert.Equal(15m, updated.LineAmount);
    }

    [Fact]
    public async Task 修改行显式改指商品资料_重新取权威快照_或清除引用改为人工录入()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-REREF", "商业发票");
        var productA = SeedProduct(db, "P500", "毛巾");
        var productB = SeedProduct(db, "P501", "浴巾", spec: "80x160");
        var controller = BuildController(db);
        var created = await AddItemAsync(controller, document.Id,
            Item(quantity: 1m, unitPrice: 1m, productId: productA.Id));

        var switched = AssertOk<TradeDocumentItemDto>(
            await controller.UpdateItem(created.Id, Item(quantity: 1m, unitPrice: 1m, productId: productB.Id)));
        Assert.Equal(productB.Id, switched.ProductId);
        Assert.Equal("P501", switched.ProductCode);
        Assert.Equal("浴巾", switched.ProductNameCn);
        Assert.Equal("80x160", switched.Spec);

        var freeText = AssertOk<TradeDocumentItemDto>(await controller.UpdateItem(created.Id,
            Item(quantity: 1m, unitPrice: 2m, productId: 0, productCode: "FREE-1", productNameCn: "自由文本毛巾")));
        Assert.Equal(0, freeText.ProductId);
        Assert.Equal("FREE-1", freeText.ProductCode);
        Assert.Equal("自由文本毛巾", freeText.ProductNameCn);
        Assert.Equal(2m, freeText.LineAmount);
        Assert.Contains("未引用商品资料", freeText.ProductAvailabilityText);
    }

    [Fact]
    public async Task 修改或删除不存在与已删除的明细行_返回数据不存在()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-GONE", "商业发票");
        var controller = BuildController(db);
        var created = await AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 1m));

        await controller.DeleteItem(created.Id);

        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.UpdateItem(created.Id, Item(quantity: 2m, unitPrice: 1m)));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.DeleteItem(created.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.UpdateItem(99_999, Item(quantity: 2m, unitPrice: 1m)));
    }

    [Fact]
    public async Task 单证不存在或已删除_清单与新增一律返回数据不存在()
    {
        using var db = TestDbFactory.Create();
        var removed = SeedDocument(db, "CI-DELETED", "商业发票");
        removed.IsDeleted = true;
        db.SaveChanges();
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.NotFound, () => ListAsync(controller, removed.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => AddItemAsync(controller, removed.Id, Item()));
    }

    // ==================== 5. 历史单证、币种精度与读取有界 ====================

    [Fact]
    public async Task 历史单证没有明细行_照实可读且不做任何回填()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-LEGACY", "商业发票", amount: 0m, remark: "历史单证");
        var controller = BuildController(db);

        var list = await ListAsync(controller, document.Id);

        Assert.Empty(list.Items);
        Assert.Equal(0, list.LineCount);
        Assert.Equal(0m, list.LineAmountTotal);
        Assert.False(list.HeaderAmountRecorded);
        Assert.False(list.AmountMismatch);
        Assert.Contains("没有明细行", list.AmountMismatchText);
        Assert.Null(list.PackageCountTotal);
        Assert.Null(list.NetWeightTotal);
        Assert.Null(list.GrossWeightTotal);
        Assert.Equal(0, list.PackageCountRecordedLines);
        Assert.Equal(0, list.WeightRecordedLines);

        /* 读取不产生任何明细行（不做生产回填），单证原始备注保持原样 */
        Assert.Equal(0, await db.TradeDocumentItems.CountAsync());
        Assert.Equal("历史单证", (await db.TradeDocuments.AsNoTracking()
            .FirstAsync(d => d.Id == document.Id)).Remark);
    }

    [Fact]
    public async Task 表头金额未登记_行合计只作参考且不判定差异()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-NOAMOUNT", "商业发票", amount: 0m);
        var controller = BuildController(db);
        await AddItemAsync(controller, document.Id, Item(quantity: 10m, unitPrice: 2.5m));

        var list = await ListAsync(controller, document.Id);

        Assert.False(list.HeaderAmountRecorded);
        Assert.False(list.AmountMismatch);
        Assert.Contains("未登记金额", list.AmountMismatchText);
        Assert.Contains("仅作明细参考", list.AmountMismatchText);
    }

    [Fact]
    public async Task 行金额按单证币种精度取整_日元为零位小数()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-JPY", "商业发票", currency: "JPY");
        var controller = BuildController(db);

        var created = await AddItemAsync(controller, document.Id, Item(quantity: 3m, unitPrice: 10.4m));

        Assert.Equal("JPY", created.Currency);
        Assert.Equal(0, created.AmountDecimals);
        Assert.Equal(31m, created.LineAmount);   // 31.2 → 无小数币种取整 31
    }

    [Fact]
    public async Task 商业发票币种不在系统口径_拒绝明细行()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-AUD", "商业发票", currency: "AUD");
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 1m)));

        Assert.Contains("系统币种口径", ex.Message);
        Assert.Equal(0, await db.TradeDocumentItems.CountAsync());
    }

    [Fact]
    public async Task 装箱单不作币种严格校验_币种快照照实保存()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "PL-AUD", "装箱单", currency: "AUD");
        var controller = BuildController(db);

        var created = await AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 0m));

        Assert.Equal("AUD", created.Currency);
        Assert.Equal(0m, created.LineAmount);
    }

    [Fact]
    public async Task 清单读取有界_超出单次上限时标记截断且不判定差异()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-BOUNDED", "商业发票", amount: 25m);
        var controller = BuildController(db);
        await AddItemAsync(controller, document.Id, Item(quantity: 10m, unitPrice: 2.5m));
        await AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 1m));

        var truncated = await ListAsync(controller, document.Id, take: 1);
        Assert.Equal(1, truncated.Take);
        Assert.Equal(1, truncated.LineCount);
        Assert.True(truncated.Truncated);
        Assert.False(truncated.AmountMismatch);           // 部分合计不判定差异
        Assert.Contains("上限", truncated.AmountMismatchText);

        var capped = await ListAsync(controller, document.Id, take: 100_000);
        Assert.Equal(TradeDocumentItemRules.MaxLinesPerDocument, capped.Take);   // 读取上限收敛
        Assert.Equal(2, capped.LineCount);
        Assert.False(capped.Truncated);
        Assert.True(capped.AmountMismatch);              // 25 ≠ 26 且表头已登记金额
    }

    [Fact]
    public async Task 明细行数达到上限_拒绝继续新增()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "CI-CAP", "商业发票");
        for (var line = 1; line <= TradeDocumentItemRules.MaxLinesPerDocument; line++)
        {
            db.TradeDocumentItems.Add(new TradeDocumentItem
            {
                TradeDocumentId = document.Id, LineNo = line, Quantity = 1m, UnitPrice = 1m,
                LineAmount = 1m, Currency = "USD", ProductCode = "P001", ProductNameCn = "毛巾",
            });
        }
        db.SaveChanges();
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AddItemAsync(controller, document.Id, Item(quantity: 1m, unitPrice: 1m)));

        Assert.Contains("已达上限", ex.Message);
        Assert.Equal(TradeDocumentItemRules.MaxLinesPerDocument, await db.TradeDocumentItems.CountAsync());
    }

    // ==================== 6. 非变更边界：不改写商品资料与相邻业务记录 ====================

    [Fact]
    public async Task 维护明细行_不改写商品资料与相邻业务记录()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, "P900", "毛巾", nameEn: "Towel", spec: "70x140", unit: "箱");

        var salesOrder = new SalesOrder { OrderNo = "SO-1", CustomerId = 0, TotalAmount = 500m, Status = DocumentStatus.Approved };
        var purchaseOrder = new PurchaseOrder
        {
            OrderNo = "PO-1", SupplierId = 0, TotalAmount = 300m, Status = DocumentStatus.Approved,
            ArrivalProgress = "未到货", SettlementProgress = "未结算",
        };
        var loadingList = new ContainerLoadingList
        {
            LoadingListNo = "LL-1", ContainerNo = "CONT1", CustomerId = 0, Status = DocumentStatus.Approved,
            TotalCartons = 10m, TotalWeight = 500m, TotalVolume = 3.5m,
        };
        var stock = new Stock
        {
            WarehouseId = 1, ProductId = product.Id, Quantity = 10m, AvailableQuantity = 10m,
            TotalCost = 20m, AverageCost = 2m,
        };
        var movement = new StockMovement
        {
            WarehouseId = 1, ProductId = product.Id, Quantity = 5m, Direction = 1, UnitCost = 2m,
            Amount = 10m, BalanceQuantity = 5m, SourceDocType = "StockAdjustment", SourceDocNo = "SA-1",
        };
        var payment = new FinancePayment
        {
            PaymentNo = "FK-1", SupplierId = 0, Amount = 100m, Currency = Currency.CNY,
            PaymentMethod = PaymentMethod.BankTransfer, Status = DocumentStatus.Approved,
        };
        var expense = new FinanceExpense { ExpenseNo = "E1", AllocationBase = "不分摊", Amount = 50m };
        var invoice = new PurchaseInvoice
        {
            InvoiceCode = "044001900111", InvoiceNumber = "12345678", SupplierId = 0,
            NetAmount = 100m, GrossAmount = 100m, Currency = "CNY",
        };
        var taxRefund = new BaseTaxRefund
        {
            RefundNo = "TR-1", ExportAmount = 1000m, RefundableAmount = 130m, RefundedAmount = 0m,
        };

        db.AddRange(salesOrder, purchaseOrder, loadingList, stock, movement, payment, expense, invoice, taxRefund);
        db.SaveChanges();

        var document = SeedDocument(db, "CI-BOUND", "商业发票", amount: 10m);
        var controller = BuildController(db);

        var created = await AddItemAsync(controller, document.Id,
            Item(quantity: 1m, unitPrice: 10m, productId: product.Id));
        AssertOk<TradeDocumentItemDto>(await controller.UpdateItem(created.Id,
            Item(quantity: 2m, unitPrice: 10m, productId: product.Id)));
        await controller.DeleteItem(created.Id);

        /* 商品资料完全未被改写（含名称 / 规格 / 单位 / 状态） */
        var storedProduct = await db.BaseProducts.AsNoTracking().FirstAsync(p => p.Id == product.Id);
        Assert.Equal("毛巾", storedProduct.ProductName);
        Assert.Equal("Towel", storedProduct.EnglishName);
        Assert.Equal("70x140", storedProduct.Spec);
        Assert.Equal("箱", storedProduct.Unit);
        Assert.Equal(1, storedProduct.Status);

        /* 相邻业务记录（销售订单 / 采购订单 / 装柜清单 / 库存 / 库存流水 / 付款 / 费用）未被改写 */
        Assert.Equal(500m, (await db.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == salesOrder.Id)).TotalAmount);
        Assert.Equal(DocumentStatus.Approved, (await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == purchaseOrder.Id)).Status);
        Assert.Equal("未到货", (await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == purchaseOrder.Id)).ArrivalProgress);
        Assert.Equal(10m, (await db.ContainerLoadingLists.AsNoTracking().FirstAsync(l => l.Id == loadingList.Id)).TotalCartons);
        Assert.Equal(10m, (await db.Stocks.AsNoTracking().FirstAsync(s => s.Id == stock.Id)).Quantity);
        Assert.Equal(2m, (await db.Stocks.AsNoTracking().FirstAsync(s => s.Id == stock.Id)).AverageCost);
        Assert.Equal(5m, (await db.StockMovements.AsNoTracking().FirstAsync(m => m.Id == movement.Id)).Quantity);
        Assert.Equal(100m, (await db.FinancePayments.AsNoTracking().FirstAsync(p => p.Id == payment.Id)).Amount);
        Assert.Equal(DocumentStatus.Approved, (await db.FinancePayments.AsNoTracking().FirstAsync(p => p.Id == payment.Id)).Status);
        Assert.Equal(50m, (await db.FinanceExpenses.AsNoTracking().FirstAsync(e => e.Id == expense.Id)).Amount);
        Assert.Equal(100m, (await db.PurchaseInvoices.AsNoTracking().FirstAsync(i => i.Id == invoice.Id)).GrossAmount);
        Assert.Equal(130m, (await db.BaseTaxRefunds.AsNoTracking().FirstAsync(t => t.Id == taxRefund.Id)).RefundableAmount);
        Assert.Equal("待申报", (await db.BaseTaxRefunds.AsNoTracking().FirstAsync(t => t.Id == taxRefund.Id)).Status);

        /* 单证台账表头同样保持原样 */
        var reloadedDocument = await db.TradeDocuments.AsNoTracking().FirstAsync(d => d.Id == document.Id);
        Assert.Equal(10m, reloadedDocument.Amount);
        Assert.Equal("待制作", reloadedDocument.Status);
        Assert.Equal(3, reloadedDocument.Copies);
    }

    // ==================== 7. 契约：数据模型、幂等升级、接口路由与前端接线 ====================

    [Fact]
    public void 数据模型契约_行序唯一索引_数值精度_不建商品外键()
    {
        using var db = TestDbFactory.Create();
        var entityType = db.Model.FindEntityType(typeof(TradeDocumentItem));

        Assert.NotNull(entityType);
        /* 表名（db_owner.TradeDocumentItems）由 DbSet 属性名约定生成，其与建表脚本的一致性由幂等升级契约测试断言 */
        Assert.Equal(nameof(TradeDocumentItem), entityType!.ClrType.Name);

        var lineIndex = Assert.Single(entityType.GetIndexes(),
            i => i.GetDatabaseName() == "UX_TradeDocumentItems_Document_LineNo");
        Assert.True(lineIndex.IsUnique);
        Assert.Contains("IsDeleted = 0", lineIndex.GetFilter());
        Assert.Equal(new[] { nameof(TradeDocumentItem.TradeDocumentId), nameof(TradeDocumentItem.LineNo) },
            lineIndex.Properties.Select(p => p.Name).ToArray());

        Assert.Equal(2, entityType.FindProperty(nameof(TradeDocumentItem.LineAmount))!.GetScale());
        Assert.Equal(18, entityType.FindProperty(nameof(TradeDocumentItem.LineAmount))!.GetPrecision());
        Assert.Equal(4, entityType.FindProperty(nameof(TradeDocumentItem.Quantity))!.GetScale());
        Assert.Equal(4, entityType.FindProperty(nameof(TradeDocumentItem.UnitPrice))!.GetScale());
        Assert.Equal(4, entityType.FindProperty(nameof(TradeDocumentItem.NetWeight))!.GetScale());
        Assert.Equal(4, entityType.FindProperty(nameof(TradeDocumentItem.GrossWeight))!.GetScale());

        /* 只与单证主表建外键（级联清理）；**刻意不建**到商品资料的外键，历史快照必须可读 */
        var foreignKey = Assert.Single(entityType.GetForeignKeys());
        Assert.Equal(typeof(TradeDocument), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        Assert.DoesNotContain(entityType.GetForeignKeys(),
            fk => fk.PrincipalEntityType.ClrType == typeof(BaseProduct));
    }

    [Fact]
    public void 幂等升级契约_第35段只建表建索引建外键且不含任何数据改写()
    {
        var path = RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs");
        Assert.True(File.Exists(path), "SchemaUpgrader.cs 不存在：" + path);

        var lines = File.ReadAllLines(path);
        var start = Array.FindIndex(lines, line => line.Contains("35. 单证明细行快照", StringComparison.Ordinal));
        Assert.True(start >= 0, "SchemaUpgrader 缺少 ERP-051 第 35 段（幂等建表）");

        /* 与第 30 / 31 / 33 段同一口径：只看 SQL 语句（去掉 // 注释行） */
        var sql = string.Join('\n', lines.Skip(start)
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.Contains("IF OBJECT_ID('db_owner.TradeDocumentItems') IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE db_owner.TradeDocumentItems", sql, StringComparison.Ordinal);
        Assert.Contains("UX_TradeDocumentItems_Document_LineNo", sql, StringComparison.Ordinal);
        Assert.Contains("FK_TradeDocumentItems_TradeDocument", sql, StringComparison.Ordinal);
        Assert.Contains("PackageCount INT NULL", sql, StringComparison.Ordinal);          // 未登记箱数为 NULL，不臆造为 0
        Assert.Contains("NetWeight DECIMAL(18,4) NULL", sql, StringComparison.Ordinal);
        Assert.Contains("LineAmount DECIMAL(18,2) NOT NULL DEFAULT 0", sql, StringComparison.Ordinal);
        Assert.Contains("Quantity DECIMAL(18,4) NOT NULL DEFAULT 0", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE IsDeleted = 0;", sql, StringComparison.Ordinal);

        /* 幂等补齐不得回填 / 改写既有数据，也不得改动单证主表（与 ERP-045 / ERP-047 同一断言口径） */
        foreach (var forbidden in new[] { "UPDATE ", "DELETE ", "DROP ", "MERGE ", "TRUNCATE " })
            Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("ALTER TABLE db_owner.TradeDocuments", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BaseProducts", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SalesOrders", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ContainerLoadingLists", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void 保存请求契约_不接受客户端金额与币种()
    {
        var properties = typeof(TradeDocumentItemSaveDto).GetProperties().Select(p => p.Name).ToList();

        Assert.Contains(nameof(TradeDocumentItemSaveDto.Quantity), properties);
        Assert.Contains(nameof(TradeDocumentItemSaveDto.UnitPrice), properties);
        Assert.DoesNotContain("LineAmount", properties);      // 金额一律服务端计算
        Assert.DoesNotContain("Amount", properties);
        Assert.DoesNotContain("Currency", properties);        // 币种取自单证台账
        Assert.DoesNotContain("TradeDocumentId", properties); // 单证 Id 只走路由
    }

    [Fact]
    public void 控制器路由契约_明细行四式齐全()
    {
        var routes = typeof(TradeDocumentController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()
                    .Select(a => new { m.Name, a.Template }))
            .ToList();

        Assert.Contains(routes, r => r.Name == "GetItems" && r.Template == "{id:long}/items");
        Assert.Contains(routes, r => r.Name == "CreateItem" && r.Template == "{id:long}/items");
        Assert.Contains(routes, r => r.Name == "UpdateItem" && r.Template == "items/{itemId:long}");
        Assert.Contains(routes, r => r.Name == "DeleteItem" && r.Template == "items/{itemId:long}");
    }

    [Fact]
    public void 读取契约_一次批量装载商品且不存在逐行查询()
    {
        var service = File.ReadAllText(
            RepoFile("src", "ERP.Application", "Services", "TradeDocumentItemService.cs"));

        Assert.Contains("LoadProductsAsync", service);
        Assert.Contains("ids.Contains(p.Id)", service);              // 商品引用一次批量装载
        Assert.Contains("Take(limit + 1)", service);                 // 有界读取 + 截断判定
        Assert.Contains("TradeDocumentItemRules.MaxLinesPerDocument", service);
        Assert.Contains("AsNoTracking()", service);                  // 读取不跟踪、不写库
    }

    [Fact]
    public void 前端接线契约_脚本注册_行操作与接口口径()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/trade-doc-items.js", index);

        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules.js"));
        Assert.Contains("manageTradeDocumentItems", modules);
        Assert.Contains("商品明细行", modules);

        var script = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "trade-doc-items.js"));
        Assert.Contains("/api/trade/documents", script);
        Assert.Contains("items/", script);
        Assert.Contains("准备状态", script);                         // 冻结状态不可删改
        Assert.Contains("不会", script);                             // 声明不改写来源与表头
        Assert.Contains("未登记", script);                           // 空值不臆造为 0
        Assert.Contains("amountMismatchText", script);                // 展示差异提示
        Assert.Contains("escapeHtml", script);                      // 纯文本渲染，避免注入
    }
}
