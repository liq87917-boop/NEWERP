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
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 验货记录与样品记录附件证据单元测试（ERP-063）：把**既有**验货记录与样品记录接入 ERP-061 的**同一**
/// 附件证据模型（同一张 <c>AttachmentEvidences</c> 表、同一 <c>IAttachmentContentStore</c> 内容接缝、
/// 同一套格式 / 大小 / 下载 / 作废口径）。覆盖：
/// <list type="bullet">
/// <item><b>审计结论</b>：本仓库**没有**独立验货 / 质检实体（<c>docs/菜单与业务流程优化建议-20260918.md</c>
///   明确「验货单 QC 未实现、未立项，采购订单仅有 QcStatus」），因此验货记录的**权威记录**就是既有
///   采购订单上的 QC 字段，归属只使用其**既有持久化 Id**；本任务**不**新建验货 / 样品 / 存储主数据，
///   也**不**新建第二张表或第二个上传引擎；</item>
/// <item>两类归属的**显式类型 + Id** 归集、上传、服务端权威元数据（净化文件名 / 媒体类型 / 长度 / 摘要 /
///   归属号码快照 / 上传人 / 登记时间不接受客户端提交）；</item>
/// <item>归属不存在 / 已软删除 / Id 非法时上传与清单一律拒绝；归属软删除后历史证据仍可只读查看、
///   不可下载、**绝不改派**；</item>
/// <item>作废（原因必填、保留原始文件名 / 摘要 / 媒体类型 / 登记历史、重复作废拒绝、无硬删除、
///   无二进制替换、无跨记录改派）；</item>
/// <item>归属候选与列表摘要：号码 / 验货状态 / 到货进度 / 样品类型 / 客户反馈**只读原文**，
///   绝不推断合格 / 不合格、样品批准或客户确认；摘要为**有界计数**、固定数据集访问、**零存储访问**；</item>
/// <item>非变更：上传 / 读取 / 下载 / 作废都**不**改写采购订单（QcStatus / ArrivalProgress / 状态 /
///   金额 / 明细）、样品台账（客户反馈 / 样品费 / 寄出信息）、商品、供应商、客户、销售订单、装柜、
///   库存与流水、供应商发票、退税台账与费用单；</item>
/// <item>同一采购订单上的「采购订单证据」与「验货记录证据」是**两个各自独立**的归属类型，
///   同名同摘要的不同记录绝不合并、绝不隐式共享；</item>
/// <item>控制器契约（类级授权、无匿名端点、无 PUT / PATCH / DELETE、附件方式下载与防御性响应头）
///   与前端接线契约（样品管理行操作 / 概览、采购订单行操作「验货证据」、界面不接触存储键）。</item>
/// </list>
/// <para>全部使用内存库（<see cref="TestDbFactory"/>）与**测试内**的内存内容存储：不连接 SQL Server、
/// 不执行任何 SQL / 部署脚本、不访问生产 OSS、不做任何浏览器 / UI 验收（浏览器验收按项目策略
/// 记录为 browser_deferred，延后到 FINAL-UI-ACCEPTANCE）。</para>
/// </summary>
public class QualityInspectionSampleAttachmentEvidenceTests
{
    // ==================== 0. 测试脚手架 ====================

    private static byte[] PdfBytes(string body = "abc") => Encoding.UTF8.GetBytes("%PDF-1.4\n" + body);

    private static byte[] PngBytes()
        => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };

    private static byte[] HtmlBytes()
        => Encoding.UTF8.GetBytes("<html><body><script>alert('x')</script></body></html>");

    /// <summary>
    /// 播种一张采购订单（**验货记录的权威记录**：QcStatus 验货状态 + ArrivalProgress 到货进度），
    /// 含一行明细，用于断言附件证据不改写订单与明细。
    /// </summary>
    private static PurchaseOrder SeedPurchaseOrder(
        ErpDbContext db,
        string orderNo,
        string qcStatus = "验货中",
        string arrivalProgress = "部分到货",
        DocumentStatus status = DocumentStatus.Approved,
        bool deleted = false)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 2),
            SupplierId = 801,
            ContractNo = "PC-063",
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            TotalAmount = 8800m,
            AdvanceOnBehalf = true,
            SupplierConfirmedDate = new DateTime(2026, 9, 12),
            TaxRate = 13m,
            TaxIncluded = true,
            ArrivalProgress = arrivalProgress,
            QcStatus = qcStatus,
            SettlementProgress = "部分结算",
            PaymentTerms = "月结 30 天",
            Remark = "采购订单备注",
            Status = status,
            IsDeleted = deleted
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();

        db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
        {
            PurchaseOrderId = order.Id,
            ProductId = 9101,
            ProductName = "测试商品",
            Spec = "规格-A",
            Quantity = 20m,
            Unit = "箱",
            UnitPrice = 440m,
            Amount = 8800m
        });
        db.SaveChanges();
        return order;
    }

    /// <summary>播种一条样品记录（既有 Sample 台账：样品类型 / 样品费 / 寄出信息 / 客户反馈结果）</summary>
    private static Sample SeedSample(
        ErpDbContext db,
        string sampleNo,
        string sampleType = "寄样",
        string result = "待反馈",
        bool deleted = false)
    {
        var sample = new Sample
        {
            SampleNo = sampleNo,
            SampleDate = new DateTime(2026, 9, 3),
            CustomerId = 501,
            CustomerName = "测试客户",
            ProductId = 9101,
            ProductName = "测试商品",
            Spec = "规格-A",
            SampleType = sampleType,
            Quantity = 2m,
            Unit = "件",
            SampleFee = 120m,
            Currency = "USD",
            FeeSettled = false,
            SendDate = new DateTime(2026, 9, 4),
            Express = "顺丰",
            TrackingNo = "SF-063",
            Result = result,
            SalesmanId = 12,
            SalesmanName = "业务员甲",
            Remark = "样品备注",
            IsDeleted = deleted
        };
        db.Samples.Add(sample);
        db.SaveChanges();
        return sample;
    }

    /// <summary>
    /// 播种相邻业务记录（商品 / 供应商 / 客户 / 销售订单与明细 / 装柜清单 / 库存与流水），用于非变更断言。
    /// </summary>
    private static void SeedAdjacentRecords(ErpDbContext db)
    {
        db.BaseProducts.Add(new BaseProduct
        {
            ProductCode = "P-063",
            ProductName = "测试商品",
            Spec = "规格-A",
            Unit = "箱",
            Certification = "CE"
        });
        db.BaseSuppliers.Add(new BaseSupplier
        {
            SupplierCode = "S-063",
            SupplierName = "测试供应商",
            ContactPerson = "供应商联系人"
        });
        db.BaseCustomers.Add(new BaseCustomer
        {
            CustomerCode = "C-063",
            CustomerName = "测试客户",
            ContactPerson = "客户联系人"
        });
        db.SalesOrders.Add(new SalesOrder
        {
            OrderNo = "SO-063",
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = 501,
            Currency = Currency.USD,
            TotalAmount = 4321.09m,
            DepositAmount = 500m,
            InspectionRequirement = "客户验货 SGS-063",
            ShippingMarks = "MARKS-063",
            Remark = "销售订单备注",
            Status = DocumentStatus.Approved
        });
        db.SaveChanges();

        var salesOrder = db.SalesOrders.AsNoTracking().Single(o => o.OrderNo == "SO-063");
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = salesOrder.Id,
            ProductId = 9101,
            ProductName = "测试商品",
            Quantity = 10m,
            UnitPrice = 432.109m,
            Amount = 4321.09m
        });
        db.ContainerLoadingLists.Add(new ContainerLoadingList
        {
            LoadingListNo = "LL-063",
            LoadingDate = new DateTime(2026, 9, 5),
            ContainerNo = "CONT-063",
            CustomerId = 501,
            ShippingMark = "MARKS-063",
            TotalCartons = 4m,
            TotalWeight = 22m,
            TotalVolume = 1.5m,
            Status = DocumentStatus.Approved,
            Remark = "装柜清单备注"
        });
        db.Stocks.Add(new Stock
        {
            WarehouseId = 1,
            ProductId = 9101,
            Quantity = 100m,
            AvailableQuantity = 100m,
            LockedQuantity = 0m
        });
        db.StockMovements.Add(new StockMovement
        {
            MovementDate = new DateTime(2026, 9, 3),
            MovementType = InventoryMovementType.PurchaseIn,
            SourceDocType = "StockIn",
            SourceDocNo = "RK-063",
            WarehouseId = 1,
            WarehouseName = "主仓"
        });
    }

    /// <summary>
    /// 播种相邻的财务 / 税务 / 单证记录（费用单 / 供应商发票 / 退税台账 / 单证与明细行 / ERP-045 引用册），
    /// 用于非变更断言。
    /// </summary>
    private static void SeedAdjacentFinanceRecords(ErpDbContext db)
    {
        db.FinanceExpenses.Add(new FinanceExpense
        {
            ExpenseNo = "FY-063",
            ExpenseDate = new DateTime(2026, 9, 6),
            ExpenseType = "验货费",
            Amount = 320m,
            Currency = "CNY",
            AmountCny = 320m,
            PaymentStatus = "未付",
            AllocatedAmount = 160m,
            Remark = "费用单备注"
        });
        db.PurchaseInvoices.Add(new PurchaseInvoice
        {
            InvoiceType = "专票",
            InvoiceCode = "033002100211",
            InvoiceNumber = "06300001",
            NormalizedInvoiceCode = "033002100211",
            NormalizedInvoiceNumber = "06300001",
            InvoiceDate = new DateTime(2026, 9, 7),
            SupplierId = 801,
            SupplierName = "测试供应商",
            SupplierCode = "S-063",
            Currency = "CNY",
            NetAmount = 800m,
            TaxAmount = 104m,
            GrossAmount = 904m,
            Status = 1,
            Remark = "供应商发票备注"
        });
        db.BaseTaxRefunds.Add(new BaseTaxRefund
        {
            RefundNo = "TR-063",
            RefundPeriod = "2026-08",
            DeclareDate = new DateTime(2026, 9, 8),
            DeclareNo = "DEC-063",
            InvoiceNo = "INV-063",
            SalesOrderNo = "SO-063",
            RefundableAmount = 1200m,
            Status = "已申报"
        });
        db.TradeDocuments.Add(new TradeDocument
        {
            DocNo = "DOC-063",
            DocType = "装箱单",
            Amount = 1000m,
            Currency = "USD",
            Status = "待制作",
            FileNote = "附件说明 / 存放位置（历史文本：不解析、不抓取、不回填）",
            Remark = "单证备注"
        });
        db.SaveChanges();

        var document = db.TradeDocuments.AsNoTracking().Single(d => d.DocNo == "DOC-063");
        db.TradeDocumentItems.Add(new TradeDocumentItem
        {
            TradeDocumentId = document.Id,
            LineNo = 1,
            ProductId = 9101,
            ProductCode = "P-063",
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
        db.DocumentAttachmentReferences.Add(new DocumentAttachmentReference
        {
            ParentType = DocumentAttachmentReferenceRules.ParentTypeSalesOrder,
            ParentId = 1,
            ParentNo = "SO-063",
            ParentTypeText = "销售订单",
            Category = DocumentAttachmentReferenceRules.CategoryContract,
            DisplayName = "ERP-045 既有引用",
            ReferenceId = "ref-keep-063",
            Status = DocumentAttachmentReferenceRules.StatusActive
        });
        db.SaveChanges();
    }

    /// <summary>相邻业务记录的只读快照（断言验货 / 样品附件证据不改写任何既有记录）</summary>
    private static string Snapshot(ErpDbContext db)
    {
        var purchaseOrders = db.PurchaseOrders.AsNoTracking().OrderBy(o => o.Id)
            .Select(o => new
            {
                o.Id, o.OrderNo, o.Status, o.TotalAmount, o.ArrivalProgress, o.QcStatus,
                o.SettlementProgress, o.ContractNo, o.AdvanceOnBehalf, o.Remark, o.IsDeleted
            }).ToList();
        var purchaseDetails = db.PurchaseOrderDetails.AsNoTracking().OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.PurchaseOrderId, d.ProductId, d.Quantity, d.UnitPrice, d.Amount }).ToList();
        var samples = db.Samples.AsNoTracking().OrderBy(s => s.Id)
            .Select(s => new
            {
                s.Id, s.SampleNo, s.SampleType, s.Quantity, s.SampleFee, s.Currency, s.FeeSettled,
                s.SendDate, s.Express, s.TrackingNo, s.Result, s.Remark, s.IsDeleted
            }).ToList();
        var products = db.BaseProducts.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.ProductCode, p.ProductName, p.Spec, p.Certification }).ToList();
        var suppliers = db.BaseSuppliers.AsNoTracking().OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.SupplierCode, s.SupplierName, s.ContactPerson }).ToList();
        var customers = db.BaseCustomers.AsNoTracking().OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.CustomerCode, c.CustomerName, c.ContactPerson }).ToList();
        var salesOrders = db.SalesOrders.AsNoTracking().OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.OrderNo, o.Status, o.TotalAmount, o.InspectionRequirement, o.Remark }).ToList();
        var salesDetails = db.SalesOrderDetails.AsNoTracking().OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.SalesOrderId, d.ProductId, d.Quantity, d.Amount }).ToList();
        var loadingLists = db.ContainerLoadingLists.AsNoTracking().OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.LoadingListNo, l.ContainerNo, l.TotalCartons, l.Status }).ToList();
        var stocks = db.Stocks.AsNoTracking().OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.ProductId, s.Quantity, s.LockedQuantity }).ToList();
        var movements = db.StockMovements.AsNoTracking().OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.MovementType, m.SourceDocNo, m.WarehouseName }).ToList();
        var expenses = db.FinanceExpenses.AsNoTracking().OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.ExpenseNo, e.ExpenseType, e.Amount, e.AllocatedAmount, e.PaymentStatus }).ToList();
        var invoices = db.PurchaseInvoices.AsNoTracking().OrderBy(i => i.Id)
            .Select(i => new { i.Id, i.InvoiceNumber, i.NetAmount, i.TaxAmount, i.GrossAmount, i.Status }).ToList();
        var refunds = db.BaseTaxRefunds.AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.RefundNo, r.DeclareNo, r.RefundableAmount, r.Status }).ToList();
        var documents = db.TradeDocuments.AsNoTracking().OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.DocNo, d.Status, d.Amount, d.FileNote }).ToList();
        var documentItems = db.TradeDocumentItems.AsNoTracking().OrderBy(i => i.Id)
            .Select(i => new { i.Id, i.TradeDocumentId, i.LineNo, i.ProductId, i.Quantity, i.LineAmount }).ToList();
        var references = db.DocumentAttachmentReferences.AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.ParentType, r.ParentId, r.ReferenceId, r.Status }).ToList();

        return string.Join("|", new[]
        {
            System.Text.Json.JsonSerializer.Serialize(purchaseOrders),
            System.Text.Json.JsonSerializer.Serialize(purchaseDetails),
            System.Text.Json.JsonSerializer.Serialize(samples),
            System.Text.Json.JsonSerializer.Serialize(products),
            System.Text.Json.JsonSerializer.Serialize(suppliers),
            System.Text.Json.JsonSerializer.Serialize(customers),
            System.Text.Json.JsonSerializer.Serialize(salesOrders),
            System.Text.Json.JsonSerializer.Serialize(salesDetails),
            System.Text.Json.JsonSerializer.Serialize(loadingLists),
            System.Text.Json.JsonSerializer.Serialize(stocks),
            System.Text.Json.JsonSerializer.Serialize(movements),
            System.Text.Json.JsonSerializer.Serialize(expenses),
            System.Text.Json.JsonSerializer.Serialize(invoices),
            System.Text.Json.JsonSerializer.Serialize(refunds),
            System.Text.Json.JsonSerializer.Serialize(documents),
            System.Text.Json.JsonSerializer.Serialize(documentItems),
            System.Text.Json.JsonSerializer.Serialize(references)
        });
    }

    private static AttachmentEvidenceUploadRequest UploadRequest(
        byte[] content,
        string ownerType,
        long ownerId,
        string fileName = "验货报告.pdf",
        string declaredContentType = AttachmentEvidenceRules.MediaPdf,
        string description = "")
        => new()
        {
            OwnerType = ownerType,
            OwnerId = ownerId,
            FileName = fileName,
            DeclaredContentType = declaredContentType,
            DeclaredLength = content.LongLength,
            Description = description,
            Content = new MemoryStream(content, writable: false)
        };

    /// <summary>上传成功路径（断言服务端权威字段已写入）</summary>
    private static async Task<AttachmentEvidenceDto> UploadOkAsync(
        ErpDbContext db,
        IAttachmentContentStore store,
        byte[] content,
        string ownerType,
        long ownerId,
        string fileName = "验货报告.pdf",
        string declaredContentType = AttachmentEvidenceRules.MediaPdf,
        string description = "")
    {
        var saved = await AttachmentEvidenceService.UploadAsync(
            db,
            store,
            UploadRequest(content, ownerType, ownerId, fileName, declaredContentType, description),
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

    // ==================== 1. 上传：同类模型的服务端权威元数据与边界 ====================

    [Fact]
    public async Task 上传到验货记录与样品记录_元数据与归属快照由服务端生成且边界不推断结论()
    {
        using var db = TestDbFactory.Create();
        var order = SeedPurchaseOrder(db, "PO-063-A", qcStatus: "验货中", arrivalProgress: "部分到货");
        var sample = SeedSample(db, "SP-063-A", sampleType: "寄样", result: "待反馈");
        var store = new InMemoryAttachmentContentStore();

        var inspection = await UploadOkAsync(
            db, store, PdfBytes("qc"), AttachmentEvidenceRules.OwnerTypeQualityInspection, order.Id,
            description: "第三方验货报告（用户上传）");
        Assert.Equal(AttachmentEvidenceRules.OwnerTypeQualityInspection, inspection.OwnerType);
        Assert.Equal("验货记录", inspection.OwnerTypeText);
        Assert.Equal(order.Id, inspection.OwnerId);
        Assert.Equal("PO-063-A", inspection.OwnerNo);
        Assert.Equal("验货记录 PO-063-A", inspection.OwnerSnapshotText);
        Assert.Equal("验货报告.pdf", inspection.OriginalFileName);
        Assert.Equal(AttachmentEvidenceRules.MediaPdf, inspection.MediaType);
        Assert.Equal("测试用户", inspection.UploadedBy);
        Assert.True(inspection.ContentDownloadable);
        Assert.Equal("/api/attachment-evidences/" + inspection.Id + "/content", inspection.DownloadPath);
        Assert.Contains("不是验货合格 / 不合格判定", inspection.BoundaryText);
        Assert.Contains("不代表供应商绩效结论", inspection.BoundaryText);

        var sampleEvidence = await UploadOkAsync(
            db, store, PngBytes(), AttachmentEvidenceRules.OwnerTypeSample, sample.Id,
            fileName: "样品照片.png", declaredContentType: AttachmentEvidenceRules.MediaPng,
            description: "客户来样照片");
        Assert.Equal(AttachmentEvidenceRules.OwnerTypeSample, sampleEvidence.OwnerType);
        Assert.Equal("样品记录", sampleEvidence.OwnerTypeText);
        Assert.Equal("样品记录 SP-063-A", sampleEvidence.OwnerSnapshotText);
        Assert.Equal(AttachmentEvidenceRules.MediaPng, sampleEvidence.MediaType);
        Assert.Contains("不代表样品已获批准", sampleEvidence.BoundaryText);
        Assert.Contains("不代表样品费已结算", sampleEvidence.BoundaryText);

        // 存储键由服务端生成（不透明、不含原始文件名），且存储提供程序是隔离的非生产本地存储
        var row = await db.AttachmentEvidences.AsNoTracking().SingleAsync(r => r.Id == inspection.Id);
        Assert.DoesNotContain("验货报告", row.StorageKey, StringComparison.Ordinal);
        Assert.Equal(AttachmentEvidenceRules.ProviderIsolatedLocal, row.StorageProvider);
        Assert.Equal(2, await db.AttachmentEvidences.AsNoTracking().CountAsync());

        // 两类归属各自独立：验货记录清单里看不到样品证据（无隐式共享）
        var forInspection = await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeQualityInspection, order.Id);
        Assert.Single(forInspection);
        Assert.Equal(inspection.Id, forInspection[0].Id);

        // 上传不改写权威记录：验货状态 / 到货进度 / 客户反馈 / 样品费结算都不变
        var persistedOrder = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal("验货中", persistedOrder.QcStatus);
        Assert.Equal("部分到货", persistedOrder.ArrivalProgress);
        Assert.Equal(DocumentStatus.Approved, persistedOrder.Status);
        var persistedSample = await db.Samples.AsNoTracking().SingleAsync(s => s.Id == sample.Id);
        Assert.Equal("待反馈", persistedSample.Result);
        Assert.False(persistedSample.FeeSettled);
        Assert.Equal(120m, persistedSample.SampleFee);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("samples")]
    [InlineData("quality-inspection")]
    [InlineData("QualityInspections")]
    [InlineData("ContainerLoadingList")]
    [InlineData("ContainerBooking")]
    public async Task 未接入的归属类型仍一律拒绝且不落库不落盘(string ownerType)
    {
        using var db = TestDbFactory.Create();
        var order = SeedPurchaseOrder(db, "PO-063-B");
        var store = new InMemoryAttachmentContentStore();

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.UploadAsync(
            db, store, UploadRequest(PdfBytes(), ownerType, order.Id), "测试用户", 7));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, ownerType, null));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.SummarizeOwnersAsync(
            db, ownerType, new[] { order.Id }));
        // 台账的归属类型筛选：留空 = 「全部类型」（既有 ERP-061 口径），非空的不支持类型一律拒绝
        if (!string.IsNullOrWhiteSpace(ownerType))
        {
            await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListAsync(
                db, new AttachmentEvidenceQuery { OwnerType = ownerType, PageSize = 1 }));
        }

        Assert.Equal(0, await db.AttachmentEvidences.CountAsync());
        Assert.Equal(0, store.SaveCalls);
    }

    // ==================== 2. 归属复核：不存在 / 已删除 / Id 非法 / 软删除后历史 ====================

    [Fact]
    public async Task 归属不存在或已删除或Id非法时上传与清单一律拒绝()
    {
        using var db = TestDbFactory.Create();
        var deletedOrder = SeedPurchaseOrder(db, "PO-063-C", deleted: true);
        var deletedSample = SeedSample(db, "SP-063-C", deleted: true);
        var store = new InMemoryAttachmentContentStore();

        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.UploadAsync(db, store,
            UploadRequest(PdfBytes(), AttachmentEvidenceRules.OwnerTypeQualityInspection, 999_999), "测试用户", 7));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.UploadAsync(db, store,
            UploadRequest(PdfBytes(), AttachmentEvidenceRules.OwnerTypeQualityInspection, deletedOrder.Id), "测试用户", 7));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.UploadAsync(db, store,
            UploadRequest(PdfBytes(), AttachmentEvidenceRules.OwnerTypeSample, 999_999), "测试用户", 7));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.UploadAsync(db, store,
            UploadRequest(PdfBytes(), AttachmentEvidenceRules.OwnerTypeSample, deletedSample.Id), "测试用户", 7));

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.UploadAsync(db, store,
            UploadRequest(PdfBytes(), AttachmentEvidenceRules.OwnerTypeQualityInspection, 0), "测试用户", 7));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.UploadAsync(db, store,
            UploadRequest(PdfBytes(), AttachmentEvidenceRules.OwnerTypeSample, -3), "测试用户", 7));

        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeQualityInspection, deletedOrder.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeSample, 999_999));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeSample, 0));

        Assert.Equal(0, await db.AttachmentEvidences.CountAsync());
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task 归属软删除后历史证据仍可只读查看_不可下载且绝不改派()
    {
        using var db = TestDbFactory.Create();
        var order = SeedPurchaseOrder(db, "PO-063-D");
        var sample = SeedSample(db, "SP-063-D");
        var store = new InMemoryAttachmentContentStore();
        var inspection = await UploadOkAsync(
            db, store, PdfBytes("d"), AttachmentEvidenceRules.OwnerTypeQualityInspection, order.Id);
        var sampleEvidence = await UploadOkAsync(
            db, store, PngBytes(), AttachmentEvidenceRules.OwnerTypeSample, sample.Id,
            fileName: "样品照片.png", declaredContentType: AttachmentEvidenceRules.MediaPng);

        // 软删除权威记录（只改台账标记，不改证据行）
        var persistedOrder = await db.PurchaseOrders.FirstAsync(o => o.Id == order.Id);
        persistedOrder.IsDeleted = true;
        var persistedSample = await db.Samples.FirstAsync(s => s.Id == sample.Id);
        persistedSample.IsDeleted = true;
        await db.SaveChangesAsync();

        // 历史证据照常可读，但显式标注不可用 / 不可下载；号码快照照实显示，绝不改派
        var detail = await AttachmentEvidenceService.GetAsync(db, inspection.Id);
        Assert.False(detail.OwnerAvailable);
        Assert.Contains("已不存在或已删除", detail.OwnerAvailabilityText);
        Assert.False(detail.ContentDownloadable);
        Assert.Contains("不提供下载", detail.DownloadAvailabilityText);
        Assert.Equal("PO-063-D", detail.OwnerNo);
        Assert.Equal(AttachmentEvidenceRules.OwnerTypeQualityInspection, detail.OwnerType);

        var page = await AttachmentEvidenceService.ListAsync(
            db, new AttachmentEvidenceQuery
            {
                OwnerType = AttachmentEvidenceRules.OwnerTypeQualityInspection,
                PageSize = 50
            });
        Assert.Single(page.Items);
        Assert.False(page.Items[0].OwnerAvailable);

        var summary = (await AttachmentEvidenceService.SummarizeOwnersAsync(
            db, AttachmentEvidenceRules.OwnerTypeQualityInspection, new[] { order.Id })).Single();
        Assert.False(summary.OwnerAvailable);
        Assert.Equal(1, summary.TotalCount);

        // 已软删除的归属不能再上挂新证据；历史内容也不再可下载（且连存储都不打开）
        await AssertBusinessAsync(ErrorCodes.NotFound, () => AttachmentEvidenceService.UploadAsync(db, store,
            UploadRequest(PdfBytes(), AttachmentEvidenceRules.OwnerTypeSample, sample.Id), "测试用户", 7));
        var openCallsBefore = store.OpenCalls;
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () =>
            AttachmentEvidenceService.OpenContentAsync(db, store, sampleEvidence.Id));
        Assert.Equal(openCallsBefore, store.OpenCalls);

        var rows = await db.AttachmentEvidences.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(order.Id, rows.Single(r => r.Id == inspection.Id).OwnerId);
        Assert.Equal(sample.Id, rows.Single(r => r.Id == sampleEvidence.Id).OwnerId);
    }

    // ==================== 3. 作废：原因必填、保留历史、无硬删除 / 替换 / 改派 ====================

    [Fact]
    public async Task 作废保留原始元数据与历史_重复作废拒绝_不硬删除不替换二进制()
    {
        using var db = TestDbFactory.Create();
        var order = SeedPurchaseOrder(db, "PO-063-E", qcStatus: "合格");
        var sample = SeedSample(db, "SP-063-E", sampleType: "客户来样", result: "需修改");
        var store = new InMemoryAttachmentContentStore();
        var inspection = await UploadOkAsync(
            db, store, PdfBytes("e"), AttachmentEvidenceRules.OwnerTypeQualityInspection, order.Id,
            description: "验货报告初版");
        var sampleEvidence = await UploadOkAsync(
            db, store, PngBytes(), AttachmentEvidenceRules.OwnerTypeSample, sample.Id,
            fileName: "样品照片.png", declaredContentType: AttachmentEvidenceRules.MediaPng);

        // 作废原因必填（空 / 空白一律拒绝）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            AttachmentEvidenceService.VoidAsync(db, sampleEvidence.Id, "   "));
        Assert.Equal(AttachmentEvidenceRules.StatusActive,
            (await db.AttachmentEvidences.AsNoTracking().SingleAsync(r => r.Id == sampleEvidence.Id)).Status);

        var voided = await AttachmentEvidenceService.VoidAsync(db, sampleEvidence.Id, "照片模糊，重新上传");
        Assert.True(voided.IsVoided);
        Assert.Equal("照片模糊，重新上传", voided.VoidReason);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal(sampleEvidence.Sha256, voided.Sha256);
        Assert.Equal(sampleEvidence.OriginalFileName, voided.OriginalFileName);
        Assert.Equal(sampleEvidence.MediaType, voided.MediaType);
        Assert.Equal(sampleEvidence.SizeBytes, voided.SizeBytes);
        Assert.Equal("测试用户", voided.UploadedBy);
        Assert.False(voided.ContentDownloadable);
        Assert.Contains("已作废", voided.DownloadAvailabilityText);

        // 重复作废拒绝
        await AssertBusinessAsync(ErrorCodes.Duplicate, () =>
            AttachmentEvidenceService.VoidAsync(db, sampleEvidence.Id, "再次作废"));

        // 有效清单排除已作废；显式历史清单仍可查（原始文件名与摘要保留）
        Assert.Empty(await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeSample, sample.Id, AttachmentEvidenceRules.StatusActive));
        var history = await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeSample, sample.Id, AttachmentEvidenceRules.StatusVoided);
        Assert.Single(history);
        Assert.Equal("样品照片.png", history[0].OriginalFileName);
        Assert.Equal(sampleEvidence.Sha256, history[0].Sha256);

        // 已作废证据不提供下载（也不打开存储）
        var openCallsBefore = store.OpenCalls;
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () =>
            AttachmentEvidenceService.OpenContentAsync(db, store, sampleEvidence.Id));
        Assert.Equal(openCallsBefore, store.OpenCalls);

        // 不硬删除、不二进制替换、不改派：证据行仍在、内容仍在、归属与摘要不变；另一类归属不受影响
        var persisted = await db.AttachmentEvidences.AsNoTracking().SingleAsync(r => r.Id == sampleEvidence.Id);
        Assert.Equal(AttachmentEvidenceRules.StatusVoided, persisted.Status);
        Assert.Equal(sampleEvidence.Sha256, persisted.Sha256);
        Assert.Equal(AttachmentEvidenceRules.OwnerTypeSample, persisted.OwnerType);
        Assert.Equal(sample.Id, persisted.OwnerId);
        Assert.Equal(2, await db.AttachmentEvidences.AsNoTracking().CountAsync());
        Assert.Equal(2, store.Items.Count);

        // 另一类归属的证据不受影响（仍有效、仍可下载）
        var other = await AttachmentEvidenceService.GetAsync(db, inspection.Id);
        Assert.Equal(AttachmentEvidenceRules.OwnerTypeQualityInspection, other.OwnerType);
        Assert.True(other.IsActive);
        Assert.True(other.ContentDownloadable);
        Assert.Equal(string.Empty, other.VoidReason);

        // 作废不改写权威记录：验货状态与客户反馈保持原样
        Assert.Equal("合格", (await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).QcStatus);
        Assert.Equal("需修改", (await db.Samples.AsNoTracking().SingleAsync(s => s.Id == sample.Id)).Result);
    }

    // ==================== 4. 台账与归属候选（只读原文，不推断结论） ====================

    [Fact]
    public async Task 台账与归属候选_验货状态与样品反馈只读原文且筛选按类型与归属收敛()
    {
        using var db = TestDbFactory.Create();
        var orderA = SeedPurchaseOrder(
            db, "PO-063-F1", qcStatus: "未验货", arrivalProgress: "未到货", status: DocumentStatus.Pending);
        var orderB = SeedPurchaseOrder(db, "PO-063-F2", qcStatus: "", arrivalProgress: "");
        var sampleA = SeedSample(db, "SP-063-F1", sampleType: "打样", result: "");
        var store = new InMemoryAttachmentContentStore();

        await UploadOkAsync(
            db, store, PdfBytes("f1"), AttachmentEvidenceRules.OwnerTypeQualityInspection, orderA.Id);
        await UploadOkAsync(
            db, store, PdfBytes("f2"), AttachmentEvidenceRules.OwnerTypeQualityInspection, orderB.Id);
        var sampleEvidence = await UploadOkAsync(
            db, store, PngBytes(), AttachmentEvidenceRules.OwnerTypeSample, sampleA.Id,
            fileName: "打样照片.png", declaredContentType: AttachmentEvidenceRules.MediaPng);

        // 归属候选（验货记录）：权威记录 = 既有采购订单 QC 记录，验货状态取**原文**（空值照实标注，不推断）
        var inspectionOptions = await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypeQualityInspection, "PO-063-F");
        Assert.Equal(2, inspectionOptions.Count);
        var optionA = inspectionOptions.Single(o => o.OwnerId == orderA.Id);
        Assert.Equal(AttachmentEvidenceRules.OwnerTypeQualityInspection, optionA.OwnerType);
        Assert.Equal("验货记录", optionA.OwnerTypeText);
        Assert.Equal("PO-063-F1", optionA.OwnerNo);
        Assert.Equal("未验货", optionA.StatusText);
        Assert.Contains("验货状态 未验货", optionA.SummaryText);
        Assert.Contains("到货进度 未到货", optionA.SummaryText);
        Assert.Equal(1, optionA.EvidenceCount);
        Assert.True(optionA.Selectable);
        Assert.Contains("已有附件证据 1 条", optionA.SelectableText);

        var optionB = inspectionOptions.Single(o => o.OwnerId == orderB.Id);
        Assert.Equal("未登记验货状态", optionB.StatusText);
        Assert.Contains("未登记到货进度", optionB.SummaryText);
        Assert.Equal(1, optionB.EvidenceCount);

        // 尚无证据的采购订单：计数为 0 只表示「暂无证据」，绝不是「验货合格」
        var orderC = SeedPurchaseOrder(db, "PO-063-F3", qcStatus: "未验货");
        var optionC = (await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypeQualityInspection, "PO-063-F3"))
            .Single(o => o.OwnerId == orderC.Id);
        Assert.Equal(0, optionC.EvidenceCount);
        Assert.DoesNotContain("合格", optionC.SummaryText);
        Assert.DoesNotContain("通过", optionC.SummaryText);

        // 归属候选（样品记录）：客户反馈取原文，空值照实标注
        var sampleOption = (await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypeSample, "SP-063-F"))
            .Single(o => o.OwnerId == sampleA.Id);
        Assert.Equal("样品记录", sampleOption.OwnerTypeText);
        Assert.Equal("SP-063-F1", sampleOption.OwnerNo);
        Assert.Equal("未登记客户反馈", sampleOption.StatusText);
        Assert.Contains("样品类型 打样", sampleOption.SummaryText);
        Assert.Contains("客户反馈 未登记客户反馈", sampleOption.SummaryText);

        // 台账按归属类型 / 归属 Id / 关键字收敛（不跨类型合并）
        var inspectionPage = await AttachmentEvidenceService.ListAsync(
            db, new AttachmentEvidenceQuery
            {
                OwnerType = AttachmentEvidenceRules.OwnerTypeQualityInspection,
                PageSize = 50
            });
        Assert.Equal(2, inspectionPage.Total);
        Assert.All(inspectionPage.Items, i => Assert.Equal("验货记录", i.OwnerTypeText));

        var byOwner = await AttachmentEvidenceService.ListAsync(
            db, new AttachmentEvidenceQuery
            {
                OwnerType = AttachmentEvidenceRules.OwnerTypeQualityInspection,
                OwnerId = orderA.Id,
                PageSize = 50
            });
        Assert.Single(byOwner.Items);
        Assert.Equal(orderA.Id, byOwner.Items[0].OwnerId);

        var byKeyword = await AttachmentEvidenceService.ListAsync(
            db, new AttachmentEvidenceQuery { Keyword = "SP-063-F1", PageSize = 50 });
        Assert.Single(byKeyword.Items);
        Assert.Equal(sampleEvidence.Id, byKeyword.Items[0].Id);
        Assert.Equal("样品记录", byKeyword.Items[0].OwnerTypeText);

        // 候选与台账都不读取存储内容
        Assert.Equal(3, store.SaveCalls);
        Assert.Equal(0, store.OpenCalls);
        Assert.Equal(0, store.LengthCalls);
    }

    // ==================== 5. 非变更：不改写权威记录与相邻业务记录 ====================

    [Fact]
    public async Task 验货与样品附件证据不改写权威记录与相邻业务记录()
    {
        using var db = TestDbFactory.Create();
        SeedAdjacentRecords(db);
        SeedAdjacentFinanceRecords(db);
        var order = SeedPurchaseOrder(db, "PO-063-G", qcStatus: "验货中");
        var sample = SeedSample(db, "SP-063-G");
        var store = new InMemoryAttachmentContentStore();
        var before = Snapshot(db);

        var inspection = await UploadOkAsync(
            db, store, PdfBytes("g"), AttachmentEvidenceRules.OwnerTypeQualityInspection, order.Id,
            description: "验货报告（用户上传）");
        var sampleEvidence = await UploadOkAsync(
            db, store, PngBytes(), AttachmentEvidenceRules.OwnerTypeSample, sample.Id,
            fileName: "样品照片.png", declaredContentType: AttachmentEvidenceRules.MediaPng);

        await AttachmentEvidenceService.ListAsync(db, new AttachmentEvidenceQuery { PageSize = 50 });
        await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypeQualityInspection, null);
        await AttachmentEvidenceService.ListOwnerOptionsAsync(db, AttachmentEvidenceRules.OwnerTypeSample, null);
        await AttachmentEvidenceService.SummarizeOwnersAsync(
            db, AttachmentEvidenceRules.OwnerTypeQualityInspection, new[] { order.Id });
        await AttachmentEvidenceService.SummarizeOwnersAsync(
            db, AttachmentEvidenceRules.OwnerTypeSample, new[] { sample.Id });
        _ = await BuildController(db, store).DownloadContent(inspection.Id);
        await AttachmentEvidenceService.VoidAsync(db, sampleEvidence.Id, "重复上传，作废本条");

        Assert.Equal(before, Snapshot(db));
        Assert.Equal("验货中", (await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).QcStatus);
        Assert.Equal(20m, (await db.PurchaseOrderDetails.AsNoTracking()
            .SingleAsync(d => d.PurchaseOrderId == order.Id)).Quantity);
        Assert.Equal("待反馈", (await db.Samples.AsNoTracking().SingleAsync(s => s.Id == sample.Id)).Result);
        Assert.Equal("附件说明 / 存放位置（历史文本：不解析、不抓取、不回填）",
            (await db.TradeDocuments.AsNoTracking().SingleAsync()).FileNote);
        Assert.Equal(
            "客户验货 SGS-063",
            (await db.SalesOrders.AsNoTracking().SingleAsync(o => o.OrderNo == "SO-063")).InspectionRequirement);
    }

    // ==================== 6. 列表页有界摘要（计数 + 归属可用性；零存储访问） ====================

    [Fact]
    public async Task 列表摘要_有界计数与归属可用性_固定数据集访问且零存储访问()
    {
        using var db = TestDbFactory.Create();
        var aliveOrder = SeedPurchaseOrder(db, "PO-063-H1");
        var deletedOrder = SeedPurchaseOrder(db, "PO-063-H2");
        var aliveSample = SeedSample(db, "SP-063-H1");
        var deletedSample = SeedSample(db, "SP-063-H2");
        const long missingId = 999_999L;
        var store = new InMemoryAttachmentContentStore();

        await UploadOkAsync(
            db, store, PdfBytes("h1"), AttachmentEvidenceRules.OwnerTypeQualityInspection, aliveOrder.Id);
        var wrong = await UploadOkAsync(
            db, store, PdfBytes("h2"), AttachmentEvidenceRules.OwnerTypeQualityInspection, aliveOrder.Id,
            fileName: "第二份.pdf");
        await AttachmentEvidenceService.VoidAsync(db, wrong.Id, "重复上传，作废本条");
        await UploadOkAsync(
            db, store, PngBytes(), AttachmentEvidenceRules.OwnerTypeQualityInspection, deletedOrder.Id,
            fileName: "历史照片.png", declaredContentType: AttachmentEvidenceRules.MediaPng);
        await UploadOkAsync(
            db, store, PngBytes(), AttachmentEvidenceRules.OwnerTypeSample, aliveSample.Id,
            fileName: "样品照片.png", declaredContentType: AttachmentEvidenceRules.MediaPng);
        await UploadOkAsync(
            db, store, PdfBytes("h3"), AttachmentEvidenceRules.OwnerTypeSample, deletedSample.Id);

        // 上传完成后才软删除这两条权威记录（只改台账标记，不改证据行）
        var toDeleteOrder = await db.PurchaseOrders.FirstAsync(o => o.Id == deletedOrder.Id);
        toDeleteOrder.IsDeleted = true;
        var toDeleteSample = await db.Samples.FirstAsync(s => s.Id == deletedSample.Id);
        toDeleteSample.IsDeleted = true;
        await db.SaveChangesAsync();

        var storeCallsBefore = store.TotalCalls;
        var counting = AttachmentEvidenceTests.CountingDbContext.Wrap(db);
        var summaries = await AttachmentEvidenceService.SummarizeOwnersAsync(
            counting.Proxy, AttachmentEvidenceRules.OwnerTypeQualityInspection,
            new[] { aliveOrder.Id, deletedOrder.Id, missingId });

        Assert.Equal(3, summaries.Count);
        Assert.True(summaries.Select(s => s.OwnerId)
            .SequenceEqual(summaries.Select(s => s.OwnerId).OrderBy(id => id)));   // 升序、无合并

        var aliveSummary = summaries.Single(s => s.OwnerId == aliveOrder.Id);
        Assert.Equal(AttachmentEvidenceRules.OwnerTypeQualityInspection, aliveSummary.OwnerType);
        Assert.Equal("验货记录", aliveSummary.OwnerTypeText);
        Assert.Equal("PO-063-H1", aliveSummary.OwnerNo);
        Assert.Equal("验货记录 PO-063-H1", aliveSummary.OwnerSnapshotText);
        Assert.True(aliveSummary.OwnerAvailable);
        Assert.Equal(2, aliveSummary.TotalCount);
        Assert.Equal(1, aliveSummary.ActiveCount);
        Assert.Equal(1, aliveSummary.VoidedCount);
        Assert.True(aliveSummary.HasActiveEvidence);
        Assert.Contains("仓库附件证据 2 条", aliveSummary.SummaryText);
        Assert.Contains("不代表验货合格 / 不合格判定", aliveSummary.SummaryText);
        Assert.Contains("不代表供应商绩效结论", aliveSummary.BoundaryText);

        var deletedSummary = summaries.Single(s => s.OwnerId == deletedOrder.Id);
        Assert.False(deletedSummary.OwnerAvailable);
        Assert.Equal(1, deletedSummary.TotalCount);
        Assert.Equal("PO-063-H2", deletedSummary.OwnerNo);      // 号码照实显示，绝不改派

        var missingSummary = summaries.Single(s => s.OwnerId == missingId);
        Assert.False(missingSummary.OwnerAvailable);
        Assert.Equal(0, missingSummary.TotalCount);
        Assert.False(missingSummary.HasActiveEvidence);
        Assert.Contains("暂无仓库附件证据", missingSummary.SummaryText);

        // 常数级数据集访问：证据表 + 采购订单表各一次（无逐行查库）、只读不写库、零存储访问
        Assert.Equal(2, counting.DatasetReads);
        Assert.Equal(
            new[] { nameof(IErpDbContext.AttachmentEvidences), nameof(IErpDbContext.PurchaseOrders) },
            counting.ReadProperties.Distinct().ToArray());
        Assert.Equal(0, counting.WriteCalls);
        Assert.Equal(storeCallsBefore, store.TotalCalls);

        // 样品记录：证据表 + 样品表各一次，同样零存储访问
        var sampleCounting = AttachmentEvidenceTests.CountingDbContext.Wrap(db);
        var sampleSummaries = await AttachmentEvidenceService.SummarizeOwnersAsync(
            sampleCounting.Proxy, AttachmentEvidenceRules.OwnerTypeSample,
            new[] { aliveSample.Id, deletedSample.Id });
        Assert.Equal(2, sampleSummaries.Count);
        Assert.Contains("不代表样品已获批准",
            sampleSummaries.Single(s => s.OwnerId == aliveSample.Id).BoundaryText);
        Assert.Equal(2, sampleCounting.DatasetReads);
        Assert.Equal(
            new[] { nameof(IErpDbContext.AttachmentEvidences), nameof(IErpDbContext.Samples) },
            sampleCounting.ReadProperties.Distinct().ToArray());
        Assert.Equal(0, sampleCounting.WriteCalls);
        Assert.Equal(storeCallsBefore, store.TotalCalls);

        // 空 Id 集合：直接返回空摘要，不查询任何数据集
        var emptyCounting = AttachmentEvidenceTests.CountingDbContext.Wrap(db);
        Assert.Empty(await AttachmentEvidenceService.SummarizeOwnersAsync(
            emptyCounting.Proxy, AttachmentEvidenceRules.OwnerTypeSample, Array.Empty<long>()));
        Assert.Equal(0, emptyCounting.DatasetReads);

        // 非法 Id 一律拒绝（不静默丢弃）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.SummarizeOwnersAsync(
            emptyCounting.Proxy, AttachmentEvidenceRules.OwnerTypeQualityInspection, new[] { 1L, 0L }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.SummarizeOwnersAsync(
            emptyCounting.Proxy, AttachmentEvidenceRules.OwnerTypeSample, new[] { -3L }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.SummarizeOwnersAsync(
            emptyCounting.Proxy, AttachmentEvidenceRules.OwnerTypeSample,
            Enumerable.Range(1, AttachmentEvidenceRules.MaxSummaryOwnerIds + 1).Select(i => (long)i)));
        Assert.Equal(0, emptyCounting.DatasetReads);

        // Id 数量显著增加（跨多页规模）也不改变访问次数
        for (var i = 0; i < 120; i++) SeedSample(db, $"SP-063-BULK-{i:d3}");
        var allSampleIds = await db.Samples.AsNoTracking().Select(s => s.Id).ToListAsync();
        var bulkCounting = AttachmentEvidenceTests.CountingDbContext.Wrap(db);
        var bulk = await AttachmentEvidenceService.SummarizeOwnersAsync(
            bulkCounting.Proxy, AttachmentEvidenceRules.OwnerTypeSample, allSampleIds);
        Assert.Equal(allSampleIds.Count, bulk.Count);
        Assert.Equal(2, bulkCounting.DatasetReads);
        Assert.Equal(0, bulkCounting.WriteCalls);
        Assert.Equal(storeCallsBefore, store.TotalCalls);
    }

    // ==================== 7. 不同归属类型 / 记录绝不合并或改派 ====================

    [Fact]
    public async Task 同名同摘要的验货与样品证据不会合并_且不隐式共享采购订单证据()
    {
        using var db = TestDbFactory.Create();
        var order = SeedPurchaseOrder(db, "PO-063-K");
        var sample = SeedSample(db, "SP-063-K");
        var store = new InMemoryAttachmentContentStore();
        var content = PdfBytes("same-digest-063");

        // 同一份内容分别挂到验货记录与样品记录（各自独立、绝不按摘要去重或合并）
        var inspection = await UploadOkAsync(
            db, store, content, AttachmentEvidenceRules.OwnerTypeQualityInspection, order.Id,
            fileName: "报告.pdf", description: "验货记录上的报告");
        var sampleEvidence = await UploadOkAsync(
            db, store, content, AttachmentEvidenceRules.OwnerTypeSample, sample.Id,
            fileName: "报告.pdf", description: "样品记录上的报告");
        // 同一采购订单上的「采购订单证据」是另一类独立归属（不与该订单的验货记录证据隐式共享）
        var orderEvidence = await UploadOkAsync(
            db, store, content, AttachmentEvidenceRules.OwnerTypePurchaseOrder, order.Id,
            fileName: "报告.pdf", description: "采购订单上的报告");

        Assert.Equal(inspection.Sha256, sampleEvidence.Sha256);
        Assert.NotEqual(inspection.Id, sampleEvidence.Id);
        Assert.Equal(3, await db.AttachmentEvidences.AsNoTracking().CountAsync());
        Assert.Equal(3, store.Items.Count);            // 三条独立内容，绝不静默合并

        var forInspection = await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeQualityInspection, order.Id);
        Assert.Single(forInspection);
        Assert.Equal(inspection.Id, forInspection[0].Id);

        var forOrder = await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypePurchaseOrder, order.Id);
        Assert.Single(forOrder);
        Assert.Equal(orderEvidence.Id, forOrder[0].Id);
        Assert.Contains("采购订单", forOrder[0].OwnerTypeText);
        Assert.DoesNotContain("验货", forOrder[0].OwnerTypeText);

        var forSample = await AttachmentEvidenceService.ListForOwnerAsync(
            db, AttachmentEvidenceRules.OwnerTypeSample, sample.Id);
        Assert.Single(forSample);
        Assert.Equal(sampleEvidence.Id, forSample[0].Id);

        // 归属类型不会被猜测改写：各行的归属类型与 Id 保持登记当时的口径
        var rows = await db.AttachmentEvidences.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(
            new[]
            {
                AttachmentEvidenceRules.OwnerTypeQualityInspection,
                AttachmentEvidenceRules.OwnerTypeSample,
                AttachmentEvidenceRules.OwnerTypePurchaseOrder
            },
            rows.Select(r => r.OwnerType).ToArray());
        Assert.Equal("PO-063-K", rows[0].OwnerNo);
        Assert.Equal("SP-063-K", rows[1].OwnerNo);
        Assert.Equal("PO-063-K", rows[2].OwnerNo);
    }

    // ==================== 8. 下载：每次重新校验权威记录 + 防御性响应头 ====================

    [Fact]
    public async Task 下载每次重新校验归属存在性_以附件方式返回且防御性响应头不变()
    {
        using var db = TestDbFactory.Create();
        var sample = SeedSample(db, "SP-063-J");
        var store = new InMemoryAttachmentContentStore();
        var saved = await UploadOkAsync(
            db, store, PngBytes(), AttachmentEvidenceRules.OwnerTypeSample, sample.Id,
            fileName: "样品照片.png", declaredContentType: AttachmentEvidenceRules.MediaPng);

        var controller = BuildController(db, store);
        var file = Assert.IsType<FileStreamResult>(await controller.DownloadContent(saved.Id));
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal("样品照片.png", file.FileDownloadName);
        Assert.False(file.EnableRangeProcessing);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("default-src 'none'; sandbox", controller.Response.Headers["Content-Security-Policy"].ToString());
        Assert.Equal("no-store, no-cache, must-revalidate", controller.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("noopen", controller.Response.Headers["X-Download-Options"].ToString());
        Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"].ToString());

        // 权威样品记录被软删除后：同一段内容不再可下载（每次请求都重新校验）
        var persisted = await db.Samples.FirstAsync(s => s.Id == sample.Id);
        persisted.IsDeleted = true;
        await db.SaveChangesAsync();

        var openCallsBefore = store.OpenCalls;
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.DownloadContent(saved.Id));
        Assert.Equal(openCallsBefore, store.OpenCalls);
    }

    // ==================== 9. 控制器契约（同一路由 / 同一鉴权口径） ====================

    [Fact]
    public void 控制器契约_类级授权且不提供改写二进制或删除端点()
    {
        var type = typeof(AttachmentEvidenceController);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Null(type.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("api/attachment-evidences", type.GetCustomAttribute<RouteAttribute>()!.Template);

        // 读端点：台账 / 模块元数据 / 归属候选 / 有界摘要 / 证据详情 / 内容下载；写端点只有上传与作废
        Assert.NotNull(type.GetMethod(nameof(AttachmentEvidenceController.GetPaged))!
            .GetCustomAttribute<HttpGetAttribute>());
        Assert.NotNull(type.GetMethod(nameof(AttachmentEvidenceController.Metadata))!
            .GetCustomAttribute<HttpGetAttribute>());
        Assert.NotNull(type.GetMethod(nameof(AttachmentEvidenceController.OwnerOptions))!
            .GetCustomAttribute<HttpGetAttribute>());
        Assert.NotNull(type.GetMethod(nameof(AttachmentEvidenceController.OwnerSummary))!
            .GetCustomAttribute<HttpGetAttribute>());
        Assert.NotNull(type.GetMethod(nameof(AttachmentEvidenceController.DownloadContent))!
            .GetCustomAttribute<HttpGetAttribute>());
        Assert.NotNull(type.GetMethod(nameof(AttachmentEvidenceController.Upload))!
            .GetCustomAttribute<HttpPostAttribute>());

        // 摘要端点与内容下载端点的路由模板（同一控制器 = 同一鉴权口径）
        Assert.Equal("owner-summary", type.GetMethod(nameof(AttachmentEvidenceController.OwnerSummary))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Contains("content", type.GetMethod(nameof(AttachmentEvidenceController.DownloadContent))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template);

        // 上传请求大小限制与规则同源
        var limit = type.GetMethod(nameof(AttachmentEvidenceController.Upload))!
            .GetCustomAttribute<RequestSizeLimitAttribute>();
        Assert.NotNull(limit);
        Assert.Equal(
            (long?)AttachmentEvidenceRules.MaxRequestBytes,
            ((IRequestSizeLimitMetadata)limit!).MaxRequestBodySize);

        // 不提供改写二进制 / 硬删除 / 改派端点
        var declared = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(declared, m => m.GetCustomAttribute<HttpPutAttribute>() is not null);
        Assert.DoesNotContain(declared, m => m.GetCustomAttribute<HttpPatchAttribute>() is not null);
        Assert.DoesNotContain(declared, m => m.GetCustomAttribute<HttpDeleteAttribute>() is not null);
        Assert.DoesNotContain(declared, m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null);

        // DTO 层同样不暴露存储键 / 文件系统路径
        var dtoProperties = typeof(AttachmentEvidenceDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("StorageKey", dtoProperties);
        var summaryProperties = typeof(AttachmentEvidenceOwnerSummaryDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("StorageKey", summaryProperties);
        Assert.DoesNotContain("StorageProvider", summaryProperties);
        Assert.DoesNotContain("DownloadPath", summaryProperties);

        // 序列化结果里同样没有存储键 / 根目录
        var json = System.Text.Json.JsonSerializer.Serialize(
            new AttachmentEvidenceOwnerSummaryDto(
                AttachmentEvidenceRules.OwnerTypeQualityInspection, "验货记录", 1, "PO-063",
                "验货记录 PO-063", true, 1, 1, 0, true, "摘要", "边界"));
        Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rootPath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 前端接线契约_样品与验货附件证据入口_且界面不接触存储键()
    {
        var js = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "attachment-evidences.js"));
        Assert.Contains("case 'sample': return 'Sample';", js);
        Assert.Contains("async function openQualityInspectionEvidencesForCurrentModule(ownerId)", js);
        Assert.Contains("await openAttachmentEvidences('QualityInspection', ownerId);", js);
        Assert.Contains("function aeLegacyFileNoteNoticeHtml(ownerType)", js);
        Assert.Contains("qualityInspectionEvidenceBoundaryText", js);
        Assert.Contains("sampleEvidenceBoundaryText", js);
        Assert.Contains("aeEsc(", js);
        Assert.DoesNotContain("storageKey", js);
        Assert.DoesNotContain("OssStorageService", js);
        Assert.DoesNotContain("AccessKey", js);

        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules.js"));
        Assert.Contains("  sample: {", modules);
        Assert.Contains("附件证据概览", modules);
        Assert.Contains("openAttachmentEvidenceSummaryForCurrentPage", modules);
        Assert.Contains("openAttachmentEvidencesForCurrentModule", modules);

        var modulesDoc = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc.js"));
        Assert.Contains("openQualityInspectionEvidencesForCurrentModule", modulesDoc);
        Assert.Contains("验货证据", modulesDoc);
        Assert.Contains("不改写验货状态与到货进度", modulesDoc);

        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/attachment-evidences.js", index);
    }

    // ==================== 10. 全部验货状态与样品类型 / 反馈（只读标注，绝不推断） ====================

    [Theory]
    [InlineData("未验货")]
    [InlineData("验货中")]
    [InlineData("合格")]
    [InlineData("不合格")]
    [InlineData("免验")]
    [InlineData("")]
    public async Task 各验货状态都可挂证据且只读标注不推断(string qcStatus)
    {
        using var db = TestDbFactory.Create();
        var order = SeedPurchaseOrder(db, $"PO-063-T-{qcStatus}", qcStatus: qcStatus);
        var store = new InMemoryAttachmentContentStore();

        var saved = await UploadOkAsync(
            db, store, PdfBytes(qcStatus), AttachmentEvidenceRules.OwnerTypeQualityInspection, order.Id);
        Assert.Equal("验货记录", saved.OwnerTypeText);
        Assert.Equal(order.OrderNo, saved.OwnerNo);
        Assert.Equal(AttachmentEvidenceRules.MediaPdf, saved.MediaType);

        // 归属候选：状态取验货状态**原文**（空状态照实标注「未登记验货状态」，不推断）
        var option = (await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypeQualityInspection, null))
            .Single(o => o.OwnerId == order.Id);
        Assert.Equal(AttachmentEvidenceRules.QualityInspectionStatusText(qcStatus), option.StatusText);
        Assert.Equal(1, option.EvidenceCount);

        // 只读：上传不改写验货状态（含空状态与「不合格」这类结论性文本）
        Assert.Equal(qcStatus, (await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).QcStatus);
        // 证据本身绝不生成合格 / 不合格判定文案
        Assert.DoesNotContain("判定为合格", saved.BoundaryText);
        Assert.DoesNotContain("判定为不合格", saved.BoundaryText);
    }

    [Theory]
    [InlineData("打样", "待反馈")]
    [InlineData("寄样", "满意")]
    [InlineData("借样", "需修改")]
    [InlineData("客户来样", "已下单")]
    [InlineData("", "未采用")]
    [InlineData("打样", "")]
    public async Task 各样品类型与反馈都可挂证据且只读标注不推断(string sampleType, string result)
    {
        using var db = TestDbFactory.Create();
        var sample = SeedSample(db, $"SP-063-T", sampleType: sampleType, result: result);
        var store = new InMemoryAttachmentContentStore();

        var saved = await UploadOkAsync(
            db, store, PngBytes(), AttachmentEvidenceRules.OwnerTypeSample, sample.Id,
            fileName: "样品照片.png", declaredContentType: AttachmentEvidenceRules.MediaPng);

        var option = (await AttachmentEvidenceService.ListOwnerOptionsAsync(
            db, AttachmentEvidenceRules.OwnerTypeSample, null))
            .Single(o => o.OwnerId == sample.Id);
        Assert.Equal(AttachmentEvidenceRules.SampleResultText(result), option.StatusText);
        Assert.Contains($"样品类型 {AttachmentEvidenceRules.SampleTypeText(sampleType)}", option.SummaryText);
        Assert.Contains($"客户反馈 {AttachmentEvidenceRules.SampleResultText(result)}", option.SummaryText);

        // 只读：上传不改写样品台账（客户反馈 / 样品类型 / 样品费结算状态）
        var persisted = await db.Samples.AsNoTracking().SingleAsync(s => s.Id == sample.Id);
        Assert.Equal(result, persisted.Result);
        Assert.Equal(sampleType, persisted.SampleType);
        Assert.False(persisted.FeeSettled);
        Assert.Contains("不代表样品已获批准", saved.BoundaryText);
        Assert.Equal("样品记录", saved.OwnerTypeText);
    }

    // ==================== 11. 纯规则文案（按归属类型区分口径，缺失不解释为已通过） ====================

    [Fact]
    public void 规则文案_按归属类型区分边界且不把缺失解释成已通过()
    {
        var inspectionSummary = AttachmentEvidenceRules.OwnerSummaryText(
            AttachmentEvidenceRules.OwnerTypeQualityInspection, "验货记录", "PO-1", 2, 1, 1);
        Assert.Contains("仓库附件证据 2 条", inspectionSummary);
        Assert.Contains("不代表验货合格 / 不合格判定", inspectionSummary);
        Assert.DoesNotContain("已向海关", inspectionSummary);

        var sampleSummary = AttachmentEvidenceRules.OwnerSummaryText(
            AttachmentEvidenceRules.OwnerTypeSample, "样品记录", "SP-1", 1, 1, 0);
        Assert.Contains("不代表样品已获批准", sampleSummary);
        Assert.DoesNotContain("已向海关", sampleSummary);

        // ERP-062 口径保持不变（出口单证仍按海关 / 税务 / 承运人口径）
        Assert.Contains("不代表已向海关", AttachmentEvidenceRules.OwnerSummaryText(
            AttachmentEvidenceRules.OwnerTypeTradeDocument, "出口单证", "DOC-1", 1, 1, 0));
        Assert.Contains("不代表已向海关", AttachmentEvidenceRules.OwnerSummaryText(
            "出口单证", "DOC-1", 1, 1, 0));

        // 计数为 0 时只说明「暂无仓库附件证据」，绝不解释成「无缺陷 / 已通过」
        var none = AttachmentEvidenceRules.OwnerSummaryText(
            AttachmentEvidenceRules.OwnerTypeQualityInspection, "验货记录", "PO-1", 0, 0, 0);
        Assert.Contains("暂无仓库附件证据", none);
        Assert.DoesNotContain("合格", none);
        Assert.DoesNotContain("通过", none);

        // 边界文案按类型（大小写不敏感）选择；未知 / 历史类型回落通用文案，不借用别的类型口径
        Assert.Equal(AttachmentEvidenceRules.QualityInspectionEvidenceBoundaryText,
            AttachmentEvidenceRules.BoundaryTextOf("qualityinspection"));
        Assert.Equal(AttachmentEvidenceRules.SampleEvidenceBoundaryText,
            AttachmentEvidenceRules.BoundaryTextOf(" SAMPLE "));
        Assert.Equal(AttachmentEvidenceRules.BoundaryText, AttachmentEvidenceRules.BoundaryTextOf("UnknownType"));
        Assert.Equal(AttachmentEvidenceRules.BoundaryText, AttachmentEvidenceRules.BoundaryTextOf(null));

        // 自由文本一律只做有界修剪与空值标注，绝不推断结论
        Assert.Equal("未登记验货状态", AttachmentEvidenceRules.QualityInspectionStatusText("  "));
        Assert.Equal("未登记到货进度", AttachmentEvidenceRules.ArrivalProgressText(" "));
        Assert.Equal("未登记样品类型", AttachmentEvidenceRules.SampleTypeText(null));
        Assert.Equal("未登记客户反馈", AttachmentEvidenceRules.SampleResultText(""));
        Assert.Equal("合格", AttachmentEvidenceRules.QualityInspectionStatusText(" 合格 "));
        Assert.Equal("未采用", AttachmentEvidenceRules.SampleResultText("未采用"));
    }

    // ==================== 12. 两类归属共用同一套内容校验（拒绝不安全内容） ====================

    [Theory]
    [InlineData(AttachmentEvidenceRules.OwnerTypeQualityInspection, "报告.html", AttachmentEvidenceRules.MediaPdf, "html")]
    [InlineData(AttachmentEvidenceRules.OwnerTypeQualityInspection, "报告.pdf", AttachmentEvidenceRules.MediaPdf, "html")]
    [InlineData(AttachmentEvidenceRules.OwnerTypeQualityInspection, "报告.png", AttachmentEvidenceRules.MediaPng, "html")]
    [InlineData(AttachmentEvidenceRules.OwnerTypeQualityInspection, "报告", AttachmentEvidenceRules.MediaPdf, "pdf")]
    [InlineData(AttachmentEvidenceRules.OwnerTypeSample, "照片.html", AttachmentEvidenceRules.MediaPng, "png")]
    [InlineData(AttachmentEvidenceRules.OwnerTypeSample, "照片.png", AttachmentEvidenceRules.MediaPng, "html")]
    [InlineData(AttachmentEvidenceRules.OwnerTypeSample, "照片.svg", AttachmentEvidenceRules.MediaJpeg, "jpeg")]
    [InlineData(AttachmentEvidenceRules.OwnerTypeSample, "照片.pdf", AttachmentEvidenceRules.MediaJpeg, "pdf")]
    public async Task 两类归属都拒绝不安全内容与伪装改名(string ownerType, string fileName, string declaredContentType, string body)
    {
        using var db = TestDbFactory.Create();
        var ownerId = ownerType == AttachmentEvidenceRules.OwnerTypeQualityInspection
            ? SeedPurchaseOrder(db, "PO-063-L").Id
            : SeedSample(db, "SP-063-L").Id;
        var store = new InMemoryAttachmentContentStore();
        var content = body switch
        {
            "html" => HtmlBytes(),
            "png" => PngBytes(),
            "jpeg" => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 },
            _ => PdfBytes("l")
        };

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.UploadAsync(
            db, store, UploadRequest(content, ownerType, ownerId, fileName, declaredContentType), "测试用户", 7));

        Assert.Equal(0, await db.AttachmentEvidences.CountAsync());
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task 空内容与缺失内容一律拒绝且不落库不落盘()
    {
        using var db = TestDbFactory.Create();
        var order = SeedPurchaseOrder(db, "PO-063-M");
        var sample = SeedSample(db, "SP-063-M");
        var store = new InMemoryAttachmentContentStore();

        // 缺失内容（未选择文件）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.UploadAsync(
            db, store,
            new AttachmentEvidenceUploadRequest
            {
                OwnerType = AttachmentEvidenceRules.OwnerTypeQualityInspection,
                OwnerId = order.Id,
                FileName = "报告.pdf",
                DeclaredContentType = AttachmentEvidenceRules.MediaPdf,
                DeclaredLength = 0,
                Content = null
            },
            "测试用户", 7));

        // 空内容（0 字节）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.UploadAsync(
            db, store, UploadRequest(Array.Empty<byte>(), AttachmentEvidenceRules.OwnerTypeSample, sample.Id),
            "测试用户", 7));

        // 声明长度超限：提前拒绝（不读取也不保存任何内容）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.UploadAsync(
            db, store,
            new AttachmentEvidenceUploadRequest
            {
                OwnerType = AttachmentEvidenceRules.OwnerTypeSample,
                OwnerId = sample.Id,
                FileName = "大图.png",
                DeclaredContentType = AttachmentEvidenceRules.MediaPng,
                DeclaredLength = AttachmentEvidenceRules.MaxSizeBytes + 1,
                Content = new MemoryStream(PngBytes(), writable: false)
            },
            "测试用户", 7));

        // 文件名不安全（控制字符 / 路径）只做净化，不改变内容的拒绝口径
        var sanitized = await UploadOkAsync(
            db, store, PdfBytes("m"), AttachmentEvidenceRules.OwnerTypeQualityInspection, order.Id,
            fileName: @"C:\scans\..\报告.pdf");
        Assert.Equal("报告.pdf", sanitized.OriginalFileName);

        Assert.Equal(1, await db.AttachmentEvidences.CountAsync());
        Assert.Equal(1, store.SaveCalls);
    }
}
