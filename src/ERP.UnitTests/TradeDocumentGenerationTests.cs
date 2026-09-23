using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 单证中心（ERP-019）「由销售订单 / 装柜清单生成单证 + Excel 导出」单元测试：
/// 两种来源的字段映射、单证编号生成与冲突回退、重复生成守卫、带入预填（不落库）、
/// 生成后的可编辑性、列表 / 单条 Excel 导出、接口路由与前端接线契约。
/// 口径：只使用单证台账既有字段，不新增数据库结构（见 <see cref="TradeDocumentGeneration"/>）。
/// </summary>
public class TradeDocumentGenerationTests
{
    private const string 商业发票 = "商业发票";
    private const string 装箱单 = "装箱单";
    private const string 报关单 = "报关单";

    // ==================== 销售订单来源 ====================

    [Fact]
    public async Task 销售订单生成单证_默认类型_字段映射与来源留痕完整()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-DOC-1", customer.Id);

        var ok = Assert.IsType<OkObjectResult>(
            await NewSalesOrderController(db).GenerateTradeDocuments(order.Id, null));
        var result = Assert.IsType<ApiResponse<TradeDocGenerateResult>>(ok.Value).Data!;

        // 默认生成商业发票 + 装箱单（出口必备两件套）
        Assert.Equal(TradeDocumentGeneration.SalesOrderSourceType, result.SourceType);
        Assert.Equal(order.Id, result.SourceId);
        Assert.Equal("SO-DOC-1", result.SourceNo);
        Assert.Equal(new[] { 商业发票, 装箱单 }, result.Documents.Select(d => d.DocType).ToArray());
        Assert.Equal("CI-SO-DOC-1", result.Documents[0].DocNo);
        Assert.Equal("PL-SO-DOC-1", result.Documents[1].DocNo);
        Assert.All(result.Documents, d => Assert.Equal("待制作", d.Status));

        var docs = db.TradeDocuments.OrderBy(d => d.Id).ToList();
        Assert.Equal(2, docs.Count);

        var ci = docs[0];
        Assert.Equal(商业发票, ci.DocType);
        Assert.Equal("SO-DOC-1", ci.SalesOrderNo);              // 来源订单号留痕
        Assert.Equal(customer.Id, ci.CustomerId);
        Assert.Equal("义乌客户（单证测试）", ci.CustomerName);
        Assert.Equal(750m, ci.Amount);                          // 金额取订单总额
        Assert.Equal("USD", ci.Currency);                       // 币种取订单币种名
        Assert.Equal("HAMBURG", ci.DestinationPort);            // 目的港取订单文本
        Assert.Equal(DateTime.Today, ci.IssueDate);
        Assert.Equal(3, ci.Copies);                             // 商业发票默认 3 份
        Assert.Equal(string.Empty, ci.RefNo);                   // 柜号待装柜后人工回填
        Assert.StartsWith(TradeDocumentGeneration.SalesOrderRemarkPrefix + "SO-DOC-1", ci.Remark);
        Assert.Contains("客户 PO：PO-9988", ci.Remark);
        Assert.Contains("贸易条款：FOB", ci.Remark);

        var pl = docs[1];
        Assert.Equal(装箱单, pl.DocType);
        Assert.Equal("SO-DOC-1", pl.SalesOrderNo);
        Assert.Equal("USD", pl.Currency);
        Assert.Equal(3, pl.Copies);
        Assert.StartsWith(TradeDocumentGeneration.SalesOrderRemarkPrefix + "SO-DOC-1", pl.Remark);
    }

    [Fact]
    public async Task 销售订单生成单证_指定类型_制作人与份数按类型取默认值()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-DOC-2", customer.Id);

        var request = new TradeDocGenerateRequest { DocTypes = new List<string> { 报关单, "产地证" } };
        var ok = Assert.IsType<OkObjectResult>(
            await NewSalesOrderController(db).GenerateTradeDocuments(order.Id, request));
        var result = Assert.IsType<ApiResponse<TradeDocGenerateResult>>(ok.Value).Data!;
        Assert.Equal(new[] { 报关单, "产地证" }, result.Documents.Select(d => d.DocType).ToArray());

        var docs = db.TradeDocuments.OrderBy(d => d.Id).ToList();
        Assert.Equal("报关行", docs[0].IssuedBy);
        Assert.Equal(1, docs[0].Copies);
        Assert.Equal("贸促会 / 海关", docs[1].IssuedBy);
        Assert.Equal(2, docs[1].Copies);
    }

    [Fact]
    public async Task 销售订单生成单证_订单目的港为空_回退客户档案目的港()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-DOC-3", customer.Id);
        order.DestinationPort = string.Empty;
        db.SaveChanges();

        await NewSalesOrderController(db).GenerateTradeDocuments(order.Id, null);

        Assert.All(db.TradeDocuments.ToList(), d => Assert.Equal("ROTTERDAM", d.DestinationPort));
    }

    [Fact]
    public async Task 销售订单生成单证_无客户档案_客户字段安全留空()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-DOC-4", customerId: 0L);
        order.DestinationPort = string.Empty;          // 订单未维护目的港且无客户档案可回退
        db.SaveChanges();

        await NewSalesOrderController(db).GenerateTradeDocuments(order.Id, null);

        var docs = db.TradeDocuments.ToList();
        Assert.Equal(2, docs.Count);
        Assert.All(docs, d =>
        {
            Assert.Null(d.CustomerId);
            Assert.Equal(string.Empty, d.CustomerName);
            Assert.Equal(string.Empty, d.DestinationPort);
        });
    }

    [Fact]
    public async Task 销售订单生成单证_已作废订单_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-DOC-5", customer.Id, DocumentStatus.Cancelled);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewSalesOrderController(db).GenerateTradeDocuments(order.Id, null));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已作废", ex.Message);
        Assert.Empty(db.TradeDocuments);
    }

    [Fact]
    public async Task 销售订单生成单证_未知单证类型_拒绝且提示可选类型()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-DOC-6", customer.Id);

        var request = new TradeDocGenerateRequest { DocTypes = new List<string> { "空运单" } };
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewSalesOrderController(db).GenerateTradeDocuments(order.Id, request));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("不支持的单证类型", ex.Message);
        Assert.Contains(商业发票, ex.Message);
        Assert.Empty(db.TradeDocuments);
    }

    [Fact]
    public async Task 销售订单生成单证_重复生成同一类型_被守卫拒绝且不新增()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-DOC-7", customer.Id);
        var ctl = NewSalesOrderController(db);
        var request = new TradeDocGenerateRequest { DocTypes = new List<string> { 商业发票 } };

        await ctl.GenerateTradeDocuments(order.Id, request);              // 首次生成

        // 重复点击（等价于双击「生成所选单证」）必须被来源字段守卫拦住
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GenerateTradeDocuments(order.Id, request));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已生成", ex.Message);
        Assert.Contains("CI-SO-DOC-7", ex.Message);
        Assert.Single(db.TradeDocuments);

        // 部分重复（再次勾选「已生成 + 未生成」）同样整体拒绝，不产生半成品数据
        var mixed = new TradeDocGenerateRequest { DocTypes = new List<string> { 商业发票, 装箱单 } };
        var exMixed = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.GenerateTradeDocuments(order.Id, mixed));
        Assert.Contains("不能重复生成", exMixed.Message);
        Assert.Single(db.TradeDocuments);
    }

    [Fact]
    public async Task 销售订单生成单证_已软删除同类型_可重新生成()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-DOC-8", customer.Id);
        var ctl = NewSalesOrderController(db);
        var request = new TradeDocGenerateRequest { DocTypes = new List<string> { 装箱单 } };

        await ctl.GenerateTradeDocuments(order.Id, request);
        var existing = db.TradeDocuments.Single();
        existing.IsDeleted = true;                       // 单证中心删除（软删除）
        db.SaveChanges();

        await ctl.GenerateTradeDocuments(order.Id, request);
        Assert.Equal(1, db.TradeDocuments.Count(d => !d.IsDeleted));
        Assert.Equal(2, db.TradeDocuments.Count());
    }

    [Fact]
    public async Task 销售订单带入预填_返回未落库草稿且标记已生成类型()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-DOC-9", customer.Id);
        var ctl = NewSalesOrderController(db);

        var ok = Assert.IsType<OkObjectResult>(await ctl.TradeDocumentPrefill(order.Id));
        var prefill = Assert.IsType<ApiResponse<TradeDocPrefillResult>>(ok.Value).Data!;

        Assert.Equal(TradeDocumentGeneration.SalesOrderSourceType, prefill.SourceType);
        Assert.Equal(order.Id, prefill.SourceId);
        Assert.Equal("SO-DOC-9", prefill.SourceNo);
        Assert.Equal(string.Empty, prefill.SourceRefNo);
        Assert.Equal(new[] { "商业发票", "装箱单", "报关单", "产地证", "提单" },
            prefill.SupportedDocTypes.ToArray());
        Assert.Equal(new[] { 商业发票, 装箱单 }, prefill.DefaultDocTypes.ToArray());
        Assert.Empty(prefill.GeneratedDocTypes);
        Assert.Equal(5, prefill.Documents.Count);
        Assert.Equal("CI-SO-DOC-9", prefill.Documents[0].DocNo);
        Assert.Equal(750m, prefill.Documents[0].Amount);
        Assert.Empty(db.TradeDocuments);                 // 预填不落库

        await ctl.GenerateTradeDocuments(order.Id,
            new TradeDocGenerateRequest { DocTypes = new List<string> { 商业发票 } });

        var okAgain = Assert.IsType<OkObjectResult>(await ctl.TradeDocumentPrefill(order.Id));
        var prefillAgain = Assert.IsType<ApiResponse<TradeDocPrefillResult>>(okAgain.Value).Data!;
        Assert.Equal(new[] { 商业发票 }, prefillAgain.GeneratedDocTypes.ToArray());
    }

    // ==================== 装柜清单来源 ====================

    [Fact]
    public async Task 装柜清单生成单证_柜号客户港口与体积重量映射完整()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, currency: "EUR");
        var seed = SeedLoadingList(db, "ZQ-DOC-1", customer.Id);
        var loadingDate = seed.List.LoadingDate;

        var ok = Assert.IsType<OkObjectResult>(await NewLoadingListController(db)
            .GenerateTradeDocuments(seed.List.Id, null));
        var result = Assert.IsType<ApiResponse<TradeDocGenerateResult>>(ok.Value).Data!;

        Assert.Equal(TradeDocumentGeneration.LoadingListSourceType, result.SourceType);
        Assert.Equal(seed.List.Id, result.SourceId);
        Assert.Equal("ZQ-DOC-1", result.SourceNo);
        Assert.Equal(new[] { 装箱单 }, result.Documents.Select(d => d.DocType).ToArray());  // 默认装箱单

        var doc = db.TradeDocuments.Single();
        Assert.Equal("PL-ZQ-DOC-1", doc.DocNo);
        Assert.Equal("CTN-1001", doc.RefNo);                       // 来源柜号留痕
        Assert.Equal(customer.Id, doc.CustomerId);
        Assert.Equal("义乌客户（单证测试）", doc.CustomerName);
        Assert.Equal("EUR", doc.Currency);                         // 币种回退客户档案
        Assert.Equal("NINGBO", doc.DeparturePort);                 // 起运港取订柜信息
        Assert.Equal("HAMBURG", doc.DestinationPort);
        Assert.Equal(0m, doc.Amount);                              // 装柜阶段金额未定，留 0 人工填写
        Assert.Equal(loadingDate.Date, doc.IssueDate);
        Assert.Equal(3, doc.Copies);
        Assert.Equal("待制作", doc.Status);
        Assert.StartsWith(TradeDocumentGeneration.LoadingListRemarkPrefix + "ZQ-DOC-1", doc.Remark);
        Assert.Contains("柜号：CTN-1001", doc.Remark);
        Assert.Contains("箱数：120", doc.Remark);
        Assert.Contains("毛重：2100.5kg", doc.Remark);
        Assert.Contains("体积：28.75m³", doc.Remark);
    }

    [Fact]
    public async Task 装柜清单生成单证_无预装柜单_起运港留空且目的港回退客户档案()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var seed = SeedLoadingList(db, "ZQ-DOC-2", customer.Id, withBooking: false);

        var request = new TradeDocGenerateRequest { DocTypes = new List<string> { 装箱单, "提单" } };
        await NewLoadingListController(db).GenerateTradeDocuments(seed.List.Id, request);

        var docs = db.TradeDocuments.OrderBy(d => d.Id).ToList();
        Assert.Equal(2, docs.Count);
        Assert.All(docs, d =>
        {
            Assert.Equal(string.Empty, d.DeparturePort);
            Assert.Equal("ROTTERDAM", d.DestinationPort);           // ← 客户档案目的港
            Assert.Equal("CTN-1001", d.RefNo);
        });
        Assert.Equal(3, docs[0].Copies);                            // 装箱单 3 份
        Assert.Equal("船公司", docs[1].IssuedBy);                   // 提单默认出证机构
        Assert.Equal(3, docs[1].Copies);
    }

    [Fact]
    public async Task 装柜清单生成单证_同柜号同类型重复_被守卫拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var first = SeedLoadingList(db, "ZQ-DOC-3", customer.Id, containerNo: "CTN-3001");
        var second = SeedLoadingList(db, "ZQ-DOC-4", customer.Id, containerNo: "CTN-3001");
        var ctl = NewLoadingListController(db);
        var request = new TradeDocGenerateRequest { DocTypes = new List<string> { 装箱单 } };

        await ctl.GenerateTradeDocuments(first.List.Id, request);

        // 同一清单重复生成 / 同一柜号换一张清单再生成，都应被守卫拦住（一柜一类单证只有一份）
        var exSame = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.GenerateTradeDocuments(first.List.Id, request));
        Assert.Equal(ErrorCodes.RuleConflict, exSame.Code);
        Assert.Contains("已生成", exSame.Message);

        var exSameContainer = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.GenerateTradeDocuments(second.List.Id, request));
        Assert.Contains("PL-ZQ-DOC-3", exSameContainer.Message);
        Assert.Single(db.TradeDocuments);

        // 其他类型不受影响（提单仍可生成）
        await ctl.GenerateTradeDocuments(second.List.Id,
            new TradeDocGenerateRequest { DocTypes = new List<string> { "提单" } });
        Assert.Equal(2, db.TradeDocuments.Count());
    }

    [Fact]
    public async Task 装柜清单生成单证_已作废_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var seed = SeedLoadingList(db, "ZQ-DOC-5", customer.Id);
        seed.List.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewLoadingListController(db).GenerateTradeDocuments(seed.List.Id, null));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已作废", ex.Message);
        Assert.Empty(db.TradeDocuments);
    }

    [Fact]
    public async Task 装柜清单带入预填_柜号与可生成类型齐备且不落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var seed = SeedLoadingList(db, "ZQ-DOC-6", customer.Id);

        var ok = Assert.IsType<OkObjectResult>(
            await NewLoadingListController(db).TradeDocumentPrefill(seed.List.Id));
        var prefill = Assert.IsType<ApiResponse<TradeDocPrefillResult>>(ok.Value).Data!;

        Assert.Equal(TradeDocumentGeneration.LoadingListSourceType, prefill.SourceType);
        Assert.Equal("ZQ-DOC-6", prefill.SourceNo);
        Assert.Equal("CTN-1001", prefill.SourceRefNo);
        Assert.Equal(new[] { 装箱单, "提单", "报关单", "订舱确认" }, prefill.SupportedDocTypes.ToArray());
        Assert.Equal(new[] { 装箱单 }, prefill.DefaultDocTypes.ToArray());
        Assert.Empty(prefill.GeneratedDocTypes);
        Assert.Equal(4, prefill.Documents.Count);
        Assert.Equal("PL-ZQ-DOC-6", prefill.Documents[0].DocNo);
        Assert.Equal("CTN-1001", prefill.Documents[0].RefNo);
        Assert.Empty(db.TradeDocuments);
    }

    // ==================== 单证编号 / 人工可编辑性 / Excel 导出 ====================

    [Fact]
    public async Task 单证编号_与历史人工单证冲突_自动追加序号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-DOC-10", customer.Id);
        db.TradeDocuments.Add(new TradeDocument
        {
            DocNo = "CI-SO-DOC-10", DocType = "其他", CustomerName = "历史手工单证", Status = "待制作"
        });
        db.SaveChanges();

        var request = new TradeDocGenerateRequest { DocTypes = new List<string> { 商业发票 } };
        await NewSalesOrderController(db).GenerateTradeDocuments(order.Id, request);

        var generated = db.TradeDocuments.Single(d => d.DocType == 商业发票);
        Assert.Equal("CI-SO-DOC-10-2", generated.DocNo);
        Assert.Equal("CI-SO-DOC-10", db.TradeDocuments.Single(d => d.DocType == "其他").DocNo);
    }

    [Fact]
    public async Task 生成的单证_可在单证中心人工修改与流转状态()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-DOC-11", customer.Id);
        await NewSalesOrderController(db).GenerateTradeDocuments(order.Id, null);

        var ci = db.TradeDocuments.Single(d => d.DocType == 商业发票);
        Assert.Equal("待制作", ci.Status);                       // 生成后仍是「待制作」，人工确认前可自由修改

        var edit = new TradeDocument
        {
            Id = ci.Id, DocNo = ci.DocNo, DocType = 商业发票, SalesOrderNo = ci.SalesOrderNo,
            IssueDate = ci.IssueDate, CustomerId = ci.CustomerId, CustomerName = ci.CustomerName,
            Amount = 800m, Currency = "USD", DeparturePort = "NINGBO", DestinationPort = ci.DestinationPort,
            IssuedBy = "报关行 A", Copies = 5, Status = "已制作", FileNote = "扫描件在共享盘 /出口/2026",
            Remark = ci.Remark + " ｜ 人工核对：金额按实际发票调整"
        };
        var ok = Assert.IsType<OkObjectResult>(await NewTradeDocumentController(db).Update(ci.Id, edit));
        Assert.Equal("更新成功", Assert.IsType<ApiResponse<TradeDocument>>(ok.Value).Message);

        var saved = db.TradeDocuments.AsNoTracking().Single(d => d.Id == ci.Id);
        Assert.Equal("已制作", saved.Status);
        Assert.Equal(800m, saved.Amount);
        Assert.Equal(5, saved.Copies);
        Assert.Equal("报关行 A", saved.IssuedBy);
        Assert.Equal("NINGBO", saved.DeparturePort);
        Assert.StartsWith(TradeDocumentGeneration.SalesOrderRemarkPrefix + "SO-DOC-11", saved.Remark);  // 来源留痕保留
        Assert.Equal(2, db.TradeDocuments.Count());              // 人工修改不产生新单证
    }

    [Fact]
    public async Task 单证导出_按关键字类型日期与状态过滤_表头与数据行正确()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-DOC-EX", customer.Id);
        await NewSalesOrderController(db).GenerateTradeDocuments(order.Id, null);
        db.TradeDocuments.Add(new TradeDocument
        {
            DocNo = "OTHER-1", DocType = "其他", CustomerName = "别的客户", Status = "已使用",
            IssueDate = DateTime.Today.AddDays(-40)
        });
        db.SaveChanges();
        var ctl = NewTradeDocumentController(db);

        // 列表导出：关键字命中生成的两张单证（不含 OTHER-1）
        var all = Assert.IsType<FileContentResult>(
            await ctl.ExportExcel(null, "SO-DOC-EX", null, null, null, null));
        Assert.StartsWith("TradeDocuments_", all.FileDownloadName);
        Assert.EndsWith(".xlsx", all.FileDownloadName);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", all.ContentType);

        var sheet = ReadSheet(all.FileContents);
        Assert.Equal("单证编号", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("单证类型", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("份数", sheet.GetRow(0).GetCell(12).StringCellValue);
        Assert.Equal("备注", sheet.GetRow(0).GetCell(15).StringCellValue);
        Assert.Equal(2, sheet.LastRowNum);                        // 表头 + 2 行
        var docNos = new[] { sheet.GetRow(1).GetCell(0).StringCellValue, sheet.GetRow(2).GetCell(0).StringCellValue };
        Assert.Contains("PL-SO-DOC-EX", docNos);
        Assert.Contains("CI-SO-DOC-EX", docNos);
        var plRow = docNos[0] == "PL-SO-DOC-EX" ? sheet.GetRow(1) : sheet.GetRow(2);
        Assert.Equal("装箱单", plRow.GetCell(1).StringCellValue);
        Assert.Equal("义乌客户（单证测试）", plRow.GetCell(6).StringCellValue);
        Assert.Equal(3d, plRow.GetCell(12).NumericCellValue);     // 份数（数值单元格）

        // 类型 + 状态 + 日期过滤：只留「待制作」且 90 天内出具的商业发票
        var filtered = Assert.IsType<FileContentResult>(await ctl.ExportExcel(null, null, 商业发票, "待制作",
            DateTime.Today.AddDays(-90), DateTime.Today));
        var filteredSheet = ReadSheet(filtered.FileContents);
        Assert.Equal(1, filteredSheet.LastRowNum);
        Assert.Equal("CI-SO-DOC-EX", filteredSheet.GetRow(1).GetCell(0).StringCellValue);

        // 不匹配的状态：只保留表头
        var none = Assert.IsType<FileContentResult>(
            await ctl.ExportExcel(null, null, null, "已使用", null, null));
        Assert.Equal(1, ReadSheet(none.FileContents).LastRowNum);
    }

    [Fact]
    public async Task 单证导出_单条导出_仅一行且文件名带单证Id()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-DOC-EX2", customer.Id);
        await NewSalesOrderController(db).GenerateTradeDocuments(order.Id, null);
        var ci = db.TradeDocuments.Single(d => d.DocType == 商业发票);

        var file = Assert.IsType<FileContentResult>(
            await NewTradeDocumentController(db).ExportExcel(ci.Id, null, null, null, null, null));

        Assert.Contains($"TradeDocument_{ci.Id}_", file.FileDownloadName);
        var sheet = ReadSheet(file.FileContents);
        Assert.Equal(1, sheet.LastRowNum);
        Assert.Equal("CI-SO-DOC-EX2", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("SO-DOC-EX2", sheet.GetRow(1).GetCell(3).StringCellValue);
    }

    private static ISheet ReadSheet(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var wb = new XSSFWorkbook(ms);
        return wb.GetSheetAt(0);
    }

    // ==================== 接口路由与前端接线（浏览器验收延后阶段的静态兜底） ====================

    [Fact]
    public void 生成接口路由_两种来源均提供预填与生成端点_单证中心提供导出端点()
    {
        AssertRouteTemplate(typeof(SalesOrderController), nameof(SalesOrderController.TradeDocumentPrefill),
            "{id:long}/trade-documents/prefill");
        AssertRouteTemplate(typeof(SalesOrderController), nameof(SalesOrderController.GenerateTradeDocuments),
            "{id:long}/trade-documents");
        AssertRouteTemplate(typeof(ContainerLoadingListController),
            nameof(ContainerLoadingListController.TradeDocumentPrefill), "{id:long}/trade-documents/prefill");
        AssertRouteTemplate(typeof(ContainerLoadingListController),
            nameof(ContainerLoadingListController.GenerateTradeDocuments), "{id:long}/trade-documents");
        AssertRouteTemplate(typeof(TradeDocumentController), nameof(TradeDocumentController.ExportExcel),
            "export-excel");
    }

    [Fact]
    public void 前端行操作_与生成与导出接口接线一致()
    {
        var directory = JsDirectory();
        Assert.True(Directory.Exists(directory), $"未找到前端脚本目录：{directory}");

        var tradeDocJs = File.ReadAllText(Path.Combine(directory, "trade-doc-gen.js"));
        var modulesJs = File.ReadAllText(Path.Combine(directory, "modules.js"));
        var modulesDocJs = File.ReadAllText(Path.Combine(directory, "modules-doc.js"));
        var modulesDoc2Js = File.ReadAllText(Path.Combine(directory, "modules-doc2.js"));
        var indexHtml = File.ReadAllText(Path.Combine(directory, "..", "index.html"));

        // 行操作 → 全局函数名（必须成对存在，拼写漂移会让「更多」里的按钮点了没反应）
        foreach (var function in new[] { "generateTradeDocsFromSource", "prefillTradeDocFromSource" })
        {
            Assert.Contains($"onclick: '{function}'", modulesDocJs);        // 销售订单页
            Assert.Contains($"onclick: '{function}'", modulesDoc2Js);       // 装柜清单页
            Assert.Contains($"function {function}(", tradeDocJs);
        }
        // 单证中心页：列表导出 + 单条导出
        Assert.Contains("onclick: 'openTradeDocExportDialog()'", modulesJs);
        Assert.Contains("onclick: 'exportTradeDocument'", modulesJs);
        Assert.Contains("function openTradeDocExportDialog(", tradeDocJs);
        Assert.Contains("function exportTradeDocument(", tradeDocJs);

        // 函数 → 后端接口路径与来源配置
        Assert.Contains("const TRADE_DOC_SOURCES = {", tradeDocJs);
        Assert.Contains("api: '/api/sales-orders'", tradeDocJs);
        Assert.Contains("api: '/api/container/loading-lists'", tradeDocJs);
        Assert.Contains("`${source.api}/${id}/trade-documents/prefill`", tradeDocJs);
        Assert.Contains("`${ctx.source.api}/${ctx.sourceId}/trade-documents`", tradeDocJs);
        Assert.Contains("/api/trade/documents/export-excel", tradeDocJs);
        // 带入预填复用单证中心模块与既有表单渲染
        Assert.Contains("gotoModulePage('doc-center'", tradeDocJs);
        Assert.Contains("fillTradeDocForm(", tradeDocJs);
        Assert.Contains("MODULES['doc-center']", tradeDocJs);
        // 脚本已注册进页面（否则函数不存在，浏览器验收会「点了没反应」）
        Assert.Contains("/js/trade-doc-gen.js", indexHtml);
    }

    [Fact]
    public void 生成报文_JSON字段名与枚举口径符合前端约定()
    {
        // 与 Program.cs 的 JSON 配置一致：camelCase（前端按 data.documents[i].docNo 取值）
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var payload = new TradeDocPrefillResult
        {
            SourceType = TradeDocumentGeneration.LoadingListSourceType,
            SourceId = 9L,
            SourceNo = "ZQ2609230001",
            SourceRefNo = "CTN-1001",
            GeneratedDocTypes = new List<string> { "装箱单" },
            DefaultDocTypes = new List<string> { "装箱单" },
            SupportedDocTypes = new List<string> { "装箱单", "提单" },
            Documents = new List<TradeDocument>
            {
                new()
                {
                    DocNo = "PL-ZQ2609230001", DocType = "装箱单", Copies = 3, Amount = 0m,
                    Currency = "USD", RefNo = "CTN-1001", Status = "待制作",
                    IssueDate = new DateTime(2026, 9, 23)
                }
            }
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, options));
        var root = doc.RootElement;
        Assert.Equal("LoadingList", root.GetProperty("sourceType").GetString());
        Assert.Equal("ZQ2609230001", root.GetProperty("sourceNo").GetString());
        Assert.Equal("CTN-1001", root.GetProperty("sourceRefNo").GetString());
        Assert.Equal("装箱单", root.GetProperty("generatedDocTypes")[0].GetString());
        Assert.Equal("提单", root.GetProperty("supportedDocTypes")[1].GetString());

        var draft = root.GetProperty("documents")[0];
        Assert.Equal("PL-ZQ2609230001", draft.GetProperty("docNo").GetString());
        Assert.Equal("装箱单", draft.GetProperty("docType").GetString());
        Assert.Equal("CTN-1001", draft.GetProperty("refNo").GetString());
        Assert.Equal(3, draft.GetProperty("copies").GetInt32());
        Assert.StartsWith("2026-09-23", draft.GetProperty("issueDate").GetString());

        // 生成请求：前端提交 { docTypes: [...] }
        var request = JsonSerializer.Deserialize<TradeDocGenerateRequest>(
            "{\"docTypes\":[\"商业发票\",\"装箱单\"]}", options);
        Assert.Equal(new[] { "商业发票", "装箱单" }, request!.DocTypes.ToArray());
    }

    // ==================== 工厂与种子数据 ====================

    private static SalesOrderController NewSalesOrderController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static ContainerLoadingListController NewLoadingListController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static TradeDocumentController NewTradeDocumentController(ErpDbContext db)
        => new(new GenericService<TradeDocument>(db), db);

    /// <summary>断言控制器方法上存在指定路由模板</summary>
    private static void AssertRouteTemplate(Type controller, string methodName, string expectedTemplate)
    {
        var method = controller.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        var templates = method!.GetCustomAttributes<HttpMethodAttribute>(true)
            .Select(a => a.Template ?? string.Empty)
            .ToList();
        Assert.Contains(expectedTemplate, templates);
    }

    /// <summary>前端脚本目录（沿测试程序集输出目录上溯到仓库根，与 UiTestFixture 同一约定）</summary>
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    /// <summary>客户档案：币种 / 目的港 / 贸易条款等默认值齐备</summary>
    private static BaseCustomer SeedCustomer(ErpDbContext db, string currency = "EUR")
    {
        var customer = new BaseCustomer
        {
            CustomerCode = "C-DOC-1",
            CustomerName = "义乌客户（单证测试）",
            Currency = currency,
            DestinationPort = "ROTTERDAM",
            TradeTerms = "CIF",
            PaymentTerms = "T/T 30% deposit",
            Consignee = "ACME IMPORT GMBH",
            NotifyParty = "ACME LOGISTICS",
            DefaultShippingMark = "ACME\r\nHAMBURG\r\nC/NO.1-120"
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    /// <summary>销售订单：金额 750、目的港 HAMBURG、含客户 PO / 合同号 / 贸易条款与一行明细</summary>
    private static SalesOrder SeedSalesOrder(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = no,
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            CustomerPoNo = "PO-9988",
            ContractNo = "SC-2026-001",
            TradeTerms = "FOB",
            DestinationPort = "HAMBURG",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            TotalAmount = 750m,
            DepositRatio = 30m,
            DepositAmount = 225m,
            DeliveryDate = DateTime.Today.AddDays(30),
            Status = status,
            Details = new List<SalesOrderDetail>
            {
                new()
                {
                    ProductId = 1, ProductName = "商品 A", Spec = "大", Unit = "PCS",
                    Quantity = 100m, UnitPrice = 2.5m, Amount = 250m
                }
            }
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private sealed record LoadingListSeed(ContainerLoadingList List, ContainerBooking? Booking);

    /// <summary>
    /// 装柜清单：120 箱 / 2100.5kg / 28.75m³，默认带「订柜信息 → 预装柜单」链路（用于验证港口回退）；
    /// <paramref name="withBooking"/>=false 时不挂预装柜单（港口只能回退客户档案）。
    /// </summary>
    private static LoadingListSeed SeedLoadingList(ErpDbContext db, string no, long customerId,
        string containerNo = "CTN-1001", bool withBooking = true)
    {
        long? preLoadingId = null;
        ContainerBooking? booking = null;
        if (withBooking)
        {
            booking = new ContainerBooking
            {
                BookingNo = "DG-" + no, BookingDate = DateTime.Today, CustomerId = customerId,
                DeparturePort = "NINGBO", DestinationPort = "HAMBURG",
                ContainerType = ContainerType.GP20, Status = DocumentStatus.Approved
            };
            db.ContainerBookings.Add(booking);
            db.SaveChanges();

            var preLoading = new ContainerPreLoading
            {
                PreLoadingNo = "YZ-" + no, LoadingDate = DateTime.Today, BookingId = booking.Id,
                ContainerNo = containerNo, Status = DocumentStatus.Approved
            };
            db.ContainerPreLoadings.Add(preLoading);
            db.SaveChanges();
            preLoadingId = preLoading.Id;
        }

        var list = new ContainerLoadingList
        {
            LoadingListNo = no,
            LoadingDate = DateTime.Today,
            PreLoadingId = preLoadingId,
            ContainerNo = containerNo,
            CustomerId = customerId,
            ShippingMark = "ACME\r\nC/NO.1-120",
            TotalCartons = 120m,
            TotalWeight = 2100.5m,
            TotalVolume = 28.75m,
            Status = DocumentStatus.Approved,
            Remark = "装柜确认"
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return new LoadingListSeed(list, booking);
    }
}


