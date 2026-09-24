using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 出口单证附件证据单元测试（ERP-062）：把**既有**单证台账（<see cref="TradeDocument"/>）接入
/// ERP-061 的**同一**附件证据模型（同一张 <c>AttachmentEvidences</c> 表、同一 <c>IAttachmentContentStore</c>
/// 内容接缝、同一套格式 / 大小 / 下载 / 作废口径），覆盖：
/// <list type="bullet">
/// <item>归属白名单扩展到三类后的**显式类型 + Id** 归集（其余类型仍一律拒绝，绝不按编号 / 文件名猜测）；</item>
/// <item>单证不存在 / 已软删除 / Id 非法时上传与清单拒绝，单证软删除后历史证据仍可只读查看、不可下载、绝不改派；</item>
/// <item>作废（必填原因、保留原始文件名 / 摘要 / 媒体类型 / 登记历史、重复作废拒绝、无硬删除与二进制替换）；</item>
/// <item>单证列表按当前页 Id 的**有界摘要**（有效 / 已作废计数 + 归属可用性）：常数级数据集访问、零存储访问，
///   内容只在显式带认证下载时才被读取；</item>
/// <item>既有的「附件说明 / 存放位置」自由文本保持原样可读，不解析、不抓取、不转附件、不在请求时回填；</item>
/// <item>编号 / 文件名 / 摘要相同的不同单证绝不合并或改派；</item>
/// <item>上传 / 读取 / 下载 / 作废 / 摘要都**不**改写单证状态、金额、明细行与来源销售订单 / 装柜清单、
///   库存与流水、费用、税务与结算记录；</item>
/// <item>控制器契约（<c>owner-summary</c> 端点、类级授权、无 PUT / PATCH / DELETE）与前端接线契约
///   （单证中心行操作「附件证据」与工具栏「附件证据概览」）。</item>
/// </list>
/// <para>全部使用内存库（<see cref="TestDbFactory"/>）与**测试内**的内存内容存储：不连接 SQL Server、
/// 不执行任何 SQL / 部署脚本、不访问生产 OSS、不做任何浏览器 / UI 验收（浏览器验收按项目策略
/// 记录为 browser_deferred，延后到 FINAL-UI-ACCEPTANCE）。</para>
/// </summary>
public class TradeDocumentAttachmentEvidenceTests
{
    // ==================== 0. 测试脚手架 ====================

    private static byte[] PdfBytes(string body = "abc") => Encoding.UTF8.GetBytes("%PDF-1.4\n" + body);

    private static byte[] PngBytes()
        => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };

    /// <summary>播种一张出口单证（可选历史「附件说明 / 存放位置」文本；只用既有字段，不新增列）</summary>
    private static TradeDocument SeedTradeDocument(
        ErpDbContext db,
        string docNo,
        string docType = "商业发票",
        string status = "已制作",
        bool deleted = false,
        string fileNote = "",
        bool withItem = false)
    {
        var document = new TradeDocument
        {
            DocNo = docNo,
            DocType = docType,
            IssueDate = new DateTime(2026, 9, 10),
            DeclareNo = "DEC-062",
            RefNo = "CONT-062",
            SalesOrderNo = "SO-062",
            CustomerId = 701,
            CustomerName = "测试客户",
            Amount = 4321.09m,
            Currency = "USD",
            DeparturePort = "NINGBO",
            DestinationPort = "HAMBURG",
            IssuedBy = "测试出证机构",
            Copies = 3,
            Status = status,
            FileNote = fileNote,
            Remark = "单证备注",
            IsDeleted = deleted
        };
        db.TradeDocuments.Add(document);
        db.SaveChanges();

        if (withItem)
        {
            db.TradeDocumentItems.Add(new TradeDocumentItem
            {
                TradeDocumentId = document.Id,
                LineNo = 1,
                ProductId = 9001,
                ProductCode = "P-062",
                ProductNameCn = "测试商品",
                Quantity = 10m,
                Unit = "箱",
                UnitPrice = 12.5m,
                LineAmount = 125m,
                PackageCount = 4,
                NetWeight = 20m,
                GrossWeight = 22m,
                Currency = "USD"
            });
            db.SaveChanges();
        }

        return document;
    }

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string orderNo)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = 701,
            Currency = Currency.USD,
            TotalAmount = 4321.09m,
            DepositAmount = 500m,
            ShippingMarks = "MARKS-062",
            Remark = "销售订单备注",
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();

        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id,
            ProductId = 9001,
            ProductName = "测试商品",
            Quantity = 10m
        });
        db.SaveChanges();
        return order;
    }

    /// <summary>播种相邻业务记录（装柜清单 / 库存与流水 / 费用单），用于非变更断言</summary>
    private static void SeedAdjacentRecords(ErpDbContext db)
    {
        db.ContainerLoadingLists.Add(new ContainerLoadingList
        {
            LoadingListNo = "LL-062",
            LoadingDate = new DateTime(2026, 9, 5),
            ContainerNo = "CONT-062",
            CustomerId = 701,
            ShippingMark = "MARKS-062",
            TotalCartons = 4m,
            TotalWeight = 22m,
            TotalVolume = 1.5m,
            Status = DocumentStatus.Approved,
            Remark = "装柜清单备注"
        });
        db.Stocks.Add(new Stock
        {
            WarehouseId = 1,
            ProductId = 9001,
            Quantity = 100m,
            AvailableQuantity = 100m,
            LockedQuantity = 0m
        });
        db.StockMovements.Add(new StockMovement
        {
            MovementDate = new DateTime(2026, 9, 3),
            MovementType = InventoryMovementType.PurchaseIn,
            SourceDocType = "StockIn",
            SourceDocNo = "RK-062",
            WarehouseId = 1,
            WarehouseName = "主仓"
        });
        db.FinanceExpenses.Add(new FinanceExpense
        {
            ExpenseNo = "FY-062",
            ExpenseDate = new DateTime(2026, 9, 6),
            ExpenseType = "报关费",
            Amount = 320m,
            Currency = "CNY",
            AmountCny = 320m,
            PaymentStatus = "未付",
            AllocatedAmount = 160m,
            Remark = "费用单备注"
        });
        db.SaveChanges();
    }

    /// <summary>相邻业务记录的只读快照（断言出口单证附件证据不改写任何既有记录）</summary>
    private static string Snapshot(ErpDbContext db)
    {
        var documents = db.TradeDocuments.AsNoTracking().OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.DocNo, d.DocType, d.Status, d.Amount, d.Currency, d.Copies, d.FileNote, d.Remark, d.IsDeleted })
            .ToList();
        var items = db.TradeDocumentItems.AsNoTracking().OrderBy(i => i.Id)
            .Select(i => new { i.Id, i.TradeDocumentId, i.LineNo, i.ProductId, i.Quantity, i.UnitPrice, i.LineAmount, i.PackageCount })
            .ToList();
        var salesOrders = db.SalesOrders.AsNoTracking().OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.OrderNo, o.Status, o.TotalAmount, o.ShippingMarks, o.Remark }).ToList();
        var salesDetails = db.SalesOrderDetails.AsNoTracking().OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.SalesOrderId, d.ProductId, d.Quantity }).ToList();
        var loadingLists = db.ContainerLoadingLists.AsNoTracking().OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.LoadingListNo, l.ContainerNo, l.TotalCartons, l.TotalWeight, l.Status }).ToList();
        var stocks = db.Stocks.AsNoTracking().OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.Quantity, s.LockedQuantity }).ToList();
        var movements = db.StockMovements.AsNoTracking().OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.MovementType, m.SourceDocNo, m.WarehouseName }).ToList();
        var expenses = db.FinanceExpenses.AsNoTracking().OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.ExpenseNo, e.Amount, e.AllocatedAmount, e.PaymentStatus }).ToList();

        return string.Join("|", new[]
        {
            System.Text.Json.JsonSerializer.Serialize(documents),
            System.Text.Json.JsonSerializer.Serialize(items),
            System.Text.Json.JsonSerializer.Serialize(salesOrders),
            System.Text.Json.JsonSerializer.Serialize(salesDetails),
            System.Text.Json.JsonSerializer.Serialize(loadingLists),
            System.Text.Json.JsonSerializer.Serialize(stocks),
            System.Text.Json.JsonSerializer.Serialize(movements),
            System.Text.Json.JsonSerializer.Serialize(expenses)
        });
    }

    /// <summary>上传一条出口单证附件证据并断言登记成功（默认 PDF，文件名为中文扫描件）</summary>
    private static async Task<AttachmentEvidenceDto> UploadOkAsync(
        ErpDbContext db, IAttachmentContentStore store, byte[] content, long ownerId,
        string fileName = "扫描件.pdf",
        string declaredContentType = AttachmentEvidenceRules.MediaPdf,
        string description = "")
    {
        var saved = await AttachmentEvidenceService.UploadAsync(
            db,
            store,
            new AttachmentEvidenceUploadRequest
            {
                OwnerType = AttachmentEvidenceRules.OwnerTypeTradeDocument,
                OwnerId = ownerId,
                FileName = fileName,
                DeclaredContentType = declaredContentType,
                DeclaredLength = content.LongLength,
                Description = description,
                Content = new MemoryStream(content, writable: false)
            },
            uploadedBy: "测试用户",
            uploadedById: 7);

        Assert.Equal(AttachmentEvidenceRules.StatusActive, saved.Status);
        Assert.Equal(AttachmentEvidenceRules.Sha256Length, saved.Sha256.Length);
        return saved;
    }

    /// <summary>断言业务异常的错误码（避免只断言消息文案）</summary>
    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    /// <summary>构造控制器（下载等用例需要 HttpContext 才能写入防御性响应头）</summary>
    private static AttachmentEvidenceController BuildController(ErpDbContext db, IAttachmentContentStore store)
        => new(db, store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
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

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    /// <summary>
    /// 测试内内存内容存储（实现唯一内容接缝）：键同样由「服务端」生成的不透明标识；
    /// 额外统计保存 / 打开 / 长度探测次数，用于断言摘要接口**完全不访问存储**。
    /// </summary>
    private sealed class InMemoryAttachmentContentStore : IAttachmentContentStore
    {
        public Dictionary<string, byte[]> Items { get; } = new();

        public int SaveCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public int LengthCalls { get; private set; }
        public int TotalCalls => SaveCalls + OpenCalls + LengthCalls;

        public bool IsProductionProvider { get; set; }

        public string ProviderCode { get; set; } = AttachmentEvidenceRules.ProviderIsolatedLocal;

        public string ProviderText { get; set; } =
            AttachmentEvidenceRules.ProviderText(AttachmentEvidenceRules.ProviderIsolatedLocal);

        public Task<string> SaveAsync(Stream content, string extension, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var key = $"{DateTime.UtcNow:yyyyMMdd}/{Guid.NewGuid():N}{extension}";
            Items[key] = buffer.ToArray();
            return Task.FromResult(key);
        }

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            return Task.FromResult<Stream?>(Items.TryGetValue(storageKey, out var bytes)
                ? new MemoryStream(bytes, writable: false)
                : null);
        }

        public Task<long?> GetLengthAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            LengthCalls++;
            return Task.FromResult<long?>(Items.TryGetValue(storageKey, out var bytes) ? bytes.LongLength : null);
        }
    }

    // ==================== 1. 上传：同一模型的服务端权威元数据 ====================

    [Fact]
    public async Task 上传到出口单证_元数据与归属快照由服务端生成且边界区分仓库证据()
    {
        using var db = TestDbFactory.Create();
        var document = SeedTradeDocument(db, "DOC-062-A", docType: "报关单", status: "已提交客户");
        var store = new InMemoryAttachmentContentStore();
        var before = DateTime.Now;

        var pdf = PdfBytes();
        var saved = await UploadOkAsync(db, store, pdf, document.Id, description: "报关单扫描件（仓库留存）");

        Assert.Equal(AttachmentEvidenceRules.OwnerTypeTradeDocument, saved.OwnerType);
        Assert.Equal("出口单证", saved.OwnerTypeText);
        Assert.Equal(document.Id, saved.OwnerId);
        Assert.Equal("DOC-062-A", saved.OwnerNo);
        Assert.Equal("出口单证 DOC-062-A", saved.OwnerSnapshotText);
        Assert.Equal(AttachmentEvidenceRules.MediaPdf, saved.MediaType);
        Assert.Equal(pdf.LongLength, saved.SizeBytes);
        Assert.Equal("扫描件.pdf", saved.OriginalFileName);
        Assert.Equal("报关单扫描件（仓库留存）", saved.Description);
        Assert.Equal("测试用户", saved.UploadedBy);
        Assert.True(saved.OwnerAvailable);
        Assert.True(saved.ContentDownloadable);
        Assert.True(saved.RecordedAt >= before && saved.RecordedAt <= DateTime.Now);
        Assert.Equal(AttachmentEvidenceRules.ProviderIsolatedLocal, saved.StorageProvider);
        Assert.Equal(AttachmentEvidenceRules.ContentApiPath(saved.Id), saved.DownloadPath);

        // 边界：单证证据是仓库留存，不是报关 / 报税 / 承运人确认，也不代表单证已提交或放行
        Assert.Contains("不是报关单回执", saved.BoundaryText);
        Assert.Contains("不代表单证已提交", saved.BoundaryText);

        // 存储键由服务端生成、不含原始文件名、不经接口返回
        Assert.Equal(1, store.SaveCalls);
        var key = store.Items.Keys.Single();
        Assert.Matches(@"^\d{8}/[0-9a-f]{32}\.pdf$", key);
        Assert.DoesNotContain("扫描", key);
        Assert.DoesNotContain("StorageKey",
            typeof(AttachmentEvidenceDto).GetProperties().Select(p => p.Name).ToList());

        // 单证台账本身未被改写（状态 / 金额 / 份数保持原样）
        var persisted = await db.TradeDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        Assert.Equal("已提交客户", persisted.Status);
        Assert.Equal(4321.09m, persisted.Amount);
        Assert.Equal(3, persisted.Copies);
    }

    [Theory]
    [InlineData("ContainerLoadingList")]
    [InlineData("TradeDocuments")]
    [InlineData("trade-document")]
    [InlineData("")]
    public async Task 未接入的归属类型仍一律拒绝且不落库不落盘(string ownerType)
    {
        using var db = TestDbFactory.Create();
        var document = SeedTradeDocument(db, "DOC-062-W");
        var store = new InMemoryAttachmentContentStore();
        var content = PdfBytes();

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.UploadAsync(
            db,
            store,
            new AttachmentEvidenceUploadRequest
            {
                OwnerType = ownerType,
                OwnerId = document.Id,
                FileName = "证据.pdf",
                DeclaredContentType = AttachmentEvidenceRules.MediaPdf,
                DeclaredLength = content.LongLength,
                Content = new MemoryStream(content, writable: false)
            },
            "测试用户", 7));

        Assert.Contains("归属单据类型", ex.Message);
        Assert.Contains("TradeDocument", ex.Message);          // 白名单回显包含已接入的单证类型
        Assert.Equal(0, store.SaveCalls);
        Assert.Empty(await db.AttachmentEvidences.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 单证不存在已删除或Id非法时上传与清单一律拒绝()
    {
        using var db = TestDbFactory.Create();
        var alive = SeedTradeDocument(db, "DOC-062-B");
        var deleted = SeedTradeDocument(db, "DOC-062-B2", deleted: true);
        var store = new InMemoryAttachmentContentStore();

        await AssertBusinessAsync(ErrorCodes.NotFound, () => UploadOkAsync(db, store, PdfBytes(), 999_999));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => UploadOkAsync(db, store, PdfBytes(), deleted.Id));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UploadOkAsync(db, store, PdfBytes(), 0));
        Assert.Equal(0, store.SaveCalls);
        Assert.Empty(await db.AttachmentEvidences.AsNoTracking().ToListAsync());

        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, deleted.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, 999_999));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, 0));

        var saved = await UploadOkAsync(db, store, PdfBytes("ok"), alive.Id);
        Assert.Equal(alive.Id, saved.OwnerId);
        Assert.Equal(1, await db.AttachmentEvidences.AsNoTracking().CountAsync());
    }

    // ==================== 2. 单证软删除后的历史证据（可读 / 不可下载 / 绝不改派） ====================

    [Fact]
    public async Task 单证软删除后历史证据仍可只读查看_不可下载且绝不改派()
    {
        using var db = TestDbFactory.Create();
        var document = SeedTradeDocument(db, "DOC-062-C");
        var other = SeedTradeDocument(db, "DOC-062-C2");
        var store = new InMemoryAttachmentContentStore();

        var mine = await UploadOkAsync(db, store, PdfBytes(), document.Id);
        var theirs = await UploadOkAsync(db, store, PdfBytes("other"), other.Id, fileName: "他单证证据.pdf");
        var key = await db.AttachmentEvidences.AsNoTracking()
            .Where(r => r.Id == mine.Id).Select(r => r.StorageKey).SingleAsync();

        // 模拟单证被软删除（只改台账标记，不改证据行）
        var persisted = await db.TradeDocuments.FirstAsync(d => d.Id == document.Id);
        persisted.IsDeleted = true;
        await db.SaveChangesAsync();

        // 详情：照实标注归属不可用，历史证据仍可只读查看
        var detail = await AttachmentEvidenceService.GetAsync(db, mine.Id);
        Assert.False(detail.OwnerAvailable);
        Assert.False(detail.ContentDownloadable);
        Assert.Equal("DOC-062-C", detail.OwnerNo);
        Assert.Contains("已不存在或已删除", detail.OwnerAvailabilityText);

        // 台账：历史证据照常可读并标注不可用；另一张单证的证据不受影响
        var ledger = await AttachmentEvidenceService.ListAsync(db,
            new AttachmentEvidenceQuery { OwnerType = AttachmentEvidenceRules.OwnerTypeTradeDocument });
        Assert.Equal(2, ledger.Total);
        Assert.False(ledger.Items.Single(r => r.Id == mine.Id).OwnerAvailable);
        Assert.True(ledger.Items.Single(r => r.Id == theirs.Id).OwnerAvailable);

        // 下载：归属不可用 → 拒绝（且不打开存储）
        var openCallsBefore = store.OpenCalls;
        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AttachmentEvidenceService.OpenContentAsync(db, store, mine.Id));
        Assert.Equal(openCallsBefore, store.OpenCalls);

        // 新的上挂 / 按归属清单：软删除单证一律拒绝
        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, document.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => UploadOkAsync(db, store, PdfBytes("new"), document.Id));

        // 不改派：证据行仍只属于原单证，存储对象仍在隔离存储中（保留留痕）
        var row = await db.AttachmentEvidences.AsNoTracking().SingleAsync(r => r.Id == mine.Id);
        Assert.Equal(document.Id, row.OwnerId);
        Assert.Equal("DOC-062-C", row.OwnerNo);
        Assert.Equal(key, row.StorageKey);
        Assert.True(store.Items.ContainsKey(key));

        // 历史证据仍可显式作废（更正留痕不被软删除的单证阻断），并保留原始元数据
        var voided = await AttachmentEvidenceService.VoidAsync(db, mine.Id, "单证已删除，证据作废留痕");
        Assert.True(voided.IsVoided);
        Assert.False(voided.OwnerAvailable);
        Assert.Equal("DOC-062-C", voided.OwnerNo);
        Assert.Equal(row.OriginalFileName, voided.OriginalFileName);
        Assert.Equal(row.Sha256, voided.Sha256);
    }

    [Fact]
    public async Task 作废保留原始元数据与历史_重复作废拒绝_不硬删除不替换二进制()
    {
        using var db = TestDbFactory.Create();
        var document = SeedTradeDocument(db, "DOC-062-D");
        var store = new InMemoryAttachmentContentStore();
        var saved = await UploadOkAsync(db, store, PngBytes(), document.Id,
            fileName: "订舱确认.png",
            declaredContentType: AttachmentEvidenceRules.MediaPng,
            description: "订舱确认截图");

        // 原因必填：仍有效时用空白原因作废 → 参数错误，且证据状态不变
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AttachmentEvidenceService.VoidAsync(db, saved.Id, "   "));
        Assert.Equal(AttachmentEvidenceRules.StatusActive,
            (await db.AttachmentEvidences.AsNoTracking().SingleAsync(r => r.Id == saved.Id)).Status);

        var voided = await AttachmentEvidenceService.VoidAsync(db, saved.Id, " 上传错误：应为报关单扫描件 ");
        Assert.True(voided.IsVoided);
        Assert.Equal("上传错误：应为报关单扫描件", voided.VoidReason);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal(saved.OriginalFileName, voided.OriginalFileName);
        Assert.Equal(saved.Sha256, voided.Sha256);
        Assert.Equal(saved.SizeBytes, voided.SizeBytes);
        Assert.Equal(saved.MediaType, voided.MediaType);
        Assert.Equal("DOC-062-D", voided.OwnerNo);
        Assert.False(voided.ContentDownloadable);
        Assert.Contains("不提供下载", voided.DownloadAvailabilityText);

        // 不作硬删除：行仍在、审计字段保留（内容本体仍在隔离存储中）
        var row = await db.AttachmentEvidences.AsNoTracking().SingleAsync(r => r.Id == saved.Id);
        Assert.False(row.IsDeleted);
        Assert.Equal(AttachmentEvidenceRules.StatusVoided, row.Status);

        // 重复作废拒绝（状态优先于原因校验：已作废时即使原因为空也报「不重复作废」）
        await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => AttachmentEvidenceService.VoidAsync(db, saved.Id, "再作废一次"));
        await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => AttachmentEvidenceService.VoidAsync(db, saved.Id, "   "));

        // 下载已作废证据：拒绝
        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AttachmentEvidenceService.OpenContentAsync(db, store, saved.Id));

        // 归属清单：默认含已作废历史，可按状态收敛
        Assert.Single(await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, document.Id));
        Assert.Empty(await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, document.Id, AttachmentEvidenceRules.StatusActive));
        Assert.Single(await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, document.Id, AttachmentEvidenceRules.StatusVoided));
    }

    // ==================== 3. 台账筛选 / 归属候选（显式选择，状态取台账原文） ====================

    [Fact]
    public async Task 台账筛选按类型与归属收敛_候选含各单证类型与台账状态原文()
    {
        using var db = TestDbFactory.Create();
        var invoice = SeedTradeDocument(db, "DOC-062-E1", docType: "商业发票", status: "已制作");
        var packing = SeedTradeDocument(db, "DOC-062-E2", docType: "装箱单", status: "待制作");
        SeedTradeDocument(db, "DOC-062-E3", docType: "提单", status: "已使用", deleted: true);
        var order = SeedSalesOrder(db, "SO-062-E");
        var store = new InMemoryAttachmentContentStore();

        var evidence = await UploadOkAsync(db, store, PdfBytes(), invoice.Id);
        await UploadOkAsync(db, store, PngBytes(), packing.Id,
            fileName: "装箱单.png", declaredContentType: AttachmentEvidenceRules.MediaPng);

        var orderContent = PdfBytes("so");
        await AttachmentEvidenceService.UploadAsync(db, store, new AttachmentEvidenceUploadRequest
        {
            OwnerType = AttachmentEvidenceRules.OwnerTypeSalesOrder,
            OwnerId = order.Id,
            FileName = "订单证据.pdf",
            DeclaredContentType = AttachmentEvidenceRules.MediaPdf,
            DeclaredLength = orderContent.LongLength,
            Content = new MemoryStream(orderContent, writable: false)
        }, "测试用户", 7);

        // 台账：按归属类型 / 归属 Id 收敛（销售订单证据不会混进单证行）
        var byType = await AttachmentEvidenceService.ListAsync(db,
            new AttachmentEvidenceQuery { OwnerType = AttachmentEvidenceRules.OwnerTypeTradeDocument });
        Assert.Equal(2, byType.Total);
        Assert.All(byType.Items, r => Assert.Equal("出口单证", r.OwnerTypeText));

        var byOwner = await AttachmentEvidenceService.ListAsync(db, new AttachmentEvidenceQuery
        {
            OwnerType = AttachmentEvidenceRules.OwnerTypeTradeDocument,
            OwnerId = invoice.Id
        });
        Assert.Equal(1, byOwner.Total);
        Assert.Equal(evidence.Id, byOwner.Items[0].Id);

        // 关键字同时匹配单证编号与文件名
        Assert.Equal(2, (await AttachmentEvidenceService.ListAsync(db,
            new AttachmentEvidenceQuery { OwnerType = AttachmentEvidenceRules.OwnerTypeTradeDocument, Keyword = "DOC-062-E" })).Total);

        // 归属候选：只列未删除单证，状态取台账原文（只读标注）
        var options = await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, null);
        Assert.Equal(2, options.Count);
        Assert.All(options, o => Assert.Equal("出口单证", o.OwnerTypeText));
        Assert.Equal("已制作", options.Single(o => o.OwnerId == invoice.Id).StatusText);
        Assert.Equal("待制作", options.Single(o => o.OwnerId == packing.Id).StatusText);
        Assert.Equal(1, options.Single(o => o.OwnerId == invoice.Id).EvidenceCount);
        Assert.Contains("单证类型 商业发票", options.Single(o => o.OwnerId == invoice.Id).SummaryText);
        Assert.Contains("台账状态 待制作", options.Single(o => o.OwnerId == packing.Id).SummaryText);

        Assert.Single(await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, "DOC-062-E2"));

        // 类型大小写不敏感，服务端统一输出规范写法
        var lower = await AttachmentEvidenceService.ListOwnerOptionsAsync(db, "tradedocument", null);
        Assert.Equal(2, lower.Count);
        Assert.All(lower, o => Assert.Equal(AttachmentEvidenceRules.OwnerTypeTradeDocument, o.OwnerType));

        // 常数级数据集访问：候选只查证据表 + 单证表（不会顺带查询销售订单 / 采购订单表），只读不写库
        var counting = AttachmentEvidenceTests.CountingDbContext.Wrap(db);
        _ = await AttachmentEvidenceService.ListOwnerOptionsAsync(
            counting.Proxy, AttachmentEvidenceRules.OwnerTypeTradeDocument, null);
        Assert.Equal(2, counting.DatasetReads);
        Assert.Equal(
            new[] { nameof(IErpDbContext.AttachmentEvidences), nameof(IErpDbContext.TradeDocuments) }
                .OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            counting.ReadProperties.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 4. 非变更边界（单证 / 明细行 / 来源单据 / 库存 / 财务） ====================

    [Fact]
    public async Task 出口单证附件证据不改写单证状态明细行与来源单据与库存财务记录()
    {
        using var db = TestDbFactory.Create();
        SeedSalesOrder(db, "SO-062-F");
        var document = SeedTradeDocument(db, "DOC-062-F", docType: "装箱单", withItem: true,
            fileNote: "历史附件说明：纸质件存放在档案柜（不得被当作路径抓取）");
        SeedAdjacentRecords(db);
        var store = new InMemoryAttachmentContentStore();

        var before = Snapshot(db);
        var saved = await UploadOkAsync(db, store, PdfBytes(), document.Id, description: "装箱单扫描件");
        _ = await AttachmentEvidenceService.ListAsync(db,
            new AttachmentEvidenceQuery { OwnerType = AttachmentEvidenceRules.OwnerTypeTradeDocument });
        _ = await AttachmentEvidenceService.SummarizeOwnersAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, new[] { document.Id });
        _ = await AttachmentEvidenceService.GetAsync(db, saved.Id);
        _ = await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, document.Id);
        await AttachmentEvidenceService.VoidAsync(db, saved.Id, "扫描件不清晰：作废后重新上传");
        _ = await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, null);
        var after = Snapshot(db);

        Assert.Equal(before, after);
        Assert.Equal("装箱单", (await db.TradeDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id)).DocType);
        Assert.Equal(3, (await db.TradeDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id)).Copies);
        Assert.Equal(0, await db.StockMovements.AsNoTracking()
            .Where(m => m.SourceDocType == "TradeDocument" || m.SourceDocNo == "DOC-062-F").CountAsync());
    }

    // ==================== 5. 列表页有界摘要（计数 + 归属可用性；无逐行查库、零存储访问） ====================

    [Fact]
    public async Task 单证列表摘要_有界计数与归属可用性_固定数据集访问且零存储访问()
    {
        using var db = TestDbFactory.Create();
        var alive = SeedTradeDocument(db, "DOC-062-S1");
        var deleted = SeedTradeDocument(db, "DOC-062-S2");
        const long missingId = 999_999L;
        var store = new InMemoryAttachmentContentStore();

        await UploadOkAsync(db, store, PdfBytes("s1"), alive.Id);
        var wrong = await UploadOkAsync(db, store, PngBytes(), alive.Id,
            fileName: "图片.png", declaredContentType: AttachmentEvidenceRules.MediaPng);
        await UploadOkAsync(db, store, PdfBytes("s3"), deleted.Id);
        await AttachmentEvidenceService.VoidAsync(db, wrong.Id, "重复上传，作废本条");

        // 模拟单据软删除（只改台账标记，不改证据行）
        var deletedDocument = await db.TradeDocuments.FirstAsync(d => d.Id == deleted.Id);
        deletedDocument.IsDeleted = true;
        await db.SaveChangesAsync();

        var storeCallsBefore = store.TotalCalls;
        var counting = AttachmentEvidenceTests.CountingDbContext.Wrap(db);
        var summaries = await AttachmentEvidenceService.SummarizeOwnersAsync(
            counting.Proxy,
            AttachmentEvidenceRules.OwnerTypeTradeDocument,
            new[] { alive.Id, deleted.Id, missingId });

        Assert.Equal(3, summaries.Count);
        Assert.True(summaries.Select(s => s.OwnerId)
            .SequenceEqual(summaries.Select(s => s.OwnerId).OrderBy(id => id)));   // 升序、无合并

        var aliveSummary = summaries.Single(s => s.OwnerId == alive.Id);
        Assert.Equal(AttachmentEvidenceRules.OwnerTypeTradeDocument, aliveSummary.OwnerType);
        Assert.Equal("出口单证", aliveSummary.OwnerTypeText);
        Assert.Equal("DOC-062-S1", aliveSummary.OwnerNo);
        Assert.Equal("出口单证 DOC-062-S1", aliveSummary.OwnerSnapshotText);
        Assert.True(aliveSummary.OwnerAvailable);
        Assert.Equal(2, aliveSummary.TotalCount);
        Assert.Equal(1, aliveSummary.ActiveCount);
        Assert.Equal(1, aliveSummary.VoidedCount);
        Assert.True(aliveSummary.HasActiveEvidence);
        Assert.Contains("仓库附件证据 2 条", aliveSummary.SummaryText);
        Assert.Contains("不代表已向海关", aliveSummary.SummaryText);
        Assert.Contains("不是报关单回执", aliveSummary.BoundaryText);

        var deletedSummary = summaries.Single(s => s.OwnerId == deleted.Id);
        Assert.False(deletedSummary.OwnerAvailable);
        Assert.Equal(1, deletedSummary.TotalCount);
        Assert.Equal(0, deletedSummary.VoidedCount);
        Assert.Equal("DOC-062-S2", deletedSummary.OwnerNo);    // 号码照实显示，绝不改派

        var missingSummary = summaries.Single(s => s.OwnerId == missingId);
        Assert.False(missingSummary.OwnerAvailable);
        Assert.Equal(0, missingSummary.TotalCount);
        Assert.False(missingSummary.HasActiveEvidence);
        Assert.Contains("暂无仓库附件证据", missingSummary.SummaryText);

        // 常数级数据集访问：证据表 + 单证表各一次（无逐行查库）、只读不写库
        Assert.Equal(2, counting.DatasetReads);
        Assert.Equal(
            new[] { nameof(IErpDbContext.AttachmentEvidences), nameof(IErpDbContext.TradeDocuments) },
            counting.ReadProperties.Distinct().ToArray());
        Assert.Equal(0, counting.WriteCalls);

        // 摘要完全不访问存储：保存 / 打开 / 长度探测次数都不变
        Assert.Equal(storeCallsBefore, store.TotalCalls);

        // Id 数量显著增加（跨多页规模）也不改变访问次数
        for (var i = 0; i < 120; i++) SeedTradeDocument(db, $"DOC-062-BULK-{i:d3}");
        var allIds = await db.TradeDocuments.AsNoTracking().Select(d => d.Id).ToListAsync();
        var second = AttachmentEvidenceTests.CountingDbContext.Wrap(db);
        var bulk = await AttachmentEvidenceService.SummarizeOwnersAsync(
            second.Proxy, AttachmentEvidenceRules.OwnerTypeTradeDocument, allIds);
        Assert.Equal(allIds.Count, bulk.Count);
        Assert.Equal(2, second.DatasetReads);
        Assert.Equal(0, second.WriteCalls);
        Assert.Equal(storeCallsBefore, store.TotalCalls);
    }

    [Fact]
    public async Task 摘要入参校验_空Id集合返回空摘要_非法与超量拒绝且零数据集访问()
    {
        using var db = TestDbFactory.Create();
        var document = SeedTradeDocument(db, "DOC-062-G");
        var store = new InMemoryAttachmentContentStore();
        await UploadOkAsync(db, store, PdfBytes(), document.Id);

        var counting = AttachmentEvidenceTests.CountingDbContext.Wrap(db);
        var empty = await AttachmentEvidenceService.SummarizeOwnersAsync(
            counting.Proxy, AttachmentEvidenceRules.OwnerTypeTradeDocument, Array.Empty<long>());
        Assert.Empty(empty);
        Assert.Equal(0, counting.DatasetReads);                // 空 Id 集合：不查询任何数据集
        Assert.Equal(0, counting.WriteCalls);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.SummarizeOwnersAsync(
            counting.Proxy, AttachmentEvidenceRules.OwnerTypeTradeDocument, new[] { 1L, 0L }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.SummarizeOwnersAsync(
            counting.Proxy, AttachmentEvidenceRules.OwnerTypeTradeDocument, new[] { -3L }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.SummarizeOwnersAsync(
            counting.Proxy, "ContainerLoadingList", new[] { 1L }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.SummarizeOwnersAsync(
            counting.Proxy,
            AttachmentEvidenceRules.OwnerTypeTradeDocument,
            Enumerable.Range(1, AttachmentEvidenceRules.MaxSummaryOwnerIds + 1).Select(i => (long)i)));

        Assert.True(AttachmentEvidenceRules.MaxSummaryOwnerIds >= 100);   // 至少覆盖常见分页规模

        // 解析：逗号 / 分号 / 空白 / 去重 / 升序 / 空串
        Assert.Equal(new[] { 2L, 5L, 9L }, AttachmentEvidenceRules.ParseOwnerIds(" 5,2;9 "));
        Assert.Equal(new[] { 3L }, AttachmentEvidenceRules.ParseOwnerIds("3,3,3"));
        Assert.Empty(AttachmentEvidenceRules.ParseOwnerIds("   "));
        Assert.Empty(AttachmentEvidenceRules.ParseOwnerIds(null));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
        {
            _ = AttachmentEvidenceRules.ParseOwnerIds("1,abc");
            return Task.CompletedTask;
        });
    }

    // ==================== 6. 历史「附件说明 / 存放位置」文本（不解析 / 不抓取 / 不回填） ====================

    [Fact]
    public async Task 历史附件说明文本保持原样_不解析不抓取不转附件且不在请求时回填()
    {
        using var db = TestDbFactory.Create();
        const string legacy = @"历史填写：C:\scans\DOC-062-H.pdf；http://example.com/doc.pdf；\\\\fileshare\\doc\\h.pdf";
        var document = SeedTradeDocument(db, "DOC-062-H", fileNote: legacy);
        var store = new InMemoryAttachmentContentStore();

        // 未上传前：任何读取都不会把历史文本变成附件行（不回填）
        Assert.Empty(await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, document.Id));
        var summaryBefore = (await AttachmentEvidenceService.SummarizeOwnersAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, new[] { document.Id })).Single();
        Assert.Equal(0, summaryBefore.TotalCount);
        Assert.Contains("暂无仓库附件证据", summaryBefore.SummaryText);
        Assert.Empty(await db.AttachmentEvidences.AsNoTracking().ToListAsync());
        Assert.Equal(0, store.TotalCalls);                     // 不抓取任何地址（零存储访问）

        // 上传 / 作废 / 台账 / 候选读取之后：历史文本逐字保持原样
        var saved = await UploadOkAsync(db, store, PdfBytes(), document.Id);
        await AttachmentEvidenceService.VoidAsync(db, saved.Id, "更正：重新扫描后上传");
        _ = await AttachmentEvidenceService.ListAsync(db,
            new AttachmentEvidenceQuery { OwnerType = AttachmentEvidenceRules.OwnerTypeTradeDocument });
        _ = await AttachmentEvidenceService.GetAsync(db, saved.Id);
        _ = await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, document.Id);
        _ = await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, null);

        var persisted = await db.TradeDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        Assert.Equal(legacy, persisted.FileNote);
        Assert.Equal(1, await db.AttachmentEvidences.AsNoTracking().CountAsync());   // 只有显式上传的那一条

        // 服务端与控制器刻意不访问该字段；规则层给出明确的不解析 / 不抓取 / 不回填口径
        var service = File.ReadAllText(RepoFile("src", "ERP.Application", "Services", "AttachmentEvidenceService.cs"));
        Assert.DoesNotContain(".FileNote", service);
        var controller = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "AttachmentEvidenceController.cs"));
        Assert.DoesNotContain(".FileNote", controller);
        Assert.Contains("解析成文件路径", AttachmentEvidenceRules.LegacyFileNotePolicyText);
        Assert.Contains("绝不转成附件证据", AttachmentEvidenceRules.LegacyFileNotePolicyText);
        Assert.Contains("也不在读取时按它回填附件行", AttachmentEvidenceRules.LegacyFileNotePolicyText);
    }

    // ==================== 7. 同一编号 / 同一内容的不同单证绝不合并或改派 ====================

    [Fact]
    public async Task 编号文件名与摘要相同的不同单证不会合并或改派()
    {
        using var db = TestDbFactory.Create();
        var first = SeedTradeDocument(db, "DOC-062-X", docType: "商业发票");
        var second = SeedTradeDocument(db, "DOC-062-X", docType: "商业发票");   // 同一编号文本
        var store = new InMemoryAttachmentContentStore();
        var content = PdfBytes("same");

        var a = await UploadOkAsync(db, store, content, first.Id, fileName: "同一份.pdf");
        var b = await UploadOkAsync(db, store, content, second.Id, fileName: "同一份.pdf");

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.Sha256, b.Sha256);                      // 同一内容：摘要相同但仍是两条独立证据

        var rows = await db.AttachmentEvidences.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(first.Id, rows[0].OwnerId);
        Assert.Equal(second.Id, rows[1].OwnerId);
        Assert.NotEqual(rows[0].StorageKey, rows[1].StorageKey);   // 不做内容寻址去重、不覆盖

        // 读取按显式归属 Id 收敛；两单据各自的计数互不合并
        Assert.Single(await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, first.Id));
        Assert.Single(await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, second.Id));

        var summaries = await AttachmentEvidenceService.SummarizeOwnersAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, new[] { first.Id, second.Id });
        Assert.Equal(1, summaries.Single(s => s.OwnerId == first.Id).TotalCount);
        Assert.Equal(1, summaries.Single(s => s.OwnerId == second.Id).TotalCount);

        // 关键字按编号检索会同时命中两条（只读检索），但绝不合并成一条证据
        Assert.Equal(2, (await AttachmentEvidenceService.ListAsync(db, new AttachmentEvidenceQuery
        {
            OwnerType = AttachmentEvidenceRules.OwnerTypeTradeDocument,
            Keyword = "DOC-062-X"
        })).Total);
    }

    // ==================== 8. 控制器与前端接线契约 ====================

    [Fact]
    public async Task 控制器契约_摘要端点类级授权且不提供改写二进制或删除端点()
    {
        using var db = TestDbFactory.Create();
        var first = SeedTradeDocument(db, "DOC-062-I1");
        var second = SeedTradeDocument(db, "DOC-062-I2");
        var store = new InMemoryAttachmentContentStore();
        await UploadOkAsync(db, store, PdfBytes(), first.Id, description: "控制器契约");

        var controller = BuildController(db, store);
        var summaries = AssertOk<List<AttachmentEvidenceOwnerSummaryDto>>(
            await controller.OwnerSummary(
                AttachmentEvidenceRules.OwnerTypeTradeDocument, $"{first.Id}, {second.Id}"));
        Assert.Equal(2, summaries.Count);
        Assert.Equal(1, summaries.Single(s => s.OwnerId == first.Id).TotalCount);
        Assert.Contains("不代表已向海关", summaries.Single(s => s.OwnerId == first.Id).SummaryText);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.OwnerSummary(AttachmentEvidenceRules.OwnerTypeTradeDocument, "1,abc"));
        Assert.Equal(0, store.OpenCalls);      // 摘要端点不读取任何内容

        // 路由与授权契约：类级 [Authorize]，无 [AllowAnonymous]，摘要端点映射为 owner-summary
        Assert.NotNull(typeof(AttachmentEvidenceController).GetCustomAttribute<AuthorizeAttribute>());
        Assert.Null(typeof(AttachmentEvidenceController).GetCustomAttribute<AllowAnonymousAttribute>());

        var summaryMethod = typeof(AttachmentEvidenceController)
            .GetMethod(nameof(AttachmentEvidenceController.OwnerSummary));
        Assert.NotNull(summaryMethod);
        var route = summaryMethod!.GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(route);
        Assert.Equal("owner-summary", route!.Template);
        Assert.Equal(typeof(Task<IActionResult>), summaryMethod.ReturnType);
        Assert.Equal(new[] { typeof(string), typeof(string), typeof(CancellationToken) },
            summaryMethod.GetParameters().Select(p => p.ParameterType).ToArray());

        // 不提供改写二进制 / 硬删除 / 改派端点
        var declared = typeof(AttachmentEvidenceController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(declared, m => m.GetCustomAttribute<HttpPutAttribute>() is not null);
        Assert.DoesNotContain(declared, m => m.GetCustomAttribute<HttpPatchAttribute>() is not null);
        Assert.DoesNotContain(declared, m => m.GetCustomAttribute<HttpDeleteAttribute>() is not null);

        // 摘要只返回计数与可用性：不含存储键 / 内容 / 路径
        var properties = typeof(AttachmentEvidenceOwnerSummaryDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("StorageKey", properties);
        Assert.DoesNotContain("StorageProvider", properties);
        Assert.DoesNotContain("DownloadPath", properties);
        var json = System.Text.Json.JsonSerializer.Serialize(
            await AttachmentEvidenceService.SummarizeOwnersAsync(
                db, AttachmentEvidenceRules.OwnerTypeTradeDocument, new[] { first.Id }));
        Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rootPath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 前端接线契约_单证中心附件证据入口与概览_且界面不接触存储键()
    {
        var js = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "attachment-evidences.js"));
        Assert.Contains("case 'doc-center': return 'TradeDocument';", js);
        Assert.Contains("async function openAttachmentEvidenceSummaryForCurrentPage()", js);
        Assert.Contains("function aeRenderSummary(", js);
        Assert.Contains("const AE_SUMMARY_MAX_OWNER_IDS = 200;", js);
        Assert.Contains("'/api/attachment-evidences/owner-summary?ownerType='", js);
        Assert.Contains("window.__moduleRows", js);
        Assert.Contains("aeEsc(", js);
        Assert.Contains("不转成附件证据", js);
        Assert.Contains("不是报关 / 报税", js);
        Assert.DoesNotContain("storageKey", js);
        Assert.DoesNotContain("OssStorageService", js);
        Assert.DoesNotContain("AccessKey", js);

        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules.js"));
        Assert.Contains("'doc-center': {", modules);
        Assert.Contains("附件证据概览", modules);
        Assert.Contains("openAttachmentEvidenceSummaryForCurrentPage", modules);
        Assert.Contains("openAttachmentEvidencesForCurrentModule", modules);

        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/attachment-evidences.js", index);
    }

    // ==================== 9. 下载：每次重新校验权威单证 + 防御性响应头 ====================

    [Fact]
    public async Task 下载每次重新校验单证存在性_以附件方式返回且防御性响应头不变()
    {
        using var db = TestDbFactory.Create();
        var document = SeedTradeDocument(db, "DOC-062-J", docType: "提单");
        var store = new InMemoryAttachmentContentStore();
        var saved = await UploadOkAsync(db, store, PdfBytes(), document.Id);

        var controller = BuildController(db, store);
        var file = Assert.IsType<FileStreamResult>(await controller.DownloadContent(saved.Id));
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("扫描件.pdf", file.FileDownloadName);
        Assert.False(file.EnableRangeProcessing);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("default-src 'none'; sandbox", controller.Response.Headers["Content-Security-Policy"].ToString());
        Assert.Equal("no-store, no-cache, must-revalidate", controller.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("noopen", controller.Response.Headers["X-Download-Options"].ToString());
        Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"].ToString());

        // 单证被软删除后：同一段内容不再可下载（每次请求都重新校验权威单证）
        var persisted = await db.TradeDocuments.FirstAsync(d => d.Id == document.Id);
        persisted.IsDeleted = true;
        await db.SaveChangesAsync();

        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.DownloadContent(saved.Id));
        Assert.Equal(1, store.OpenCalls);      // 软删除后连存储都不再打开
    }

    // ==================== 10. 全部单证类型与台账状态（只读标注，绝不推断） ====================

    [Theory]
    [InlineData("报关单", "待制作")]
    [InlineData("装箱单", "已制作")]
    [InlineData("商业发票", "已提交客户")]
    [InlineData("形式发票", "已使用")]
    [InlineData("产地证", "已制作")]
    [InlineData("提单", "已提交客户")]
    [InlineData("订舱确认", "待制作")]
    [InlineData("外汇核销单", "已使用")]
    [InlineData("其他", "")]
    public async Task 各单证类型都可挂证据且台账状态只读标注不推断(string docType, string status)
    {
        using var db = TestDbFactory.Create();
        var document = SeedTradeDocument(db, $"DOC-062-T-{docType}", docType: docType, status: status);
        var store = new InMemoryAttachmentContentStore();

        var saved = await UploadOkAsync(db, store, PdfBytes(docType), document.Id, description: $"{docType}扫描件");
        Assert.Equal("出口单证", saved.OwnerTypeText);
        Assert.Equal(document.DocNo, saved.OwnerNo);
        Assert.Equal(AttachmentEvidenceRules.MediaPdf, saved.MediaType);

        // 归属候选：单证类型与台账状态原文（空状态照实标注「未登记状态」，不推断）
        var option = (await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypeTradeDocument, null))
            .Single(o => o.OwnerId == document.Id);
        Assert.Equal(AttachmentEvidenceRules.TradeDocumentStatusText(status), option.StatusText);
        Assert.Contains($"单证类型 {docType}", option.SummaryText);
        Assert.Equal(1, option.EvidenceCount);

        // 单证台账状态与明细行都不被附件证据改写
        var persisted = await db.TradeDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        Assert.Equal(status, persisted.Status);
        Assert.Equal(0, await db.TradeDocumentItems.AsNoTracking().CountAsync());
    }
}
