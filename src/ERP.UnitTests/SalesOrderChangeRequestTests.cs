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
/// 销售订单变更申请登记单元测试（ERP-047）。覆盖：草稿登记与来源快照（主表 + 逐行明细 + 状态 / 更新时间 /
/// 明细签名）、来源校验（不存在 / 已删除 / 未选 / 已作废）、有界文本与主表值校验、拟议金额由服务端按
/// 销售订单唯一权威算法重算（不接受客户端金额）、提交冻结与不可编辑 / 不可重复提交、取消必须填原因且保留证据、
/// 来源变化检测（显式提示但不覆盖拟议值）、明细新增 / 移除 / 修改对照、分页与过滤有界、
/// 非变更边界（来源订单与报价单 / 出库 / 装柜 / 收款 / 库存 / 单证 / 财务一律不变）、
/// 控制器 / 模型 / SchemaUpgrader 幂等结构与前端接线契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 迁移 / 部署脚本。
/// </summary>
public class SalesOrderChangeRequestTests
{
    // ==================== 0. 测试脚手架 ====================

    private static SalesOrderChangeRequestController BuildController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    /// <summary>来源销售订单：2 行明细（10 × 5 = 50、4 × 25 = 100；总额 150、定金比例 30% → 45）</summary>
    private static SalesOrder SeedSalesOrder(
        ErpDbContext db, string orderNo = "SO2026001", bool deleted = false,
        DocumentStatus status = DocumentStatus.Pending, decimal depositRatio = 30m)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = 7,
            SalesmanId = 3,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = depositRatio,
            PaymentTerms = "T/T 30%",
            DeliveryDate = new DateTime(2026, 10, 1),
            ShippingMethod = "SEA",
            PortId = 11,
            Remark = "原始备注",
            CustomerPoNo = "PO-C-1",
            ContractNo = "CT-1",
            TradeTerms = "FOB",
            DestinationPort = "Ningbo",
            Consignee = "ACME",
            NotifyParty = "ACME Notify",
            ShippingMarks = "N/M",
            ExportMode = "0110",
            CommissionRatio = 2m,
            BusinessNature = "自营出口",
            SplitShipment = false,
            InspectionRequirement = "SGS",
            PackagingRequirement = "12 pcs/箱",
            Status = status,
            IsDeleted = deleted,
            CreatedAt = new DateTime(2026, 9, 1, 8, 0, 0)
        };
        order.Details = new List<SalesOrderDetail>
        {
            new()
            {
                ProductId = 101, ProductName = "A 商品", Spec = "红", Unit = "PCS",
                Quantity = 10, UnitPrice = 5, Amount = 50, Remark = "行1"
            },
            new()
            {
                ProductId = 102, ProductName = "B 商品", Spec = "蓝", Unit = "PCS",
                Quantity = 4, UnitPrice = 25, Amount = 100, Remark = "行2"
            }
        };
        SalesOrderController.Calculate(order);
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SalesOrderChangeRequestDetailSaveDto Detail(
        long productId, string productName, decimal quantity, decimal unitPrice,
        bool removed = false, string spec = "红", string unit = "PCS", string remark = "")
        => new()
        {
            ProductId = productId,
            ProductName = productName,
            Spec = spec,
            Unit = unit,
            Quantity = quantity,
            UnitPrice = unitPrice,
            Removed = removed,
            Remark = remark
        };

    private static SalesOrderChangeRequestSaveDto SaveDto(
        long salesOrderId = 0, string reason = "客户要求改数量与交期",
        List<SalesOrderChangeRequestDetailSaveDto>? details = null)
        => new() { SalesOrderId = salesOrderId, Reason = reason, Details = details };

    private static async Task<SalesOrderChangeRequestDto> CreateAsync(
        SalesOrderChangeRequestController controller, long salesOrderId,
        string reason = "客户要求改数量与交期",
        List<SalesOrderChangeRequestDetailSaveDto>? details = null)
        => AssertOk<SalesOrderChangeRequestDto>(
            await controller.Create(SaveDto(salesOrderId, reason, details)));

    private static async Task<SalesOrderChangeRequestDto> UpdateAsync(
        SalesOrderChangeRequestController controller, long id, SalesOrderChangeRequestSaveDto dto)
        => AssertOk<SalesOrderChangeRequestDto>(await controller.Update(id, dto));

    private static async Task<SalesOrderChangeRequestDto> SubmitAsync(
        SalesOrderChangeRequestController controller, long id)
        => AssertOk<SalesOrderChangeRequestDto>(await controller.Submit(id));

    private static async Task<SalesOrderChangeRequestDto> CancelAsync(
        SalesOrderChangeRequestController controller, long id, string reason = "客户撤回变更要求")
        => AssertOk<SalesOrderChangeRequestDto>(
            await controller.Cancel(id, new SalesOrderChangeRequestCancelRequest { Reason = reason }));

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

    private static SalesOrderChangeRequestFieldComparisonDto Comparison(
        SalesOrderChangeRequestDto dto, string field)
        => dto.HeaderComparisons.Single(c => c.Field == field);

    // ==================== 1. 草稿登记与来源快照 ====================

    [Fact]
    public async Task 草稿登记_生成申请单号并冻结来源主表与明细快照()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO2026-0001");
        var controller = BuildController(db);

        var created = await CreateAsync(controller, order.Id, "客户要求改数量与交期");

        Assert.StartsWith("SOC", created.RequestNo, StringComparison.Ordinal);
        Assert.Equal(order.Id, created.SalesOrderId);
        Assert.Equal("SO2026-0001", created.SalesOrderNo);
        Assert.Equal("待提交", created.SourceStatusText);
        Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, created.Status);
        Assert.True(created.IsDraft);
        Assert.True(created.Editable);
        Assert.False(created.IsSubmitted);
        Assert.False(created.IsCancelled);
        Assert.Contains("草稿", created.StatusText);

        /* 来源快照：总额 / 定金 / 状态 / 更新时间 / 明细签名 / 快照标记全部由服务端写入 */
        Assert.Equal(150m, created.SourceTotalAmount);
        Assert.Equal(45m, created.SourceDepositAmount);
        /* 来源最后更新时间 = 来源订单 UpdatedAt ?? CreatedAt（服务端快照口径） */
        Assert.Equal(order.UpdatedAt, created.SourceUpdatedAt);
        Assert.Contains("SO2026-0001", created.SourceSnapshotMarker);
        Assert.Contains("明细 2 行", created.SourceSnapshotMarker);
        Assert.Contains("总额 150", created.SourceSnapshotMarker);
        Assert.Null(created.SubmittedAt);
        Assert.True(created.SourceAvailable);
        Assert.False(created.SourceChanged);
        Assert.Contains("未发生变化", created.SourceChangedText);
        Assert.Equal("客户要求改数量与交期", created.Reason);
    }

    [Fact]
    public async Task 草稿登记_缺省拟议值等于来源快照_来源订单与下游记录保持不变()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);

        var created = await CreateAsync(controller, order.Id);

        /* 拟议基线 = 来源快照：不传拟议值不会产生任何隐性修改 */
        Assert.Equal(created.SourceTotalAmount, created.ProposedTotalAmount);
        Assert.Equal(created.SourceDepositAmount, created.ProposedDepositAmount);
        Assert.Equal(0, created.HeaderChangedCount);
        Assert.Equal(0, created.ChangedLineCount);
        Assert.Equal(0, created.AddedLineCount);
        Assert.Equal(0, created.RemovedLineCount);
        Assert.Contains("完全一致", created.ChangeSummaryText);
        Assert.Equal(2, created.Details.Count);
        Assert.All(created.Details, d => Assert.True(d.HasSourceLine));
        Assert.All(created.Details, d => Assert.False(d.Changed));

        /* 来源主表与明细逐字段未变 */
        var orderAfter = await db.SalesOrders.AsNoTracking().Include(o => o.Details)
            .SingleAsync(o => o.Id == order.Id);
        Assert.Equal("SO2026001", orderAfter.OrderNo);
        Assert.Equal(150m, orderAfter.TotalAmount);
        Assert.Equal(45m, orderAfter.DepositAmount);
        Assert.Equal(30m, orderAfter.DepositRatio);
        Assert.Equal("原始备注", orderAfter.Remark);
        /* 变更申请只是只读快照：来源订单的最后更新时间没有被申请登记 / 编辑动作改写 */
        Assert.Equal(order.UpdatedAt, orderAfter.UpdatedAt);
        Assert.Equal(2, orderAfter.Details.Count(d => !d.IsDeleted));
        Assert.Equal(50m, orderAfter.Details.Single(d => d.ProductId == 101).Amount);
        Assert.Equal(10m, orderAfter.Details.Single(d => d.ProductId == 101).Quantity);

        /* 本模块只写自己的两张表 */
        Assert.Equal(1, await db.SalesOrderChangeRequests.CountAsync());
        Assert.Equal(2, await db.SalesOrderChangeRequestDetails.CountAsync());
        Assert.Equal(0, await db.Quotations.CountAsync());
        Assert.Equal(0, await db.StockOuts.CountAsync());
        Assert.Equal(0, await db.FinanceReceipts.CountAsync());
        Assert.Equal(0, await db.Stocks.CountAsync());
        Assert.Equal(0, await db.StockMovements.CountAsync());
        Assert.Equal(0, await db.TradeDocuments.CountAsync());
        Assert.Equal(0, await db.FinanceExpenses.CountAsync());
    }

    [Fact]
    public async Task 草稿登记_来源不存在或已删除_拒绝()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedSalesOrder(db, "SO-DELETED", deleted: true);
        var controller = BuildController(db);

        var missing = await AssertBusinessAsync(ErrorCodes.NotFound, () => CreateAsync(controller, 9999));
        Assert.Contains("不存在或已删除", missing.Message);

        var removed = await AssertBusinessAsync(ErrorCodes.NotFound, () => CreateAsync(controller, deleted.Id));
        Assert.Contains("不存在或已删除", removed.Message);

        Assert.Equal(0, await db.SalesOrderChangeRequests.CountAsync());
    }

    [Fact]
    public async Task 草稿登记_来源已作废_拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db, "SO-CANCELLED", status: DocumentStatus.Cancelled);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => CreateAsync(controller, order.Id));
        Assert.Contains("已作废", ex.Message);
        Assert.Equal(0, await db.SalesOrderChangeRequests.CountAsync());
    }

    [Fact]
    public async Task 草稿登记_未选择来源_拒绝()
    {
        using var db = TestDbFactory.Create();
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => CreateAsync(controller, 0));
        Assert.Contains("请选择", ex.Message);
    }

    [Fact]
    public async Task 草稿登记_变更原因为空或超长_拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);

        var empty = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => CreateAsync(controller, order.Id, "   "));
        Assert.Contains("变更原因", empty.Message);

        var tooLong = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => CreateAsync(controller, order.Id, new string('长', 501)));
        Assert.Contains("500", tooLong.Message);
    }

    // ==================== 2. 来源可用性与来源变化检测 ====================

    [Fact]
    public async Task 来源订单被删除_申请仍可读并标注来源不可用()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        var created = await CreateAsync(controller, order.Id);

        var stored = await db.SalesOrders.SingleAsync(o => o.Id == order.Id);
        stored.IsDeleted = true;
        await db.SaveChangesAsync();

        var detail = AssertOk<SalesOrderChangeRequestDto>(await controller.GetById(created.Id));
        Assert.False(detail.SourceAvailable);
        Assert.False(detail.SourceChanged);
        Assert.Contains("已不存在或已删除", detail.SourceAvailabilityText);
        Assert.Contains("无法核对", detail.SourceChangedText);
        /* 历史快照照常可读 */
        Assert.Equal("SO2026001", detail.SalesOrderNo);
        Assert.Equal(150m, detail.SourceTotalAmount);
        Assert.Equal(2, detail.Details.Count);
    }

    [Fact]
    public async Task 来源订单变化_显式提示且不覆盖拟议值()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        var created = await CreateAsync(controller, order.Id, "先改数量",
            new List<SalesOrderChangeRequestDetailSaveDto>
            {
                Detail(101, "A 商品", 20, 5),
                Detail(102, "B 商品", 4, 25)
            });

        Assert.Equal(200m, created.ProposedTotalAmount);
        Assert.False(created.SourceChanged);

        /* 来源订单在快照之后被修改（总额 / 备注 / 更新时间 / 明细都变） */
        var stored = await db.SalesOrders.Include(o => o.Details).SingleAsync(o => o.Id == order.Id);
        stored.TotalAmount = 999m;
        stored.Remark = "来源已被业务改过";
        stored.Details.First(d => d.ProductId == 101).Quantity = 11;
        await db.SaveChangesAsync();
        var modifiedUpdatedAt = stored.UpdatedAt;
        Assert.NotNull(modifiedUpdatedAt);
        Assert.NotEqual(created.SourceUpdatedAt, modifiedUpdatedAt);   /* 快照口径下确实已变化 */

        var detail = AssertOk<SalesOrderChangeRequestDto>(await controller.GetById(created.Id));
        Assert.True(detail.SourceChanged);
        Assert.Contains("已发生变化", detail.SourceChangedText);
        Assert.Contains("不覆盖", detail.SourceChangedText);
        Assert.Contains("最后更新时间", detail.SourceChangeDetailText);
        Assert.Contains("订单总额", detail.SourceChangeDetailText);
        Assert.Contains("明细签名", detail.SourceChangeDetailText);

        /* 拟议值保持原样（不覆盖、不合并、不静默刷新）；来源列是**登记时快照**，不是实时来源值 */
        Assert.Equal(200m, detail.ProposedTotalAmount);
        Assert.Equal("150", Comparison(detail, "totalAmount").SourceText);
        Assert.Equal("200", Comparison(detail, "totalAmount").ProposedText);
        Assert.True(Comparison(detail, "totalAmount").Changed);
        Assert.Equal(20m, detail.Details.Single(d => d.HasSourceLine && d.ProposedProductId == 101).ProposedQuantity);

        /* 再保存一次只改拟议，永远不写回来源 */
        var saved = await UpdateAsync(controller, created.Id, SaveDto(reason: "再调整",
            details: new List<SalesOrderChangeRequestDetailSaveDto>
            {
                Detail(101, "A 商品", 30, 5),
                Detail(102, "B 商品", 4, 25)
            }));
        Assert.Equal(250m, saved.ProposedTotalAmount);
        Assert.True(saved.SourceChanged);

        var sourceAfter = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(999m, sourceAfter.TotalAmount);
        Assert.Equal("来源已被业务改过", sourceAfter.Remark);
        Assert.Equal(modifiedUpdatedAt, sourceAfter.UpdatedAt);
    }

    [Fact]
    public async Task 来源状态变化_也被检测为来源已变化()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        var created = await CreateAsync(controller, order.Id);

        var stored = await db.SalesOrders.SingleAsync(o => o.Id == order.Id);
        stored.Status = DocumentStatus.Approved;
        await db.SaveChangesAsync();

        var detail = AssertOk<SalesOrderChangeRequestDto>(await controller.GetById(created.Id));
        Assert.True(detail.SourceChanged);
        Assert.Contains("状态", detail.SourceChangeDetailText);
        Assert.Equal("待提交", detail.SourceStatusText);
    }

    // ==================== 3. 拟议金额：服务端唯一权威算法 ====================

    [Fact]
    public async Task 拟议金额_由服务端按销售订单权威算法重算()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        var created = await CreateAsync(controller, order.Id);

        var dto = SaveDto(reason: "改数量与定金比例");
        dto.DepositRatio = 20m;
        dto.Details = new List<SalesOrderChangeRequestDetailSaveDto>
        {
            Detail(101, "A 商品", 20, 5),                             /* 100（数量由 10 改为 20） */
            Detail(102, "B 商品", 4, 25, spec: "蓝", remark: "行2")   /* 100（与来源完全一致） */
        };

        var saved = await UpdateAsync(controller, created.Id, dto);

        /* 总额 = Σ 数量 × 单价 = 200；定金 = 200 × 20% = 40（销售订单唯一权威算法） */
        Assert.Equal(200m, saved.ProposedTotalAmount);
        Assert.Equal(40m, saved.ProposedDepositAmount);
        Assert.Equal(200m, saved.Details.Sum(d => d.ProposedAmount));
        Assert.Equal(100m, saved.Details.Single(d => d.ProposedProductId == 101).ProposedAmount);
        Assert.Equal(20m * 5m, saved.Details.Single(d => d.ProposedProductId == 101).ProposedAmount);

        /* 来源快照与来源订单都不受影响 */
        Assert.Equal(150m, saved.SourceTotalAmount);
        Assert.Equal(45m, saved.SourceDepositAmount);
        var sourceAfter = await db.SalesOrders.AsNoTracking().Include(o => o.Details)
            .SingleAsync(o => o.Id == order.Id);
        Assert.Equal(150m, sourceAfter.TotalAmount);
        Assert.Equal(45m, sourceAfter.DepositAmount);
        Assert.Equal(30m, sourceAfter.DepositRatio);
        Assert.Equal(10m, sourceAfter.Details.Single(d => d.ProductId == 101).Quantity);
        Assert.Equal(order.UpdatedAt, sourceAfter.UpdatedAt);

        /* 差异统计：只有第 1 行数量变化 */
        Assert.Equal(1, saved.ChangedLineCount);
        Assert.Equal(0, saved.AddedLineCount);
        Assert.Equal(0, saved.RemovedLineCount);
        Assert.Equal("20", Comparison(saved, "depositRatio").ProposedText);
        Assert.True(Comparison(saved, "totalAmount").Changed);
    }

    [Fact]
    public void 拟议报文契约_不含任何客户端金额或审批字段()
    {
        foreach (var type in new[] { typeof(SalesOrderChangeRequestSaveDto), typeof(SalesOrderChangeRequestDetailSaveDto) })
        {
            var names = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToList();

            Assert.DoesNotContain(names, n => n.Contains("Amount", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("Total", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("Approved", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("Applied", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task 拟议明细_数量非正或单价为负_拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        var created = await CreateAsync(controller, order.Id);

        var zeroQuantity = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UpdateAsync(
            controller, created.Id, SaveDto(details: new List<SalesOrderChangeRequestDetailSaveDto>
            {
                Detail(101, "A 商品", 0, 5),
                Detail(102, "B 商品", 4, 25)
            })));
        Assert.Contains("数量", zeroQuantity.Message);

        var negativePrice = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UpdateAsync(
            controller, created.Id, SaveDto(details: new List<SalesOrderChangeRequestDetailSaveDto>
            {
                Detail(101, "A 商品", 10, -1),
                Detail(102, "B 商品", 4, 25)
            })));
        Assert.Contains("单价", negativePrice.Message);

        /* 失败后草稿拟议值保持原样（不写入部分非法数据） */
        var after = AssertOk<SalesOrderChangeRequestDto>(await controller.GetById(created.Id));
        Assert.Equal(150m, after.ProposedTotalAmount);
        Assert.Equal(10m, after.Details.Single(d => d.ProposedProductId == 101).ProposedQuantity);
    }

    [Fact]
    public async Task 拟议主表值_定金比例与佣金比例越界_拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        var created = await CreateAsync(controller, order.Id);

        var badDeposit = SaveDto(details: new List<SalesOrderChangeRequestDetailSaveDto>
        {
            Detail(101, "A 商品", 10, 5, remark: "行1"),
            Detail(102, "B 商品", 4, 25, spec: "蓝", remark: "行2")
        });
        badDeposit.DepositRatio = 101m;
        var depositEx = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => UpdateAsync(controller, created.Id, badDeposit));
        Assert.Contains("定金比例", depositEx.Message);

        var badCommission = SaveDto(details: new List<SalesOrderChangeRequestDetailSaveDto>
        {
            Detail(101, "A 商品", 10, 5, remark: "行1"),
            Detail(102, "B 商品", 4, 25, spec: "蓝", remark: "行2")
        });
        badCommission.CommissionRatio = -0.5m;
        var commissionEx = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => UpdateAsync(controller, created.Id, badCommission));
        Assert.Contains("佣金比例", commissionEx.Message);
    }

    [Fact]
    public async Task 来源订单汇率缺省为零_拒绝建立申请()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        order.ExchangeRate = 0m;
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => CreateAsync(controller, order.Id));
        Assert.Contains("汇率", ex.Message);
    }

    [Fact]
    public async Task 拟议主表值_文本超长或含控制字符_拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        var created = await CreateAsync(controller, order.Id);

        var tooLong = SaveDto(reason: "改备注");
        tooLong.Remark = new string('备', 501);
        var lengthEx = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => UpdateAsync(controller, created.Id, tooLong));
        Assert.Contains("拟议备注", lengthEx.Message);

        var controlChar = SaveDto(reason: "改合同号");
        controlChar.ContractNo = "CT-1\u0001";
        var controlEx = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => UpdateAsync(controller, created.Id, controlChar));
        Assert.Contains("控制字符", controlEx.Message);
    }

    // ==================== 4. 明细：新增 / 移除 / 修改与整体替换语义 ====================

    [Fact]
    public async Task 拟议明细_新增行与移除行对照正确且来源快照保留()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        var created = await CreateAsync(controller, order.Id);

        var saved = await UpdateAsync(controller, created.Id, SaveDto(reason: "换货",
            details: new List<SalesOrderChangeRequestDetailSaveDto>
            {
                Detail(101, "A 商品", 10, 5, remark: "行1"),                   /* 保持（与来源完全一致） */
                Detail(102, "B 商品", 4, 25, removed: true, spec: "蓝"),         /* 移除 */
                Detail(103, "C 商品", 30, 2, spec: "绿")                        /* 新增 60 */
            }));

        Assert.Equal(3, saved.Details.Count);
        Assert.Equal(1, saved.AddedLineCount);
        Assert.Equal(1, saved.RemovedLineCount);
        Assert.Equal(0, saved.ChangedLineCount);
        Assert.Equal(110m, saved.ProposedTotalAmount);   /* 50 + 60，移除行不计入 */
        Assert.Equal(33m, saved.ProposedDepositAmount);  /* 110 × 30% */

        var removed = saved.Details.Single(d => d.ProposedRemoved);
        Assert.True(removed.HasSourceLine);
        Assert.Contains("移除", removed.ComparisonText);
        /* 移除行一律回到来源快照，不发明新值 */
        Assert.Equal(102, removed.ProposedProductId);
        Assert.Equal(4m, removed.ProposedQuantity);
        Assert.Equal(25m, removed.ProposedUnitPrice);
        Assert.Equal(100m, removed.ProposedAmount);

        var added = saved.Details.Single(d => !d.HasSourceLine);
        Assert.False(added.ProposedRemoved);
        Assert.Equal("拟议新增行（来源没有对应行）", added.ComparisonText);
        Assert.Equal(103, added.ProposedProductId);
        Assert.Equal(60m, added.ProposedAmount);

        /* 来源明细两行都还在（快照永不被删除） */
        var storedRows = await db.SalesOrderChangeRequestDetails.AsNoTracking()
            .Where(d => d.ChangeRequestId == created.Id && !d.IsDeleted)
            .ToListAsync();
        Assert.Equal(3, storedRows.Count);
        Assert.Equal(2, storedRows.Count(r => r.HasSourceLine));
        Assert.Equal(2, await db.SalesOrderDetails.CountAsync(d => d.SalesOrderId == order.Id));
    }

    [Fact]
    public async Task 拟议明细_空集合表示回到来源快照()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        var created = await CreateAsync(controller, order.Id);

        var modified = await UpdateAsync(controller, created.Id, SaveDto(details:
            new List<SalesOrderChangeRequestDetailSaveDto>
            {
                Detail(101, "A 商品", 99, 5),
                Detail(102, "B 商品", 4, 25, spec: "蓝", remark: "行2"),
                Detail(103, "C 商品", 1, 1, spec: "绿")
            }));
        Assert.Equal(1, modified.AddedLineCount);
        Assert.Equal(1, modified.ChangedLineCount);

        /* 传空集合 = 拟议回到来源快照（原样留痕），来源行不会被删除 */
        var reset = await UpdateAsync(controller, created.Id, SaveDto(reason: "撤销拟议改动"));
        Assert.Equal(0, reset.AddedLineCount);
        Assert.Equal(0, reset.RemovedLineCount);
        Assert.Equal(0, reset.ChangedLineCount);
        Assert.Equal(150m, reset.ProposedTotalAmount);
        Assert.Equal(45m, reset.ProposedDepositAmount);
        Assert.Equal(2, reset.Details.Count);
        Assert.All(reset.Details, d => Assert.True(d.HasSourceLine));

        /* 旧的拟议新增行按软删除留痕，不物理删除 */
        var allRows = await db.SalesOrderChangeRequestDetails.AsNoTracking()
            .Where(d => d.ChangeRequestId == created.Id)
            .ToListAsync();
        Assert.Equal(1, allRows.Count(r => !r.HasSourceLine && r.IsDeleted));
    }

    [Fact]
    public async Task 拟议明细_行数不足或全部移除或超出上限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        var created = await CreateAsync(controller, order.Id);

        var tooFew = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UpdateAsync(
            controller, created.Id, SaveDto(details: new List<SalesOrderChangeRequestDetailSaveDto>
            {
                Detail(101, "A 商品", 10, 5)
            })));
        Assert.Contains("来源明细行", tooFew.Message);

        var allRemoved = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UpdateAsync(
            controller, created.Id, SaveDto(details: new List<SalesOrderChangeRequestDetailSaveDto>
            {
                Detail(101, "A 商品", 10, 5, removed: true),
                Detail(102, "B 商品", 4, 25, removed: true)
            })));
        Assert.Contains("至少保留一行", allRemoved.Message);

        var removedNewLine = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UpdateAsync(
            controller, created.Id, SaveDto(details: new List<SalesOrderChangeRequestDetailSaveDto>
            {
                Detail(101, "A 商品", 10, 5),
                Detail(102, "B 商品", 4, 25),
                Detail(103, "C 商品", 1, 1, removed: true)
            })));
        Assert.Contains("新增行不能标记", removedNewLine.Message);

        var rows = new List<SalesOrderChangeRequestDetailSaveDto>
        {
            Detail(101, "A 商品", 10, 5),
            Detail(102, "B 商品", 4, 25)
        };
        for (var i = 0; i < 199; i++) rows.Add(Detail(200 + i, "X 商品", 1, 1));
        var tooMany = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => UpdateAsync(
            controller, created.Id, SaveDto(details: rows)));
        Assert.Contains("200", tooMany.Message);

        /* 被拒绝的请求不写入任何数据 */
        var after = AssertOk<SalesOrderChangeRequestDto>(await controller.GetById(created.Id));
        Assert.Equal(150m, after.ProposedTotalAmount);
        Assert.Equal(0, after.AddedLineCount);
    }

    // ==================== 5. 提交 / 取消生命周期（不可变与留痕） ====================

    [Fact]
    public async Task 提交_冻结拟议快照并记录提交时间_之后不可编辑与不可重复提交()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        var created = await CreateAsync(controller, order.Id);
        await UpdateAsync(controller, created.Id, SaveDto(reason: "改数量",
            details: new List<SalesOrderChangeRequestDetailSaveDto>
            {
                Detail(101, "A 商品", 20, 5),
                Detail(102, "B 商品", 4, 25)
            }));

        var submitted = await SubmitAsync(controller, created.Id);

        Assert.Equal(SalesOrderChangeRequestRules.StatusSubmitted, submitted.Status);
        Assert.True(submitted.IsSubmitted);
        Assert.False(submitted.IsDraft);
        Assert.False(submitted.Editable);
        Assert.Contains("未批准、未套用", submitted.StatusText);
        Assert.NotNull(submitted.SubmittedAt);
        Assert.Equal(200m, submitted.ProposedTotalAmount);

        var editAfterSubmit = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => UpdateAsync(
            controller, created.Id, SaveDto(reason: "提交后想再改", details:
                new List<SalesOrderChangeRequestDetailSaveDto>
                {
                    Detail(101, "A 商品", 5, 5),
                    Detail(102, "B 商品", 4, 25)
                })));
        Assert.Contains("已提交", editAfterSubmit.Message);

        var submitAgain = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => SubmitAsync(controller, created.Id));
        Assert.Contains("不能重复提交", submitAgain.Message);

        /* 拟议快照与来源订单都没有被提交动作改写 */
        var mine = await db.SalesOrderChangeRequests.AsNoTracking().Include(x => x.Details)
            .SingleAsync(x => x.Id == created.Id);
        Assert.Equal(200m, mine.ProposedTotalAmount);
        Assert.Equal(20m, mine.Details.Single(d => d.ProposedProductId == 101).ProposedQuantity);
        var sourceOrder = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(150m, sourceOrder.TotalAmount);
        Assert.Equal(order.UpdatedAt, sourceOrder.UpdatedAt);
    }

    [Fact]
    public async Task 取消草稿_必须填原因并保留原始与拟议证据()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        var created = await CreateAsync(controller, order.Id);
        await UpdateAsync(controller, created.Id, SaveDto(reason: "改数量",
            details: new List<SalesOrderChangeRequestDetailSaveDto>
            {
                Detail(101, "A 商品", 20, 5),
                Detail(102, "B 商品", 4, 25)
            }));

        var noReason = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Cancel(created.Id, new SalesOrderChangeRequestCancelRequest { Reason = "  " }));
        Assert.Contains("取消原因", noReason.Message);

        var cancelled = await CancelAsync(controller, created.Id, "客户撤回变更要求");

        Assert.Equal(SalesOrderChangeRequestRules.StatusCancelled, cancelled.Status);
        Assert.True(cancelled.IsCancelled);
        Assert.False(cancelled.Editable);
        Assert.NotNull(cancelled.CancelledAt);
        Assert.Equal("客户撤回变更要求", cancelled.CancelledReason);
        /* 原始与拟议证据保留 */
        Assert.Equal("SO2026001", cancelled.SalesOrderNo);
        Assert.Equal(150m, cancelled.SourceTotalAmount);
        Assert.Equal(200m, cancelled.ProposedTotalAmount);
        Assert.Equal(2, cancelled.Details.Count);

        var editAfterCancel = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => UpdateAsync(
            controller, created.Id, SaveDto(reason: "取消后想改")));
        Assert.Contains("已取消", editAfterCancel.Message);

        var cancelAgain = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => CancelAsync(controller, created.Id));
        Assert.Contains("不能重复取消", cancelAgain.Message);

        var submitAfterCancel = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => SubmitAsync(controller, created.Id));
        Assert.Contains("已取消", submitAfterCancel.Message);

        /* 记录仍在库（软删除都没有发生） */
        var stored = await db.SalesOrderChangeRequests.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.False(stored.IsDeleted);
        Assert.Equal(2, await db.SalesOrderChangeRequestDetails.CountAsync(d => d.ChangeRequestId == created.Id));
    }

    [Fact]
    public async Task 已提交申请仍可取消_取消不改写来源订单()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);
        var created = await CreateAsync(controller, order.Id);
        await SubmitAsync(controller, created.Id);

        var cancelled = await CancelAsync(controller, created.Id, "审批前业务撤回");

        Assert.Equal(SalesOrderChangeRequestRules.StatusCancelled, cancelled.Status);
        Assert.NotNull(cancelled.SubmittedAt);
        Assert.Equal("审批前业务撤回", cancelled.CancelledReason);
        Assert.Equal(0, await db.FinanceReceipts.CountAsync());
        Assert.Equal(0, await db.StockOuts.CountAsync());
        Assert.Equal(0, await db.TradeDocuments.CountAsync());
        Assert.Equal(order.UpdatedAt,
            (await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).UpdatedAt);
    }

    // ==================== 6. 台账读取：分页 / 过滤 / 有界 ====================

    [Fact]
    public async Task 台账_分页与过滤有界_每页上限收敛()
    {
        using var db = TestDbFactory.Create();
        var first = SeedSalesOrder(db, "SO-A");
        var second = SeedSalesOrder(db, "SO-B");
        var controller = BuildController(db);

        await CreateAsync(controller, first.Id, "A1 草稿");
        var submitted = await CreateAsync(controller, first.Id, "A2 提交");
        await SubmitAsync(controller, submitted.Id);
        var cancelled = await CreateAsync(controller, second.Id, "B1 取消");
        await CancelAsync(controller, cancelled.Id, "撤回");

        var all = AssertOk<PagedResult<SalesOrderChangeRequestDto>>(
            await controller.GetPaged(new SalesOrderChangeRequestQuery()));
        Assert.Equal(3, all.Total);
        Assert.Equal(3, all.Items.Count);

        var byOrder = AssertOk<PagedResult<SalesOrderChangeRequestDto>>(
            await controller.GetPaged(new SalesOrderChangeRequestQuery { SalesOrderId = first.Id }));
        Assert.Equal(2, byOrder.Total);
        Assert.All(byOrder.Items, item => Assert.Equal(first.Id, item.SalesOrderId));

        var drafts = AssertOk<PagedResult<SalesOrderChangeRequestDto>>(
            await controller.GetPaged(new SalesOrderChangeRequestQuery
            {
                Status = SalesOrderChangeRequestRules.StatusDraft
            }));
        Assert.Equal(1, drafts.Total);
        Assert.All(drafts.Items, item => Assert.True(item.IsDraft));

        var submittedOnly = AssertOk<PagedResult<SalesOrderChangeRequestDto>>(
            await controller.GetPaged(new SalesOrderChangeRequestQuery
            {
                Status = SalesOrderChangeRequestRules.StatusSubmitted
            }));
        Assert.Equal(1, submittedOnly.Total);

        var cancelledOnly = AssertOk<PagedResult<SalesOrderChangeRequestDto>>(
            await controller.GetPaged(new SalesOrderChangeRequestQuery
            {
                Status = SalesOrderChangeRequestRules.StatusCancelled
            }));
        Assert.Equal(1, cancelledOnly.Total);

        var keyword = AssertOk<PagedResult<SalesOrderChangeRequestDto>>(
            await controller.GetPaged(new SalesOrderChangeRequestQuery { Keyword = "A2 提交" }));
        Assert.Equal(1, keyword.Total);

        /* 每页条数收敛到上限（有界） */
        var capped = AssertOk<PagedResult<SalesOrderChangeRequestDto>>(
            await controller.GetPaged(new SalesOrderChangeRequestQuery { PageSize = 500 }));
        Assert.Equal(SalesOrderChangeRequestQuery.MaxPageSize, capped.PageSize);

        /* 非法筛选一律拒绝，不做静默兜底 */
        var badStatus = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new SalesOrderChangeRequestQuery { Status = 9 }));
        Assert.Contains("状态筛选", badStatus.Message);

        var badSource = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new SalesOrderChangeRequestQuery { SalesOrderId = -1 }));
        Assert.Contains("来源销售订单 Id", badSource.Message);

        var longKeyword = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new SalesOrderChangeRequestQuery
            {
                Keyword = new string('k', SalesOrderChangeRequestService.MaxKeywordLength + 1)
            }));
        Assert.Contains("关键字", longKeyword.Message);
    }

    [Fact]
    public async Task 单据行入口_按来源清单有界返回()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var other = SeedSalesOrder(db, "SO-OTHER");
        var controller = BuildController(db);

        await CreateAsync(controller, order.Id, "1");
        await CreateAsync(controller, order.Id, "2");
        await CreateAsync(controller, other.Id, "3");

        var list = AssertOk<List<SalesOrderChangeRequestDto>>(
            await controller.GetForSource(order.Id, SalesOrderChangeRequestService.MaxPerSourceOrder));
        Assert.Equal(2, list.Count);
        Assert.All(list, item => Assert.Equal(order.Id, item.SalesOrderId));

        var bounded = AssertOk<List<SalesOrderChangeRequestDto>>(await controller.GetForSource(order.Id, 1));
        Assert.Single(bounded);

        var badId = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetForSource(0));
        Assert.Contains("来源销售订单 Id", badId.Message);
    }

    [Fact]
    public async Task 历史订单_没有变更申请时行为不变()
    {
        using var db = TestDbFactory.Create();
        var order = SeedSalesOrder(db);
        var controller = BuildController(db);

        var list = AssertOk<List<SalesOrderChangeRequestDto>>(await controller.GetForSource(order.Id));
        Assert.Empty(list);
        Assert.Equal(0, await db.SalesOrderChangeRequests.CountAsync());
        Assert.Equal(0, await db.SalesOrderChangeRequestDetails.CountAsync());

        /* 既有订单本身照常可读（没有回填、没有新增列、没有状态变化） */
        var stored = await db.SalesOrders.AsNoTracking().Include(o => o.Details)
            .SingleAsync(o => o.Id == order.Id);
        Assert.Equal(150m, stored.TotalAmount);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(order.UpdatedAt, stored.UpdatedAt);
        Assert.Equal(2, stored.Details.Count);
    }

    [Fact]
    public async Task 来源候选_排除已删除_标注已作废不可选择()
    {
        using var db = TestDbFactory.Create();
        SeedSalesOrder(db, "SO-NORMAL");
        SeedSalesOrder(db, "SO-DELETED", deleted: true);
        var cancelled = SeedSalesOrder(db, "SO-CANCELLED", status: DocumentStatus.Cancelled);
        var controller = BuildController(db);

        var options = AssertOk<List<SalesOrderChangeRequestSourceOptionDto>>(
            await controller.SourceOptions(null, SalesOrderChangeRequestService.MaxSourceOptions));

        Assert.Equal(2, options.Count);
        Assert.DoesNotContain(options, o => o.OrderNo == "SO-DELETED");
        Assert.True(options.Single(o => o.OrderNo == "SO-NORMAL").Selectable);
        var cancelledOption = options.Single(o => o.SalesOrderId == cancelled.Id);
        Assert.False(cancelledOption.Selectable);
        Assert.Contains("不可选择", cancelledOption.SelectableText);
        Assert.Contains("已取消", cancelledOption.SummaryText);

        var filtered = AssertOk<List<SalesOrderChangeRequestSourceOptionDto>>(
            await controller.SourceOptions("SO-NORMAL", 200));
        Assert.Single(filtered);
    }

    [Fact]
    public void 模块元数据_状态白名单与口径文案与服务端同源()
    {
        using var db = TestDbFactory.Create();
        var meta = AssertOk<SalesOrderChangeRequestMetadataDto>(BuildController(db).Metadata());

        Assert.Equal(3, meta.StatusOptions.Count);
        Assert.Equal(new[] { "0", "1", "2" }, meta.StatusOptions.Select(o => o.Value).ToArray());
        Assert.Contains(meta.StatusOptions, o => o.Value == "1" && o.Label.Contains("未批准"));
        Assert.Equal(6, meta.CurrencyOptions.Count);
        Assert.Contains(meta.CurrencyOptions, o => o.Value == "USD" && o.Label.Contains("美元"));
        Assert.Equal(SalesOrderChangeRequestQuery.MaxPageSize, meta.MaxPageSize);
        Assert.Equal(SalesOrderChangeRequestRules.MaxDetailLines, meta.MaxDetailLines);
        Assert.Contains("SalesOrderAmountRules", meta.AmountPolicyText);
        Assert.Contains("不套用变更", meta.ApprovalBoundaryText);
        Assert.Contains("不改写来源销售订单", meta.BoundaryText);
        Assert.Contains("冻结", meta.SubmitPolicyText);
        Assert.Contains("冻结来源销售订单", meta.SnapshotPolicyText);
    }

    // ==================== 7. 控制器 / 模型 / 幂等结构契约 ====================

    [Fact]
    public void 控制器契约_不提供删除或审批接口_只有提交与取消的动作式接口()
    {
        var type = typeof(SalesOrderChangeRequestController);
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToList();

        Assert.DoesNotContain(methods, m => m.GetCustomAttributes<HttpDeleteAttribute>(true).Any());
        Assert.DoesNotContain(methods, m => m.GetCustomAttributes<HttpPatchAttribute>(true).Any());
        Assert.Contains(methods, m => m.Name == "Update"
            && m.GetCustomAttributes<HttpPutAttribute>(true).Any());
        Assert.Contains(methods, m => m.Name == "Submit"
            && m.GetCustomAttributes<HttpPostAttribute>(true).Any());
        Assert.Contains(methods, m => m.Name == "Cancel"
            && m.GetCustomAttributes<HttpPostAttribute>(true).Any());
        Assert.Contains(methods, m => m.Name == "GetPaged"
            && m.GetCustomAttributes<HttpGetAttribute>(true).Any());
        Assert.Contains(methods, m => m.Name == "Metadata"
            && m.GetCustomAttributes<HttpGetAttribute>(true).Any());
        Assert.Contains(methods, m => m.Name == "SourceOptions"
            && m.GetCustomAttributes<HttpGetAttribute>(true).Any());
        Assert.Contains(methods, m => m.Name == "GetForSource"
            && m.GetCustomAttributes<HttpGetAttribute>(true).Any());

        /* 控制器上没有「审批 / 套用」这类名字的动作（本模块不定义审批） */
        Assert.DoesNotContain(methods, m => m.Name.Contains("Approve", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methods, m => m.Name.Contains("Apply", StringComparison.OrdinalIgnoreCase));

        var route = type.GetCustomAttributes<RouteAttribute>(true).Single().Template;
        Assert.Equal("api/sales-order-change-requests", route);
    }

    [Fact]
    public void 数据上下文_注册两张表并配置唯一索引与精度_且不建到销售订单的外键()
    {
        using var db = TestDbFactory.Create();
        Assert.NotNull(db.SalesOrderChangeRequests);
        Assert.NotNull(db.SalesOrderChangeRequestDetails);

        var entityType = db.Model.FindEntityType(typeof(SalesOrderChangeRequest));
        Assert.NotNull(entityType);

        var indexes = entityType!.GetIndexes().ToList();
        var unique = indexes.Single(i => i.GetDatabaseName() == "UX_SalesOrderChangeRequests_RequestNo");
        Assert.True(unique.IsUnique);
        Assert.Contains("IsDeleted = 0", unique.GetFilter() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(indexes, i => i.GetDatabaseName() == "IX_SalesOrderChangeRequests_SourceOrder_Status");
        Assert.Contains(indexes, i => i.GetDatabaseName() == "IX_SalesOrderChangeRequests_Status_CreatedAt");

        var total = entityType.FindProperty(nameof(SalesOrderChangeRequest.ProposedTotalAmount));
        Assert.NotNull(total);
        Assert.Equal(18, total!.GetPrecision());
        Assert.Equal(4, total.GetScale());
        var rate = entityType.FindProperty(nameof(SalesOrderChangeRequest.ProposedExchangeRate));
        Assert.Equal(18, rate!.GetPrecision());
        Assert.Equal(6, rate.GetScale());

        /* 来源销售订单只存快照：刻意不建到 SalesOrders / SalesOrderDetails 的外键 */
        Assert.DoesNotContain(entityType.GetForeignKeys(), fk =>
            fk.PrincipalEntityType.ClrType == typeof(SalesOrder)
            || fk.PrincipalEntityType.ClrType == typeof(SalesOrderDetail));

        var detailType = db.Model.FindEntityType(typeof(SalesOrderChangeRequestDetail));
        Assert.NotNull(detailType);
        Assert.Contains(detailType!.GetIndexes(),
            i => i.GetDatabaseName() == "IX_SalesOrderChangeRequestDetails_Request_LineNo");
        var headerFk = detailType.GetForeignKeys()
            .Single(fk => fk.PrincipalEntityType.ClrType == typeof(SalesOrderChangeRequest));
        Assert.Equal(DeleteBehavior.Cascade, headerFk.DeleteBehavior);

        /* 非持久化读取标注不落库 */
        var mappedNames = entityType.GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(nameof(SalesOrderChangeRequest.SourceChanged), mappedNames);
        Assert.DoesNotContain(nameof(SalesOrderChangeRequest.BoundaryText), mappedNames);
        Assert.DoesNotContain(nameof(SalesOrderChangeRequest.StatusText), mappedNames);
    }

    [Fact]
    public void 结构升级_第33段幂等建表且不改写既有数据()
    {
        var path = RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs");
        Assert.True(File.Exists(path), "SchemaUpgrader.cs 不存在：" + path);

        var lines = File.ReadAllLines(path);
        var start = Array.FindIndex(lines, line => line.Contains("33. 销售订单变更申请登记", StringComparison.Ordinal));
        Assert.True(start >= 0, "SchemaUpgrader 缺少 ERP-047 第 33 段（幂等建表）");

        var sql = string.Join('\n', lines.Skip(start)
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.Contains("IF OBJECT_ID('db_owner.SalesOrderChangeRequests') IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("IF OBJECT_ID('db_owner.SalesOrderChangeRequestDetails') IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("UX_SalesOrderChangeRequests_RequestNo", sql, StringComparison.Ordinal);
        Assert.Contains("IX_SalesOrderChangeRequests_SourceOrder_Status", sql, StringComparison.Ordinal);
        Assert.Contains("IX_SalesOrderChangeRequests_Status_CreatedAt", sql, StringComparison.Ordinal);
        Assert.Contains("IX_SalesOrderChangeRequestDetails_Request_LineNo", sql, StringComparison.Ordinal);
        Assert.Contains("FK_SalesOrderChangeRequestDetails_Request", sql, StringComparison.Ordinal);
        Assert.Contains("DECIMAL(18,4)", sql, StringComparison.Ordinal);
        Assert.Contains("DECIMAL(18,6)", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE IsDeleted = 0", sql, StringComparison.Ordinal);

        /* 幂等补齐不得回填 / 改写既有数据，也不得改动来源订单表 */
        foreach (var forbidden in new[] { "UPDATE ", "DELETE ", "DROP ", "MERGE ", "TRUNCATE " })
            Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("ALTER TABLE db_owner.SalesOrders", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE db_owner.SalesOrderDetails", sql, StringComparison.OrdinalIgnoreCase);
    }

    // ==================== 8. 唯一权威算法复用与单号字轨 ====================

    [Fact]
    public void 服务与控制器_复用销售订单唯一权威金额算法_没有第二套公式()
    {
        var servicePath = RepoFile("src", "ERP.Application", "Services", "SalesOrderChangeRequestService.cs");
        var service = File.ReadAllText(servicePath);
        Assert.Contains("SalesOrderAmountRules.ValidateDetailValues", service, StringComparison.Ordinal);
        Assert.Contains("SalesOrderAmountRules.ApplyDetailAmounts", service, StringComparison.Ordinal);
        Assert.Contains("SalesOrderAmountRules.Calculate", service, StringComparison.Ordinal);
        Assert.Contains("SalesOrderAmountRules.Validate", service, StringComparison.Ordinal);
        Assert.Contains("SalesOrderAmountRules.ValidateDepositRatio", service, StringComparison.Ordinal);
        /* 服务内不得自己写金额公式（否则就是第二套引擎） */
        Assert.DoesNotContain("/ 100", service, StringComparison.Ordinal);
        Assert.DoesNotContain(".Sum(d =>", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Quantity * ", service, StringComparison.Ordinal);

        var controllerPath = RepoFile("src", "ERP.Api", "Controllers", "SalesOrderController.cs");
        var salesOrderController = File.ReadAllText(controllerPath);
        Assert.Contains("SalesOrderAmountRules.Calculate(entity)", salesOrderController, StringComparison.Ordinal);
        Assert.Contains("SalesOrderAmountRules.Validate(entity)", salesOrderController, StringComparison.Ordinal);
        Assert.Contains("SalesOrderAmountRules.ApplyDetailAmounts(entity)", salesOrderController, StringComparison.Ordinal);
        Assert.DoesNotContain("entity.DepositAmount = entity.TotalAmount * entity.DepositRatio / 100",
            salesOrderController, StringComparison.Ordinal);

        var rulesPath = RepoFile("src", "ERP.Application", "Services", "SalesOrderAmountRules.cs");
        Assert.True(File.Exists(rulesPath), "SalesOrderAmountRules.cs 不存在：" + rulesPath);
    }

    [Fact]
    public async Task 单号字轨_变更申请使用专属单据类型与SOC前缀()
    {
        Assert.Equal(23, (int)DocumentType.SalesOrderChangeRequest);

        using var db = TestDbFactory.Create();
        var no = await new DocumentNumberService(db)
            .GenerateAsync(DocumentType.SalesOrderChangeRequest, new DateTime(2026, 9, 25));

        Assert.StartsWith("SOC20260925", no, StringComparison.Ordinal);
        /* 不影响既有销售订单字轨 */
        Assert.DoesNotContain("SO20260925", no[3..], StringComparison.Ordinal);
    }

    [Fact]
    public void 前端接线_登记册入口存在且界面不声明已批准不自行改写来源订单()
    {
        var jsPath = RepoFile("src", "ERP.Api", "wwwroot", "js", "sales-order-change-requests.js");
        Assert.True(File.Exists(jsPath), "变更申请前端脚本不存在：" + jsPath);
        var js = File.ReadAllText(jsPath);

        Assert.Contains("openSalesOrderChangeRequests", js, StringComparison.Ordinal);
        Assert.Contains("openSalesOrderChangeRequestsForCurrentModule", js, StringComparison.Ordinal);
        Assert.Contains("scrEsc", js, StringComparison.Ordinal);
        Assert.Contains("scrSubmit", js, StringComparison.Ordinal);
        Assert.Contains("scrSubmitCancel", js, StringComparison.Ordinal);
        Assert.Contains("scrFormFromDto", js, StringComparison.Ordinal);
        /* 界面明确标注金额是预览、由服务端重算 */
        Assert.Contains("预览，服务端重算", js, StringComparison.Ordinal);
        Assert.Contains("未批准", js, StringComparison.Ordinal);

        /* 界面没有「批准 / 套用」动作，也不直接调用销售订单写接口 */
        Assert.DoesNotContain("approve", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/apply", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/sales-orders", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fetch(", js, StringComparison.OrdinalIgnoreCase);

        var indexHtml = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/sales-order-change-requests.js", indexHtml, StringComparison.Ordinal);

        var modulesDoc = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc.js"));
        Assert.Contains("openSalesOrderChangeRequests()", modulesDoc, StringComparison.Ordinal);
        Assert.Contains("openSalesOrderChangeRequestsForCurrentModule", modulesDoc, StringComparison.Ordinal);
    }
}
