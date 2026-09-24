using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 业务单据附件内容证据单元测试（ERP-061）。ERP-062 在**同一**模型上接入出口单证、ERP-063 在**同一**模型上
/// 接入验货记录与样品记录（单证侧与验货 / 样品侧的用例分别见 <c>TradeDocumentAttachmentEvidenceTests</c> 与
/// <c>QualityInspectionSampleAttachmentEvidenceTests</c>），因此本文件的归属白名单断言同步扩展为五类。
/// 覆盖：归属单据白名单与存在性复核（不存在 / 已删除 / Id 非法）、
/// 内容格式三重判定（扩展名 + 声明 Content-Type + 文件签名）与可执行 / 标记类格式拒绝、大小上限与「超限即中止
/// 不保存」、空内容拒绝、文件名净化（客户端路径忽略 / 控制字符 / 超长截断）与摘要完整性（SHA-256）、
/// 同一内容重复上传**不静默合并**、服务端权威元数据（存储键 / 摘要 / 长度 / 媒体类型 / 归属快照 / 上传人 /
/// 登记时间均不可由客户端提交）、台账分页与过滤有界、指定归属有界清单、归属单据删除后历史可读且标注不可用
/// （绝不改派）、下载（有效可下载 / 已作废拒绝 / 内容缺失 / 长度或摘要不一致拒绝）、作废（必填原因 / 有界 /
/// 重复拒绝 / 保留原始元数据 / 不提供硬删除与二进制替换）、模块元数据与归属候选、隔离本地存储的安全口径
/// （不透明键 / 目录穿越拒绝 / wwwroot 拒绝）与存储提供程序守卫（生产 OSS 显式拒绝）、相邻业务记录非变更、
/// 模型配置契约 / 幂等结构 / 前端与路由接线契约，以及 ERP-061 审计结论（唯一附件内容模型、不引入第二套
/// 文件存储、不改写既有 OSS 客户端与 ERP-045 引用册）。
/// <para>全部使用内存库（<c>TestDbFactory</c>）与**测试内**的内存内容存储；隔离本地存储只在系统临时目录
/// 验证。不连接 SQL Server、不执行任何 SQL / 部署脚本、不访问生产 OSS、不做任何浏览器 / UI 验收
/// （浏览器验收按项目策略延后到 FINAL-UI-ACCEPTANCE）。</para>
/// </summary>
public class AttachmentEvidenceTests
{
    // ==================== 0. 测试脚手架 ====================

    /// <summary>已知内容「%PDF-1.4\nabc」的 SHA-256（独立基线值，用于证明摘要确实由服务端按内容计算）</summary>
    private const string KnownPdfDigest = "af2f0bbdd0af4f20ead54a13f594b47a10f7d4ce98879388eb0536a1f4a4907f";

    private static byte[] PdfBytes(string body = "abc") => Encoding.UTF8.GetBytes("%PDF-1.4\n" + body);

    private static byte[] PngBytes()
        => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };

    private static byte[] JpegBytes()
        => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

    private static byte[] HtmlBytes()
        => Encoding.UTF8.GetBytes("<html><body><script>alert('x')</script></body></html>");

    private static SalesOrder SeedSalesOrder(
        ErpDbContext db, string orderNo, DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = 501,
            Currency = Currency.USD,
            TotalAmount = 12345.67m,
            DepositAmount = 2345.67m,
            ShippingMarks = "MARKS-061",
            Remark = "销售订单备注",
            Status = status,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id,
            ProductId = 9001,
            ProductName = "测试商品",
            Quantity = 12m
        });
        db.SaveChanges();
        return order;
    }

    private static PurchaseOrder SeedPurchaseOrder(
        ErpDbContext db, string orderNo, DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 2),
            SupplierId = 601,
            Currency = Currency.CNY,
            TotalAmount = 8888.88m,
            ArrivalProgress = "未到货",
            QcStatus = "未验货",
            Remark = "采购订单备注",
            Status = status,
            IsDeleted = deleted
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
        {
            PurchaseOrderId = order.Id,
            ProductId = 9001,
            ProductName = "测试商品",
            Quantity = 5m
        });
        db.SaveChanges();
        return order;
    }

    /// <summary>播种相邻业务记录（库存 / 库存流水 / 费用单 / 出口单证 / ERP-045 引用册），用于非变更断言</summary>
    private static void SeedAdjacentRecords(ErpDbContext db)
    {
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
            SourceDocNo = "RK-061",
            WarehouseId = 1,
            WarehouseName = "主仓"
        });
        db.FinanceExpenses.Add(new FinanceExpense
        {
            ExpenseNo = "FY-061",
            ExpenseDate = new DateTime(2026, 9, 4),
            ExpenseType = "报关费",
            Amount = 320m,
            Currency = "CNY",
            AmountCny = 320m,
            PaymentStatus = "未付",
            AllocatedAmount = 160m,
            Remark = "费用单备注"
        });
        db.TradeDocuments.Add(new TradeDocument
        {
            DocNo = "DOC-061",
            DocType = "装箱单",
            Amount = 1000m,
            Currency = "USD",
            Status = "待制作",
            FileNote = "附件说明 / 存放位置（历史文本：ERP-061 不导入、不解析、不当作路径或附件）",
            Remark = "单证备注"
        });
        db.DocumentAttachmentReferences.Add(new DocumentAttachmentReference
        {
            ParentType = DocumentAttachmentReferenceRules.ParentTypeSalesOrder,
            ParentId = 1,
            ParentNo = "SO-KEEP",
            ParentTypeText = "销售订单",
            Category = DocumentAttachmentReferenceRules.CategoryContract,
            DisplayName = "ERP-045 既有引用",
            ReferenceId = "ref-keep-061",
            Status = DocumentAttachmentReferenceRules.StatusActive
        });
        db.SaveChanges();
    }

    /// <summary>相邻业务记录的只读快照（用于断言附件证据模块不改写任何既有记录）</summary>
    private static string Snapshot(ErpDbContext db)
    {
        var salesOrders = db.SalesOrders.AsNoTracking().OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.OrderNo, o.Status, o.TotalAmount, o.DepositAmount, o.Remark, o.ShippingMarks, o.IsDeleted })
            .ToList();
        var purchaseOrders = db.PurchaseOrders.AsNoTracking().OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.OrderNo, o.Status, o.TotalAmount, o.ArrivalProgress, o.QcStatus, o.Remark, o.IsDeleted })
            .ToList();
        var salesDetails = db.SalesOrderDetails.AsNoTracking().OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.SalesOrderId, d.ProductId, d.Quantity }).ToList();
        var purchaseDetails = db.PurchaseOrderDetails.AsNoTracking().OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.PurchaseOrderId, d.ProductId, d.Quantity }).ToList();
        var stocks = db.Stocks.AsNoTracking().OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.Quantity, s.LockedQuantity }).ToList();
        var movements = db.StockMovements.AsNoTracking().OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.MovementType, m.SourceDocNo, m.WarehouseName }).ToList();
        var expenses = db.FinanceExpenses.AsNoTracking().OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.ExpenseNo, e.Amount, e.AllocatedAmount, e.PaymentStatus }).ToList();
        var documents = db.TradeDocuments.AsNoTracking().OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.DocNo, d.Status, d.Amount, d.FileNote }).ToList();
        var references = db.DocumentAttachmentReferences.AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.ParentType, r.ParentId, r.ReferenceId, r.Status }).ToList();

        return string.Join("|", new[]
        {
            System.Text.Json.JsonSerializer.Serialize(salesOrders),
            System.Text.Json.JsonSerializer.Serialize(purchaseOrders),
            System.Text.Json.JsonSerializer.Serialize(salesDetails),
            System.Text.Json.JsonSerializer.Serialize(purchaseDetails),
            System.Text.Json.JsonSerializer.Serialize(stocks),
            System.Text.Json.JsonSerializer.Serialize(movements),
            System.Text.Json.JsonSerializer.Serialize(expenses),
            System.Text.Json.JsonSerializer.Serialize(documents),
            System.Text.Json.JsonSerializer.Serialize(references)
        });
    }

    private static AttachmentEvidenceUploadRequest UploadRequest(
        byte[] content,
        string ownerType = AttachmentEvidenceRules.OwnerTypeSalesOrder,
        long ownerId = 0,
        string fileName = "证据.pdf",
        string declaredContentType = AttachmentEvidenceRules.MediaPdf,
        string description = "",
        long? declaredLength = null)
        => new()
        {
            OwnerType = ownerType,
            OwnerId = ownerId,
            FileName = fileName,
            DeclaredContentType = declaredContentType,
            DeclaredLength = declaredLength ?? content.LongLength,
            Description = description,
            Content = new MemoryStream(content, writable: false)
        };

    private static Task<AttachmentEvidenceDto> UploadAsync(
        ErpDbContext db, IAttachmentContentStore store, AttachmentEvidenceUploadRequest request,
        string? uploadedBy = "测试用户", long? uploadedById = 7)
        => AttachmentEvidenceService.UploadAsync(db, store, request, uploadedBy, uploadedById);

    private static async Task<AttachmentEvidenceDto> UploadOkAsync(
        ErpDbContext db, IAttachmentContentStore store, byte[] content, long ownerId,
        string ownerType = AttachmentEvidenceRules.OwnerTypeSalesOrder,
        string fileName = "证据.pdf",
        string declaredContentType = AttachmentEvidenceRules.MediaPdf,
        string description = "")
    {
        var saved = await UploadAsync(db, store,
            UploadRequest(content, ownerType, ownerId, fileName, declaredContentType, description));
        Assert.Equal(AttachmentEvidenceRules.StatusActive, saved.Status);   // 上传成功即「有效」
        Assert.Equal(AttachmentEvidenceRules.Sha256Length, saved.Sha256.Length);   // 摘要由服务端按内容计算
        return saved;
    }

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

    // ==================== 1. 上传：服务端权威元数据（两类归属单据） ====================

    [Fact]
    public async Task 上传到销售订单与采购订单_摘要长度媒体类型存储键与归属快照全部由服务端生成()
    {
        using var db = TestDbFactory.Create();
        var salesOrder = SeedSalesOrder(db, "SO-061-A");
        var purchaseOrder = SeedPurchaseOrder(db, "PO-061-A");
        var store = new InMemoryAttachmentContentStore();
        var before = DateTime.Now;

        var pdf = PdfBytes();
        var sales = await UploadOkAsync(db, store, pdf, salesOrder.Id,
            description: "客户确认的合同扫描件", fileName: "合同扫描件.pdf");

        Assert.Equal(AttachmentEvidenceRules.OwnerTypeSalesOrder, sales.OwnerType);
        Assert.Equal("销售订单", sales.OwnerTypeText);
        Assert.Equal(salesOrder.Id, sales.OwnerId);
        Assert.Equal("SO-061-A", sales.OwnerNo);
        Assert.Equal(AttachmentEvidenceRules.MediaPdf, sales.MediaType);
        Assert.Equal("PDF 文档", sales.MediaTypeText);
        Assert.Equal(pdf.LongLength, sales.SizeBytes);
        Assert.Equal(KnownPdfDigest, sales.Sha256);
        Assert.Equal("合同扫描件.pdf", sales.OriginalFileName);
        Assert.Equal("客户确认的合同扫描件", sales.Description);
        Assert.Equal("测试用户", sales.UploadedBy);
        Assert.Equal(AttachmentEvidenceRules.StatusActive, sales.Status);
        Assert.True(sales.IsActive);
        Assert.True(sales.OwnerAvailable);
        Assert.True(sales.ContentDownloadable);
        Assert.True(sales.RecordedAt >= before && sales.RecordedAt <= DateTime.Now);
        Assert.Equal(AttachmentEvidenceRules.ProviderIsolatedLocal, sales.StorageProvider);
        Assert.Equal(AttachmentEvidenceRules.ContentApiPath(sales.Id), sales.DownloadPath);

        // 采购订单 + PNG：同一模型、同一口径，不按类型分叉出第二套附件系统
        var png = PngBytes();
        var purchase = await UploadOkAsync(db, store, png, purchaseOrder.Id,
            ownerType: AttachmentEvidenceRules.OwnerTypePurchaseOrder,
            fileName: "验货报告.png",
            declaredContentType: AttachmentEvidenceRules.MediaPng);

        Assert.Equal("采购订单", purchase.OwnerTypeText);
        Assert.Equal("PO-061-A", purchase.OwnerNo);
        Assert.Equal(AttachmentEvidenceRules.MediaPng, purchase.MediaType);
        Assert.Equal(png.LongLength, purchase.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(), purchase.Sha256);

        // 存储键：服务端生成的不透明键（日期目录 + GUID + 服务端判定的扩展名），不含原始文件名与任何路径
        Assert.Equal(2, store.SaveCalls);
        var keys = store.Items.Keys.ToList();
        Assert.All(keys, key => Assert.Matches(@"^\d{8}/[0-9a-f]{32}\.(pdf|png|jpg|jpeg)$", key));
        Assert.All(keys, key => Assert.DoesNotContain("合同", key));
        Assert.All(keys, key => Assert.DoesNotContain("验货", key));

        var persisted = await db.AttachmentEvidences.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(2, persisted.Count);
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            persisted.Select(r => r.StorageKey).OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void 上传请求与返回模型都不含客户端可提交的信任字段()
    {
        var requestProperties = typeof(AttachmentEvidenceUploadRequest)
            .GetProperties().Select(p => p.Name).ToList();

        // 客户端只能提交归属 / 文件名 / 声明类型 / 说明 / 内容：摘要、存储键、实测长度、媒体类型、
        // 归属快照、上传人、登记时间、状态与审计字段都不在请求模型里，因此无法提交「被信任」的值
        Assert.DoesNotContain("Sha256", requestProperties);
        Assert.DoesNotContain("StorageKey", requestProperties);
        Assert.DoesNotContain("StorageProvider", requestProperties);
        Assert.DoesNotContain("OwnerNo", requestProperties);
        Assert.DoesNotContain("OwnerTypeText", requestProperties);
        Assert.DoesNotContain("MediaType", requestProperties);
        Assert.DoesNotContain("UploadedBy", requestProperties);
        Assert.DoesNotContain("RecordedAt", requestProperties);
        Assert.DoesNotContain("Status", requestProperties);
        Assert.DoesNotContain("SizeBytes", requestProperties);
        Assert.DoesNotContain("DownloadPath", requestProperties);

        var dtoProperties = typeof(AttachmentEvidenceDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("StorageKey", dtoProperties);
        Assert.DoesNotContain("RootPath", dtoProperties);
        Assert.Contains(nameof(AttachmentEvidenceDto.DownloadPath), dtoProperties);
    }

    // ==================== 2. 归属单据：白名单 + 存在性 + 未删除 ====================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("trade-document")]
    [InlineData("TradeDocuments")]
    [InlineData("SalesOrders")]
    [InlineData("ContainerLoadingList")]
    [InlineData("1")]
    [InlineData("sales_order")]
    public async Task 不支持的归属单据类型一律拒绝且不落库不落盘(string ownerType)
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-B");
        var store = new InMemoryAttachmentContentStore();

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UploadAsync(db, store,
            UploadRequest(PdfBytes(), ownerType, order.Id)));

        Assert.Contains("归属单据类型", ex.Message);
        Assert.Empty(await db.AttachmentEvidences.AsNoTracking().ToListAsync());
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task 归属单据不存在已删除或Id非法一律拒绝_不按号码猜测归属()
    {
        using var db = TestDbFactory.Create();
        var live = SeedSalesOrder(db, "SO-061-C");
        var deleted = SeedSalesOrder(db, "SO-061-D", deleted: true);
        var livePurchase = SeedPurchaseOrder(db, "PO-061-C");
        var deletedPurchase = SeedPurchaseOrder(db, "PO-061-D", deleted: true);
        var store = new InMemoryAttachmentContentStore();

        var invalid = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UploadAsync(db, store,
            UploadRequest(PdfBytes(), ownerId: 0)));
        Assert.Contains("请显式选择归属单据", invalid.Message);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UploadAsync(db, store,
            UploadRequest(PdfBytes(), ownerId: -5)));

        var missing = await AssertBusinessAsync(ErrorCodes.NotFound, () => UploadAsync(db, store,
            UploadRequest(PdfBytes(), ownerId: 999_999)));
        Assert.Contains("销售订单不存在或已删除", missing.Message);

        await AssertBusinessAsync(ErrorCodes.NotFound, () => UploadAsync(db, store,
            UploadRequest(PdfBytes(), ownerId: deleted.Id)));

        var missingPurchase = await AssertBusinessAsync(ErrorCodes.NotFound, () => UploadAsync(db, store,
            UploadRequest(PdfBytes(), AttachmentEvidenceRules.OwnerTypePurchaseOrder, 999_999)));
        Assert.Contains("采购订单不存在或已删除", missingPurchase.Message);

        await AssertBusinessAsync(ErrorCodes.NotFound, () => UploadAsync(db, store,
            UploadRequest(PdfBytes(), AttachmentEvidenceRules.OwnerTypePurchaseOrder, deletedPurchase.Id)));

        Assert.Empty(await db.AttachmentEvidences.AsNoTracking().ToListAsync());
        Assert.Equal(0, store.SaveCalls);

        // 合法单据仍可上传（证明拒绝来自归属判定，而不是模型整体不可用）
        await UploadOkAsync(db, store, PdfBytes(), live.Id);
        await UploadOkAsync(db, store, PdfBytes(), livePurchase.Id,
            ownerType: AttachmentEvidenceRules.OwnerTypePurchaseOrder);

        Assert.Equal(2, store.SaveCalls);
        Assert.Equal(2, await db.AttachmentEvidences.AsNoTracking().CountAsync());
    }

    // ==================== 3. 内容判定：扩展名 + 声明类型 + 文件签名三者一致 ====================

    [Theory]
    [InlineData("application/pdf", ".pdf", "pdf")]
    [InlineData("image/png", ".png", "png")]
    [InlineData("image/jpeg", ".jpg", "jpeg")]
    [InlineData("image/jpeg", ".jpeg", "jpeg")]
    [InlineData("image/jpg", ".jpg", "jpeg")]
    [InlineData("application/pdf; charset=binary", ".pdf", "pdf")]
    public async Task 受支持格式在扩展名声明类型与签名一致时上传成功(
        string declaredContentType, string extension, string kind)
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-E");
        var store = new InMemoryAttachmentContentStore();
        var content = kind switch
        {
            "png" => PngBytes(),
            "jpeg" => JpegBytes(),
            _ => PdfBytes()
        };

        var saved = await UploadOkAsync(db, store, content, order.Id,
            fileName: "证据" + extension, declaredContentType: declaredContentType);

        var expectedMediaType = kind switch
        {
            "png" => AttachmentEvidenceRules.MediaPng,
            "jpeg" => AttachmentEvidenceRules.MediaJpeg,
            _ => AttachmentEvidenceRules.MediaPdf
        };
        Assert.Equal(expectedMediaType, saved.MediaType);
        Assert.Equal(content.LongLength, saved.SizeBytes);
        Assert.Single(store.Items);
    }

    [Theory]
    [InlineData(".pdf", "application/pdf", "png")]      // PNG 内容改名成 .pdf
    [InlineData(".png", "image/png", "pdf")]            // PDF 内容改名成 .png
    [InlineData(".jpg", "image/jpeg", "png")]           // PNG 内容改名成 .jpg
    [InlineData(".pdf", "application/pdf", "jpeg")]     // JPEG 内容声明为 PDF
    [InlineData(".png", "image/png", "html")]           // 标记内容改名成 .png
    [InlineData(".pdf", "application/pdf", "text")]     // 无法识别的二进制
    public async Task 扩展名声明类型与文件签名不一致或无法识别一律拒绝(string extension,
        string declaredContentType, string kind)
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-F");
        var store = new InMemoryAttachmentContentStore();
        var content = kind switch
        {
            "png" => PngBytes(),
            "jpeg" => JpegBytes(),
            "html" => HtmlBytes(),
            "text" => Encoding.UTF8.GetBytes("这不是受支持的证据内容"),
            _ => PdfBytes()
        };

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UploadAsync(db, store,
            UploadRequest(content, ownerId: order.Id, fileName: "伪装" + extension,
                declaredContentType: declaredContentType)));

        Assert.Empty(await db.AttachmentEvidences.AsNoTracking().ToListAsync());
        Assert.Equal(0, store.SaveCalls);   // 校验失败绝不落盘
    }

    [Theory]
    [InlineData(".html")]
    [InlineData(".htm")]
    [InlineData(".xhtml")]
    [InlineData(".svg")]
    [InlineData(".xml")]
    [InlineData(".js")]
    [InlineData(".exe")]
    [InlineData(".dll")]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".ps1")]
    [InlineData(".sh")]
    [InlineData(".php")]
    [InlineData(".jsp")]
    [InlineData(".zip")]
    [InlineData(".rar")]
    public async Task 可执行脚本与标记类扩展名一律拒绝_即使内容伪装成受支持格式(string extension)
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-G");
        var store = new InMemoryAttachmentContentStore();

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UploadAsync(db, store,
            UploadRequest(HtmlBytes(), ownerId: order.Id, fileName: "恶意" + extension,
                declaredContentType: AttachmentEvidenceRules.MediaPdf)));

        Assert.Contains("拒绝上传可执行 / 脚本 / 标记类文件", ex.Message);
        Assert.Empty(await db.AttachmentEvidences.AsNoTracking().ToListAsync());
        Assert.Equal(0, store.SaveCalls);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    [InlineData("application/x-msdownload")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 声明的ContentType白名单之外的取值一律拒绝(string declaredContentType)
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-H");
        var store = new InMemoryAttachmentContentStore();

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UploadAsync(db, store,
            UploadRequest(PdfBytes(), ownerId: order.Id, declaredContentType: declaredContentType)));

        Assert.Empty(await db.AttachmentEvidences.AsNoTracking().ToListAsync());
        Assert.Equal(0, store.SaveCalls);
    }

    [Theory]
    [InlineData("证据")]        // 无扩展名
    [InlineData("证据.")]       // 只有点
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("证据.txt")]     // 非白名单扩展名
    [InlineData("证据.docx")]
    [InlineData("证据.pdf.exe")]
    public async Task 文件名缺失或非白名单扩展名一律拒绝(string fileName)
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-I");
        var store = new InMemoryAttachmentContentStore();

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UploadAsync(db, store,
            UploadRequest(PdfBytes(), ownerId: order.Id, fileName: fileName)));

        Assert.Empty(await db.AttachmentEvidences.AsNoTracking().ToListAsync());
        Assert.Equal(0, store.SaveCalls);
    }

    // ==================== 4. 大小与内容边界（有界读取，超限即中止） ====================

    [Fact]
    public async Task 大小上限_声明超限提前拒绝_实际超限读取即中止且不保存任何内容()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-J");
        var store = new InMemoryAttachmentContentStore();

        // 1) 声明长度超限：不读取内容，直接拒绝
        var oversizeDeclared = new AttachmentEvidenceUploadRequest
        {
            OwnerType = AttachmentEvidenceRules.OwnerTypeSalesOrder,
            OwnerId = order.Id,
            FileName = "大文件.pdf",
            DeclaredContentType = AttachmentEvidenceRules.MediaPdf,
            DeclaredLength = AttachmentEvidenceRules.MaxSizeBytes + 1,
            Content = new MemoryStream(PdfBytes())
        };
        var declared = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => UploadAsync(db, store, oversizeDeclared));
        Assert.Contains("超过上限", declared.Message);

        // 2) 负数声明长度
        var negative = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UploadAsync(db, store,
            UploadRequest(PdfBytes(), ownerId: order.Id, declaredLength: -1)));
        Assert.Contains("长度声明无效", negative.Message);

        // 3) 实际内容超限（声明很小）：读取在到达上限时立即中止，不落盘不落库
        var oversizeActual = new AttachmentEvidenceUploadRequest
        {
            OwnerType = AttachmentEvidenceRules.OwnerTypeSalesOrder,
            OwnerId = order.Id,
            FileName = "大文件.pdf",
            DeclaredContentType = AttachmentEvidenceRules.MediaPdf,
            DeclaredLength = 1024,
            Content = new OversizeStream(AttachmentEvidenceRules.MaxSizeBytes + 1)
        };
        var actual = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => UploadAsync(db, store, oversizeActual));
        Assert.Contains("读取在到达上限时立即中止", actual.Message);

        // 4) 空内容 / 未收到内容
        var empty = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UploadAsync(db, store,
            new AttachmentEvidenceUploadRequest
            {
                OwnerType = AttachmentEvidenceRules.OwnerTypeSalesOrder,
                OwnerId = order.Id,
                FileName = "空.pdf",
                DeclaredContentType = AttachmentEvidenceRules.MediaPdf,
                DeclaredLength = 0,
                Content = new MemoryStream(Array.Empty<byte>())
            }));
        Assert.Contains("文件为空", empty.Message);

        var missing = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UploadAsync(db, store,
            new AttachmentEvidenceUploadRequest
            {
                OwnerType = AttachmentEvidenceRules.OwnerTypeSalesOrder,
                OwnerId = order.Id,
                FileName = "缺失.pdf",
                DeclaredContentType = AttachmentEvidenceRules.MediaPdf,
                DeclaredLength = 10,
                Content = null
            }));
        Assert.Contains("未收到文件内容", missing.Message);

        Assert.Empty(await db.AttachmentEvidences.AsNoTracking().ToListAsync());
        Assert.Equal(0, store.SaveCalls);
    }

    // ==================== 5. 文件名净化（客户端路径一律忽略） ====================

    [Theory]
    [InlineData("C:\\temp\\x\\报关资料.PDF", "报关资料.PDF")]
    [InlineData("..\\..\\etc\\passwd.pdf", "passwd.pdf")]
    [InlineData("../../etc/passwd.pdf", "passwd.pdf")]
    [InlineData("/var/tmp/evidence.png", "evidence.png")]
    [InlineData("a<b>c\td.pdf", "a_b_cd.pdf")]
    [InlineData("  .hidden.pdf", "hidden.pdf")]
    public void 文件名净化_忽略客户端路径并替换不安全字符(string clientName, string expected)
        => Assert.Equal(expected, AttachmentEvidenceRules.SanitizeFileName(clientName));

    [Fact]
    public async Task 落库的是净化后的文件名快照且存储键不含原始文件名()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-L");
        var store = new InMemoryAttachmentContentStore();

        var saved = await UploadOkAsync(db, store, PdfBytes(), order.Id,
            fileName: "C:\\temp\\x\\报关资料.PDF");

        Assert.Equal("报关资料.PDF", saved.OriginalFileName);
        var row = await db.AttachmentEvidences.AsNoTracking().SingleAsync();
        Assert.Equal("报关资料.PDF", row.OriginalFileName);
        Assert.Equal(AttachmentEvidenceRules.MediaPdf, row.MediaType);
        Assert.DoesNotContain("报关", row.StorageKey);
        Assert.DoesNotContain("\\", row.StorageKey);
        Assert.DoesNotContain(":", row.StorageKey);
        Assert.EndsWith(".pdf", row.StorageKey);   // 存储扩展名由服务端按签名判定（小写）
    }

    [Fact]
    public async Task 超长原始文件名被有界截断且保留扩展名()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-M");
        var store = new InMemoryAttachmentContentStore();
        var longName = new string('长', 400) + ".pdf";

        var sanitized = AttachmentEvidenceRules.SanitizeFileName(longName);
        Assert.Equal(AttachmentEvidenceRules.MaxOriginalFileNameLength, sanitized.Length);
        Assert.EndsWith(".pdf", sanitized);
        Assert.Equal(AttachmentEvidenceRules.MaxOriginalFileNameLength, sanitized.Length);

        var saved = await UploadOkAsync(db, store, PdfBytes(), order.Id, fileName: longName);
        Assert.Equal(sanitized, saved.OriginalFileName);
    }

    // ==================== 6. 摘要完整性 + 「重复内容不静默合并」 ====================

    [Fact]
    public async Task 同一内容重复上传是两条独立证据_不做内容寻址去重也不静默合并()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-N");
        var store = new InMemoryAttachmentContentStore();
        var content = PdfBytes("same-evidence");

        var first = await UploadOkAsync(db, store, content, order.Id, description: "第一次上传");
        var second = await UploadOkAsync(db, store, content, order.Id, description: "第二次上传（同一份内容）");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.SizeBytes, second.SizeBytes);
        Assert.NotEqual(first.DownloadPath, second.DownloadPath);
        Assert.Equal(2, store.SaveCalls);
        Assert.Equal(2, store.Items.Count);          // 两条证据各自独立落盘
        Assert.Equal(2, await db.AttachmentEvidences.AsNoTracking().CountAsync());

        // 摘要只用于人工核对：按摘要前缀能把两条都查出来（同时证明它不是证据身份）
        var page = await AttachmentEvidenceService.ListAsync(db, new AttachmentEvidenceQuery
        {
            Sha256 = first.Sha256[..16]
        });
        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Items.Count);

        // 摘要筛选只接受 8~64 位十六进制
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListAsync(
            db, new AttachmentEvidenceQuery { Sha256 = "zz" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListAsync(
            db, new AttachmentEvidenceQuery { Sha256 = new string('a', 65) }));
    }

    // ==================== 7. 相邻业务记录非变更 ====================

    [Fact]
    public async Task 上传读取与作废均不改写订单库存财务单证与ERP045引用册()
    {
        using var db = TestDbFactory.Create();
        var salesOrder = SeedSalesOrder(db, "SO-061-O");
        var purchaseOrder = SeedPurchaseOrder(db, "PO-061-O");
        SeedAdjacentRecords(db);
        var store = new InMemoryAttachmentContentStore();
        var before = Snapshot(db);

        var salesRow = await UploadOkAsync(db, store, PdfBytes(), salesOrder.Id);
        var purchaseRow = await UploadOkAsync(db, store, JpegBytes(), purchaseOrder.Id,
            ownerType: AttachmentEvidenceRules.OwnerTypePurchaseOrder,
            fileName: "验货报告.jpg",
            declaredContentType: AttachmentEvidenceRules.MediaJpeg);

        await AttachmentEvidenceService.ListAsync(db, new AttachmentEvidenceQuery());
        await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, salesOrder.Id);
        await AttachmentEvidenceService.GetAsync(db, salesRow.Id);
        var content = await AttachmentEvidenceService.OpenContentAsync(db, store, salesRow.Id);
        await content.Content.DisposeAsync();
        await AttachmentEvidenceService.VoidAsync(db, purchaseRow.Id, "误传，需重新上传");

        Assert.Equal(before, Snapshot(db));

        // 归属单据逐项复核：状态 / 金额 / 备注 / 唛头均未变化
        var reloadedSales = await db.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == salesOrder.Id);
        Assert.Equal(DocumentStatus.Approved, reloadedSales.Status);
        Assert.Equal(12345.67m, reloadedSales.TotalAmount);
        Assert.Equal(2345.67m, reloadedSales.DepositAmount);
        Assert.Equal("销售订单备注", reloadedSales.Remark);
        Assert.Equal("MARKS-061", reloadedSales.ShippingMarks);

        var reloadedPurchase = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == purchaseOrder.Id);
        Assert.Equal(DocumentStatus.Approved, reloadedPurchase.Status);
        Assert.Equal(8888.88m, reloadedPurchase.TotalAmount);
        Assert.Equal("未到货", reloadedPurchase.ArrivalProgress);
        Assert.Equal("未验货", reloadedPurchase.QcStatus);

        // ERP-045 引用册与单证 FileNote 保持原样
        Assert.Single(await db.DocumentAttachmentReferences.AsNoTracking().ToListAsync());
        var document = await db.TradeDocuments.AsNoTracking().SingleAsync();
        Assert.Contains("ERP-061 不导入", document.FileNote);
    }

    // ==================== 8. 台账：分页有界 + 过滤 + 未知取值拒绝 ====================

    [Fact]
    public async Task 台账分页与过滤有界_未知筛选取值拒绝且默认包含已作废历史()
    {
        using var db = TestDbFactory.Create();
        var orderA = SeedSalesOrder(db, "SO-061-P");
        var orderB = SeedPurchaseOrder(db, "PO-061-P");
        var store = new InMemoryAttachmentContentStore();

        var firstA = await UploadOkAsync(db, store, PdfBytes("a1"), orderA.Id, description: "A 的第一份");
        var secondA = await UploadOkAsync(db, store, PngBytes(), orderA.Id,
            fileName: "图片.png", declaredContentType: AttachmentEvidenceRules.MediaPng);
        await UploadOkAsync(db, store, PdfBytes("b1"), orderB.Id,
            ownerType: AttachmentEvidenceRules.OwnerTypePurchaseOrder);
        await AttachmentEvidenceService.VoidAsync(db, secondA.Id, "图片模糊，需重新上传");

        // 默认（无过滤）：全部 3 条，且默认包含已作废历史（原始元数据保留可读）
        var all = await AttachmentEvidenceService.ListAsync(db);
        Assert.Equal(3, all.Total);
        Assert.Equal(3, all.Items.Count);
        Assert.Single(all.Items, i => i.IsVoided);

        // 归属类型 + 归属 Id
        var onlyA = await AttachmentEvidenceService.ListAsync(db, new AttachmentEvidenceQuery
        {
            OwnerType = AttachmentEvidenceRules.OwnerTypeSalesOrder,
            OwnerId = orderA.Id
        });
        Assert.Equal(2, onlyA.Total);
        Assert.All(onlyA.Items, i => Assert.Equal(orderA.Id, i.OwnerId));
        Assert.All(onlyA.Items, i => Assert.Equal("销售订单", i.OwnerTypeText));

        // 状态
        var activeOnly = await AttachmentEvidenceService.ListAsync(db,
            new AttachmentEvidenceQuery { Status = AttachmentEvidenceRules.StatusActive });
        Assert.Equal(2, activeOnly.Total);
        var voidedOnly = await AttachmentEvidenceService.ListAsync(db,
            new AttachmentEvidenceQuery { Status = AttachmentEvidenceRules.StatusVoided });
        Assert.Single(voidedOnly.Items);
        Assert.Contains("图片模糊", voidedOnly.Items[0].VoidReason);

        // 关键字（匹配文件名 / 说明 / 归属单据号）
        Assert.Single((await AttachmentEvidenceService.ListAsync(db,
            new AttachmentEvidenceQuery { Keyword = "图片" })).Items);
        Assert.Equal(2, (await AttachmentEvidenceService.ListAsync(db,
            new AttachmentEvidenceQuery { Keyword = "SO-061-P" })).Total);

        // 分页收敛：第 2 页每页 2 条
        var page2 = await AttachmentEvidenceService.ListAsync(db,
            new AttachmentEvidenceQuery { Page = 2, PageSize = 2 });
        Assert.Equal(3, page2.Total);
        Assert.Equal(2, page2.Page);
        Assert.Equal(2, page2.PageSize);
        Assert.Single(page2.Items);

        // 每页上限截断（有界）
        var capped = await AttachmentEvidenceService.ListAsync(db,
            new AttachmentEvidenceQuery { PageSize = 5000 });
        Assert.Equal(AttachmentEvidenceQuery.MaxPageSize, capped.PageSize);

        // 未知筛选取值一律拒绝
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListAsync(
            db, new AttachmentEvidenceQuery { OwnerType = "TradeDocuments" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListAsync(
            db, new AttachmentEvidenceQuery { Status = 7 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListAsync(
            db, new AttachmentEvidenceQuery { OwnerId = 0 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListAsync(
            db, new AttachmentEvidenceQuery { Keyword = new string('k', 101) }));

        Assert.Equal(AttachmentEvidenceRules.StatusActive, firstA.Status);
    }

    [Fact]
    public async Task 台账读取是固定数量的数据集访问_分页行数变化不改变访问次数且只读不写库()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-Q");
        var store = new InMemoryAttachmentContentStore();
        await UploadOkAsync(db, store, PdfBytes(), order.Id);

        var counting = CountingDbContext.Wrap(db);
        var single = await AttachmentEvidenceService.ListAsync(
            counting.Proxy, new AttachmentEvidenceQuery { PageSize = 1 });
        var singleReads = counting.DatasetReads;

        Assert.Equal(1, single.Total);
        // 常数级访问：证据数据集（计数 + 本页）与归属单据数据集（批量可用性标注）各一次，无逐行查库
        Assert.Equal(2, singleReads);
        Assert.Equal(
            new[] { nameof(IErpDbContext.AttachmentEvidences), nameof(IErpDbContext.SalesOrders) },
            counting.ReadProperties.Distinct().ToArray());

        // 再补 300 条（跨多页）：访问次数必须保持不变（无逐行查库）
        for (var i = 0; i < 300; i++)
        {
            db.AttachmentEvidences.Add(new AttachmentEvidence
            {
                OwnerType = AttachmentEvidenceRules.OwnerTypeSalesOrder,
                OwnerId = order.Id,
                OwnerNo = "SO-061-Q",
                OwnerTypeText = "销售订单",
                OriginalFileName = $"证据{i}.pdf",
                MediaType = AttachmentEvidenceRules.MediaPdf,
                SizeBytes = 10,
                Sha256 = new string('a', 64),
                StorageKey = $"20260901/{i:d32}.pdf",
                StorageProvider = AttachmentEvidenceRules.ProviderIsolatedLocal,
                UploadedBy = "测试用户",
                RecordedAt = DateTime.Now,
                Status = AttachmentEvidenceRules.StatusActive,
                CreatedAt = DateTime.Now
            });
        }
        await db.SaveChangesAsync();

        var large = await AttachmentEvidenceService.ListAsync(
            counting.Proxy, new AttachmentEvidenceQuery { PageSize = 200 });
        var largeReads = counting.DatasetReads - singleReads;

        Assert.Equal(301, large.Total);
        Assert.Equal(AttachmentEvidenceQuery.MaxPageSize, large.Items.Count);   // 单页有界（上限 200）
        Assert.Equal(singleReads, largeReads);                                  // 行数 / 页大小变化不改变访问次数
        Assert.Equal(0, counting.WriteCalls);                                   // 只读：不落库
        Assert.All(large.Items, r => Assert.Equal("销售订单", r.OwnerTypeText));
    }

    // ==================== 9. 指定归属清单 + 归属删除后的历史可读（绝不改派） ====================

    [Fact]
    public async Task 指定归属的有界清单_归属不存在或已删除拒绝_默认含已作废历史()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-R");
        var other = SeedSalesOrder(db, "SO-061-R2");
        var deleted = SeedSalesOrder(db, "SO-061-R3", deleted: true);
        var store = new InMemoryAttachmentContentStore();

        var active = await UploadOkAsync(db, store, PdfBytes("r1"), order.Id);
        var voided = await UploadOkAsync(db, store, PngBytes(), order.Id,
            fileName: "图片.png", declaredContentType: AttachmentEvidenceRules.MediaPng);
        await UploadOkAsync(db, store, PdfBytes("r2"), other.Id, description: "另一张订单的证据");
        await AttachmentEvidenceService.VoidAsync(db, voided.Id, "重复上传，作废本条");

        var all = await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id);
        Assert.Equal(2, all.Count);
        Assert.Single(all, r => r.Id == active.Id && r.IsActive);
        Assert.Single(all, r => r.Id == voided.Id && r.IsVoided);

        Assert.Single(await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id, AttachmentEvidenceRules.StatusActive));
        Assert.Single(await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id, AttachmentEvidenceRules.StatusVoided));

        // take 回落与上限有界：绝不会退化成无界查询
        Assert.Equal(200, AttachmentEvidenceService.MaxPerOwner);
        Assert.True((await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id, null, 0)).Count
            <= AttachmentEvidenceService.MaxPerOwner);

        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, 999_999));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, deleted.Id));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListForOwnerAsync(
            db, "Quotation", order.Id));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, 0));
    }

    [Fact]
    public async Task 归属单据删除后历史证据仍可只读查看并标注不可用_绝不改派()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-S");
        var store = new InMemoryAttachmentContentStore();
        var saved = await UploadOkAsync(db, store, PdfBytes(), order.Id);

        order.IsDeleted = true;
        await db.SaveChangesAsync();

        var detail = await AttachmentEvidenceService.GetAsync(db, saved.Id);
        Assert.False(detail.OwnerAvailable);
        Assert.Contains("已不存在或已删除", detail.OwnerAvailabilityText);
        Assert.False(detail.ContentDownloadable);
        Assert.Contains("不提供下载", detail.DownloadAvailabilityText);
        Assert.Equal("SO-061-S", detail.OwnerNo);                        // 历史快照照常可读
        Assert.Equal(AttachmentEvidenceRules.OwnerTypeSalesOrder, detail.OwnerType);
        Assert.Equal(order.Id, detail.OwnerId);                          // 绝不改派到别的单据
        Assert.Equal(KnownPdfDigest, detail.Sha256);
        Assert.False(detail.IsVoided);                                   // 删除归属 ≠ 作废证据

        // 下载被拒绝（重新校验权威归属）
        var download = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AttachmentEvidenceService.OpenContentAsync(db, store, saved.Id));
        Assert.Contains("已不存在或已删除", download.Message);

        // 该单据的清单请求同样被拒绝（显式归属复核），但台账仍能看到这一行并标注不可用
        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id));

        var page = await AttachmentEvidenceService.ListAsync(db);
        Assert.Equal(1, page.Total);
        Assert.False(page.Items[0].OwnerAvailable);
        Assert.Equal(order.Id, page.Items[0].OwnerId);

        // 内容本体仍在隔离存储中：只是不提供下载，而不是删除证据
        Assert.Single(store.Items);
    }

    // ==================== 10. 下载：内容一致性 + 已作废 / 缺失 / 被改动拒绝 ====================

    [Fact]
    public async Task 下载_有效证据返回内容与安全元数据_且内容与登记一致()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-T");
        var store = new InMemoryAttachmentContentStore();
        var pdf = PdfBytes("download-check");
        var saved = await UploadOkAsync(db, store, pdf, order.Id, fileName: "下载核对.pdf");

        var content = await AttachmentEvidenceService.OpenContentAsync(db, store, saved.Id);
        await using (content.Content)
        {
            using var buffer = new MemoryStream();
            await content.Content.CopyToAsync(buffer);
            Assert.Equal(pdf, buffer.ToArray());                 // 字节级一致（未转换、未渲染）
        }

        Assert.Equal("下载核对.pdf", content.FileName);
        Assert.Equal(AttachmentEvidenceRules.MediaPdf, content.MediaType);
        Assert.Equal(saved.SizeBytes, content.SizeBytes);
        Assert.Equal(saved.Sha256, content.Sha256);
    }

    [Fact]
    public async Task 下载_已作废证据拒绝_原始元数据与内容仍保留可读()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-U");
        var store = new InMemoryAttachmentContentStore();
        var saved = await UploadOkAsync(db, store, PdfBytes(), order.Id, description: "待作废的证据");

        var voided = AssertOk<AttachmentEvidenceDto>(await BuildController(db, store)
            .Void(saved.Id, new AttachmentEvidenceVoidRequest { Reason = "上传错单据，需重新上传" }));

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AttachmentEvidenceService.OpenContentAsync(db, store, saved.Id));
        Assert.Contains("已作废", ex.Message);
        Assert.Contains("不提供下载", ex.Message);

        var detail = await AttachmentEvidenceService.GetAsync(db, saved.Id);
        Assert.True(detail.IsVoided);
        Assert.False(detail.ContentDownloadable);
        Assert.Equal(saved.OriginalFileName, detail.OriginalFileName);
        Assert.Equal(saved.Sha256, detail.Sha256);
        Assert.Equal(saved.SizeBytes, detail.SizeBytes);
        Assert.Equal(saved.UploadedBy, detail.UploadedBy);
        Assert.Equal(saved.RecordedAt, detail.RecordedAt);
        Assert.Equal("上传错单据，需重新上传", detail.VoidReason);
        Assert.NotNull(voided.VoidedAt);

        // 行仍在（无硬删除），内容仍在隔离存储中（不替换、不删除远端对象）
        Assert.Single(await db.AttachmentEvidences.AsNoTracking().ToListAsync());
        Assert.Single(store.Items);
    }

    [Fact]
    public async Task 下载_内容缺失或被外部改动一律拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-V");
        var store = new InMemoryAttachmentContentStore();

        // 1) 存储对象缺失 → 明确的不可用提示（不是静默空内容）
        var missing = await UploadOkAsync(db, store, PdfBytes("missing"), order.Id);
        var missingKey = (await db.AttachmentEvidences.AsNoTracking().SingleAsync(r => r.Id == missing.Id)).StorageKey;
        store.Remove(missingKey);
        var notFound = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => AttachmentEvidenceService.OpenContentAsync(db, store, missing.Id));
        Assert.Contains("附件内容不可用", notFound.Message);

        // 2) 内容被外部替换（长度相同）→ 摘要复核失败，拒绝下载
        var replaced = await UploadOkAsync(db, store, PdfBytes("replaced"), order.Id);
        var replacedKey = (await db.AttachmentEvidences.AsNoTracking().SingleAsync(r => r.Id == replaced.Id)).StorageKey;
        var sameLength = PdfBytes("replaced");     // 长度相同、内容不同（外部替换，不是同一份证据）
        sameLength[^1] = (byte)'X';
        store.Tamper(replacedKey, sameLength);
        var digestMismatch = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AttachmentEvidenceService.OpenContentAsync(db, store, replaced.Id));
        Assert.Contains("摘要", digestMismatch.Message);

        // 3) 内容被外部替换（长度不同）→ 长度复核失败，拒绝下载
        var resized = await UploadOkAsync(db, store, PdfBytes("resized"), order.Id);
        var resizedKey = (await db.AttachmentEvidences.AsNoTracking().SingleAsync(r => r.Id == resized.Id)).StorageKey;
        store.Tamper(resizedKey, PdfBytes("resized-and-longer-content"));
        var lengthMismatch = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => AttachmentEvidenceService.OpenContentAsync(db, store, resized.Id));
        Assert.Contains("长度", lengthMismatch.Message);

        // 4) 不存在的证据 / Id 非法
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => AttachmentEvidenceService.OpenContentAsync(db, store, 999_999));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AttachmentEvidenceService.OpenContentAsync(db, store, 0));
    }

    // ==================== 11. 作废（唯一更正方式） ====================

    [Fact]
    public async Task 作废_原因必填且有界_重复作废拒绝_保留原始元数据且不硬删除()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-W");
        var store = new InMemoryAttachmentContentStore();
        var saved = await UploadOkAsync(db, store, PdfBytes(), order.Id, description: "待作废");

        // 原因缺失 / 超长 / 含标记 → 拒绝
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AttachmentEvidenceService.VoidAsync(db, saved.Id, null));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AttachmentEvidenceService.VoidAsync(db, saved.Id, "   "));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AttachmentEvidenceService.VoidAsync(db, saved.Id, new string('原', 501)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => AttachmentEvidenceService.VoidAsync(db, saved.Id, "<script>作废</script>"));

        var voided = await AttachmentEvidenceService.VoidAsync(db, saved.Id, "扫描不清，需重新上传");

        Assert.Equal(AttachmentEvidenceRules.StatusVoided, voided.Status);
        Assert.Equal("已作废", voided.StatusText);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal("扫描不清，需重新上传", voided.VoidReason);
        Assert.NotNull(voided.UpdatedAt);
        // 原始元数据与内容标识完全保留
        Assert.Equal(saved.OriginalFileName, voided.OriginalFileName);
        Assert.Equal(saved.Sha256, voided.Sha256);
        Assert.Equal(saved.MediaType, voided.MediaType);
        Assert.Equal(saved.SizeBytes, voided.SizeBytes);
        Assert.Equal(saved.OwnerNo, voided.OwnerNo);
        Assert.Equal(saved.OwnerType, voided.OwnerType);
        Assert.Equal(saved.OwnerId, voided.OwnerId);
        Assert.Equal(saved.UploadedBy, voided.UploadedBy);
        Assert.Equal(saved.RecordedAt, voided.RecordedAt);

        // 重复作废拒绝（不静默覆盖第一次的作废原因）
        var again = await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => AttachmentEvidenceService.VoidAsync(db, saved.Id, "第二次作废尝试"));
        Assert.Contains("不重复作废", again.Message);

        // 无硬删除、无软件删除、内容不替换
        var row = await db.AttachmentEvidences.AsNoTracking().SingleAsync();
        Assert.False(row.IsDeleted);
        Assert.Equal("扫描不清，需重新上传", row.VoidReason);
        Assert.Single(store.Items);
    }

    [Fact]
    public async Task 详情读取_不存在已软删除或Id非法一律拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-X");
        var store = new InMemoryAttachmentContentStore();
        var saved = await UploadOkAsync(db, store, PdfBytes(), order.Id);

        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.GetAsync(db, 999_999));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.GetAsync(db, 0));

        var row = await db.AttachmentEvidences.FirstAsync(r => r.Id == saved.Id);
        row.IsDeleted = true;
        await db.SaveChangesAsync();

        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.GetAsync(db, saved.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => AttachmentEvidenceService.OpenContentAsync(db, store, saved.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => AttachmentEvidenceService.VoidAsync(db, saved.Id, "已软删除行不可再作废"));

        // 软删除行也不出现在台账里
        Assert.Equal(0, (await AttachmentEvidenceService.ListAsync(db)).Total);
    }

    // ==================== 12. 模块元数据 / 提供程序守卫 / 归属候选 ====================

    [Fact]
    public void 模块元数据_白名单与当前提供程序与边界文案()
    {
        var store = new InMemoryAttachmentContentStore();
        var metadata = AttachmentEvidenceService.GetMetadata(store);

        // ERP-061 接入销售订单 / 采购订单；ERP-062 在同一模型上接入出口单证；
        // ERP-063 在同一模型上接入验货记录（既有采购订单 QC 记录）与样品记录（唯一一套附件内容模型）
        Assert.Equal(new[] { "SalesOrder", "PurchaseOrder", "TradeDocument", "QualityInspection", "Sample" },
            metadata.OwnerTypes.Select(o => o.Value).ToArray());
        Assert.Equal("出口单证", metadata.OwnerTypes.Single(o => o.Value == "TradeDocument").Label);
        Assert.Equal("验货记录", metadata.OwnerTypes.Single(o => o.Value == "QualityInspection").Label);
        Assert.Equal("样品记录", metadata.OwnerTypes.Single(o => o.Value == "Sample").Label);
        Assert.Contains("不是验货合格 / 不合格判定", metadata.QualityInspectionEvidenceBoundaryText);
        Assert.Contains("不代表样品已获批准", metadata.SampleEvidenceBoundaryText);
        Assert.Equal(AttachmentEvidenceRules.MaxSummaryOwnerIds, metadata.MaxSummaryOwnerIds);
        Assert.Contains("历史自由文本", metadata.LegacyFileNotePolicyText);
        Assert.Contains("也不在读取时按它回填附件行", metadata.LegacyFileNotePolicyText);
        Assert.Contains("不是报关单回执", metadata.TradeDocumentEvidenceBoundaryText);
        Assert.Equal(3, metadata.MediaTypes.Count);
        Assert.Equal(new[] { ".pdf", ".png", ".jpg", ".jpeg" }, metadata.AllowedExtensions.ToArray());
        Assert.Equal(20L * 1024 * 1024, metadata.MaxSizeBytes);
        Assert.Equal("20 MB", AttachmentEvidenceRules.SizeText(metadata.MaxSizeBytes));
        Assert.Equal(AttachmentEvidenceQuery.MaxPageSize, metadata.MaxPageSize);
        Assert.Equal(AttachmentEvidenceService.MaxPerOwner, metadata.MaxPerOwner);
        Assert.Equal(AttachmentEvidenceService.MaxOwnerOptions, metadata.MaxOwnerOptions);

        // 当前提供程序：隔离的非生产本地存储；生产 OSS 明确未激活（Human Gate）
        Assert.Equal(AttachmentEvidenceRules.ProviderIsolatedLocal, metadata.StorageProviderCode);
        Assert.Contains("隔离非生产本地存储", metadata.StorageProviderText);
        Assert.Contains("OSS", metadata.StoragePolicyText);
        Assert.Contains("Human Gate", metadata.StoragePolicyText);
        Assert.Contains("未激活", AttachmentEvidenceRules.ProviderText(AttachmentEvidenceRules.ProviderOss));
        Assert.Contains("nosniff", metadata.DownloadPolicyText);
        Assert.Contains("sandbox", metadata.DownloadPolicyText);
        Assert.Contains("不是报关", metadata.BoundaryText);
    }

    [Fact]
    public async Task 生产存储提供程序一律拒绝_开发测试只允许隔离的非生产提供程序()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-Y");
        var production = new InMemoryAttachmentContentStore
        {
            IsProductionProvider = true,
            ProviderCode = AttachmentEvidenceRules.ProviderOss,
            ProviderText = AttachmentEvidenceRules.ProviderText(AttachmentEvidenceRules.ProviderOss)
        };

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => UploadAsync(db, production,
            UploadRequest(PdfBytes(), ownerId: order.Id)));

        Assert.Contains("Human Gate", ex.Message);
        Assert.Equal(0, production.SaveCalls);
        Assert.Empty(await db.AttachmentEvidences.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 归属单据候选有界并批量统计证据条数_未知类型与超长关键字拒绝()
    {
        using var db = TestDbFactory.Create();
        var orderA = SeedSalesOrder(db, "SO-061-Y1");
        var orderB = SeedSalesOrder(db, "SO-061-Y2");
        SeedSalesOrder(db, "SO-061-Y3", deleted: true);
        SeedPurchaseOrder(db, "PO-061-Y1");
        var store = new InMemoryAttachmentContentStore();

        var first = await UploadOkAsync(db, store, PdfBytes("y1"), orderA.Id);
        var second = await UploadOkAsync(db, store, PngBytes(), orderA.Id,
            fileName: "图片.png", declaredContentType: AttachmentEvidenceRules.MediaPng);
        await AttachmentEvidenceService.VoidAsync(db, second.Id, "作废后仍计入历史条数");

        var options = await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, null);
        Assert.Equal(2, options.Count);                       // 已删除单据不出现在候选中
        Assert.All(options, o => Assert.Equal("销售订单", o.OwnerTypeText));
        Assert.All(options, o => Assert.True(o.Selectable));
        Assert.Equal(2, options.Single(o => o.OwnerId == orderA.Id).EvidenceCount);   // 含已作废历史
        Assert.Equal(0, options.Single(o => o.OwnerId == orderB.Id).EvidenceCount);
        Assert.Contains("SO-061-Y1", options.Single(o => o.OwnerId == orderA.Id).SelectableText);
        Assert.Contains("已有附件证据 2 条", options.Single(o => o.OwnerId == orderA.Id).SelectableText);

        // 关键字只匹配单据号码
        Assert.Single(await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, "SO-061-Y2"));

        // 采购订单候选：同一套有界口径
        var purchases = await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypePurchaseOrder, null);
        Assert.Single(purchases);
        Assert.Equal("采购订单", purchases[0].OwnerTypeText);
        Assert.Equal(0, purchases[0].EvidenceCount);

        // take 有界 + 未知类型 / 超长关键字拒绝
        Assert.Single(await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypeSalesOrder, null, 1));
        Assert.Equal(200, AttachmentEvidenceService.MaxOwnerOptions);
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService
            .ListOwnerOptionsAsync(db, "Quotation", null));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService
            .ListOwnerOptionsAsync(db, AttachmentEvidenceRules.OwnerTypeSalesOrder, new string('k', 101)));
    }

    // ==================== 13. 纯规则 ====================

    [Fact]
    public void 纯规则_签名判定扩展名有界文本与文案()
    {
        // 文件签名判定只依赖内容本身
        Assert.Equal(AttachmentEvidenceRules.MediaPdf, AttachmentEvidenceRules.DetectMediaType(PdfBytes()));
        Assert.Equal(AttachmentEvidenceRules.MediaPng, AttachmentEvidenceRules.DetectMediaType(PngBytes()));
        Assert.Equal(AttachmentEvidenceRules.MediaJpeg, AttachmentEvidenceRules.DetectMediaType(JpegBytes()));
        Assert.Null(AttachmentEvidenceRules.DetectMediaType(Array.Empty<byte>()));
        Assert.Null(AttachmentEvidenceRules.DetectMediaType(HtmlBytes()));
        Assert.Null(AttachmentEvidenceRules.DetectMediaType(new byte[] { 0x25, 0x50, 0x44 }));   // 截断的 PDF 头

        Assert.True(AttachmentEvidenceRules.IsExtensionForMediaType(".pdf", AttachmentEvidenceRules.MediaPdf));
        Assert.True(AttachmentEvidenceRules.IsExtensionForMediaType(".jpeg", AttachmentEvidenceRules.MediaJpeg));
        Assert.False(AttachmentEvidenceRules.IsExtensionForMediaType(".jpeg", AttachmentEvidenceRules.MediaPng));
        Assert.False(AttachmentEvidenceRules.IsExtensionForMediaType(".exe", AttachmentEvidenceRules.MediaPdf));

        Assert.Equal(".pdf", AttachmentEvidenceRules.FileExtension("A.PDF"));
        Assert.Equal(string.Empty, AttachmentEvidenceRules.FileExtension("A"));
        Assert.Equal(".jpg", AttachmentEvidenceRules.MediaTypeExtension(AttachmentEvidenceRules.MediaJpeg));
        Assert.Equal("JPEG 图片", AttachmentEvidenceRules.MediaTypeText(AttachmentEvidenceRules.MediaJpeg));
        Assert.Equal("未知类型", AttachmentEvidenceRules.MediaTypeText("application/zip"));

        Assert.Equal("512 B", AttachmentEvidenceRules.SizeText(512));
        Assert.Equal("1.5 KB", AttachmentEvidenceRules.SizeText(1536));
        Assert.Equal("20 MB", AttachmentEvidenceRules.SizeText(AttachmentEvidenceRules.MaxSizeBytes));

        Assert.Equal("有效", AttachmentEvidenceRules.StatusText(AttachmentEvidenceRules.StatusActive));
        Assert.Equal("已作废", AttachmentEvidenceRules.StatusText(AttachmentEvidenceRules.StatusVoided));
        Assert.Equal("未知状态（9）", AttachmentEvidenceRules.StatusText(9));

        Assert.Equal("销售订单", AttachmentEvidenceRules.OwnerTypeText("SalesOrder"));
        Assert.Equal("采购订单", AttachmentEvidenceRules.OwnerTypeText("PurchaseOrder"));
        Assert.Equal("出口单证", AttachmentEvidenceRules.OwnerTypeText("TradeDocument"));
        Assert.Equal("未知单据类型", AttachmentEvidenceRules.OwnerTypeText("Quotation"));
        Assert.Equal("已制作", AttachmentEvidenceRules.TradeDocumentStatusText(" 已制作 "));
        Assert.Equal("未登记状态", AttachmentEvidenceRules.TradeDocumentStatusText("   "));
        Assert.True(AttachmentEvidenceRules.IsSupportedOwnerType("tradedocument"));
        Assert.True(AttachmentEvidenceRules.IsSupportedOwnerType("purchaseorder"));
        Assert.False(AttachmentEvidenceRules.IsSupportedOwnerType("PurchaseOrders"));

        Assert.Equal("销售订单 SO-1", AttachmentEvidenceRules.OwnerSnapshotText("销售订单", "SO-1"));
        Assert.Equal("销售订单", AttachmentEvidenceRules.OwnerSnapshotText("销售订单", "  "));
        Assert.Equal("/api/attachment-evidences/5/content", AttachmentEvidenceRules.ContentApiPath(5));
        Assert.Contains("隔离非生产本地存储", AttachmentEvidenceRules.ProviderText(
            AttachmentEvidenceRules.ProviderIsolatedLocal));
        Assert.Equal(AttachmentEvidenceRules.UnknownText, AttachmentEvidenceRules.ProviderText("something"));

        // 明确拒绝的可执行 / 脚本 / 标记类扩展名清单（正文判定始终以签名为主）
        Assert.Contains(".svg", AttachmentEvidenceRules.ActiveOrExecutableExtensions);
        Assert.Contains(".html", AttachmentEvidenceRules.ActiveOrExecutableExtensions);
        Assert.Contains(".exe", AttachmentEvidenceRules.ActiveOrExecutableExtensions);

        // 有界文本与筛选
        Assert.Equal("原因", AttachmentEvidenceRules.NormalizeVoidReason(" 原因 "));
        Assert.Equal("0123456789abcdef", AttachmentEvidenceRules.NormalizeSha256Filter("0123456789ABCDEF"));
        Assert.Null(AttachmentEvidenceRules.NormalizeSha256Filter("   "));
        Assert.Equal(AttachmentEvidenceRules.StatusActive, AttachmentEvidenceRules.NormalizeStatusFilter(0));
        Assert.Null(AttachmentEvidenceRules.NormalizeStatusFilter(null));
        Assert.Throws<BusinessException>(() => AttachmentEvidenceRules.NormalizeStatusFilter(3));
        Assert.Equal("未知用户", AttachmentEvidenceRules.NormalizeUploadedBy("  "));
        Assert.Equal(string.Empty, AttachmentEvidenceRules.NormalizeDescription(null));
        Assert.Null(AttachmentEvidenceRules.NormalizeKeyword("   "));
        Assert.Equal("未知", AttachmentEvidenceRules.DigestText("  "));
        Assert.Contains("附件证据是用户提供的仓库文件证据", AttachmentEvidenceRules.BoundaryText);

        // 大小口径：20 MiB，且请求上限在其之上（表单开销）
        Assert.Equal(20L * 1024 * 1024, AttachmentEvidenceRules.MaxSizeBytes);
        Assert.True(AttachmentEvidenceRules.MaxRequestBytes > AttachmentEvidenceRules.MaxSizeBytes);
    }

    // ==================== 14. 隔离的非生产本地存储 + 存储提供程序工厂 ====================

    [Fact]
    public async Task 隔离本地存储_不透明键与隔离目录_拒绝路径穿越与不受支持扩展名()
    {
        var root = Path.Combine(Path.GetTempPath(), "erp061-attachment-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new IsolatedLocalAttachmentContentStore(new AttachmentStorageOptions { RootPath = root });

            Assert.Equal(AttachmentEvidenceRules.ProviderIsolatedLocal, store.ProviderCode);
            Assert.Contains("隔离非生产本地存储", store.ProviderText);
            Assert.False(store.IsProductionProvider);
            Assert.True(Directory.Exists(root));

            var bytes = PdfBytes("local-store");
            var key = string.Empty;
            await using (var content = new MemoryStream(bytes, writable: false))
            {
                key = await store.SaveAsync(content, ".pdf");
            }

            // 键是服务端生成的不透明标识：日期目录 + GUID + 服务端扩展名，不含任何客户端输入
            Assert.Matches(@"^\d{8}/[0-9a-f]{32}\.pdf$", key);
            Assert.DoesNotContain("..", key);
            Assert.True(File.Exists(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(bytes.LongLength, await store.GetLengthAsync(key));

            await using var read = await store.OpenReadAsync(key);
            Assert.NotNull(read);
            using var buffer = new MemoryStream();
            await read!.CopyToAsync(buffer);
            Assert.Equal(bytes, buffer.ToArray());

            // 内容落在隔离根目录内、且根目录本身不在 Web 根目录（wwwroot）之内
            Assert.DoesNotContain("wwwroot", store.RootPath, StringComparison.OrdinalIgnoreCase);

            // 未知键 → null（由上层转成明确提示）；非法键一律拒绝
            Assert.Null(await store.OpenReadAsync("20260901/00000000000000000000000000000000.pdf"));
            Assert.Null(await store.GetLengthAsync("20260901/00000000000000000000000000000000.pdf"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.OpenReadAsync("../escape.pdf"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.OpenReadAsync("a/../../escape.pdf"));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.OpenReadAsync(Path.Combine(root, "absolute.pdf")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.OpenReadAsync("2026\\09\\x.pdf"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.OpenReadAsync(""));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.OpenReadAsync(new string('k', AttachmentEvidenceRules.MaxStorageKeyLength + 1)));

            // 不受支持的扩展名不允许写入隔离存储（第二层防护，第一层在规则 / 服务层）
            await using var exeContent = new MemoryStream(HtmlBytes(), writable: false);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(exeContent, ".exe"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 隔离本地存储_拒绝位于Web根目录内的存储根()
    {
        var webRoot = Path.Combine(
            Path.GetTempPath(), "erp061-" + Guid.NewGuid().ToString("N"), "wwwroot", "attachments");

        var ex = Assert.Throws<InvalidOperationException>(() => new IsolatedLocalAttachmentContentStore(
            new AttachmentStorageOptions { RootPath = webRoot }));

        Assert.Contains("wwwroot", ex.Message);
        Assert.Contains("静态文件服务范围之外", ex.Message);
    }

    [Fact]
    public void 存储工厂_缺省与隔离本地可用_生产OSS与未知提供程序显式拒绝()
    {
        var root = Path.Combine(Path.GetTempPath(), "erp061-factory-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 缺省（未配置 Provider）→ 隔离的非生产本地存储，根目录取配置的隔离目录
            var local = AttachmentContentStoreFactory.Create(BuildConfiguration(
                (AttachmentEvidenceRules.LocalRootConfigurationKey, root)));
            var isolated = Assert.IsType<IsolatedLocalAttachmentContentStore>(local);
            Assert.Equal(AttachmentEvidenceRules.ProviderIsolatedLocal, isolated.ProviderCode);
            Assert.False(isolated.IsProductionProvider);
            Assert.Equal(Path.GetFullPath(root), isolated.RootPath);

            // 显式 local-isolated 同样可用
            Assert.IsType<IsolatedLocalAttachmentContentStore>(AttachmentContentStoreFactory.Create(
                BuildConfiguration(
                    (AttachmentEvidenceRules.ProviderConfigurationKey,
                        AttachmentEvidenceRules.ProviderIsolatedLocal),
                    (AttachmentEvidenceRules.LocalRootConfigurationKey, root))));

            // 生产 OSS：显式拒绝（未实现、未注册、未激活），并指向 Human Gate
            var oss = Assert.Throws<InvalidOperationException>(() => AttachmentContentStoreFactory.Create(
                BuildConfiguration((AttachmentEvidenceRules.ProviderConfigurationKey,
                    AttachmentEvidenceRules.ProviderOss))));
            Assert.Contains("Human Gate", oss.Message);
            Assert.Contains("未激活", oss.Message);

            // 未知提供程序：显式拒绝，不静默降级成本地存储
            var unknown = Assert.Throws<InvalidOperationException>(() => AttachmentContentStoreFactory.Create(
                BuildConfiguration((AttachmentEvidenceRules.ProviderConfigurationKey, "s3"))));
            Assert.Contains("未知的附件内容存储提供程序", unknown.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>构造内存配置（只使用非敏感的隔离存储键，不包含任何生产凭据）</summary>
    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] values)
    {
        var pairs = values.ToDictionary(v => v.Key, v => (string?)v.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
    }

    // ==================== 15. 控制器契约：路由 / 授权 / 上传限制 / 安全下载响应头 ====================

    [Fact]
    public async Task 控制器契约_路由授权上传大小限制与安全下载响应头()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-061-Z");
        var store = new InMemoryAttachmentContentStore();
        var controller = BuildController(db, store);

        var type = typeof(AttachmentEvidenceController);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());       // 全量动作继承类级授权
        Assert.Equal("api/attachment-evidences", type.GetCustomAttribute<RouteAttribute>()!.Template);

        var upload = type.GetMethod(nameof(AttachmentEvidenceController.Upload))!;
        Assert.NotNull(upload.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(upload.GetCustomAttribute<RequestSizeLimitAttribute>());   // 请求大小有界
        Assert.Contains("file", upload.GetParameters().Select(p => p.Name!));

        Assert.NotNull(type.GetMethod(nameof(AttachmentEvidenceController.GetPaged))!
            .GetCustomAttribute<HttpGetAttribute>());
        Assert.Equal("metadata", type.GetMethod(nameof(AttachmentEvidenceController.Metadata))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("by-owner", type.GetMethod(nameof(AttachmentEvidenceController.GetForOwner))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("owner-options", type.GetMethod(nameof(AttachmentEvidenceController.OwnerOptions))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("{id:long}", type.GetMethod(nameof(AttachmentEvidenceController.GetById))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("{id:long}/content", type.GetMethod(nameof(AttachmentEvidenceController.DownloadContent))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("{id:long}/void", type.GetMethod(nameof(AttachmentEvidenceController.Void))!
            .GetCustomAttribute<HttpPostAttribute>()!.Template);

        // 刻意不提供修改 / 硬删除 / 二进制替换接口（更正只能显式作废）
        var actions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(actions, m => m.GetCustomAttribute<HttpPutAttribute>() is not null);
        Assert.DoesNotContain(actions, m => m.GetCustomAttribute<HttpPatchAttribute>() is not null);
        Assert.DoesNotContain(actions, m => m.GetCustomAttribute<HttpDeleteAttribute>() is not null);

        // 下载：附件方式 + 防御性响应头（浏览器不内联渲染上传内容）
        var saved = await UploadOkAsync(db, store, PdfBytes(), order.Id);
        var result = await controller.DownloadContent(saved.Id);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal(AttachmentEvidenceRules.MediaPdf, file.ContentType);
        Assert.Equal(saved.OriginalFileName, file.FileDownloadName);
        Assert.False(file.EnableRangeProcessing);

        var response = controller.Response;
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"].ToString());
        Assert.Contains("sandbox", response.Headers["Content-Security-Policy"].ToString());
        Assert.Contains("no-store", response.Headers["Cache-Control"].ToString());
        Assert.Equal("noopen", response.Headers["X-Download-Options"].ToString());
        Assert.Equal("no-referrer", response.Headers["Referrer-Policy"].ToString());

        await using (file.FileStream)
        {
            using var buffer = new MemoryStream();
            await file.FileStream.CopyToAsync(buffer);
            Assert.Equal(PdfBytes(), buffer.ToArray());
        }
    }

    // ==================== 16. 模型配置契约 / 幂等结构 ====================

    [Fact]
    public void 模型配置契约_长度索引与刻意不建外键和唯一索引()
    {
        using var db = TestDbFactory.Create();
        var entityType = db.Model.FindEntityType(typeof(AttachmentEvidence));
        Assert.NotNull(entityType);

        // 表名沿用既有约定（DbSet 属性名 = AttachmentEvidences，与 SchemaUpgrader 幂等建表一致）：
        // 本任务既不额外覆盖表名，也不与既有实体（如 DocumentAttachmentReferences）采用不同约定
        Assert.Null(entityType!.FindAnnotation("Relational:TableName"));
        Assert.NotNull(typeof(IErpDbContext).GetProperty(nameof(IErpDbContext.AttachmentEvidences)));
        Assert.Equal(30, entityType.FindProperty(nameof(AttachmentEvidence.OwnerType))!.GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(AttachmentEvidence.OwnerNo))!.GetMaxLength());
        Assert.Equal(30, entityType.FindProperty(nameof(AttachmentEvidence.OwnerTypeText))!.GetMaxLength());
        Assert.Equal(255, entityType.FindProperty(nameof(AttachmentEvidence.OriginalFileName))!.GetMaxLength());
        Assert.Equal(120, entityType.FindProperty(nameof(AttachmentEvidence.MediaType))!.GetMaxLength());
        Assert.Equal(64, entityType.FindProperty(nameof(AttachmentEvidence.Sha256))!.GetMaxLength());
        Assert.Equal(500, entityType.FindProperty(nameof(AttachmentEvidence.Description))!.GetMaxLength());
        Assert.Equal(200, entityType.FindProperty(nameof(AttachmentEvidence.StorageKey))!.GetMaxLength());
        Assert.Equal(30, entityType.FindProperty(nameof(AttachmentEvidence.StorageProvider))!.GetMaxLength());
        Assert.Equal(100, entityType.FindProperty(nameof(AttachmentEvidence.UploadedBy))!.GetMaxLength());
        Assert.Equal(500, entityType.FindProperty(nameof(AttachmentEvidence.VoidReason))!.GetMaxLength());

        // 刻意不建唯一索引：同一摘要的重复上传是两条独立证据（绝不静默合并 / 覆盖）
        Assert.DoesNotContain(entityType.GetIndexes(), i => i.IsUnique);
        var indexNames = entityType.GetIndexes().Select(i => i.GetDatabaseName()).ToList();
        Assert.Contains("IX_AttachmentEvidences_Owner_Status_RecordedAt", indexNames);
        Assert.Contains("IX_AttachmentEvidences_Sha256", indexNames);
        Assert.Contains("IX_AttachmentEvidences_Status_RecordedAt", indexNames);
        Assert.All(entityType.GetIndexes(), i => Assert.Equal("IsDeleted = 0", i.GetFilter()));

        // 刻意不建外键与导航属性（归属单据软删除后历史证据必须始终可读）
        Assert.Empty(entityType.GetForeignKeys());
        Assert.Empty(entityType.GetNavigations());

        // 读取侧标注一律 [NotMapped]（不落库）
        foreach (var name in new[]
                 {
                     nameof(AttachmentEvidence.StatusText),
                     nameof(AttachmentEvidence.MediaTypeText),
                     nameof(AttachmentEvidence.SizeText),
                     nameof(AttachmentEvidence.OwnerAvailable),
                     nameof(AttachmentEvidence.OwnerAvailabilityText),
                     nameof(AttachmentEvidence.DownloadPath),
                     nameof(AttachmentEvidence.BoundaryText)
                 })
            Assert.Null(entityType.FindProperty(name));
    }

    [Fact]
    public void Schema_upgrade_幂等建表建索引且不含任何回填或写语句()
    {
        var script = File.ReadAllText(
            RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.AttachmentEvidences') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.AttachmentEvidences", script);
        Assert.Contains("OwnerType NVARCHAR(30) NOT NULL DEFAULT N''", script);
        Assert.Contains("OwnerId BIGINT NOT NULL", script);
        Assert.Contains("OwnerNo NVARCHAR(50) NOT NULL DEFAULT N''", script);
        Assert.Contains("OwnerTypeText NVARCHAR(30) NOT NULL DEFAULT N''", script);
        Assert.Contains("OriginalFileName NVARCHAR(255) NOT NULL DEFAULT N''", script);
        Assert.Contains("MediaType NVARCHAR(120) NOT NULL DEFAULT N''", script);
        Assert.Contains("SizeBytes BIGINT NOT NULL DEFAULT 0", script);
        Assert.Contains("Sha256 NVARCHAR(64) NOT NULL DEFAULT N''", script);
        Assert.Contains("Description NVARCHAR(500) NOT NULL DEFAULT N''", script);
        Assert.Contains("StorageKey NVARCHAR(200) NOT NULL DEFAULT N''", script);
        Assert.Contains("StorageProvider NVARCHAR(30) NOT NULL DEFAULT N''", script);
        Assert.Contains("UploadedBy NVARCHAR(100) NOT NULL DEFAULT N''", script);
        Assert.Contains("RecordedAt DATETIME2 NOT NULL DEFAULT GETDATE()", script);
        Assert.Contains("Status INT NOT NULL DEFAULT 0", script);
        Assert.Contains("VoidedAt DATETIME2 NULL", script);
        Assert.Contains("VoidReason NVARCHAR(500) NOT NULL DEFAULT N''", script);

        Assert.Contains("CREATE INDEX IX_AttachmentEvidences_Owner_Status_RecordedAt", script);
        Assert.Contains("CREATE INDEX IX_AttachmentEvidences_Sha256", script);
        Assert.Contains("CREATE INDEX IX_AttachmentEvidences_Status_RecordedAt", script);
        Assert.Contains("WHERE IsDeleted = 0;", script);

        // 本模块不建任何唯一索引：重复内容不做内容寻址去重；也不建外键
        Assert.DoesNotContain("CREATE UNIQUE INDEX UX_AttachmentEvidences", script);
        Assert.DoesNotContain("FK_AttachmentEvidences", script);

        // 本模块段落只建表 + 普通索引：不回填、不改既有表、不写业务数据
        var start = script.IndexOf("// 40. 业务单据附件内容证据", StringComparison.Ordinal);
        Assert.True(start > 0);
        var segment = script[start..];
        Assert.DoesNotContain("ALTER TABLE", segment);
        Assert.DoesNotContain("UPDATE db_owner", segment);
        Assert.DoesNotContain("INSERT INTO db_owner", segment);
        Assert.DoesNotContain("DELETE FROM db_owner", segment);
        Assert.DoesNotContain("SalesOrders", segment);
        Assert.DoesNotContain("PurchaseOrders", segment);
        Assert.DoesNotContain("TradeDocuments", segment);
        Assert.DoesNotContain("StockMovements", segment);
        Assert.DoesNotContain("FinanceExpenses", segment);
        Assert.DoesNotContain("DocumentAttachmentReferences", segment);
    }

    // ==================== 17. 前端与路由接线契约 ====================

    [Fact]
    public void 前端与路由接线契约()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/attachment-evidences.js", index);

        // 销售订单 / 采购订单列表行操作各提供一个入口（两个模块共用同一登记册）
        var modulesDoc = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc.js"));
        Assert.Equal(2, modulesDoc.Split("openAttachmentEvidencesForCurrentModule").Length - 1);
        Assert.Contains("附件证据", modulesDoc);
        Assert.Contains("服务端按文件签名复核", modulesDoc);

        var js = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "attachment-evidences.js"));
        Assert.Contains("async function openAttachmentEvidencesForCurrentModule(ownerId)", js);
        Assert.Contains("async function openAttachmentEvidences(ownerType, ownerId)", js);
        Assert.Contains("function aeOwnerTypeOfModule(code)", js);
        Assert.Contains("case 'sales-order': return 'SalesOrder';", js);
        Assert.Contains("case 'purchase-order': return 'PurchaseOrder';", js);
        Assert.Contains("'/api/attachment-evidences'", js);
        Assert.Contains("'/api/attachment-evidences?'", js);
        Assert.Contains("/owner-options?", js);
        Assert.Contains("/content'", js);
        Assert.Contains("'/void'", js);
        Assert.Contains("uploadFile('/api/attachment-evidences', formData)", js);
        Assert.Contains("async function aeDownload(id)", js);
        Assert.Contains("async function aeConfirmVoid()", js);
        Assert.Contains("aeEsc(", js);
        Assert.Contains("不可信文件", js);
        Assert.Contains("不再提供下载", js);
        Assert.Contains("不提供编辑", js);
        Assert.Contains("存储键从不返回给界面", js);
        // 界面不接触存储键、不引用生产 OSS 客户端、不自行计算摘要
        Assert.DoesNotContain("storageKey", js);
        Assert.DoesNotContain("OssStorageService", js);
        Assert.DoesNotContain("AccessKey", js);

        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "AttachmentEvidenceController.cs"));
        Assert.Contains("[Route(\"api/attachment-evidences\")]", controller);
        Assert.Contains("[Authorize]", controller);
        Assert.Contains("RequestSizeLimit(AttachmentEvidenceRules.MaxRequestBytes)", controller);
        Assert.Contains("{id:long}/content", controller);
        Assert.Contains("{id:long}/void", controller);
        Assert.Contains("nosniff", controller);
        Assert.Contains("Content-Security-Policy", controller);
    }

    // ==================== 18. ERP-061 审计结论 ====================

    [Fact]
    public void ERP061审计_唯一附件内容模型_不引入第二套文件存储且不改写既有OSS与ERP045()
    {
        // 服务层 / 规则层 / 隔离存储只依赖唯一内容接缝：不引用基础设施存储实现、不读取任何凭据
        var service = File.ReadAllText(
            RepoFile("src", "ERP.Application", "Services", "AttachmentEvidenceService.cs"));
        Assert.Contains("IAttachmentContentStore", service);
        Assert.DoesNotContain("using ERP.Infrastructure", service);
        Assert.DoesNotContain("AccessKeySecret", service);
        Assert.DoesNotContain("GetSignedUrl", service);
        Assert.DoesNotContain("new OssStorageService", service);

        var rules = File.ReadAllText(
            RepoFile("src", "ERP.Application", "Services", "AttachmentEvidenceRules.cs"));
        Assert.DoesNotContain("using ERP.Infrastructure", rules);
        Assert.Contains("ProviderOss", rules);                       // 生产 OSS 只作为「未激活」标记存在
        Assert.Contains("唯一", rules);

        var store = File.ReadAllText(RepoFile(
            "src", "ERP.Infrastructure", "Storage", "IsolatedLocalAttachmentContentStore.cs"));
        Assert.Contains(": IAttachmentContentStore", store);
        Assert.DoesNotContain("new OssStorageService", store);
        Assert.DoesNotContain("GetSignedUrl", store);

        var factory = File.ReadAllText(RepoFile(
            "src", "ERP.Infrastructure", "Storage", "AttachmentContentStoreFactory.cs"));
        Assert.Contains("AttachmentEvidenceRules.ProviderOss", factory);
        Assert.Contains("Human Gate", factory);

        // 唯一附件内容模型：ERP-061 接入销售订单 / 采购订单，ERP-062 在**同一**模型上接入出口单证，
        // ERP-063 在**同一**模型上接入验货记录与样品记录
        // （不新增二进制表、不新增自由路径字段、不新增单据专用上传引擎）
        Assert.Equal(5, AttachmentEvidenceRules.SupportedOwnerTypes.Length);
        Assert.Contains(AttachmentEvidenceRules.OwnerTypeTradeDocument, AttachmentEvidenceRules.SupportedOwnerTypes);
        Assert.Contains(AttachmentEvidenceRules.OwnerTypeQualityInspection, AttachmentEvidenceRules.SupportedOwnerTypes);
        Assert.Contains(AttachmentEvidenceRules.OwnerTypeSample, AttachmentEvidenceRules.SupportedOwnerTypes);
        Assert.Equal(AttachmentEvidenceRules.SupportedOwnerTypes.Length,
            AttachmentEvidenceRules.SupportedOwnerTypes.Distinct(StringComparer.Ordinal).Count());

        // ERP-045「仅元数据引用册」保持原样（四类父单据白名单与边界文案均未被改写）
        var references = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "DocumentAttachmentReferenceRules.cs"));
        Assert.Contains("ParentTypeTradeDocument", references);
        Assert.Contains("MetadataOnlyNoticeText", references);

        // 既有生产 OSS 客户端保持原样（本任务不实现、不注册、不激活、不修改它）
        var oss = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Storage", "OssStorageService.cs"));
        Assert.Contains("class OssStorageService", oss);
        Assert.Contains("UploadAsync", oss);

        // 服务说明与数据库设计说明书同步（口径可追溯）
        var doc = File.ReadAllText(RepoFile("docs", "业务单据附件证据说明.md"));
        Assert.Contains("AttachmentEvidences", doc);
        Assert.Contains("IAttachmentContentStore", doc);
        Assert.Contains("Human Gate", doc);

        var design = File.ReadAllText(RepoFile("docs", "数据库设计说明书.md"));
        Assert.Contains("AttachmentEvidences", design);
    }

    // ==================== 19. 测试替身与脚手架 ====================

    /// <summary>构造控制器（下载用例需要 HttpContext 才能写入防御性响应头）</summary>
    private static AttachmentEvidenceController BuildController(ErpDbContext db, IAttachmentContentStore store)
        => new(db, store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    /// <summary>
    /// 测试内内存内容存储（实现唯一内容接缝）：键同样是服务端生成的不透明标识；
    /// 另提供 <see cref="Tamper"/> / <see cref="Remove"/> 模拟「内容被外部改动 / 对象缺失」。
    /// </summary>
    private sealed class InMemoryAttachmentContentStore : IAttachmentContentStore
    {
        public Dictionary<string, byte[]> Items { get; } = new();

        public int SaveCalls { get; private set; }

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
            => Task.FromResult<Stream?>(Items.TryGetValue(storageKey, out var bytes)
                ? new MemoryStream(bytes, writable: false)
                : null);

        public Task<long?> GetLengthAsync(string storageKey, CancellationToken cancellationToken = default)
            => Task.FromResult<long?>(Items.TryGetValue(storageKey, out var bytes) ? bytes.LongLength : null);

        /// <summary>移除内容（模拟隔离存储中对象缺失）</summary>
        public void Remove(string storageKey) => Items.Remove(storageKey);

        /// <summary>替换内容（模拟外部改动 / 二进制被替换）</summary>
        public void Tamper(string storageKey, byte[] content) => Items[storageKey] = content;
    }

    /// <summary>超限内容流：用常量字节流模拟超大上传，验证「读取在到达上限时立即中止」</summary>
    private sealed class OversizeStream : Stream
    {
        private long _remaining;

        public OversizeStream(long length) => _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var read = (int)Math.Min(count, _remaining);
            Array.Fill(buffer, (byte)0x41, offset, read);
            _remaining -= read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// 只读计数上下文代理（<see cref="DispatchProxy"/>）：记录访问的数据集（<c>DbSet</c> 属性）名称与写入次数，
    /// 用于断言「分页 / 有界查询」「无逐行查库」与「只读不写库」；不改动生产代码。
    /// </summary>
    public class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;

        /// <summary>包装后的上下文（服务 / 控制器按 <see cref="IErpDbContext"/> 使用）</summary>
        public IErpDbContext Proxy { get; private set; } = null!;

        /// <summary>数据集（<c>DbSet</c> 属性）访问次数：即本次查询实际发起的数据集访问次数</summary>
        public int DatasetReads => ReadProperties.Count;

        /// <summary>被访问的数据集属性名（本模块预期只有证据表与归属单据表）</summary>
        public List<string> ReadProperties { get; } = new();

        /// <summary><c>SaveChangesAsync</c> 调用次数：只读库恒为 0</summary>
        public int WriteCalls { get; private set; }

        /// <summary>包装一个真实上下文（计数从返回对象上读取）</summary>
        public static CountingDbContext Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, CountingDbContext>();
            var counting = (CountingDbContext)(object)proxy;
            counting._inner = inner;
            counting.Proxy = proxy;
            return counting;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                WriteCalls++;
                return _inner.SaveChangesAsync(args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }

            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
            {
                ReadProperties.Add(targetMethod.Name[4..]);
            }

            return targetMethod.Invoke(_inner, args);
        }
    }
}
