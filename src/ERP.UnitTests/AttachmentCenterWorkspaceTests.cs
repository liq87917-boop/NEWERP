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
/// 附件中心工作台单元测试（ERP-064）：在 ERP-061 / ERP-062 / ERP-063 交付的**同一**附件证据册之上
/// 验证一个**有界且按既有「角色 → 菜单」授权收敛**的只读工作台。
/// 覆盖：授权可见范围（无身份 / 无角色 / 无菜单 → fail closed）、未授权归属类型的存在性 / 计数 /
/// 文件名 / 摘要一律不披露、显式未授权筛选与详情 / 下载 fail closed、权限撤销后立即收敛、
/// 显式字段筛选（归属类型 / 归属 Id / 归属号码快照 / 文件名快照 / 媒体类型 / 上传人 / 登记日期区间 /
/// 状态）与分页有界、有效与已作废分开标注、归属软删除后历史仍可只读查看但不提供下载、
/// 列表 / 摘要零存储访问与固定数量数据集访问（无逐行查库）、工作台只读（源记录与相邻记录非变更）、
/// 控制器契约（类级授权 + 只读 GET + 安全下载响应头）、前端接线契约与纯规则文案。
/// <para>全部使用内存库（<c>TestDbFactory</c>）与**测试内**的内存内容存储；不连接 SQL Server、
/// 不执行任何 SQL / 部署脚本、不访问生产 OSS、不做任何浏览器 / UI 验收（浏览器验收按项目策略
/// 延后到 FINAL-UI-ACCEPTANCE）。</para>
/// </summary>
public class AttachmentCenterWorkspaceTests
{
    // ==================== 0. 测试脚手架 ====================

    /// <summary>工作台测试账号 Id（授权由 <see cref="SeedAuthorization"/> 显式播种）</summary>
    private const long CenterUserId = 7001;

    private static byte[] PdfBytes(string body = "abc") => Encoding.UTF8.GetBytes("%PDF-1.4\n" + body);

    private static byte[] PngBytes()
        => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };

    /// <summary>播种「角色 → 菜单」授权（既有口径）：只授予传入的菜单编码；空数组 = 有角色但无菜单</summary>
    private static SysRole SeedAuthorization(ErpDbContext db, long userId, params string[] menuCodes)
    {
        var role = new SysRole
        {
            RoleName = $"附件中心测试角色 {userId}",
            RoleCode = $"AC-{userId}",
            Description = "ERP-064 授权测试",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = role.Id });
        db.SaveChanges();

        foreach (var code in menuCodes)
        {
            var menu = new SysMenu
            {
                ParentId = 0,
                MenuName = $"测试菜单 {code}",
                MenuCode = code,
                Path = $"/{code}",
                Icon = "test",
                SortOrder = 1,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        return role;
    }

    /// <summary>撤销该账号的全部菜单授权（模拟「权限变更 / 被回收」，角色与用户关系保持不变）</summary>
    private static void RevokeAllMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    /// <summary>恢复此前被撤销的菜单授权（验证可见范围随授权恢复）</summary>
    private static void RestoreAllMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId)).ToList())
            grant.IsDeleted = false;
        db.SaveChanges();
    }

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string orderNo, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = 501,
            Currency = Currency.USD,
            TotalAmount = 12345.67m,
            DepositAmount = 2345.67m,
            ShippingMarks = "MARKS-064",
            Remark = "销售订单备注",
            Status = DocumentStatus.Approved,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static PurchaseOrder SeedPurchaseOrder(ErpDbContext db, string orderNo)
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
            Status = DocumentStatus.Approved
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static TradeDocument SeedTradeDocument(ErpDbContext db, string docNo)
    {
        var document = new TradeDocument
        {
            DocNo = docNo,
            DocType = "装箱单",
            IssueDate = new DateTime(2026, 9, 9),
            Amount = 1000m,
            Currency = "USD",
            Status = "待制作",
            FileNote = "附件说明 / 存放位置（历史文本：不解析、不抓取、不回填）",
            Remark = "单证备注"
        };
        db.TradeDocuments.Add(document);
        db.SaveChanges();
        return document;
    }

    private static Sample SeedSample(ErpDbContext db, string sampleNo)
    {
        var sample = new Sample
        {
            SampleNo = sampleNo,
            SampleDate = new DateTime(2026, 9, 10),
            CustomerId = 501,
            CustomerName = "测试客户",
            ProductName = "测试样品",
            SampleType = "寄样",
            Quantity = 2m,
            Unit = "件",
            SampleFee = 120m,
            Currency = "CNY",
            Result = "待反馈",
            Remark = "样品备注"
        };
        db.Samples.Add(sample);
        db.SaveChanges();
        return sample;
    }

    /// <summary>播种相邻业务记录（库存 / 库存流水 / 费用单 / ERP-045 引用册），用于非变更断言</summary>
    private static void SeedAdjacentRecords(ErpDbContext db)
    {
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
            SourceDocNo = "RK-064",
            WarehouseId = 1,
            WarehouseName = "主仓"
        });
        db.FinanceExpenses.Add(new FinanceExpense
        {
            ExpenseNo = "FY-064",
            ExpenseDate = new DateTime(2026, 9, 4),
            ExpenseType = "报关费",
            Amount = 320m,
            Currency = "CNY",
            AmountCny = 320m,
            PaymentStatus = "未付",
            AllocatedAmount = 160m,
            Remark = "费用单备注"
        });
        db.DocumentAttachmentReferences.Add(new DocumentAttachmentReference
        {
            ParentType = DocumentAttachmentReferenceRules.ParentTypeSalesOrder,
            ParentId = 1,
            ParentNo = "SO-064",
            ParentTypeText = "销售订单",
            Category = DocumentAttachmentReferenceRules.CategoryContract,
            DisplayName = "ERP-045 既有引用",
            ReferenceId = "ref-keep-064",
            Status = DocumentAttachmentReferenceRules.StatusActive
        });
        db.SaveChanges();
    }

    /// <summary>证据 + 父单据 + 相邻记录的快照（断言工作台只读：只读视图绝不改写任何记录）</summary>
    private static string Snapshot(ErpDbContext db)
    {
        var parts = new List<string>();

        parts.AddRange(db.AttachmentEvidences.AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new
            {
                r.Id, r.OwnerType, r.OwnerId, r.OwnerNo, r.OriginalFileName, r.MediaType, r.SizeBytes,
                r.Sha256, r.Description, r.StorageKey, r.StorageProvider, r.UploadedBy, r.Status,
                r.VoidedAt, r.VoidReason, r.IsDeleted, r.RecordedAt, r.UpdatedAt
            }).ToList()
            .Select(r => System.Text.Json.JsonSerializer.Serialize(r)));
        parts.AddRange(db.SalesOrders.AsNoTracking().OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.OrderNo, o.Status, o.TotalAmount, o.Remark, o.IsDeleted }).ToList()
            .Select(o => System.Text.Json.JsonSerializer.Serialize(o)));
        parts.AddRange(db.PurchaseOrders.AsNoTracking().OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.OrderNo, o.Status, o.QcStatus, o.ArrivalProgress, o.IsDeleted }).ToList()
            .Select(o => System.Text.Json.JsonSerializer.Serialize(o)));
        parts.AddRange(db.TradeDocuments.AsNoTracking().OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.DocNo, d.Status, d.FileNote, d.IsDeleted }).ToList()
            .Select(d => System.Text.Json.JsonSerializer.Serialize(d)));
        parts.AddRange(db.Samples.AsNoTracking().OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.SampleNo, s.Result, s.FeeSettled, s.IsDeleted }).ToList()
            .Select(s => System.Text.Json.JsonSerializer.Serialize(s)));
        parts.AddRange(db.Stocks.AsNoTracking().OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.ProductId, s.Quantity, s.LockedQuantity }).ToList()
            .Select(s => System.Text.Json.JsonSerializer.Serialize(s)));
        parts.AddRange(db.StockMovements.AsNoTracking().OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.MovementType, m.SourceDocNo, m.WarehouseName }).ToList()
            .Select(m => System.Text.Json.JsonSerializer.Serialize(m)));
        parts.AddRange(db.FinanceExpenses.AsNoTracking().OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.ExpenseNo, e.Amount, e.AllocatedAmount, e.PaymentStatus }).ToList()
            .Select(e => System.Text.Json.JsonSerializer.Serialize(e)));
        parts.AddRange(db.DocumentAttachmentReferences.AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.ParentNo, r.DisplayName, r.ReferenceId, r.Status }).ToList()
            .Select(r => System.Text.Json.JsonSerializer.Serialize(r)));

        return string.Join("|", parts);
    }

    /// <summary>上传成功路径（归属类型 + 归属 Id 必须显式给出；断言服务端权威字段已写入）</summary>
    private static async Task<AttachmentEvidenceDto> UploadOkAsync(
        ErpDbContext db,
        IAttachmentContentStore store,
        byte[] content,
        string ownerType,
        long ownerId,
        string fileName = "附件证据.pdf",
        string declaredContentType = AttachmentEvidenceRules.MediaPdf,
        string description = "",
        string uploadedBy = "上传人甲")
    {
        var saved = await AttachmentEvidenceService.UploadAsync(
            db,
            store,
            new AttachmentEvidenceUploadRequest
            {
                OwnerType = ownerType,
                OwnerId = ownerId,
                FileName = fileName,
                DeclaredContentType = declaredContentType,
                DeclaredLength = content.LongLength,
                Description = description,
                Content = new MemoryStream(content, writable: false)
            },
            uploadedBy: uploadedBy,
            uploadedById: 7);

        Assert.Equal(AttachmentEvidenceRules.StatusActive, saved.Status);
        Assert.Equal(AttachmentEvidenceRules.Sha256Length, saved.Sha256.Length);
        return saved;
    }

    /// <summary>直接落一条证据（用于需要显式登记时间 / 状态的历史场景；字段口径与服务端写入一致）</summary>
    private static AttachmentEvidence SeedEvidence(
        ErpDbContext db,
        string ownerType,
        long ownerId,
        string ownerNo,
        string fileName,
        string mediaType,
        DateTime recordedAt,
        int status = AttachmentEvidenceRules.StatusActive,
        string uploadedBy = "上传人甲",
        string? sha256 = null)
    {
        var row = new AttachmentEvidence
        {
            OwnerType = ownerType,
            OwnerId = ownerId,
            OwnerNo = ownerNo,
            OwnerTypeText = AttachmentEvidenceRules.OwnerTypeText(ownerType),
            OriginalFileName = fileName,
            MediaType = mediaType,
            SizeBytes = 16,
            Sha256 = sha256 ?? new string('a', 64),
            StorageKey = $"{recordedAt:yyyyMMdd}/{Guid.NewGuid():N}.pdf",
            StorageProvider = AttachmentEvidenceRules.ProviderIsolatedLocal,
            UploadedBy = uploadedBy,
            RecordedAt = recordedAt,
            Status = status,
            CreatedAt = recordedAt
        };
        db.AttachmentEvidences.Add(row);
        db.SaveChanges();
        return row;
    }

    private static AttachmentEvidenceCenterQuery CenterQuery(
        string? ownerType = null, long? ownerId = null, string? ownerNo = null, string? fileName = null,
        string? mediaType = null, string? uploadedBy = null, DateTime? from = null, DateTime? to = null,
        int? status = null, int page = 1, int pageSize = AttachmentEvidenceCenterQuery.DefaultPageSize)
        => new()
        {
            OwnerType = ownerType,
            OwnerId = ownerId,
            OwnerNo = ownerNo,
            FileName = fileName,
            MediaType = mediaType,
            UploadedBy = uploadedBy,
            RecordedFrom = from,
            RecordedTo = to,
            Status = status,
            Page = page,
            PageSize = pageSize
        };

    /// <summary>断言业务异常的错误码（避免只断言消息文案）</summary>
    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    /// <summary>构造控制器（下载用例需要 HttpContext 才能写入防御性响应头）</summary>
    private static AttachmentEvidenceController BuildController(ErpDbContext db, IAttachmentContentStore store)
        => new(db, store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    // ==================== 1. 授权可见范围：未授权归属类型一律不披露 ====================

    [Fact]
    public async Task 授权范围内只列已授权归属类型_未授权类型的记录计数文件名与摘要一律不披露()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, CenterUserId,
            AttachmentEvidenceRules.MenuCodeSalesOrder, AttachmentEvidenceRules.MenuCodeTradeDocument);

        var salesOrder = SeedSalesOrder(db, "SO-064-A");
        var purchaseOrder = SeedPurchaseOrder(db, "PO-064-A");
        var document = SeedTradeDocument(db, "DOC-064-A");
        var sample = SeedSample(db, "SP-064-A");
        var store = new InMemoryAttachmentContentStore();

        var visibleSales = await UploadOkAsync(db, store, PdfBytes("s"),
            AttachmentEvidenceRules.OwnerTypeSalesOrder, salesOrder.Id, fileName: "销售证据.pdf");
        var visibleDoc = await UploadOkAsync(db, store, PngBytes(),
            AttachmentEvidenceRules.OwnerTypeTradeDocument, document.Id,
            fileName: "单证扫描件.png", declaredContentType: AttachmentEvidenceRules.MediaPng);
        var hiddenPurchase = await UploadOkAsync(db, store, PdfBytes("p"),
            AttachmentEvidenceRules.OwnerTypePurchaseOrder, purchaseOrder.Id, fileName: "采购证据.pdf");
        var hiddenInspection = await UploadOkAsync(db, store, PdfBytes("q"),
            AttachmentEvidenceRules.OwnerTypeQualityInspection, purchaseOrder.Id, fileName: "验货证据.pdf");
        var hiddenSample = await UploadOkAsync(db, store, PdfBytes("m"),
            AttachmentEvidenceRules.OwnerTypeSample, sample.Id, fileName: "样品证据.pdf");

        // 授权映射：只映射到既有菜单编码；验货记录与采购订单共用采购订单菜单
        var authorized = await AttachmentEvidenceService.LoadAuthorizedOwnerTypesAsync(db, CenterUserId);
        Assert.Equal(
            new[] { AttachmentEvidenceRules.OwnerTypeSalesOrder, AttachmentEvidenceRules.OwnerTypeTradeDocument },
            authorized.ToArray());

        // 摘要：只统计授权类型；未授权类型连计数行都没有
        var summary = await AttachmentEvidenceService.GetCenterSummaryAsync(db, CenterUserId, "工作台用户");
        Assert.True(summary.Scope.HasAnyAuthorizedOwnerType);
        Assert.Equal(2, summary.TotalCount);
        Assert.Equal(2, summary.ActiveCount);
        Assert.Equal(0, summary.VoidedCount);
        Assert.Equal(2, summary.OwnerTypeCounts.Count);
        Assert.Equal(new[] { "销售订单", "出口单证" }, summary.OwnerTypeCounts.Select(c => c.OwnerTypeText).ToArray());
        Assert.Equal(2, summary.OwnerTypeOptions.Count);
        Assert.Equal(2, summary.Scope.OwnerTypes.Count(o => o.Authorized));
        Assert.Equal(3, summary.Scope.OwnerTypes.Count(o => !o.Authorized));
        Assert.Contains("销售订单", summary.Scope.ScopeText);
        Assert.Contains("另有 3 类归属未授权", summary.Scope.ScopeText);
        Assert.Contains("不披露不可访问记录的存在性", summary.Scope.UnauthorizedNoticeText);
        Assert.All(summary.Scope.OwnerTypes.Where(o => !o.Authorized),
            scope => Assert.Contains("一律不显示", scope.AuthorizationText));

        // 未授权类型的文件名 / 摘要 / 编号快照绝不出现（摘要里连字段都没有）
        var summaryJson = System.Text.Json.JsonSerializer.Serialize(summary);
        foreach (var hidden in new[] { hiddenPurchase, hiddenInspection, hiddenSample })
        {
            Assert.DoesNotContain(hidden.OriginalFileName, summaryJson);
            Assert.DoesNotContain(hidden.Sha256, summaryJson);
            Assert.DoesNotContain(hidden.OwnerNo, summaryJson);
        }

        // 台账：默认只列已授权类型，且不包含任何未授权记录
        var page = await AttachmentEvidenceService.ListForCenterAsync(db, null, CenterUserId);
        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, row => Assert.Contains(row.OwnerType,
            new[] { AttachmentEvidenceRules.OwnerTypeSalesOrder, AttachmentEvidenceRules.OwnerTypeTradeDocument }));
        Assert.All(page.Items, row => Assert.True(row.IsActive));
        Assert.Contains(page.Items, row => row.Id == visibleSales.Id);
        Assert.Contains(page.Items, row => row.Id == visibleDoc.Id);
        Assert.DoesNotContain(page.Items, row =>
            row.Id == hiddenPurchase.Id || row.Id == hiddenInspection.Id || row.Id == hiddenSample.Id);

        // 显式未授权类型筛选：按「无可见记录」返回空页（不抛错、不披露存在性）
        var filtered = await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(ownerType: AttachmentEvidenceRules.OwnerTypeSample), CenterUserId);
        Assert.Equal(0, filtered.Total);
        Assert.Empty(filtered.Items);

        // 详情 / 下载：未授权一律 fail closed，且消息里不出现编号快照、文件名、摘要或证据 Id
        foreach (var hidden in new[] { hiddenPurchase, hiddenInspection, hiddenSample })
        {
            var failure = await AssertBusinessAsync(ErrorCodes.NotFound, () =>
                AttachmentEvidenceService.GetForCenterAsync(db, hidden.Id, CenterUserId));
            Assert.DoesNotContain(hidden.OriginalFileName, failure.Message);
            Assert.DoesNotContain(hidden.Sha256, failure.Message);
            Assert.DoesNotContain(hidden.OwnerNo, failure.Message);
            Assert.DoesNotContain(hidden.Id.ToString(), failure.Message);

            await AssertBusinessAsync(ErrorCodes.NotFound, () =>
                AttachmentEvidenceService.OpenCenterContentAsync(db, store, hidden.Id, CenterUserId));
        }

        // 已授权类型仍可正常查看元数据与发起下载
        var visibleDetail = await AttachmentEvidenceService.GetForCenterAsync(db, visibleSales.Id, CenterUserId);
        Assert.Equal("销售订单", visibleDetail.OwnerTypeText);
        Assert.True(visibleDetail.ContentDownloadable);

        // 列表 / 摘要 / 详情都不访问存储：只有 5 次上传落盘
        Assert.Equal(5, store.SaveCalls);
        Assert.Equal(5, store.TotalCalls);
    }

    // ==================== 2. 显式字段筛选与分页有界 ====================

    [Fact]
    public async Task 工作台按显式元数据筛选_归属Id与号码与文件名与媒体类型与上传人与登记日期区间与状态()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, CenterUserId,
            AttachmentEvidenceRules.MenuCodeSalesOrder,
            AttachmentEvidenceRules.MenuCodePurchaseOrder,
            AttachmentEvidenceRules.MenuCodeTradeDocument,
            AttachmentEvidenceRules.MenuCodeSample);

        var orderA = SeedSalesOrder(db, "SO-064-F");
        var orderB = SeedSalesOrder(db, "SO-064-G");
        var purchaseOrder = SeedPurchaseOrder(db, "PO-064-F");
        var document = SeedTradeDocument(db, "DOC-064-F");
        var sample = SeedSample(db, "SP-064-F");

        var day1 = new DateTime(2026, 9, 1, 9, 0, 0);
        var day2 = new DateTime(2026, 9, 2, 9, 0, 0);
        var day3 = new DateTime(2026, 9, 3, 9, 0, 0);

        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeSalesOrder, orderA.Id, orderA.OrderNo,
            "合同扫描.pdf", AttachmentEvidenceRules.MediaPdf, day1, uploadedBy: "甲");
        var png = SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeSalesOrder, orderA.Id, orderA.OrderNo,
            "装箱照片.png", AttachmentEvidenceRules.MediaPng, day2, uploadedBy: "乙");
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeSalesOrder, orderB.Id, orderB.OrderNo,
            "报价单.pdf", AttachmentEvidenceRules.MediaPdf, day3, uploadedBy: "甲");
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypePurchaseOrder, purchaseOrder.Id, purchaseOrder.OrderNo,
            "采购合同.pdf", AttachmentEvidenceRules.MediaPdf, day2, uploadedBy: "甲");
        var voided = SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeTradeDocument, document.Id, document.DocNo,
            "单证扫描件.pdf", AttachmentEvidenceRules.MediaPdf, day3,
            status: AttachmentEvidenceRules.StatusVoided, uploadedBy: "乙");
        SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeSample, sample.Id, sample.SampleNo,
            "样品确认.pdf", AttachmentEvidenceRules.MediaPdf, day3, uploadedBy: "甲");

        // 无筛选：授权范围内全部 6 条
        var all = await AttachmentEvidenceService.ListForCenterAsync(db, null, CenterUserId);
        Assert.Equal(6, all.Total);

        // 归属类型 / 归属 Id / 归属号码快照 / 文件名快照 / 媒体类型 / 上传人 / 登记日期区间
        Assert.Equal(3, (await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(ownerType: AttachmentEvidenceRules.OwnerTypeSalesOrder), CenterUserId)).Total);
        Assert.Equal(2, (await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(ownerType: AttachmentEvidenceRules.OwnerTypeSalesOrder, ownerId: orderA.Id),
            CenterUserId)).Total);

        // 归属 Id 是**各归属类型内**的持久化 Id（不同表可能取值相同），因此单用 Id 会命中跨类型的同一 Id；
        // 只有与归属类型配合时才精确到某一单据（与 ERP-061 台账口径一致，绝不按 Id 猜测类型）
        Assert.Equal(5, (await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(ownerId: orderA.Id), CenterUserId)).Total);
        Assert.Equal(1, (await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(ownerNo: "SO-064-G"), CenterUserId)).Total);
        Assert.Equal(2, (await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(fileName: "合同"), CenterUserId)).Total);
        Assert.Single((await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(mediaType: AttachmentEvidenceRules.MediaPng), CenterUserId)).Items);
        Assert.Single((await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(mediaType: "IMAGE/PNG"), CenterUserId)).Items);   // 媒体类型大小写不敏感
        Assert.Equal(2, (await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(uploadedBy: "乙"), CenterUserId)).Total);
        Assert.Equal(5, (await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(from: day2), CenterUserId)).Total);
        Assert.Equal(2, (await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(from: day2, to: day2), CenterUserId)).Total);
        Assert.Equal(1, (await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(from: day1, to: day1), CenterUserId)).Total);

        // 有效 / 已作废分开标注：状态筛选只命中已作废，且作废留痕可读
        var voidedOnly = await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(status: AttachmentEvidenceRules.StatusVoided), CenterUserId);
        Assert.Single(voidedOnly.Items);
        Assert.Equal(voided.Id, voidedOnly.Items[0].Id);
        Assert.True(voidedOnly.Items[0].IsVoided);
        Assert.Equal("已作废", voidedOnly.Items[0].StatusText);
        Assert.Equal(5, (await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(status: AttachmentEvidenceRules.StatusActive), CenterUserId)).Total);

        // 组合筛选：媒体类型 + 上传人 + 日期区间
        var combined = await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(mediaType: AttachmentEvidenceRules.MediaPng, uploadedBy: "乙", from: day2, to: day2),
            CenterUserId);
        Assert.Single(combined.Items);
        Assert.Equal(png.Id, combined.Items[0].Id);

        // 分页有界：每页上限截断、翻页收敛、越界页返回空集但合计不变
        var capped = await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(pageSize: 5000), CenterUserId);
        Assert.Equal(AttachmentEvidenceCenterQuery.MaxPageSize, capped.PageSize);
        Assert.Equal(6, capped.Total);

        var page2 = await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(page: 2, pageSize: 2), CenterUserId);
        Assert.Equal(6, page2.Total);
        Assert.Equal(2, page2.Page);
        Assert.Equal(2, page2.PageSize);
        Assert.Equal(2, page2.Items.Count);

        var beyond = await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(page: 99, pageSize: 2), CenterUserId);
        Assert.Equal(6, beyond.Total);
        Assert.Empty(beyond.Items);
    }

    [Fact]
    public async Task 未知或越界的筛选取值一律拒绝_不静默截断也不做跨记录模糊匹配()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, CenterUserId, AttachmentEvidenceRules.MenuCodeSalesOrder);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(ownerType: "TradeDocuments"), CenterUserId));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(mediaType: "text/html"), CenterUserId));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(ownerId: 0), CenterUserId));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(status: 7), CenterUserId));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(from: new DateTime(2026, 9, 3), to: new DateTime(2026, 9, 1)), CenterUserId));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(fileName: new string('f', AttachmentEvidenceRules.MaxOriginalFileNameLength + 1)),
            CenterUserId));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(uploadedBy: new string('u', AttachmentEvidenceRules.MaxUploadedByLength + 1)),
            CenterUserId));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(ownerNo: new string('o', AttachmentEvidenceRules.MaxOwnerNoLength + 1)), CenterUserId));

        // 工作台台账不接受摘要筛选（本视图刻意不提供「按摘要猜内容」的入口）
        Assert.DoesNotContain(
            typeof(AttachmentEvidenceCenterQuery).GetProperties().Select(p => p.Name),
            name => name.Contains("Sha256", StringComparison.OrdinalIgnoreCase));
    }

    // ==================== 3. 权限变更：撤销授权后立即 fail closed ====================

    [Fact]
    public async Task 权限变更_撤销菜单授权后记录计数详情与下载立即fail_closed_恢复授权后可见()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, CenterUserId, AttachmentEvidenceRules.MenuCodeSalesOrder);

        var order = SeedSalesOrder(db, "SO-064-P");
        var store = new InMemoryAttachmentContentStore();
        var saved = await UploadOkAsync(db, store, PdfBytes("p"), AttachmentEvidenceRules.OwnerTypeSalesOrder,
            order.Id, fileName: "权限变更证据.pdf");

        // 授权在：可见、可看元数据、可下载
        Assert.Equal(1, (await AttachmentEvidenceService.ListForCenterAsync(db, null, CenterUserId)).Total);
        Assert.Equal(1, (await AttachmentEvidenceService.GetCenterSummaryAsync(db, CenterUserId, "工作台用户")).TotalCount);
        var detail = await AttachmentEvidenceService.GetForCenterAsync(db, saved.Id, CenterUserId);
        Assert.True(detail.ContentDownloadable);

        var content = await AttachmentEvidenceService.OpenCenterContentAsync(db, store, saved.Id, CenterUserId);
        await using (content.Content)
        {
            using var buffer = new MemoryStream();
            await content.Content.CopyToAsync(buffer);
            Assert.Equal(PdfBytes("p"), buffer.ToArray());
        }
        Assert.Equal(1, store.OpenCalls);

        // 撤销菜单授权：记录 / 计数立即消失，详情与下载 fail closed（不披露存在性）
        RevokeAllMenus(db, CenterUserId);
        Assert.Empty(await AttachmentEvidenceService.LoadAuthorizedOwnerTypesAsync(db, CenterUserId));

        var revokedList = await AttachmentEvidenceService.ListForCenterAsync(db, null, CenterUserId);
        Assert.Equal(0, revokedList.Total);
        Assert.Empty(revokedList.Items);

        var revokedSummary = await AttachmentEvidenceService.GetCenterSummaryAsync(db, CenterUserId, "工作台用户");
        Assert.False(revokedSummary.Scope.HasAnyAuthorizedOwnerType);
        Assert.Equal(0, revokedSummary.TotalCount);
        Assert.Empty(revokedSummary.OwnerTypeCounts);
        Assert.Contains("fail closed", revokedSummary.Scope.ScopeText);

        var denied = await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            AttachmentEvidenceService.GetForCenterAsync(db, saved.Id, CenterUserId));
        Assert.DoesNotContain(saved.OriginalFileName, denied.Message);
        Assert.DoesNotContain(saved.Sha256, denied.Message);

        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            AttachmentEvidenceService.OpenCenterContentAsync(db, store, saved.Id, CenterUserId));
        Assert.Equal(1, store.OpenCalls);   // 下载被拒绝：没有读取任何内容

        // 恢复授权：可见性随既有「角色 → 菜单」授权立即恢复（不改写任何证据）
        RestoreAllMenus(db, CenterUserId);
        Assert.Equal(1, (await AttachmentEvidenceService.ListForCenterAsync(db, null, CenterUserId)).Total);
        Assert.Equal(saved.Id, (await AttachmentEvidenceService.GetForCenterAsync(db, saved.Id, CenterUserId)).Id);
    }

    // ==================== 4. 无身份 / 无角色 / 无授权菜单一律 fail closed ====================

    [Fact]
    public async Task 无身份或无角色或无授权菜单的账号一律fail_closed_不显示任何记录与计数()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-064-N");
        var store = new InMemoryAttachmentContentStore();
        var saved = await UploadOkAsync(db, store, PdfBytes("n"),
            AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id);

        // 7002：有角色但没有任何菜单授权；7003：连角色都没有
        SeedAuthorization(db, 7002);

        foreach (long? userId in new long?[] { null, 0, 7002, 7003 })
        {
            Assert.Empty(await AttachmentEvidenceService.LoadAuthorizedOwnerTypesAsync(db, userId));

            var page = await AttachmentEvidenceService.ListForCenterAsync(db, null, userId);
            Assert.Equal(0, page.Total);
            Assert.Empty(page.Items);
            Assert.Equal(AttachmentEvidenceCenterQuery.DefaultPageSize, page.PageSize);

            var summary = await AttachmentEvidenceService.GetCenterSummaryAsync(db, userId, null);
            Assert.False(summary.Scope.HasAnyAuthorizedOwnerType);
            Assert.Equal(0, summary.TotalCount);
            Assert.Empty(summary.OwnerTypeCounts);
            Assert.Empty(summary.OwnerTypeOptions);
            Assert.Contains("fail closed", summary.Scope.ScopeText);
            Assert.Equal(AttachmentEvidenceRules.UnknownText, summary.Scope.UserName);

            // 显式筛选已授权类型：白名单取值 → 空页（不抛错）；未知取值 → 参数错误
            Assert.Equal(0, (await AttachmentEvidenceService.ListForCenterAsync(
                db, CenterQuery(ownerType: AttachmentEvidenceRules.OwnerTypeSalesOrder), userId)).Total);
            await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => AttachmentEvidenceService.ListForCenterAsync(
                db, CenterQuery(ownerType: "SalesOrders"), userId));

            // 详情与下载：未授权一律按「不存在」处理
            await AssertBusinessAsync(ErrorCodes.NotFound, () =>
                AttachmentEvidenceService.GetForCenterAsync(db, saved.Id, userId));
            await AssertBusinessAsync(ErrorCodes.NotFound, () =>
                AttachmentEvidenceService.OpenCenterContentAsync(db, store, saved.Id, userId));
        }

        // 只授权「样品记录」时，销售订单筛选同样空页（授权集合非空但筛选类型不在其中）
        SeedAuthorization(db, 7004, AttachmentEvidenceRules.MenuCodeSample);
        var filtered = await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(ownerType: AttachmentEvidenceRules.OwnerTypeSalesOrder), 7004);
        Assert.Equal(0, filtered.Total);
        Assert.Empty(filtered.Items);

        var sampleScope = await AttachmentEvidenceService.GetCenterSummaryAsync(db, 7004, "样品用户");
        Assert.True(sampleScope.Scope.HasAnyAuthorizedOwnerType);
        Assert.Single(sampleScope.OwnerTypeCounts);
        Assert.Equal(AttachmentEvidenceRules.OwnerTypeSample, sampleScope.OwnerTypeCounts[0].OwnerType);
        Assert.Equal("样品用户", sampleScope.Scope.UserName);

        // 仓库里确有记录，但上述账号一条都看不到（fail closed 而不是「没有记录」）
        Assert.Equal(1, db.AttachmentEvidences.AsNoTracking().Count(r => !r.IsDeleted));
    }

    // ==================== 5. 归属软删除：历史可见（标注不可用）+ 下载 fail closed ====================

    [Fact]
    public async Task 归属软删除后历史证据仍对授权查看者可见并标注不可用_下载fail_closed且不改派()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, CenterUserId, AttachmentEvidenceRules.MenuCodeSalesOrder);
        SeedAdjacentRecords(db);

        var order = SeedSalesOrder(db, "SO-064-D");
        var store = new InMemoryAttachmentContentStore();
        var saved = await UploadOkAsync(db, store, PdfBytes("d"),
            AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id);

        order.IsDeleted = true;
        db.SaveChanges();

        var page = await AttachmentEvidenceService.ListForCenterAsync(db, null, CenterUserId);
        Assert.Equal(1, page.Total);
        var row = Assert.Single(page.Items);
        Assert.False(row.OwnerAvailable);
        Assert.False(row.ContentDownloadable);
        Assert.Contains("已不存在或已删除", row.OwnerAvailabilityText);
        Assert.Equal("销售订单 SO-064-D", row.OwnerSnapshotText);        // 号码快照按登记当时口径可读

        var detail = await AttachmentEvidenceService.GetForCenterAsync(db, saved.Id, CenterUserId);
        Assert.False(detail.OwnerAvailable);
        Assert.False(detail.IsVoided);
        Assert.Equal(saved.OriginalFileName, detail.OriginalFileName);
        Assert.Equal(saved.Sha256, detail.Sha256);

        // 下载 fail closed（归属已删除 → 拒绝；不改派、不静默修复）
        var failure = await AssertBusinessAsync(ErrorCodes.RuleConflict, () =>
            AttachmentEvidenceService.OpenCenterContentAsync(db, store, saved.Id, CenterUserId));
        Assert.Contains("已不存在或已删除", failure.Message);
        Assert.Equal(0, store.OpenCalls);

        // 历史仍可见：授权范围内计数照实统计（不把不可用当成无效删除）
        var summary = await AttachmentEvidenceService.GetCenterSummaryAsync(db, CenterUserId, "工作台用户");
        Assert.Equal(1, summary.TotalCount);
        Assert.Equal(1, summary.ActiveCount);
        Assert.Equal(0, summary.VoidedCount);

        // 证据本身没有被改写（仍为有效、无作废留痕）
        var reloaded = db.AttachmentEvidences.AsNoTracking().Single(r => r.Id == saved.Id);
        Assert.Equal(AttachmentEvidenceRules.StatusActive, reloaded.Status);
        Assert.Empty(reloaded.VoidReason ?? string.Empty);
        Assert.Equal(saved.Sha256, reloaded.Sha256);
    }

    // ==================== 6. 有界查询：零存储访问 + 固定数量数据集访问 + 显式下载 ====================

    [Fact]
    public async Task 列表与摘要零存储访问且数据集访问次数固定_无逐行查库且只读不写库()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, CenterUserId, AttachmentEvidenceRules.MenuCodeSalesOrder);
        var order = SeedSalesOrder(db, "SO-064-C");
        var store = new InMemoryAttachmentContentStore();
        var saved = await UploadOkAsync(db, store, PdfBytes("c"),
            AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id);
        var uploadCalls = store.TotalCalls;
        Assert.Equal(1, uploadCalls);

        var counting = CountingDbContext.Wrap(db);

        var list = await AttachmentEvidenceService.ListForCenterAsync(
            counting.Proxy, CenterQuery(pageSize: 1), CenterUserId);
        var summary = await AttachmentEvidenceService.GetCenterSummaryAsync(counting.Proxy, CenterUserId, "工作台用户");
        var smallReads = counting.DatasetReads;

        Assert.Equal(1, list.Total);
        Assert.Single(list.Items);
        Assert.Equal(1, summary.TotalCount);

        // 数据集访问固定：授权（角色 / 角色菜单 / 菜单）+ 证据表（计数与本页共用同一次数据集访问）
        // + 归属（批量可用性）；台账与摘要都不逐行查库，且工作台全程只读（不落库）
        Assert.Equal(
            new[]
            {
                nameof(IErpDbContext.SysUserRoles),
                nameof(IErpDbContext.SysRoleMenus),
                nameof(IErpDbContext.SysMenus),
                nameof(IErpDbContext.AttachmentEvidences),
                nameof(IErpDbContext.SalesOrders)
            },
            counting.ReadProperties.Distinct().ToArray());
        Assert.Equal(0, counting.WriteCalls);
        Assert.Equal(uploadCalls, store.TotalCalls);          // 列表 / 摘要零存储访问

        // 再补 300 条（跨多页）：单次台账的数据集访问次数必须保持不变（无逐行查库）
        for (var i = 0; i < 300; i++)
        {
            SeedEvidence(db, AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id, order.OrderNo,
                $"批量证据{i}.pdf", AttachmentEvidenceRules.MediaPdf, DateTime.Now.AddMinutes(-i));
        }

        var large = await AttachmentEvidenceService.ListForCenterAsync(
            counting.Proxy, CenterQuery(pageSize: 200), CenterUserId);
        var largeReads = counting.DatasetReads - smallReads;

        Assert.Equal(301, large.Total);
        Assert.Equal(AttachmentEvidenceCenterQuery.MaxPageSize, large.Items.Count);   // 单页有界（上限 200）
        Assert.Equal(5, largeReads);                                                  // 固定 5 次数据集访问
        Assert.Equal(0, counting.WriteCalls);
        Assert.Equal(uploadCalls, store.TotalCalls);

        // 内容只在显式下载时读取：列表 / 摘要 / 详情调用之后存储访问次数仍为 0
        await AttachmentEvidenceService.GetForCenterAsync(counting.Proxy, saved.Id, CenterUserId);
        Assert.Equal(uploadCalls, store.TotalCalls);

        var content = await AttachmentEvidenceService.OpenCenterContentAsync(
            counting.Proxy, store, saved.Id, CenterUserId);
        await using (content.Content)
        {
            using var buffer = new MemoryStream();
            await content.Content.CopyToAsync(buffer);
            Assert.Equal(PdfBytes("c"), buffer.ToArray());
        }

        Assert.Equal(1, store.OpenCalls);                     // 仅这一次显式下载读取了内容
        Assert.Equal(uploadCalls + 1, store.TotalCalls);
    }

    // ==================== 7. 工作台只读：不改写证据 / 父单据 / 相邻记录 ====================

    [Fact]
    public async Task 工作台是只读的_不改写证据父单据与相邻记录且已作废历史照常可见()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, CenterUserId,
            AttachmentEvidenceRules.MenuCodeSalesOrder,
            AttachmentEvidenceRules.MenuCodeTradeDocument,
            AttachmentEvidenceRules.MenuCodeSample);
        SeedAdjacentRecords(db);

        var order = SeedSalesOrder(db, "SO-064-R");
        var document = SeedTradeDocument(db, "DOC-064-R");
        var sample = SeedSample(db, "SP-064-R");
        var store = new InMemoryAttachmentContentStore();

        var salesEvidence = await UploadOkAsync(db, store, PdfBytes("r1"),
            AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id, fileName: "销售证据.pdf");
        var docEvidence = await UploadOkAsync(db, store, PdfBytes("r2"),
            AttachmentEvidenceRules.OwnerTypeTradeDocument, document.Id, fileName: "单证扫描件.pdf");
        var sampleEvidence = await UploadOkAsync(db, store, PdfBytes("r3"),
            AttachmentEvidenceRules.OwnerTypeSample, sample.Id, fileName: "样品证据.pdf");
        await AttachmentEvidenceService.VoidAsync(db, docEvidence.Id, "扫描件不清晰，需重新扫描");

        var before = Snapshot(db);

        var payloads = new List<string>
        {
            System.Text.Json.JsonSerializer.Serialize(
                await AttachmentEvidenceService.GetCenterScopeAsync(db, CenterUserId, "工作台用户")),
            System.Text.Json.JsonSerializer.Serialize(
                await AttachmentEvidenceService.GetCenterSummaryAsync(db, CenterUserId, "工作台用户")),
            System.Text.Json.JsonSerializer.Serialize(
                await AttachmentEvidenceService.ListForCenterAsync(db, CenterQuery(pageSize: 200), CenterUserId)),
            System.Text.Json.JsonSerializer.Serialize(
                await AttachmentEvidenceService.GetForCenterAsync(db, salesEvidence.Id, CenterUserId)),
            System.Text.Json.JsonSerializer.Serialize(
                await AttachmentEvidenceService.GetForCenterAsync(db, docEvidence.Id, CenterUserId)),
            System.Text.Json.JsonSerializer.Serialize(
                await AttachmentEvidenceService.GetForCenterAsync(db, sampleEvidence.Id, CenterUserId))
        };

        // 只读：证据 / 父单据 / 相邻记录全部逐字节不变
        Assert.Equal(before, Snapshot(db));

        // 响应里不出现存储键（界面只拿到 API 相对地址与权威元数据）
        foreach (var payload in payloads)
            Assert.DoesNotContain("StorageKey", payload, StringComparison.OrdinalIgnoreCase);

        // 有效与已作废分开标注；已作废历史照常可见且保留原始文件名 / 摘要 / 作废留痕
        var docRow = (await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(ownerType: AttachmentEvidenceRules.OwnerTypeTradeDocument), CenterUserId)).Items.Single();
        Assert.True(docRow.IsVoided);
        Assert.False(docRow.IsActive);
        Assert.Equal("已作废", docRow.StatusText);
        Assert.Contains("不清晰", docRow.VoidReason);
        Assert.Equal(docEvidence.OriginalFileName, docRow.OriginalFileName);
        Assert.Equal(docEvidence.Sha256, docRow.Sha256);
        Assert.NotNull(docRow.VoidedAt);

        var salesRow = (await AttachmentEvidenceService.ListForCenterAsync(
            db, CenterQuery(ownerType: AttachmentEvidenceRules.OwnerTypeSalesOrder), CenterUserId)).Items.Single();
        Assert.True(salesRow.IsActive);
        Assert.False(salesRow.IsVoided);
        Assert.Equal("有效", salesRow.StatusText);

        // 归属可用性来自只读批量装载：三张父单据都仍可用且都没有被改写
        Assert.All((await AttachmentEvidenceService.ListForCenterAsync(db, null, CenterUserId)).Items,
            row => Assert.True(row.OwnerAvailable));
        Assert.Equal(3, (await AttachmentEvidenceService.ListForCenterAsync(db, null, CenterUserId)).Total);
        Assert.False(order.IsDeleted);
        Assert.False(document.IsDeleted);
        Assert.False(sample.IsDeleted);
        Assert.Equal(before, Snapshot(db));
    }

    // ==================== 8. 控制器契约：类级授权 + 只读 GET + 安全下载 ====================

    [Fact]
    public async Task 控制器契约_工作台端点类级授权且只有只读GET与安全下载响应头()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, CenterUserId, AttachmentEvidenceRules.MenuCodeSalesOrder);
        var order = SeedSalesOrder(db, "SO-064-Z");
        var store = new InMemoryAttachmentContentStore();
        var saved = await UploadOkAsync(db, store, PdfBytes("z"),
            AttachmentEvidenceRules.OwnerTypeSalesOrder, order.Id);

        var type = typeof(AttachmentEvidenceController);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Equal("api/attachment-evidences", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Single(type.GetCustomAttributes<RouteAttribute>());        // 不新增第二个路由族

        Assert.Equal("center", type.GetMethod(nameof(AttachmentEvidenceController.GetCenterPaged))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("center/summary", type.GetMethod(nameof(AttachmentEvidenceController.GetCenterSummary))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("center/{id:long}", type.GetMethod(nameof(AttachmentEvidenceController.GetCenterDetail))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("center/{id:long}/content",
            type.GetMethod(nameof(AttachmentEvidenceController.DownloadCenterContent))!
                .GetCustomAttribute<HttpGetAttribute>()!.Template);

        var actions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Equal(4, actions.Count(m =>
            m.GetCustomAttribute<HttpGetAttribute>()?.Template?.StartsWith("center", StringComparison.Ordinal) == true));
        Assert.DoesNotContain(actions, m => m.GetCustomAttribute<HttpPutAttribute>() is not null);
        Assert.DoesNotContain(actions, m => m.GetCustomAttribute<HttpPatchAttribute>() is not null);
        Assert.DoesNotContain(actions, m => m.GetCustomAttribute<HttpDeleteAttribute>() is not null);
        Assert.DoesNotContain(actions, m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null);

        // 未认证（缺少用户 Id）：工作台详情 / 下载一律 fail closed，且不返回文件
        var unauthenticated = new AttachmentEvidenceController(db, store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        await AssertBusinessAsync(ErrorCodes.NotFound, () => unauthenticated.GetCenterDetail(saved.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => unauthenticated.DownloadCenterContent(saved.Id));

        // 已授权用户（测试内显式声明用户 Id 声明）：详情返回权威元数据；下载以「附件」方式返回
        var controller = BuildController(db, store);
        controller.ControllerContext.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.NameIdentifier, CenterUserId.ToString()),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "工作台用户")
            }, "test"));

        var detailResult = Assert.IsType<OkObjectResult>(await controller.GetCenterDetail(saved.Id));
        var detailResponse = Assert.IsType<ApiResponse<AttachmentEvidenceDto>>(detailResult.Value);
        Assert.Equal(ErrorCodes.Success, detailResponse.Code);
        Assert.Equal(saved.OriginalFileName, detailResponse.Data!.OriginalFileName);
        Assert.Equal(saved.Sha256, detailResponse.Data.Sha256);
        Assert.Equal(AttachmentEvidenceRules.ContentApiPath(saved.Id), detailResponse.Data.DownloadPath);

        var fileResult = Assert.IsType<FileStreamResult>(await controller.DownloadCenterContent(saved.Id));
        Assert.Equal(AttachmentEvidenceRules.MediaPdf, fileResult.ContentType);
        Assert.Equal(saved.OriginalFileName, fileResult.FileDownloadName);
        Assert.False(fileResult.EnableRangeProcessing);

        var response = controller.Response;
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"].ToString());
        Assert.Contains("sandbox", response.Headers["Content-Security-Policy"].ToString());
        Assert.Contains("no-store", response.Headers["Cache-Control"].ToString());
        Assert.Equal("noopen", response.Headers["X-Download-Options"].ToString());
        Assert.Equal("no-referrer", response.Headers["Referrer-Policy"].ToString());

        await using (fileResult.FileStream)
        {
            using var buffer = new MemoryStream();
            await fileResult.FileStream.CopyToAsync(buffer);
            Assert.Equal(PdfBytes("z"), buffer.ToArray());
        }
    }

    // ==================== 9. 前端接线契约（复用既有页面 / 菜单 / 脚本注册） ====================

    [Fact]
    public void 前端接线契约_工作台入口复用既有页面与菜单且界面不接触存储键()
    {
        // 页面 / 脚本注册沿用既有约定：仍由 index.html 注册同一份 attachment-evidences.js（不新增页面脚本）
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/attachment-evidences.js", index);

        // 菜单入口复用既有模块工具栏（单证中心 extraActions）：不新增菜单、不新增端点族
        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules.js"));
        Assert.Contains("openAttachmentCenterWorkspace()", modules);
        Assert.Contains("附件中心", modules);
        Assert.Contains("未获菜单授权的归属类型不显示任何记录、计数、文件名或摘要", modules);
        Assert.Contains("fail closed", modules);

        var js = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "attachment-evidences.js"));
        Assert.Contains("async function openAttachmentCenterWorkspace()", js);
        Assert.Contains("async function aecLoadSummary()", js);
        Assert.Contains("async function aecLoadList()", js);
        Assert.Contains("/api/attachment-evidences/center/summary", js);
        Assert.Contains("'/api/attachment-evidences/center?'", js);
        Assert.Contains("'/api/attachment-evidences/center/'", js);
        Assert.Contains("/content'", js);
        Assert.Contains("function aecRowHtml(row)", js);
        Assert.Contains("async function aecOpenDetail(id)", js);
        Assert.Contains("async function aecDownload(id)", js);
        Assert.Contains("function aecDetailHtml()", js);
        Assert.Contains("aecBackToList()", js);
        Assert.Contains("不可信文件", js);
        Assert.Contains("fail closed", js);
        Assert.Contains("aeEsc(", js);

        // 界面不接触存储键、不引用生产 OSS 客户端、不自行计算摘要、不把内容拼进 DOM
        Assert.DoesNotContain("storageKey", js);
        Assert.DoesNotContain("OssStorageService", js);
        Assert.DoesNotContain("AccessKey", js);
        Assert.DoesNotContain("innerHTML = blob", js);
        Assert.DoesNotContain("innerHTML = content", js);

        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "AttachmentEvidenceController.cs"));
        Assert.Contains("[Route(\"api/attachment-evidences\")]", controller);
        Assert.Contains("[Authorize]", controller);
        Assert.Contains("[HttpGet(\"center\")]", controller);
        Assert.Contains("[HttpGet(\"center/summary\")]", controller);
        Assert.Contains("[HttpGet(\"center/{id:long}\")]", controller);
        Assert.Contains("[HttpGet(\"center/{id:long}/content\")]", controller);
        Assert.Contains("SetDefensiveDownloadHeaders()", controller);

        // 说明文档同步（口径可追溯）
        var doc = File.ReadAllText(RepoFile("docs", "业务单据附件证据说明.md"));
        Assert.Contains("附件中心工作台", doc);
        Assert.Contains("ERP-064", doc);
    }

    // ==================== 10. 纯规则与工作台文案 ====================

    [Fact]
    public void 纯规则_归属类型到既有菜单编码的映射与筛选规范化与工作台文案()
    {
        // 归属类型 → 既有菜单编码（五类归属复用四类既有菜单：验货记录与采购订单共用采购订单菜单）
        Assert.Equal(AttachmentEvidenceRules.MenuCodeSalesOrder,
            AttachmentEvidenceRules.RequiredMenuCodeOf("SalesOrder"));
        Assert.Equal(AttachmentEvidenceRules.MenuCodePurchaseOrder,
            AttachmentEvidenceRules.RequiredMenuCodeOf("PurchaseOrder"));
        Assert.Equal(AttachmentEvidenceRules.MenuCodePurchaseOrder,
            AttachmentEvidenceRules.RequiredMenuCodeOf("QualityInspection"));
        Assert.Equal(AttachmentEvidenceRules.MenuCodeTradeDocument,
            AttachmentEvidenceRules.RequiredMenuCodeOf("TradeDocument"));
        Assert.Equal(AttachmentEvidenceRules.MenuCodeSample,
            AttachmentEvidenceRules.RequiredMenuCodeOf("Sample"));
        Assert.Equal(string.Empty, AttachmentEvidenceRules.RequiredMenuCodeOf("ContainerLoadingList"));
        Assert.Equal(string.Empty, AttachmentEvidenceRules.RequiredMenuCodeOf(null));
        Assert.Contains("未知来源菜单", AttachmentEvidenceRules.RequiredMenuTextOf("Unknown"));
        Assert.Equal(4, AttachmentEvidenceRules.SupportedOwnerTypes
            .Select(AttachmentEvidenceRules.RequiredMenuCodeOf)
            .Distinct(StringComparer.Ordinal).Count());

        // 授权 / 未授权文案：未授权必须明确「不显示」，绝不把「看不到」写成「没有」
        Assert.Contains("已获", AttachmentEvidenceRules.CenterAuthorizationText("SalesOrder", true));
        var denied = AttachmentEvidenceRules.CenterAuthorizationText("SalesOrder", false);
        Assert.Contains("一律不显示", denied);
        Assert.Contains("不披露不可访问记录的存在性", denied);

        // 可见范围 / 摘要文案：空范围明确 fail closed
        Assert.Contains("fail closed", AttachmentEvidenceRules.CenterScopeText(new List<string>(), 5));
        Assert.Contains("另有 1 类归属未授权", AttachmentEvidenceRules.CenterScopeText(new[] { "销售订单" }, 1));
        Assert.Contains("计数只统计当前账号已授权的归属类型",
            AttachmentEvidenceRules.CenterSummaryText(3, 2, 1, 2));

        // 证据性质 / 边界文案：明确不是批准、验货结论、报关税务、付款授权、结算确认与出运许可
        foreach (var text in new[]
                 {
                     AttachmentEvidenceRules.CenterUntrustedEvidenceNoticeText,
                     AttachmentEvidenceRules.CenterBoundaryText
                 })
        {
            Assert.Contains("不可信", text);
            Assert.Contains("批准", text);
            Assert.Contains("验货", text);
            Assert.Contains("报关", text);
            Assert.Contains("付款授权", text);
            Assert.Contains("结算", text);
            Assert.Contains("出运许可", text);
        }

        Assert.Contains("只读", AttachmentEvidenceRules.CenterReadOnlyNoticeText);
        Assert.Contains("不扫描文件内容", AttachmentEvidenceRules.CenterFilterPolicyText);
        Assert.Contains("不抓取", AttachmentEvidenceRules.CenterFilterPolicyText);
        Assert.Contains("合并", AttachmentEvidenceRules.CenterFilterPolicyText);

        // 筛选规范化：媒体类型白名单（大小写不敏感、输出规范值）、有界文本、日期区间合法性
        Assert.Null(AttachmentEvidenceRules.NormalizeMediaTypeFilter("   "));
        Assert.Equal(AttachmentEvidenceRules.MediaPng, AttachmentEvidenceRules.NormalizeMediaTypeFilter("IMAGE/PNG"));
        Assert.Throws<BusinessException>(() => AttachmentEvidenceRules.NormalizeMediaTypeFilter("text/html"));
        Assert.Null(AttachmentEvidenceRules.NormalizeCenterOwnerNoFilter(null));
        Assert.Null(AttachmentEvidenceRules.NormalizeCenterFileNameFilter("   "));
        Assert.Equal("证据", AttachmentEvidenceRules.NormalizeCenterFileNameFilter(" 证据 "));
        Assert.Equal("甲", AttachmentEvidenceRules.NormalizeCenterUploadedByFilter(" 甲 "));
        Assert.Null(AttachmentEvidenceRules.NormalizeCenterUploadedByFilter(""));

        var range = AttachmentEvidenceRules.NormalizeRecordedRange(
            new DateTime(2026, 9, 1, 13, 0, 0), new DateTime(2026, 9, 2, 8, 0, 0));
        Assert.Equal(new DateTime(2026, 9, 1), range.From);
        Assert.Equal(new DateTime(2026, 9, 2), range.To);
        Assert.Null(AttachmentEvidenceRules.NormalizeRecordedRange(null, null).From);
        Assert.Throws<BusinessException>(() => AttachmentEvidenceRules.NormalizeRecordedRange(
            new DateTime(2026, 9, 3), new DateTime(2026, 9, 1)));

        // 有界上限与类型数一致（工作台不会随数据增长）
        Assert.Equal(50, AttachmentEvidenceCenterQuery.DefaultPageSize);
        Assert.Equal(200, AttachmentEvidenceCenterQuery.MaxPageSize);
        Assert.Equal(AttachmentEvidenceService.MaxCenterOwnerTypes,
            AttachmentEvidenceRules.SupportedOwnerTypes.Length);
    }

    // ==================== 11. 测试替身 ====================

    /// <summary>
    /// 测试内内存内容存储（实现唯一内容接缝）：键同样由「服务端」生成的不透明标识；
    /// 额外统计保存 / 打开 / 长度探测次数，用于断言列表与摘要**完全不访问存储**、
    /// 内容只在显式下载时被读取。
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

        /// <summary>被访问的数据集属性名（工作台预期只有授权表、证据表与归属单据表）</summary>
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
                ReadProperties.Add(targetMethod.Name[4..]);

            return targetMethod.Invoke(_inner, args);
        }
    }
}
