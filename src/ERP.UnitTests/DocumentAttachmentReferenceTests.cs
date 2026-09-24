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
/// 业务单据附件引用登记单元测试（ERP-045）。覆盖：四种白名单父单据（销售订单 / 采购订单 / 装柜清单 / 出口单证）
/// 的父单据号码与类型快照、未知与不存在 / 已删除父单据拒绝、有界元数据（显示名 / 引用标识 / 内容类型 /
/// 字节数 / 校验和 / 备注）、不安全引用（链接 / 数据方案 / HTML / 脚本 / 路径穿越 / 空白）拒绝与历史不安全值
/// 只显示不可用文本、来源授权确认、作废与历史保留、有效身份唯一与作废后重登记、台账过滤 / 分页 / 有界、
/// 父单据候选、模块元数据、非变更边界（父单据 / 图片位与既有 FileNote）、控制器与模型 / SchemaUpgrader /
/// 前端接线契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 迁移 / 部署脚本，也不访问任何对象存储。
/// </summary>
public class DocumentAttachmentReferenceTests
{
    // ==================== 0. 测试脚手架 ====================

    private static DocumentAttachmentReferenceController BuildController(ErpDbContext db) => new(db);

    private static SalesOrder SeedSalesOrder(
        ErpDbContext db, string orderNo = "SO2026001", bool deleted = false, string remark = "")
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = 7,
            TotalAmount = 1000m,
            Status = DocumentStatus.Approved,
            Remark = remark,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static BaseSupplier SeedSupplier(ErpDbContext db, string code = "S001")
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = "义乌档口", Status = 1 };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static PurchaseOrder SeedPurchaseOrder(
        ErpDbContext db, long supplierId, string orderNo = "PO2026001", bool deleted = false)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 2),
            SupplierId = supplierId,
            Currency = Currency.CNY,
            TotalAmount = 500m,
            Status = DocumentStatus.Approved,
            IsDeleted = deleted
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static ContainerLoadingList SeedLoadingList(
        ErpDbContext db, string loadingListNo = "LL2026001", bool deleted = false)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = loadingListNo,
            LoadingDate = new DateTime(2026, 9, 3),
            ContainerNo = "MSKU1234567",
            CustomerId = 7,
            Status = DocumentStatus.Approved,
            IsDeleted = deleted
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    private static TradeDocument SeedTradeDocument(
        ErpDbContext db, string docNo = "DOC2026001", string fileNote = "", bool deleted = false)
    {
        var doc = new TradeDocument
        {
            DocNo = docNo,
            DocType = "装箱单",
            Status = "已制作",
            FileNote = fileNote,
            IsDeleted = deleted
        };
        db.TradeDocuments.Add(doc);
        db.SaveChanges();
        return doc;
    }

    private static BaseProduct SeedProduct(
        ErpDbContext db, string code = "P001", string image1 = "https://cdn.example.com/p1.jpg")
    {
        var product = new BaseProduct
        {
            ProductCode = code,
            ProductName = "商品 1",
            Image1 = image1,
            Image2 = string.Empty,
            Image3 = string.Empty,
            Status = 1
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    /// <summary>附件引用登记请求（默认：合同分类 + 合法不透明标识 + 显式来源授权确认）</summary>
    private static DocumentAttachmentReferenceSaveDto DarDto(
        string parentType, long parentId,
        string category = DocumentAttachmentReferenceRules.CategoryContract,
        string displayName = "采购合同扫描件",
        string referenceId = "att-2026-000123",
        string contentType = "", long sizeBytes = 0, string checksum = "", string notes = "",
        bool acknowledged = true, string authorizationNote = "客户邮件确认可引用该扫描件",
        string authorizedBy = "张三")
        => new()
        {
            ParentType = parentType,
            ParentId = parentId,
            Category = category,
            DisplayName = displayName,
            ReferenceId = referenceId,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Checksum = checksum,
            Notes = notes,
            SourceAuthorizationAcknowledged = acknowledged,
            SourceAuthorizationNote = authorizationNote,
            AuthorizedBy = authorizedBy
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

    private static async Task<DocumentAttachmentReferenceDto> CreateAsync(
        DocumentAttachmentReferenceController controller, DocumentAttachmentReferenceSaveDto dto)
        => AssertOk<DocumentAttachmentReferenceDto>(await controller.Create(dto));

    private static async Task<DocumentAttachmentReferenceDto> VoidAsync(
        DocumentAttachmentReferenceController controller, long id, string reason)
        => AssertOk<DocumentAttachmentReferenceDto>(
            await controller.Void(id, new DocumentAttachmentReferenceVoidRequest { Reason = reason }));

    // ==================== 1. 四种白名单父单据与父单据快照 ====================

    [Fact]
    public async Task 销售订单_登记附件引用_写入父单据号码与类型快照()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO2026-0001");
        var controller = BuildController(db);

        var created = await CreateAsync(controller,
            DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id));

        Assert.Equal(DocumentAttachmentReferenceRules.StatusActive, created.Status);
        Assert.Equal("有效", created.StatusText);
        Assert.True(created.IsActive);
        Assert.False(created.IsVoided);
        Assert.Equal("SO2026-0001", created.ParentNo);
        Assert.Equal("销售订单", created.ParentTypeText);
        Assert.Equal("销售订单 SO2026-0001", created.ParentSnapshotText);
        Assert.True(created.ParentAvailable);
        Assert.Equal("合同 / 协议", created.CategoryText);
        Assert.True(created.SourceAuthorizationAcknowledged);
        Assert.Equal("张三", created.AuthorizedBy);
        Assert.True(created.ReferenceAvailable);
        Assert.Equal(string.Empty, created.ReferenceUnavailableText);
        Assert.True(created.RegisteredAt > DateTime.MinValue);
        Assert.True(created.AuthorizedAt > DateTime.MinValue);
        Assert.Contains("元数据", created.MetadataOnlyNoticeText);
        Assert.Contains("不做对象存储读写", created.BoundaryText);
    }

    [Fact]
    public async Task 采购订单_登记附件引用_写入父单据号码与类型快照()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db);
        var order = SeedPurchaseOrder(db, supplier.Id, "PO2026-0009");
        var controller = BuildController(db);

        var created = await CreateAsync(controller,
            DarDto(DocumentAttachmentReferenceRules.ParentTypePurchaseOrder, order.Id, referenceId: "po-9-att"));

        Assert.Equal("PO2026-0009", created.ParentNo);
        Assert.Equal("采购订单", created.ParentTypeText);
        Assert.Equal("采购订单 PO2026-0009", created.ParentSnapshotText);
        Assert.True(created.ParentAvailable);
    }

    [Fact]
    public async Task 装柜清单_登记附件引用_写入父单据号码与类型快照()
    {
        using var db = TestDbFactory.Create();
        var list = SeedLoadingList(db, "LL2026-0007");
        var controller = BuildController(db);

        var created = await CreateAsync(controller,
            DarDto(DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList, list.Id,
                category: DocumentAttachmentReferenceRules.CategoryPackingList));

        Assert.Equal("LL2026-0007", created.ParentNo);
        Assert.Equal("装柜清单", created.ParentTypeText);
        Assert.Equal("装箱单 / 重量单", created.CategoryText);
        Assert.True(created.ParentAvailable);
    }

    [Fact]
    public async Task 出口单证_登记附件引用_写入父单据号码与类型快照()
    {
        using var db = TestDbFactory.Create();
        var doc = SeedTradeDocument(db, "DOC2026-0005");
        var controller = BuildController(db);

        var created = await CreateAsync(controller,
            DarDto(DocumentAttachmentReferenceRules.ParentTypeTradeDocument, doc.Id,
                category: DocumentAttachmentReferenceRules.CategoryCustoms));

        Assert.Equal("DOC2026-0005", created.ParentNo);
        Assert.Equal("出口单证", created.ParentTypeText);
        Assert.Equal("报关资料", created.CategoryText);
        Assert.True(created.ParentAvailable);
    }

    [Fact]
    public async Task 未知父单据类型_拒绝登记()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto("Quotation", order.Id)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(string.Empty, order.Id)));

        Assert.Equal(0, await db.DocumentAttachmentReferences.CountAsync());
    }

    [Fact]
    public async Task 父单据不存在_拒绝登记()
    {
        using var db = TestDbFactory.Create();
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, 999)));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(DarDto(DocumentAttachmentReferenceRules.ParentTypeTradeDocument, 999)));

        Assert.Equal(0, await db.DocumentAttachmentReferences.CountAsync());
    }

    [Fact]
    public async Task 父单据已删除_拒绝登记()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, deleted: true);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id)));

        Assert.Contains("不存在或已删除", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await db.DocumentAttachmentReferences.CountAsync());
    }

    [Fact]
    public async Task 父单据号码超长_拒绝登记_不形成超界快照()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, new string('A', 60));
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id)));

        Assert.Equal(0, await db.DocumentAttachmentReferences.CountAsync());
    }

    // ==================== 2. 有界元数据校验 ====================

    [Fact]
    public async Task 未知分类_拒绝登记()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id,
                category: "Secret")));
    }

    [Fact]
    public async Task 显示名必填_超长_标记或链接_均拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        const string type = DocumentAttachmentReferenceRules.ParentTypeSalesOrder;

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, displayName: "   ")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id,
                displayName: new string('名', DocumentAttachmentReferenceRules.MaxDisplayNameLength + 1))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, displayName: "<script>alert(1)</script>")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, displayName: "https://cdn.example.com/contract.pdf")));

        Assert.Equal(0, await db.DocumentAttachmentReferences.CountAsync());
    }

    [Fact]
    public async Task 引用标识必填且超长拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        const string type = DocumentAttachmentReferenceRules.ParentTypeSalesOrder;

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, referenceId: "  ")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id,
                referenceId: new string('a', DocumentAttachmentReferenceRules.MaxReferenceIdLength + 1))));
    }

    [Theory]
    [InlineData("https://oss.example.com/bucket/contract.pdf")]   // 任意 URL
    [InlineData("//oss.example.com/contract.pdf")]                // 协议相对链接
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]            // data 方案
    [InlineData("javascript:alert(1)")]                           // 脚本方案
    [InlineData("file:///C:/secrets/contract.pdf")]               // 本地文件方案
    [InlineData("../..\\etc/passwd")]                             // 文件系统穿越
    [InlineData("..\\..\\windows\\win.ini")]                      // Windows 反斜杠穿越
    [InlineData("att 2026 0001")]                                 // 空白分隔
    [InlineData("%2e%2e%2fetc%2fpasswd")]                         // 百分号编码穿越
    [InlineData("<script>alert(1)</script>")]                     // HTML / 脚本标记
    [InlineData("a/b/c")]                                         // 路径形态
    [InlineData("att-2026-0001?token=abc")]                       // 查询串
    public async Task 不安全引用标识_一律拒绝登记(string referenceId)
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id,
                referenceId: referenceId)));

        Assert.Equal(0, await db.DocumentAttachmentReferences.CountAsync());
    }

    [Theory]
    [InlineData("att-2026-000123")]
    [InlineData("9F8E7D6C1234")]
    [InlineData("oss_2026.09+01")]
    [InlineData("a")]
    public async Task 合法不透明引用标识_接受登记(string referenceId)
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);

        var created = await CreateAsync(controller,
            DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id, referenceId: referenceId));

        Assert.Equal(referenceId, created.ReferenceId);
        Assert.True(created.ReferenceAvailable);
        Assert.Equal(string.Empty, created.ReferenceUnavailableText);
        Assert.Contains("元数据引用", DocumentAttachmentReferenceRules.ReferenceLabelText(true));
    }

    [Fact]
    public async Task 内容类型与校验和_只接受合法形态()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        const string type = DocumentAttachmentReferenceRules.ParentTypeSalesOrder;
        var sha256 = new string('a', 64);

        var created = await CreateAsync(controller,
            DarDto(type, order.Id, contentType: "application/pdf", checksum: sha256, sizeBytes: 1024));
        Assert.Equal("application/pdf", created.ContentType);
        Assert.Equal(sha256, created.Checksum);
        Assert.Equal("1 KB", created.SizeText);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, referenceId: "att-bad-ct",
                contentType: "application/pdf; charset=utf-8")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, referenceId: "att-bad-ct2", contentType: "<script>")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, referenceId: "att-bad-sum",
                checksum: "not-a-hex-digest")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, referenceId: "att-bad-sum2", checksum: "abc")));
    }

    [Fact]
    public async Task 字节大小_负数与超上限拒绝_边界值接受()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        const string type = DocumentAttachmentReferenceRules.ParentTypeSalesOrder;

        var maxOk = await CreateAsync(controller,
            DarDto(type, order.Id, referenceId: "att-max", sizeBytes: DocumentAttachmentReferenceRules.MaxSizeBytes));
        Assert.Equal(DocumentAttachmentReferenceRules.MaxSizeBytes, maxOk.SizeBytes);
        Assert.EndsWith("GB", maxOk.SizeText, StringComparison.Ordinal);

        var zero = await CreateAsync(controller, DarDto(type, order.Id, referenceId: "att-zero", sizeBytes: 0));
        Assert.Equal("未提供大小（未知）", zero.SizeText);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, referenceId: "att-neg", sizeBytes: -1)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, referenceId: "att-over",
                sizeBytes: DocumentAttachmentReferenceRules.MaxSizeBytes + 1)));
    }

    [Fact]
    public async Task 备注超长或含控制字符_拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        const string type = DocumentAttachmentReferenceRules.ParentTypeSalesOrder;

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id,
                notes: new string('备', DocumentAttachmentReferenceRules.MaxNotesLength + 1))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, notes: "备注\u0007含响铃控制字符")));
    }

    [Fact]
    public async Task 历史不安全引用值_读取时只标注不可用并保留原文()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var legacy = new DocumentAttachmentReference
        {
            ParentType = DocumentAttachmentReferenceRules.ParentTypeSalesOrder,
            ParentId = order.Id,
            ParentNo = order.OrderNo,
            ParentTypeText = "销售订单",
            Category = DocumentAttachmentReferenceRules.CategoryOther,
            DisplayName = "历史外部写入值",
            ReferenceId = "https://legacy.example.com/a.pdf",
            SourceAuthorizationAcknowledged = true,
            SourceAuthorizationNote = "历史数据（外部写入）",
            AuthorizedAt = DateTime.Now,
            RegisteredAt = DateTime.Now,
            Status = DocumentAttachmentReferenceRules.StatusActive
        };
        db.DocumentAttachmentReferences.Add(legacy);
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var dto = AssertOk<DocumentAttachmentReferenceDto>(await controller.GetById(legacy.Id));

        Assert.False(dto.ReferenceAvailable);
        Assert.Contains("不可用", dto.ReferenceUnavailableText);
        Assert.Equal("https://legacy.example.com/a.pdf", dto.ReferenceId);
        Assert.Contains("不可用", DocumentAttachmentReferenceRules.ReferenceLabelText(false));
        Assert.False(DocumentAttachmentReferenceRules.IsSafeReferenceId(dto.ReferenceId));
    }

    // ==================== 3. 来源授权确认、唯一身份与作废历史 ====================

    [Fact]
    public async Task 未确认来源授权_拒绝登记()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        const string type = DocumentAttachmentReferenceRules.ParentTypeSalesOrder;

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, acknowledged: false)));
        Assert.Contains("来源授权", ex.Message, StringComparison.Ordinal);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, acknowledged: false, authorizationNote: "仍不确认")));
        Assert.Equal(0, await db.DocumentAttachmentReferences.CountAsync());
    }

    [Fact]
    public async Task 确认说明必填_超长或含标记_拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        const string type = DocumentAttachmentReferenceRules.ParentTypeSalesOrder;

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, authorizationNote: "   ")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id,
                authorizationNote: new string('授', DocumentAttachmentReferenceRules.MaxAuthorizationNoteLength + 1))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(DarDto(type, order.Id, authorizationNote: "<b>客户已授权</b>")));
    }

    [Fact]
    public async Task 授权确认_落库并保留确认人说明与时间()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);

        var created = await CreateAsync(controller,
            DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id,
                authorizationNote: "客户 2026-09-20 邮件确认可引用", authorizedBy: "李四"));

        var stored = await db.DocumentAttachmentReferences.AsNoTracking().FirstAsync(x => x.Id == created.Id);
        Assert.True(stored.SourceAuthorizationAcknowledged);
        Assert.Equal("客户 2026-09-20 邮件确认可引用", stored.SourceAuthorizationNote);
        Assert.Equal("李四", stored.AuthorizedBy);
        Assert.True(stored.AuthorizedAt > DateTime.MinValue);
        Assert.Contains("授权", DocumentAttachmentReferenceRules.AuthorizationPolicyText);
    }

    [Fact]
    public async Task 同一父单据同一分类同一引用标识_重复登记拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        const string type = DocumentAttachmentReferenceRules.ParentTypeSalesOrder;

        await CreateAsync(controller, DarDto(type, order.Id));

        var ex = await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => controller.Create(DarDto(type, order.Id, displayName: "同一标识重复登记")));
        Assert.Contains("重复登记被拒绝", ex.Message, StringComparison.Ordinal);

        // 不同分类 / 不同引用标识仍可登记（唯一口径不扩大到「同一父单据只能有一条」）
        await CreateAsync(controller, DarDto(type, order.Id,
            category: DocumentAttachmentReferenceRules.CategoryInvoice));
        await CreateAsync(controller, DarDto(type, order.Id, referenceId: "att-2026-000124"));

        Assert.Equal(3, await db.DocumentAttachmentReferences.CountAsync());
    }

    [Fact]
    public async Task 作废必须填原因_作废保留原始元数据与授权留痕()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);

        var created = await CreateAsync(controller,
            DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id,
                displayName: "待作废的合同扫描件", notes: "原始备注", contentType: "application/pdf",
                checksum: new string('b', 32), sizeBytes: 2048, authorizedBy: "王五"));

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(created.Id, new DocumentAttachmentReferenceVoidRequest { Reason = "  " }));

        var voided = await VoidAsync(controller, created.Id, "引用标识填错，重新登记");

        Assert.Equal(DocumentAttachmentReferenceRules.StatusVoided, voided.Status);
        Assert.Equal("已作废", voided.StatusText);
        Assert.True(voided.IsVoided);
        Assert.False(voided.IsActive);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal("引用标识填错，重新登记", voided.VoidReason);
        // 原始元数据与授权留痕完整保留
        Assert.Equal(created.ReferenceId, voided.ReferenceId);
        Assert.Equal("待作废的合同扫描件", voided.DisplayName);
        Assert.Equal("原始备注", voided.Notes);
        Assert.Equal("application/pdf", voided.ContentType);
        Assert.Equal(new string('b', 32), voided.Checksum);
        Assert.Equal(2048, voided.SizeBytes);
        Assert.True(voided.SourceAuthorizationAcknowledged);
        Assert.Equal("王五", voided.AuthorizedBy);

        // 作废是状态留痕，不是物理删除
        var stored = await db.DocumentAttachmentReferences.AsNoTracking().FirstAsync(x => x.Id == created.Id);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task 重复作废_拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);

        var created = await CreateAsync(controller,
            DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id));
        await VoidAsync(controller, created.Id, "第一次作废");

        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => VoidAsync(controller, created.Id, "第二次作废"));
    }

    [Fact]
    public async Task 作废后_同一引用标识可重新登记且已作废记录仍可读()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        const string type = DocumentAttachmentReferenceRules.ParentTypeSalesOrder;

        var first = await CreateAsync(controller, DarDto(type, order.Id, displayName: "第一版"));
        await VoidAsync(controller, first.Id, "登记错误，重新登记");

        var second = await CreateAsync(controller, DarDto(type, order.Id, displayName: "第二版"));

        Assert.NotEqual(first.Id, second.Id);
        var firstRead = AssertOk<DocumentAttachmentReferenceDto>(await controller.GetById(first.Id));
        Assert.True(firstRead.IsVoided);
        Assert.Equal("第一版", firstRead.DisplayName);
        Assert.Equal("登记错误，重新登记", firstRead.VoidReason);
        Assert.Equal(2, await db.DocumentAttachmentReferences.CountAsync());
    }

    // ==================== 4. 台账读取：过滤、分页与有界 ====================

    [Fact]
    public async Task 台账_按父单据类型与父单据Id过滤()
    {
        using var db = TestDbFactory.Create();
        var orderA = SeedSalesOrder(db, "SO-A");
        var orderB = SeedSalesOrder(db, "SO-B");
        var list = SeedLoadingList(db, "LL-A");
        var controller = BuildController(db);

        await CreateAsync(controller, DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, orderA.Id));
        await CreateAsync(controller, DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, orderB.Id));
        await CreateAsync(controller, DarDto(DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList, list.Id));

        var all = AssertOk<PagedResult<DocumentAttachmentReferenceDto>>(
            await controller.GetPaged(new DocumentAttachmentReferenceQuery()));
        Assert.Equal(3, all.Total);

        var byType = AssertOk<PagedResult<DocumentAttachmentReferenceDto>>(
            await controller.GetPaged(new DocumentAttachmentReferenceQuery
            {
                ParentType = DocumentAttachmentReferenceRules.ParentTypeSalesOrder
            }));
        Assert.Equal(2, byType.Total);

        var byParent = AssertOk<PagedResult<DocumentAttachmentReferenceDto>>(
            await controller.GetPaged(new DocumentAttachmentReferenceQuery
            {
                ParentType = DocumentAttachmentReferenceRules.ParentTypeSalesOrder,
                ParentId = orderA.Id
            }));
        Assert.Single(byParent.Items);
        Assert.Equal("SO-A", byParent.Items[0].ParentNo);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new DocumentAttachmentReferenceQuery { ParentType = "UnknownType" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new DocumentAttachmentReferenceQuery { ParentId = 0 }));
    }

    [Fact]
    public async Task 台账_状态与分类过滤_默认包含已作废历史()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        const string type = DocumentAttachmentReferenceRules.ParentTypeSalesOrder;

        var active = await CreateAsync(controller,
            DarDto(type, order.Id, category: DocumentAttachmentReferenceRules.CategoryInvoice,
                referenceId: "att-invoice-1"));
        var toVoid = await CreateAsync(controller,
            DarDto(type, order.Id, category: DocumentAttachmentReferenceRules.CategoryPhoto,
                referenceId: "att-photo-1"));
        await VoidAsync(controller, toVoid.Id, "作废留痕");

        var all = AssertOk<PagedResult<DocumentAttachmentReferenceDto>>(
            await controller.GetPaged(new DocumentAttachmentReferenceQuery()));
        Assert.Equal(2, all.Total);

        var onlyActive = AssertOk<PagedResult<DocumentAttachmentReferenceDto>>(
            await controller.GetPaged(new DocumentAttachmentReferenceQuery
            {
                Status = DocumentAttachmentReferenceRules.StatusActive
            }));
        Assert.Single(onlyActive.Items);
        Assert.Equal(active.Id, onlyActive.Items[0].Id);
        Assert.True(onlyActive.Items[0].ReferenceAvailable);

        var onlyVoided = AssertOk<PagedResult<DocumentAttachmentReferenceDto>>(
            await controller.GetPaged(new DocumentAttachmentReferenceQuery
            {
                Status = DocumentAttachmentReferenceRules.StatusVoided
            }));
        Assert.Single(onlyVoided.Items);
        Assert.Equal("作废留痕", onlyVoided.Items[0].VoidReason);

        var byCategory = AssertOk<PagedResult<DocumentAttachmentReferenceDto>>(
            await controller.GetPaged(new DocumentAttachmentReferenceQuery
            {
                Category = DocumentAttachmentReferenceRules.CategoryInvoice
            }));
        Assert.Single(byCategory.Items);
        Assert.Equal("att-invoice-1", byCategory.Items[0].ReferenceId);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new DocumentAttachmentReferenceQuery { Status = 9 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new DocumentAttachmentReferenceQuery { Category = "Unknown" }));
    }

    [Fact]
    public async Task 台账_关键字过滤_超长关键字拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-KEY");
        var controller = BuildController(db);
        const string type = DocumentAttachmentReferenceRules.ParentTypeSalesOrder;

        await CreateAsync(controller, DarDto(type, order.Id, referenceId: "att-keep-1"));
        await CreateAsync(controller, DarDto(type, order.Id, referenceId: "att-other-2",
            displayName: "装箱单扫描件"));

        var byReference = AssertOk<PagedResult<DocumentAttachmentReferenceDto>>(
            await controller.GetPaged(new DocumentAttachmentReferenceQuery { Keyword = "att-keep" }));
        Assert.Single(byReference.Items);

        var byParentNo = AssertOk<PagedResult<DocumentAttachmentReferenceDto>>(
            await controller.GetPaged(new DocumentAttachmentReferenceQuery { Keyword = "SO-KEY" }));
        Assert.Equal(2, byParentNo.Total);

        var byDisplayName = AssertOk<PagedResult<DocumentAttachmentReferenceDto>>(
            await controller.GetPaged(new DocumentAttachmentReferenceQuery { Keyword = "装箱单" }));
        Assert.Single(byDisplayName.Items);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new DocumentAttachmentReferenceQuery
            {
                Keyword = new string('k', DocumentAttachmentReferenceService.MaxKeywordLength + 1)
            }));
    }

    [Fact]
    public async Task 台账_分页参数收敛到上下界并支持翻页()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        for (var i = 1; i <= 5; i++)
        {
            await CreateAsync(controller,
                DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id, referenceId: $"att-p{i}"));
        }

        var oversized = AssertOk<PagedResult<DocumentAttachmentReferenceDto>>(
            await controller.GetPaged(new DocumentAttachmentReferenceQuery { Page = 0, PageSize = 100000 }));
        Assert.Equal(1, oversized.Page);
        Assert.Equal(DocumentAttachmentReferenceQuery.MaxPageSize, oversized.PageSize);
        Assert.Equal(5, oversized.Total);
        Assert.Equal(5, oversized.Items.Count);

        var paged = AssertOk<PagedResult<DocumentAttachmentReferenceDto>>(
            await controller.GetPaged(new DocumentAttachmentReferenceQuery { Page = 2, PageSize = 2 }));
        Assert.Equal(2, paged.Page);
        Assert.Equal(2, paged.PageSize);
        Assert.Equal(5, paged.Total);
        Assert.Equal(3, paged.TotalPages);
        Assert.Equal(2, paged.Items.Count);
    }

    [Fact]
    public async Task 单据详情清单_有界且标注元数据引用()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-DETAIL");
        var other = SeedSalesOrder(db, "SO-OTHER");
        const string type = DocumentAttachmentReferenceRules.ParentTypeSalesOrder;
        const int extra = 5;

        var rows = Enumerable.Range(1, DocumentAttachmentReferenceRules.MaxPerParent + extra)
            .Select(i => new DocumentAttachmentReference
            {
                ParentType = type,
                ParentId = order.Id,
                ParentNo = "SO-DETAIL",
                ParentTypeText = "销售订单",
                Category = DocumentAttachmentReferenceRules.CategoryContract,
                DisplayName = "批量引用 " + i,
                ReferenceId = "att-bulk-" + i,
                SourceAuthorizationAcknowledged = true,
                SourceAuthorizationNote = "批量造数",
                AuthorizedAt = DateTime.Now,
                RegisteredAt = DateTime.Now,
                Status = DocumentAttachmentReferenceRules.StatusActive
            })
            .ToList();
        db.DocumentAttachmentReferences.AddRange(rows);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        var bounded = AssertOk<List<DocumentAttachmentReferenceDto>>(
            await controller.GetForParent(type, order.Id, null, int.MaxValue));
        Assert.Equal(DocumentAttachmentReferenceRules.MaxPerParent, bounded.Count);
        Assert.All(bounded, item => Assert.Contains("元数据引用", item.MetadataOnlyNoticeText));

        var small = AssertOk<List<DocumentAttachmentReferenceDto>>(
            await controller.GetForParent(type, order.Id, null, 3));
        Assert.Equal(3, small.Count);

        // 有界清单也只返回指定父单据的记录
        await CreateAsync(controller, DarDto(type, other.Id, referenceId: "att-other-doc"));
        var otherOnly = AssertOk<List<DocumentAttachmentReferenceDto>>(
            await controller.GetForParent(type, other.Id, null, 10));
        Assert.Single(otherOnly);
        Assert.Equal("SO-OTHER", otherOnly[0].ParentNo);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetForParent(type, 0, null, 10));
    }

    [Fact]
    public async Task 父单据候选_只返回未删除单据并支持关键字与有界上限()
    {
        using var db = TestDbFactory.Create();
        SeedSalesOrder(db, "SO-CAND-1");
        SeedSalesOrder(db, "SO-CAND-2");
        SeedSalesOrder(db, "SO-DELETED", deleted: true);
        var controller = BuildController(db);

        var all = AssertOk<List<DocumentAttachmentReferenceParentOptionDto>>(
            await controller.ParentOptions(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, null, 0));
        Assert.Equal(2, all.Count);
        Assert.All(all, option => Assert.True(option.Selectable));
        Assert.All(all, option => Assert.Contains("销售订单", option.SummaryText));

        var byKeyword = AssertOk<List<DocumentAttachmentReferenceParentOptionDto>>(
            await controller.ParentOptions(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, "CAND-2", 50));
        Assert.Single(byKeyword);
        Assert.Equal("SO-CAND-2", byKeyword[0].ParentNo);

        var capped = AssertOk<List<DocumentAttachmentReferenceParentOptionDto>>(
            await controller.ParentOptions(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, null, 1));
        Assert.Single(capped);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.ParentOptions("Quotation", null, 10));
    }

    [Fact]
    public async Task 模块元数据_返回白名单与口径文案()
    {
        var metadata = await DocumentAttachmentReferenceService.GetMetadataAsync();

        Assert.Equal(DocumentAttachmentReferenceRules.SupportedParentTypes.Length, metadata.ParentTypes.Count);
        Assert.Contains(metadata.ParentTypes, option =>
            option.Value == DocumentAttachmentReferenceRules.ParentTypeTradeDocument && option.Label == "出口单证");
        Assert.Equal(DocumentAttachmentReferenceRules.SupportedCategories.Length, metadata.Categories.Count);
        Assert.Equal(2, metadata.StatusOptions.Count);
        Assert.Equal(DocumentAttachmentReferenceRules.MaxSizeBytes, metadata.MaxSizeBytes);
        Assert.Equal(DocumentAttachmentReferenceQuery.MaxPageSize, metadata.MaxPageSize);
        Assert.Equal(DocumentAttachmentReferenceRules.MaxPerParent, metadata.MaxPerParent);
        Assert.Contains("不透明", metadata.ReferencePolicyText);
        Assert.Contains("授权", metadata.AuthorizationPolicyText);
        Assert.Contains("10 GiB", metadata.SizePolicyText);
        Assert.Contains("不上传", metadata.MetadataOnlyNoticeText);
        Assert.Contains("不做对象存储读写", metadata.BoundaryText);
    }

    // ==================== 5. 非变更边界 ====================

    [Fact]
    public async Task 登记与作废_不改写父单据_既有FileNote与商品图片位保持原样()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-IMMUTABLE", remark: "原备注，不应被改动");
        var supplier = SeedSupplier(db);
        var purchaseOrder = SeedPurchaseOrder(db, supplier.Id, "PO-IMMUTABLE");
        var loadingList = SeedLoadingList(db, "LL-IMMUTABLE");
        const string fileNote = "D:\\扫描件\\装箱单.pdf（人工备注，保持原样）";
        var document = SeedTradeDocument(db, "DOC-IMMUTABLE", fileNote);
        var product = SeedProduct(db);
        var controller = BuildController(db);

        var created = await CreateAsync(controller,
            DarDto(DocumentAttachmentReferenceRules.ParentTypeSalesOrder, order.Id));
        await CreateAsync(controller,
            DarDto(DocumentAttachmentReferenceRules.ParentTypePurchaseOrder, purchaseOrder.Id, referenceId: "po-imm"));
        await CreateAsync(controller,
            DarDto(DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList, loadingList.Id, referenceId: "ll-imm"));
        await CreateAsync(controller,
            DarDto(DocumentAttachmentReferenceRules.ParentTypeTradeDocument, document.Id, referenceId: "doc-imm"));
        await VoidAsync(controller, created.Id, "作废同样不改写父单据");

        db.ChangeTracker.Clear();

        var orderAfter = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal("原备注，不应被改动", orderAfter.Remark);
        Assert.Equal(1000m, orderAfter.TotalAmount);
        Assert.Equal(DocumentStatus.Approved, orderAfter.Status);
        Assert.False(orderAfter.IsDeleted);

        var purchaseAfter = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == purchaseOrder.Id);
        Assert.Equal(500m, purchaseAfter.TotalAmount);
        Assert.Equal(DocumentStatus.Approved, purchaseAfter.Status);
        Assert.False(purchaseAfter.IsDeleted);

        var listAfter = await db.ContainerLoadingLists.AsNoTracking().SingleAsync(o => o.Id == loadingList.Id);
        Assert.Equal("MSKU1234567", listAfter.ContainerNo);
        Assert.Equal(DocumentStatus.Approved, listAfter.Status);
        Assert.False(listAfter.IsDeleted);

        var documentAfter = await db.TradeDocuments.AsNoTracking().SingleAsync(o => o.Id == document.Id);
        Assert.Equal(fileNote, documentAfter.FileNote);
        Assert.Equal("已制作", documentAfter.Status);
        Assert.False(documentAfter.IsDeleted);

        var productAfter = await db.BaseProducts.AsNoTracking().SingleAsync(p => p.Id == product.Id);
        Assert.Equal("https://cdn.example.com/p1.jpg", productAfter.Image1);

        // 既有 FileNote 不会被自动导入为附件引用（登记条数 = 手工登记的 4 条，且没有引用标识来自 FileNote）
        Assert.Equal(4, await db.DocumentAttachmentReferences.CountAsync());
        Assert.Equal(0, await db.DocumentAttachmentReferences.AsNoTracking()
            .CountAsync(x => x.ReferenceId == fileNote
                || x.ReferenceId.Contains("装箱单")
                || x.ReferenceId.Contains("扫描件")));

        // 库存与库存流水未被写入
        Assert.Equal(0, await db.Stocks.CountAsync());
        Assert.Equal(0, await db.StockMovements.CountAsync());
    }

    // ==================== 6. 控制器 / 模型 / 幂等结构与前端接线契约 ====================

    [Fact]
    public void 控制器_不提供编辑或删除接口()
    {
        var methods = typeof(DocumentAttachmentReferenceController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToList();

        Assert.DoesNotContain(methods, m => m.GetCustomAttributes<HttpDeleteAttribute>(true).Any());
        Assert.DoesNotContain(methods, m => m.GetCustomAttributes<HttpPutAttribute>(true).Any());
        Assert.DoesNotContain(methods, m => m.GetCustomAttributes<HttpPatchAttribute>(true).Any());
        Assert.Contains(methods, m => m.GetCustomAttributes<HttpGetAttribute>(true).Any());
        Assert.Contains(methods, m => m.GetCustomAttributes<HttpPostAttribute>(true).Any());
        Assert.Contains(methods, m => m.Name == "Void");
    }

    [Fact]
    public void 数据上下文_注册附件引用表并配置有效身份唯一过滤索引()
    {
        using var db = TestDbFactory.Create();
        Assert.NotNull(db.DocumentAttachmentReferences);

        var entityType = db.Model.FindEntityType(typeof(DocumentAttachmentReference));
        Assert.NotNull(entityType);

        var indexes = entityType!.GetIndexes().ToList();
        var active = indexes.Single(i => i.GetDatabaseName() == "UX_DocumentAttachmentReferences_ActiveIdentity");
        Assert.True(active.IsUnique);
        Assert.Equal(4, active.Properties.Count);

        var filter = active.GetFilter() ?? string.Empty;
        Assert.Contains("IsDeleted = 0", filter, StringComparison.Ordinal);
        Assert.Contains("Status = 0", filter, StringComparison.Ordinal);

        Assert.Contains(indexes, i => i.GetDatabaseName() == "IX_DocumentAttachmentReferences_ParentType_ParentId");
        Assert.Contains(indexes, i => i.GetDatabaseName() == "IX_DocumentAttachmentReferences_ReferenceId");
    }

    [Fact]
    public void 结构升级_第32段幂等建表且不改写既有数据()
    {
        var path = RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs");
        Assert.True(File.Exists(path), "SchemaUpgrader.cs 不存在：" + path);

        var lines = File.ReadAllLines(path);
        var start = Array.FindIndex(lines, line => line.Contains("32. 业务单据附件引用登记", StringComparison.Ordinal));
        Assert.True(start >= 0, "SchemaUpgrader 缺少 ERP-045 第 32 段（幂等建表）");

        var sql = string.Join('\n', lines.Skip(start)
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.Contains("IF OBJECT_ID('db_owner.DocumentAttachmentReferences') IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("UX_DocumentAttachmentReferences_ActiveIdentity", sql, StringComparison.Ordinal);
        Assert.Contains("IX_DocumentAttachmentReferences_ParentType_ParentId", sql, StringComparison.Ordinal);
        Assert.Contains("IX_DocumentAttachmentReferences_ReferenceId", sql, StringComparison.Ordinal);
        Assert.Contains("IsDeleted = 0 AND Status = 0", sql, StringComparison.Ordinal);

        // 幂等补齐不得回填 / 改写既有数据，也不得改动父单据表
        foreach (var forbidden in new[] { "UPDATE ", "DELETE ", "DROP ", "MERGE ", "TRUNCATE " })
            Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("ALTER TABLE db_owner.SalesOrders", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE db_owner.PurchaseOrders", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE db_owner.ContainerLoadingLists", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE db_owner.TradeDocuments", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FileNote", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 前端接线_登记册入口存在且界面不拼链接不自行抓取()
    {
        var jsPath = RepoFile("src", "ERP.Api", "wwwroot", "js", "document-attachment-references.js");
        Assert.True(File.Exists(jsPath), "附件引用前端脚本不存在：" + jsPath);
        var js = File.ReadAllText(jsPath);

        Assert.Contains("openDocumentAttachmentReferences", js, StringComparison.Ordinal);
        Assert.Contains("openDocumentAttachmentReferencesForCurrentModule", js, StringComparison.Ordinal);
        Assert.Contains("darEsc", js, StringComparison.Ordinal);
        Assert.Contains("darSubmitVoid", js, StringComparison.Ordinal);
        Assert.Contains("不上传", js, StringComparison.Ordinal);

        // 界面只显示元数据：不生成链接、不嵌入标记、不自行抓取任何远端内容
        Assert.DoesNotContain("href=", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fetch(", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", js, StringComparison.OrdinalIgnoreCase);

        var indexHtml = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/document-attachment-references.js", indexHtml, StringComparison.Ordinal);

        foreach (var moduleFile in new[] { "modules.js", "modules-doc.js", "modules-doc2.js" })
        {
            var text = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", moduleFile));
            Assert.Contains("openDocumentAttachmentReferences", text, StringComparison.Ordinal);
        }

        // 既有 FileNote 字段仍作为普通台账字段保留（未被改造为附件引用）
        var modulesJs = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules.js"));
        Assert.Contains("fileNote", modulesJs, StringComparison.Ordinal);
    }
}
