using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 代理服务费对账单证据单元测试（ERP-070）。覆盖：**显式来源链接**（只按来源类型 + 持久化 Id，绝不按文本 /
/// 金额 / 相似度猜链接）、客户与协议资格、对账单 / 协议 / 行来源三方的**客户与币种兼容性**、
/// **重复计费保护**（同一来源未作废行内全局唯一 + 对账单身份唯一）、**金额精度与服务端合计**、
/// **到期日留空即未知**（不推算）、草稿 → 登记 → 作废的**历史保留**、模型分离（不是发票 / 收入确认 /
/// 付款通知 / 记账分录，也不改业务员提成口径）、有界查询（固定数据集访问、无逐行查库）、
/// **来源记录非变更**边界，以及模型 / 幂等结构（SchemaUpgrader 第 43 段）/ 前端与路由接线契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本、不开票 / 不记账 /
/// 不收款 / 不联系客户，也不做任何浏览器 / UI 验收（浏览器验收按 browser_deferred 延后）。
/// </summary>
public class AgencyServiceFeeStatementTests
{
    // ==================== 0. 测试脚手架 ====================

    /// <summary>构造控制器并注入测试身份（登记人由服务端按 ClaimsPrincipal 写入；无身份时记「未知用户」）</summary>
    private static AgencyServiceFeeStatementController BuildController(ErpDbContext db, string? userName = null)
    {
        var controller = new AgencyServiceFeeStatementController(db);
        var identity = new ClaimsIdentity(
            userName is null ? Array.Empty<Claim>() : new[] { new Claim(ClaimTypes.Name, userName) },
            "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

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
        decimal totalAmount = 1000m, DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 8, 15),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static ContainerLoadingList SeedLoadingList(
        ErpDbContext db, string loadingListNo, long customerId,
        DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = loadingListNo,
            LoadingDate = new DateTime(2026, 8, 20),
            ContainerNo = "CONT-001",
            CustomerId = customerId,
            Status = status,
            IsDeleted = deleted
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    /// <summary>登记一条**已登记**的 ERP-069 协议证据（对账单的显式依据；草稿 / 已作废都不能作为依据）</summary>
    private static async Task<AgencyServiceFeeAgreementDto> SeedRecordedAgreementAsync(
        ErpDbContext db, long customerId, string agreementNo = "ASF-2026-001", string currency = "USD",
        string feeMethod = AgencyServiceFeeAgreementRules.FeeMethodRate, decimal? ratePercent = 1.5m,
        decimal? fixedAmount = null)
    {
        var created = await AgencyServiceFeeAgreementService.CreateAsync(db, new AgencyServiceFeeAgreementSaveDto
        {
            AgreementNo = agreementNo,
            CustomerId = customerId,
            EffectiveFrom = new DateTime(2026, 1, 1),
            Currency = currency,
            FeeMethod = feeMethod,
            RatePercent = ratePercent,
            FixedAmount = fixedAmount,
            FeeBasis = "按出口发票金额"
        });
        return await AgencyServiceFeeAgreementService.RecordAsync(db, created.Id, "协议登记员");
    }

    private static AgencyServiceFeeStatementLineSaveDto LineDto(
        string sourceType, long sourceId, decimal amount = 100m, string description = "代理服务费",
        decimal? basisQuantity = null, string basisNote = "按协议约定的服务期间", string remark = "")
        => new()
        {
            SourceType = sourceType,
            SourceId = sourceId,
            Description = description,
            Amount = amount,
            BasisQuantity = basisQuantity,
            BasisNote = basisNote,
            Remark = remark
        };

    private static AgencyServiceFeeStatementSaveDto SaveDto(
        long customerId, long agreementId, IEnumerable<AgencyServiceFeeStatementLineSaveDto> lines,
        string statementNo = "ASFS-2026-001", string currency = "USD",
        DateTime? statementDate = null, DateTime? dueDate = null,
        DateTime? periodFrom = null, DateTime? periodTo = null, string remark = "")
        => new()
        {
            StatementNo = statementNo,
            CustomerId = customerId,
            Currency = currency,
            StatementDate = statementDate ?? new DateTime(2026, 9, 20),
            DueDate = dueDate,
            ServicePeriodFrom = periodFrom ?? new DateTime(2026, 8, 1),
            ServicePeriodTo = periodTo ?? new DateTime(2026, 8, 31),
            AgreementId = agreementId,
            Remark = remark,
            Lines = lines.ToList()
        };

    /// <summary>便捷用法：单行销售订单来源的对账单请求</summary>
    private static AgencyServiceFeeStatementSaveDto SalesOrderSave(
        long customerId, long agreementId, long orderId, decimal amount = 100m,
        string statementNo = "ASFS-2026-001", string currency = "USD")
        => SaveDto(customerId, agreementId,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, orderId, amount) },
            statementNo: statementNo, currency: currency);

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

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

    // ==================== 1. 显式来源链接（绝不猜链接） ====================

    [Fact]
    public async Task 草稿对账单_写入客户与协议快照_行号与来源快照_合计由服务端计算()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(
            await controller.Create(SalesOrderSave(customer.Id, agreement.Id, order.Id, amount: 1288.5m)));

        Assert.Equal("ASFS-2026-001", created.StatementNo);
        Assert.Equal(customer.Id, created.CustomerId);
        Assert.Equal("C001", created.CustomerCode);
        Assert.Equal("义乌进出口", created.CustomerName);
        Assert.Equal("USD", created.Currency);
        Assert.Equal(2, created.AmountDecimals);
        Assert.Equal(agreement.Id, created.AgreementId);
        Assert.Equal("ASF-2026-001", created.AgreementNo);
        Assert.Equal("USD", created.AgreementCurrency);
        Assert.Equal(1288.5m, created.TotalAmount);          // 服务端按已校验行求和
        Assert.Equal(1, created.LineCount);
        Assert.Equal(0, created.Status);
        Assert.True(created.IsDraft);
        Assert.False(created.IsRecorded);
        Assert.False(created.IsVoided);
        Assert.Null(created.RecordedAt);
        Assert.Equal(string.Empty, created.RecordedBy);

        var line = Assert.Single(created.Lines);
        Assert.Equal(1, line.LineNo);
        Assert.Equal(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, line.SourceType);
        Assert.Equal("销售订单", line.SourceTypeText);
        Assert.Equal(order.Id, line.SourceId);
        Assert.Equal("SO-001", line.SourceNo);
        Assert.Equal(new DateTime(2026, 8, 15), line.SourceDate);
        Assert.Equal("已审核", line.SourceStatusText);
        Assert.Equal(customer.Id, line.SourceCustomerId);
        Assert.Equal("C001", line.SourceCustomerCode);
        Assert.Equal("USD", line.SourceCurrency);
        Assert.Equal(1288.5m, line.Amount);
        Assert.Equal("1288.50 USD", line.AmountText);
        Assert.Equal(0, line.Status);
        Assert.True(line.SourceAvailable);
        Assert.Contains("税务发票", created.SeparationText);
        Assert.Contains("收入确认", created.SeparationText);
        Assert.Contains("仓库内操作性费用证据册", created.BoundaryText);
    }

    [Fact]
    public async Task 行必须显式选择来源记录_来源Id缺失或非正被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(
            SaveDto(customer.Id, agreement.Id,
                new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, 0) })));
        Assert.Contains("显式选择", ex.Message);
        Assert.Empty(db.AgencyServiceFeeStatements);
        Assert.Empty(db.AgencyServiceFeeStatementLines);
    }

    [Fact]
    public async Task 未知来源类型或空来源类型被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(
            SaveDto(customer.Id, agreement.Id, new[] { LineDto("proforma-invoice", order.Id) })));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(
            SaveDto(customer.Id, agreement.Id, new[] { LineDto(string.Empty, order.Id) })));
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    [Fact]
    public async Task 引用不存在的来源记录被拒绝_不做文本或相似度匹配()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        // 单号文本完全一致（SO-001）但持久化 Id 不存在：必须按 Id 判定并拒绝，绝不按文本匹配到别的记录
        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(
            SaveDto(customer.Id, agreement.Id,
                new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, 999_999) })));
        Assert.Contains("不存在", ex.Message);
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    [Fact]
    public async Task 同一提交内重复引用同一来源被拒绝而不是合并()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.Duplicate, () => controller.Create(
            SaveDto(customer.Id, agreement.Id, new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id),
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id, amount: 50m)
            })));
        Assert.Contains("重复引用", ex.Message);
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    [Fact]
    public async Task 至少一行且单张对账单行数有界()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var empty = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(
            SaveDto(customer.Id, agreement.Id, Array.Empty<AgencyServiceFeeStatementLineSaveDto>())));
        Assert.Contains("至少", empty.Message);

        var tooMany = Enumerable.Range(1, AgencyServiceFeeStatementRules.MaxLinesPerStatement + 1)
            .Select(i => LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, i))
            .ToList();
        var bounded = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(
            SaveDto(customer.Id, agreement.Id, tooMany)));
        Assert.Contains("最多", bounded.Message);
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    // ==================== 2. 来源的客户 / 币种兼容性与可用性 ====================

    [Fact]
    public async Task 销售订单来源_客户与对账单客户不一致被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var other = SeedCustomer(db, "C002", "宁波机械");
        var order = SeedOrder(db, "SO-002", other.Id);      // 订单属于另一个客户
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, order.Id)));
        Assert.Contains("不一致", ex.Message);
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    [Fact]
    public async Task 销售订单来源_币种与对账单币种不一致被拒绝且不做汇率换算()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id, currency: Currency.CNY);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id, currency: "USD");
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, order.Id)));
        Assert.Contains("币种", ex.Message);
        Assert.Contains("不做汇率换算", ex.Message);
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    [Fact]
    public async Task 已取消或已删除的来源记录不能引用_历史行仍可读()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var cancelled = SeedOrder(db, "SO-CX", customer.Id, status: DocumentStatus.Cancelled);
        var deleted = SeedOrder(db, "SO-DL", customer.Id, deleted: true);
        var ok = SeedOrder(db, "SO-OK", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var cancelledEx = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, cancelled.Id)));
        Assert.Contains("已取消", cancelledEx.Message);

        var deletedEx = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, deleted.Id)));
        Assert.Contains("已删除", deletedEx.Message);

        // 先建立一张合法对账单，再让来源被取消 / 软删除：历史行必须仍可读且不改派
        var created = AssertOk<AgencyServiceFeeStatementDto>(
            await controller.Create(SalesOrderSave(customer.Id, agreement.Id, ok.Id, amount: 66m)));
        var recorded = AssertOk<AgencyServiceFeeStatementDto>(await controller.Record(created.Id));

        ok.Status = DocumentStatus.Cancelled;
        ok.IsDeleted = true;
        await db.SaveChangesAsync();

        var reloaded = AssertOk<AgencyServiceFeeStatementDto>(await controller.GetById(recorded.Id));
        var line = Assert.Single(reloaded.Lines);
        Assert.Equal("SO-OK", line.SourceNo);                 // 快照保持登记当时口径
        Assert.False(line.SourceAvailable);
        Assert.Contains("不会改派", line.SourceAvailabilityText);
        Assert.Equal(66m, reloaded.TotalAmount);
    }

    [Fact]
    public async Task 装柜清单来源_不携带币种_币种校验不适用且快照为空串()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var list = SeedLoadingList(db, "LL-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id, currency: "JPY",
            feeMethod: AgencyServiceFeeAgreementRules.FeeMethodFixed, ratePercent: null, fixedAmount: 1000m);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeLoadingList, list.Id, amount: 1200m) },
            currency: "JPY")));

        var line = Assert.Single(created.Lines);
        Assert.Equal("装柜清单", line.SourceTypeText);
        Assert.Equal("LL-001", line.SourceNo);
        Assert.Equal(string.Empty, line.SourceCurrency);      // 来源不携带币种：照实记空串，不替它补币种
        Assert.Contains("不携带币种", line.SourceCurrencyCompatibilityText);
        Assert.Contains("不适用", line.SourceCurrencyCompatibilityText);
        Assert.Equal("1200 JPY", line.AmountText);            // JPY = 0 位小数
        Assert.Equal(1200m, created.TotalAmount);
    }

    [Fact]
    public async Task 装柜清单来源_客户不一致被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var other = SeedCustomer(db, "C002", "宁波机械");
        var list = SeedLoadingList(db, "LL-002", other.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeLoadingList, list.Id) })));
        Assert.Contains("不一致", ex.Message);
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    // ==================== 3. 关联协议的资格与客户 / 币种兼容性 ====================

    [Fact]
    public async Task 未显式选择协议或协议不存在被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var controller = BuildController(db);

        var none = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(
            SalesOrderSave(customer.Id, 0, order.Id)));
        Assert.Contains("显式选择", none.Message);

        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.Create(
            SalesOrderSave(customer.Id, 999_999, order.Id)));
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    [Fact]
    public async Task 草稿或已作废的协议不能作为对账单依据()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var controller = BuildController(db);

        // 草稿协议：拒绝（未登记条款不能作为依据）
        var draft = await AgencyServiceFeeAgreementService.CreateAsync(db, new AgencyServiceFeeAgreementSaveDto
        {
            AgreementNo = "ASF-DRAFT",
            CustomerId = customer.Id,
            EffectiveFrom = new DateTime(2026, 1, 1),
            Currency = "USD",
            FeeMethod = AgencyServiceFeeAgreementRules.FeeMethodRate,
            RatePercent = 1m,
            FeeBasis = "按出口发票金额"
        });
        var draftEx = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(
            SalesOrderSave(customer.Id, draft.Id, order.Id)));
        Assert.Contains("草稿", draftEx.Message);

        // 已作废协议：拒绝
        var recorded = await SeedRecordedAgreementAsync(db, customer.Id, agreementNo: "ASF-VOID");
        await AgencyServiceFeeAgreementService.VoidAsync(db, recorded.Id, "协议终止");
        var voidEx = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(
            SalesOrderSave(customer.Id, recorded.Id, order.Id)));
        Assert.Contains("已作废", voidEx.Message);
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    [Fact]
    public async Task 协议客户或币种与对账单不一致被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var other = SeedCustomer(db, "C002", "宁波机械");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var foreignAgreement = await SeedRecordedAgreementAsync(db, other.Id, agreementNo: "ASF-OTHER");
        var usdAgreement = await SeedRecordedAgreementAsync(db, customer.Id, agreementNo: "ASF-USD");
        var controller = BuildController(db);

        var customerMismatch = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(
            SalesOrderSave(customer.Id, foreignAgreement.Id, order.Id)));
        Assert.Contains("客户", customerMismatch.Message);

        var currencyMismatch = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(
            SaveDto(customer.Id, usdAgreement.Id,
                new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id) },
                currency: "EUR")));
        Assert.Contains("币种", currencyMismatch.Message);
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    // ==================== 4. 客户权威资格与历史快照 ====================

    [Fact]
    public async Task 未选客户或客户不存在被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(
            SalesOrderSave(0, agreement.Id, order.Id)));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.Create(
            SalesOrderSave(999_999, agreement.Id, order.Id)));
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    [Fact]
    public async Task 客户已停用或已删除不能登记新对账单()
    {
        using var db = TestDbFactory.Create();
        var inactive = SeedCustomer(db, "C900", "已停用客户", status: 0);
        var deleted = SeedCustomer(db, "C901", "已删除客户", deleted: true);
        var controller = BuildController(db);

        var stopped = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(
            SaveDto(inactive.Id, 1, new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, 1) })));
        Assert.Contains("已停用", stopped.Message);

        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.Create(
            SaveDto(deleted.Id, 1, new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, 1) })));
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    [Fact]
    public async Task 客户改名或停用后历史对账单保持登记当时快照并显式标注()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(
            await controller.Create(SalesOrderSave(customer.Id, agreement.Id, order.Id)));
        var recorded = AssertOk<AgencyServiceFeeStatementDto>(await controller.Record(created.Id));

        customer.CustomerName = "义乌进出口（新名）";
        customer.Status = 0;
        await db.SaveChangesAsync();

        var reloaded = AssertOk<AgencyServiceFeeStatementDto>(await controller.GetById(recorded.Id));
        Assert.Equal("义乌进出口", reloaded.CustomerName);     // 快照保持登记当时口径
        Assert.False(reloaded.CustomerAvailable);
        Assert.Contains("已停用", reloaded.CustomerAvailabilityText);
        Assert.Contains("仍可读", reloaded.CustomerAvailabilityText);
        Assert.Equal("义乌进出口", Assert.Single(reloaded.Lines).SourceCustomerName);
    }

    // ==================== 5. 对账日期、可选到期日与服务期间 ====================

    [Fact]
    public async Task 对账日期必填()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var dto = SalesOrderSave(customer.Id, agreement.Id, order.Id);
        dto.StatementDate = null;
        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(dto));
        Assert.Contains("对账日期", ex.Message);
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    [Fact]
    public async Task 到期日可选_留空为未知且不推算()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var explicitDueOrder = SeedOrder(db, "SO-002", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        // 留空：保持未知（不读取客户账期 CreditDays=30，也不按对账日期 + 30 天推算）
        var unknown = AssertOk<AgencyServiceFeeStatementDto>(
            await controller.Create(SalesOrderSave(customer.Id, agreement.Id, order.Id)));
        Assert.Null(unknown.DueDate);
        Assert.Contains("到期日未知", unknown.DueDateText);
        Assert.DoesNotContain(
            new DateTime(2026, 9, 20).AddDays(customer.CreditDays ?? 0).ToString("yyyy-MM-dd"),
            unknown.DueDateText);

        // 显式填写：照实保存，且不与推算值相关
        var explicitDue = new DateTime(2026, 10, 25);
        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, explicitDueOrder.Id) },
            statementNo: "ASFS-2026-002", dueDate: explicitDue)));
        Assert.Equal(explicitDue, created.DueDate);
        Assert.Equal("2026-10-25", created.DueDateText);
    }

    [Fact]
    public async Task 到期日早于对账日期被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id) },
            statementDate: new DateTime(2026, 9, 20), dueDate: new DateTime(2026, 9, 19))));
        Assert.Contains("不能早于对账日期", ex.Message);
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    [Fact]
    public async Task 服务期间起止必填且结束不得早于起始()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);
        var line = new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id) };

        var missingFrom = SaveDto(customer.Id, agreement.Id, line);
        missingFrom.ServicePeriodFrom = null;
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(missingFrom));

        var missingTo = SaveDto(customer.Id, agreement.Id, line);
        missingTo.ServicePeriodTo = null;
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(missingTo));

        var reversed = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(
            SaveDto(customer.Id, agreement.Id, line,
                periodFrom: new DateTime(2026, 8, 31), periodTo: new DateTime(2026, 8, 1))));
        Assert.Contains("不能早于", reversed.Message);
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    // ==================== 6. 金额精度、服务端合计与显式行证据 ====================

    [Fact]
    public async Task 行金额按币种精度取整且必须大于0()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var usdOrder = SeedOrder(db, "SO-USD", customer.Id, currency: Currency.USD);
        var jpyOrder = SeedOrder(db, "SO-JPY", customer.Id, currency: Currency.JPY);
        var usdAgreement = await SeedRecordedAgreementAsync(db, customer.Id, agreementNo: "ASF-USD");
        var jpyAgreement = await SeedRecordedAgreementAsync(db, customer.Id, agreementNo: "ASF-JPY",
            currency: "JPY");
        var controller = BuildController(db);

        // USD：2 位小数，0.5 进位
        var usd = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, usdAgreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, usdOrder.Id, amount: 12.345m) },
            currency: "USD")));
        Assert.Equal(12.35m, usd.TotalAmount);
        Assert.Equal("12.35 USD", Assert.Single(usd.Lines).AmountText);

        // JPY：0 位小数
        var jpy = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, jpyAgreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, jpyOrder.Id, amount: 1000.4m) },
            statementNo: "ASFS-JPY-002", currency: "JPY")));
        Assert.Equal(1000m, jpy.TotalAmount);
        Assert.Equal(0, jpy.AmountDecimals);

        // 取整后为 0：拒绝（0.4 JPY 取整为 0）
        var zeroAfterRound = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(
            SaveDto(customer.Id, jpyAgreement.Id,
                new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, jpyOrder.Id, amount: 0.4m) },
                statementNo: "ASFS-BAD", currency: "JPY")));
        Assert.Contains("必须大于 0", zeroAfterRound.Message);

        var negative = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(
            SaveDto(customer.Id, usdAgreement.Id,
                new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, usdOrder.Id, amount: -1m) },
                statementNo: "ASFS-NEG")));
        Assert.Contains("必须大于 0", negative.Message);
    }

    [Fact]
    public async Task 合计由服务端按多行求和_请求模型不提供可被采信的合计字段()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order1 = SeedOrder(db, "SO-001", customer.Id);
        var order2 = SeedOrder(db, "SO-002", customer.Id);
        var list = SeedLoadingList(db, "LL-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        // 请求 DTO 刻意没有任何合计字段：客户端无法提交「被信任的合计」
        var requestNames = typeof(AgencyServiceFeeStatementSaveDto)
            .GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(requestNames, n => n.Contains("Total", StringComparison.Ordinal));

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id, new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order1.Id, amount: 100.005m),
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order2.Id, amount: 200.004m),
                LineDto(AgencyServiceFeeStatementRules.SourceTypeLoadingList, list.Id, amount: 300.001m)
            })));

        Assert.Equal(3, created.LineCount);
        Assert.Equal(600.01m, created.TotalAmount);            // 100.01 + 200.00 + 300.00
        Assert.Equal("600.01 USD", created.TotalAmountText);
        Assert.Equal(new[] { 1, 2, 3 }, created.Lines.Select(l => l.LineNo).ToArray());
    }

    [Fact]
    public async Task 行金额不从协议费率或来源单据金额折算()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id, totalAmount: 1500m);
        // 协议：比例费率 1.5% ⇒ 若系统「折算」会得到 22.50；对账单必须保持用户显式提交的值
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id, ratePercent: 1.5m);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id, amount: 42.5m) })));

        Assert.Equal(42.5m, created.TotalAmount);
        Assert.NotEqual(22.5m, created.TotalAmount);            // 协议费率 × 订单金额的结果绝不被自动带入
        Assert.NotEqual(1500m, created.TotalAmount);            // 来源单据金额也不被当作费用
        Assert.Contains("比例费率", created.AgreementTermsText); // 协议条款只作只读核对
        Assert.Contains("显式", created.AmountRuleText);
    }

    [Fact]
    public async Task 行说明与计费基础说明必填且有界_备注超长被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id, description: " ") })));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id, basisNote: string.Empty) })));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id,
                    description: new string('目', AgencyServiceFeeStatementRules.MaxDescriptionLength + 1))
            })));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id) },
            remark: new string('备', AgencyServiceFeeStatementRules.MaxRemarkLength + 1))));
        Assert.Empty(db.AgencyServiceFeeStatements);
    }

    [Fact]
    public async Task 计费基础数量可选_留空为未知且有界()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var orderA = SeedOrder(db, "SO-A", customer.Id);
        var orderB = SeedOrder(db, "SO-B", customer.Id);
        var orderC = SeedOrder(db, "SO-C", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var unknown = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, orderA.Id, basisQuantity: null)
            })));
        Assert.Null(Assert.Single(unknown.Lines).BasisQuantity);   // 留空 = 未知，不从来源数量推算

        var explicitQty = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, orderB.Id, basisQuantity: 3.14159m)
            },
            statementNo: "ASFS-2026-003")));
        Assert.Equal(3.1416m, Assert.Single(explicitQty.Lines).BasisQuantity);   // 4 位小数，0.5 进位

        var over = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, orderC.Id,
                    basisQuantity: AgencyServiceFeeStatementRules.MaxBasisQuantity + 1m)
            },
            statementNo: "ASFS-2026-004")));
        Assert.Contains("超出允许范围", over.Message);
    }

    // ==================== 7. 唯一性与重复计费保护 ====================

    [Fact]
    public async Task 同一客户同一规范化对账单号重复被拒绝_不同客户可用相同对账单号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var other = SeedCustomer(db, "C002", "宁波机械");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var otherOrder = SeedOrder(db, "SO-002", other.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var otherAgreement = await SeedRecordedAgreementAsync(db, other.Id, agreementNo: "ASF-002");
        var controller = BuildController(db);

        AssertOk<AgencyServiceFeeStatementDto>(
            await controller.Create(SalesOrderSave(customer.Id, agreement.Id, order.Id)));

        // 规范化后同一身份（大小写 / 连字符 / 空白差异）⇒ 重复被拒绝
        var duplicate = await AssertBusinessAsync(ErrorCodes.Duplicate, () => controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, order.Id, statementNo: "asfs 2026_001")));
        Assert.Contains("重复", duplicate.Message);

        // 不同客户可以使用相同对账单号
        var otherCreated = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(
            SalesOrderSave(other.Id, otherAgreement.Id, otherOrder.Id)));
        Assert.Equal("ASFS-2026-001", otherCreated.StatementNo);
        Assert.Equal(2, db.AgencyServiceFeeStatements.Count());
    }

    [Fact]
    public async Task 草稿占用对账单身份_作废后可重新登记()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var first = AssertOk<AgencyServiceFeeStatementDto>(
            await controller.Create(SalesOrderSave(customer.Id, agreement.Id, order.Id)));
        await AssertBusinessAsync(ErrorCodes.Duplicate, () => controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, order.Id)));

        AssertOk<AgencyServiceFeeStatementDto>(await controller.Void(first.Id,
            new AgencyServiceFeeStatementVoidRequest { Reason = "对账口径更正" }));

        var again = AssertOk<AgencyServiceFeeStatementDto>(
            await controller.Create(SalesOrderSave(customer.Id, agreement.Id, order.Id)));
        Assert.NotEqual(first.Id, again.Id);
        Assert.Equal(2, db.AgencyServiceFeeStatements.Count());
    }

    [Fact]
    public async Task 同一来源只能被一条未作废对账单行引用_作废后释放()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var first = AssertOk<AgencyServiceFeeStatementDto>(
            await controller.Create(SalesOrderSave(customer.Id, agreement.Id, order.Id)));

        // 另一张未作废对账单（不同对账单号）引用同一来源 ⇒ 重复计费证据被拒绝
        var occupied = await AssertBusinessAsync(ErrorCodes.Duplicate, () => controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, order.Id, statementNo: "ASFS-2026-009")));
        Assert.Contains("已被另一张未作废对账单引用", occupied.Message);

        // 作废释放来源身份后可以重新显式引用
        AssertOk<AgencyServiceFeeStatementDto>(await controller.Void(first.Id,
            new AgencyServiceFeeStatementVoidRequest { Reason = "登记错误" }));
        var reused = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(
            SalesOrderSave(customer.Id, agreement.Id, order.Id, statementNo: "ASFS-2026-009")));
        Assert.Equal(1, reused.LineCount);
    }

    [Fact]
    public async Task 修改草稿时自身的来源行不算重复_但改后仍受其他对账单占用约束()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order1 = SeedOrder(db, "SO-001", customer.Id);
        var order2 = SeedOrder(db, "SO-002", customer.Id);
        var order3 = SeedOrder(db, "SO-003", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var first = AssertOk<AgencyServiceFeeStatementDto>(
            await controller.Create(SalesOrderSave(customer.Id, agreement.Id, order1.Id, amount: 10m)));

        // 同一张对账单保留同一来源（自身不算重复），并新增另一来源行
        var updated = AssertOk<AgencyServiceFeeStatementDto>(await controller.Update(first.Id, SaveDto(
            customer.Id, agreement.Id, new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order1.Id, amount: 20m),
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order2.Id, amount: 30m)
            })));
        Assert.Equal(50m, updated.TotalAmount);
        Assert.Equal(2, updated.LineCount);

        // 另一张未作废对账单占用 order3 ⇒ 本对账单再引用它必须被拒绝（绝不改派、不合并）
        AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order3.Id) },
            statementNo: "ASFS-2026-010")));
        await AssertBusinessAsync(ErrorCodes.Duplicate, () => controller.Update(first.Id, SaveDto(
            customer.Id, agreement.Id,
            new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order1.Id, amount: 20m),
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order3.Id, amount: 5m)
            })));
    }

    // ==================== 8. 登记 / 作废与历史保留 ====================

    [Fact]
    public async Task 登记冻结证据并写入登记人与时间_合计按持久化行重算()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db, "张三");

        var created = AssertOk<AgencyServiceFeeStatementDto>(
            await controller.Create(SalesOrderSave(customer.Id, agreement.Id, order.Id, amount: 88.8m)));
        var recorded = AssertOk<AgencyServiceFeeStatementDto>(await controller.Record(created.Id));

        Assert.Equal(1, recorded.Status);
        Assert.True(recorded.IsRecorded);
        Assert.False(recorded.IsDraft);
        Assert.NotNull(recorded.RecordedAt);
        Assert.Equal("张三", recorded.RecordedBy);
        Assert.Equal(88.8m, recorded.TotalAmount);
        var line = Assert.Single(recorded.Lines);
        Assert.Equal(1, line.Status);
        Assert.True(line.IsRecorded);
        Assert.Equal("张三", line.RecordedBy);

        // 无身份时登记人记「未知用户」
        var secondOrder = SeedOrder(db, "SO-002", customer.Id);
        var other = AssertOk<AgencyServiceFeeStatementDto>(await BuildController(db).Create(
            SalesOrderSave(customer.Id, agreement.Id, secondOrder.Id, amount: 5m,
                statementNo: "ASFS-2026-011")));
        var anonymous = AssertOk<AgencyServiceFeeStatementDto>(await BuildController(db).Record(other.Id));
        Assert.Equal("未知用户", anonymous.RecordedBy);
    }

    [Fact]
    public async Task 已登记或已作废的对账单不可修改_重复登记与作废后登记被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(
            await controller.Create(SalesOrderSave(customer.Id, agreement.Id, order.Id)));
        var recorded = AssertOk<AgencyServiceFeeStatementDto>(await controller.Record(created.Id));

        var editRecorded = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Update(
            recorded.Id, SalesOrderSave(customer.Id, agreement.Id, order.Id, amount: 9m)));
        Assert.Contains("已登记", editRecorded.Message);

        var recordAgain = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Record(recorded.Id));
        Assert.Contains("不能重复登记", recordAgain.Message);

        var voided = AssertOk<AgencyServiceFeeStatementDto>(await controller.Void(recorded.Id,
            new AgencyServiceFeeStatementVoidRequest { Reason = "客户争议" }));

        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Update(
            voided.Id, SalesOrderSave(customer.Id, agreement.Id, order.Id, amount: 9m)));
        var recordVoided = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Record(voided.Id));
        Assert.Contains("已作废", recordVoided.Message);
    }

    [Fact]
    public async Task 作废原因必填且有界_重复作废被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(
            await controller.Create(SalesOrderSave(customer.Id, agreement.Id, order.Id)));

        var missing = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Void(created.Id,
            new AgencyServiceFeeStatementVoidRequest { Reason = "  " }));
        Assert.Contains("作废原因", missing.Message);

        var tooLong = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Void(created.Id,
            new AgencyServiceFeeStatementVoidRequest
            {
                Reason = new string('因', AgencyServiceFeeStatementRules.MaxVoidReasonLength + 1)
            }));
        Assert.Contains("不能超过", tooLong.Message);

        AssertOk<AgencyServiceFeeStatementDto>(await controller.Void(created.Id,
            new AgencyServiceFeeStatementVoidRequest { Reason = "对账口径更正" }));
        var again = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Void(created.Id,
            new AgencyServiceFeeStatementVoidRequest { Reason = "再次作废" }));
        Assert.Contains("不能重复作废", again.Message);
    }

    [Fact]
    public async Task 作废保留原始行与快照_行状态同步作废且历史可读()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var list = SeedLoadingList(db, "LL-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db, "李四");

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id, new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id, amount: 100m,
                    basisQuantity: 2m, basisNote: "按订单数量"),
                LineDto(AgencyServiceFeeStatementRules.SourceTypeLoadingList, list.Id, amount: 50m,
                    basisNote: "按装柜清单期间")
            })));
        var recorded = AssertOk<AgencyServiceFeeStatementDto>(await controller.Record(created.Id));
        var voided = AssertOk<AgencyServiceFeeStatementDto>(await controller.Void(recorded.Id,
            new AgencyServiceFeeStatementVoidRequest { Reason = "客户争议" }));

        Assert.Equal(2, voided.Status);
        Assert.True(voided.IsVoided);
        Assert.Equal("客户争议", voided.VoidReason);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal("李四", voided.RecordedBy);            // 作废不改写登记人
        Assert.Equal(150m, voided.TotalAmount);             // 原始金额保留
        Assert.Equal(2, voided.Lines.Count);
        Assert.All(voided.Lines, l =>
        {
            Assert.Equal(2, l.Status);
            Assert.True(l.IsVoided);
            Assert.Equal("客户争议", l.VoidReason);
            Assert.NotNull(l.RecordedAt);                   // 登记时间保留
        });
        Assert.Equal("按订单数量", voided.Lines[0].BasisNote);
        Assert.Equal(2m, voided.Lines[0].BasisQuantity);
        Assert.Equal("SO-001", voided.Lines[0].SourceNo);
        // 行不被硬删除、不被静默替换（数据库列上仍 IsDeleted = 0）
        Assert.False(db.AgencyServiceFeeStatementLines
            .Single(l => l.Id == voided.Lines[0].Id).IsDeleted);

        // 详情仍可读（历史证据保留）
        var reloaded = AssertOk<AgencyServiceFeeStatementDto>(await controller.GetById(voided.Id));
        Assert.Equal(2, reloaded.Lines.Count);
    }

    [Fact]
    public async Task 修改草稿软删除原草稿行_不物理删除且新行重新编号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order1 = SeedOrder(db, "SO-001", customer.Id);
        var order2 = SeedOrder(db, "SO-002", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order1.Id, amount: 10m),
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order2.Id, amount: 20m)
            })));
        Assert.Equal(2, db.AgencyServiceFeeStatementLines.Count(l => !l.IsDeleted));

        var updated = AssertOk<AgencyServiceFeeStatementDto>(await controller.Update(created.Id, SaveDto(
            customer.Id, agreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order2.Id, amount: 33m) })));

        Assert.Equal(33m, updated.TotalAmount);
        var line = Assert.Single(updated.Lines);
        Assert.Equal(1, line.LineNo);                        // 行号重新从 1 起（服务端按提交顺序写入）
        Assert.Equal("SO-002", line.SourceNo);
        // 原草稿行被软删除（保留可审计痕迹），绝不物理删除
        Assert.Equal(2, db.AgencyServiceFeeStatementLines.Count(l => l.IsDeleted));
        Assert.Equal(3, db.AgencyServiceFeeStatementLines.Count());
    }

    // ==================== 9. 台账读取：过滤、分页与有界 ====================

    [Fact]
    public async Task 台账支持客户_协议_状态_币种_来源类型_日期区间与关键字过滤()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var other = SeedCustomer(db, "C002", "宁波机械");
        var order = SeedOrder(db, "SO-001", customer.Id);
        SeedOrder(db, "SO-002", other.Id);
        var list = SeedLoadingList(db, "LL-001", other.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var otherAgreement = await SeedRecordedAgreementAsync(db, other.Id, agreementNo: "ASF-002");
        var controller = BuildController(db);

        AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id) },
            statementNo: "ASFS-SO-001", remark: "八月对账")));
        var loadingStatement = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            other.Id, otherAgreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeLoadingList, list.Id) },
            statementNo: "ASFS-LL-002")));
        AssertOk<AgencyServiceFeeStatementDto>(await controller.Record(loadingStatement.Id));

        var byCustomer = AssertOk<PagedResult<AgencyServiceFeeStatementDto>>(
            await controller.GetPaged(new AgencyServiceFeeStatementQuery { CustomerId = customer.Id }));
        Assert.Single(byCustomer.Items);
        Assert.Equal("ASFS-SO-001", byCustomer.Items[0].StatementNo);

        var byAgreement = AssertOk<PagedResult<AgencyServiceFeeStatementDto>>(
            await controller.GetPaged(new AgencyServiceFeeStatementQuery { AgreementId = otherAgreement.Id }));
        Assert.Single(byAgreement.Items);

        var recorded = AssertOk<PagedResult<AgencyServiceFeeStatementDto>>(
            await controller.GetPaged(new AgencyServiceFeeStatementQuery { Status = 1 }));
        Assert.Single(recorded.Items);
        Assert.True(recorded.Items[0].IsRecorded);

        var bySourceType = AssertOk<PagedResult<AgencyServiceFeeStatementDto>>(
            await controller.GetPaged(new AgencyServiceFeeStatementQuery
            {
                SourceType = AgencyServiceFeeStatementRules.SourceTypeLoadingList
            }));
        Assert.Single(bySourceType.Items);
        Assert.Equal("ASFS-LL-002", bySourceType.Items[0].StatementNo);
        Assert.Equal(1, bySourceType.Items[0].LineCount);       // 列表只给行数摘要（行快照见详情）

        var byCurrency = AssertOk<PagedResult<AgencyServiceFeeStatementDto>>(
            await controller.GetPaged(new AgencyServiceFeeStatementQuery { Currency = "usd" }));
        Assert.Equal(2, byCurrency.Items.Count);

        var byDate = AssertOk<PagedResult<AgencyServiceFeeStatementDto>>(
            await controller.GetPaged(new AgencyServiceFeeStatementQuery
            {
                StatementDateFrom = new DateTime(2026, 9, 21),
                StatementDateTo = new DateTime(2026, 9, 30)
            }));
        Assert.Empty(byDate.Items);

        var byKeyword = AssertOk<PagedResult<AgencyServiceFeeStatementDto>>(
            await controller.GetPaged(new AgencyServiceFeeStatementQuery { Keyword = "八月对账" }));
        Assert.Single(byKeyword.Items);

        var byAgreementNo = AssertOk<PagedResult<AgencyServiceFeeStatementDto>>(
            await controller.GetPaged(new AgencyServiceFeeStatementQuery { Keyword = "ASF-002" }));
        Assert.Single(byAgreementNo.Items);
    }

    [Fact]
    public async Task 台账未知筛选取值与超长关键字被拒绝()
    {
        using var db = TestDbFactory.Create();
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new AgencyServiceFeeStatementQuery { Status = 9 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new AgencyServiceFeeStatementQuery { SourceType = "container-booking" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new AgencyServiceFeeStatementQuery { Currency = "XYZ" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new AgencyServiceFeeStatementQuery
            {
                Keyword = new string('k', AgencyServiceFeeStatementRules.MaxKeywordLength + 1)
            }));
    }

    [Fact]
    public async Task 台账分页有界且按对账日期倒序()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        var orders = new List<SalesOrder>();
        for (var i = 1; i <= 6; i++) orders.Add(SeedOrder(db, $"SO-{i:d3}", customer.Id));
        for (var i = 1; i <= 6; i++)
        {
            AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
                customer.Id, agreement.Id,
                new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, orders[i - 1].Id) },
                statementNo: $"ASFS-P-{i:d3}",
                statementDate: new DateTime(2026, 9, i))));
        }

        var page1 = AssertOk<PagedResult<AgencyServiceFeeStatementDto>>(
            await controller.GetPaged(new AgencyServiceFeeStatementQuery { Page = 1, PageSize = 4 }));
        Assert.Equal(6, page1.Total);
        Assert.Equal(4, page1.Items.Count);
        Assert.Equal(new DateTime(2026, 9, 6), page1.Items[0].StatementDate);

        var page2 = AssertOk<PagedResult<AgencyServiceFeeStatementDto>>(
            await controller.GetPaged(new AgencyServiceFeeStatementQuery { Page = 2, PageSize = 4 }));
        Assert.Equal(2, page2.Items.Count);

        // 页大小有界（超过上限按上限截断）
        var huge = AssertOk<PagedResult<AgencyServiceFeeStatementDto>>(
            await controller.GetPaged(new AgencyServiceFeeStatementQuery { PageSize = 100_000 }));
        Assert.Equal(AgencyServiceFeeStatementQuery.MaxPageSize, huge.PageSize);
    }

    [Fact]
    public async Task 详情_不存在或已删除的对账单被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetById(0));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(999_999));

        var created = AssertOk<AgencyServiceFeeStatementDto>(
            await controller.Create(SalesOrderSave(customer.Id, agreement.Id, order.Id)));
        var entity = db.AgencyServiceFeeStatements.Single(x => x.Id == created.Id);
        entity.IsDeleted = true;
        await db.SaveChangesAsync();
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(created.Id));
    }

    [Fact]
    public async Task 来源候选_必须给出客户与币种_只返回未删除来源并逐条给出资格文案()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var other = SeedCustomer(db, "C002", "宁波机械");
        var okOrder = SeedOrder(db, "SO-OK", customer.Id);
        SeedOrder(db, "SO-CX", customer.Id, status: DocumentStatus.Cancelled);
        SeedOrder(db, "SO-DL", customer.Id, deleted: true);
        SeedOrder(db, "SO-OTHER", other.Id);
        SeedLoadingList(db, "LL-001", customer.Id);
        var controller = BuildController(db);

        // 必须显式给出客户与币种（资格判定依赖它们）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.SourceOptions(
            null, 0, "USD", null, 50));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.SourceOptions(
            null, customer.Id, null, null, 50));

        var options = AssertOk<List<AgencyServiceFeeStatementSourceOptionDto>>(
            await controller.SourceOptions(null, customer.Id, "USD", null, 50));

        // 已删除来源不出现在候选中；已取消来源出现但显式标注不可引用；跨客户来源不出现
        Assert.DoesNotContain(options, o => o.SourceNo == "SO-DL");
        Assert.DoesNotContain(options, o => o.SourceNo == "SO-OTHER");
        Assert.True(options.Single(o => o.SourceNo == "SO-OK").Eligible);
        Assert.False(options.Single(o => o.SourceNo == "SO-CX").Eligible);
        Assert.Contains("已取消", options.Single(o => o.SourceNo == "SO-CX").EligibilityText);
        Assert.Equal("装柜清单", options.Single(o => o.SourceNo == "LL-001").SourceTypeText);
        Assert.Equal(string.Empty, options.Single(o => o.SourceNo == "LL-001").Currency);   // 不携带币种
        Assert.Equal(okOrder.Id, options.Single(o => o.SourceNo == "SO-OK").SourceId);

        // 关键字只匹配单号 + 单次有界
        var filtered = AssertOk<List<AgencyServiceFeeStatementSourceOptionDto>>(
            await controller.SourceOptions(null, customer.Id, "USD", "LL-", 50));
        Assert.Single(filtered);
        var bounded = AssertOk<List<AgencyServiceFeeStatementSourceOptionDto>>(
            await controller.SourceOptions(AgencyServiceFeeStatementRules.SourceTypeSalesOrder,
                customer.Id, "USD", null, 1));
        Assert.Single(bounded);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.SourceOptions(
            "proforma-invoice", customer.Id, "USD", null, 50));
    }

    [Fact]
    public void 元数据_白名单与有界额度与文档同源()
    {
        var metadata = AgencyServiceFeeStatementService.GetMetadata();
        Assert.Equal(
            new[] { "sales-order", "loading-list" },
            metadata.SourceTypes.Select(t => t.Value).ToArray());
        Assert.Equal(new[] { "销售订单", "装柜清单" }, metadata.SourceTypes.Select(t => t.Label).ToArray());
        Assert.Equal(3, metadata.StatusOptions.Count);
        Assert.Equal(AgencyServiceFeeStatementRules.MaxLinesPerStatement, metadata.MaxLinesPerStatement);
        Assert.Equal(AgencyServiceFeeStatementQuery.MaxPageSize, metadata.MaxPageSize);
        Assert.Contains("持久化标识符", metadata.SourceLinkRuleText);
        Assert.Contains("到期日", metadata.DueDateRuleText);
        Assert.Contains("税务发票", metadata.SeparationText);
        Assert.Contains("到期日未知", AgencyServiceFeeStatementRules.DueDateText(null));
    }

    // ==================== 10. 有界查询（无逐行查库）与来源记录非变更 ====================

    [Fact]
    public async Task 台账读取是固定数量的数据集访问_行数变化不改变访问次数且只读不写库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var order = SeedOrder(db, "SO-001", customer.Id);
        var controller = BuildController(db);
        AssertOk<AgencyServiceFeeStatementDto>(
            await controller.Create(SalesOrderSave(customer.Id, agreement.Id, order.Id)));

        var counting = StatementReadCounter.Wrap(db);
        var single = await AgencyServiceFeeStatementService.ListAsync(
            counting.Proxy, new AgencyServiceFeeStatementQuery { PageSize = 1 });
        var singleReads = counting.DatasetReads;

        Assert.Equal(1, single.Total);
        Assert.Equal(
            new[]
            {
                nameof(IErpDbContext.AgencyServiceFeeStatements),
                nameof(IErpDbContext.BaseCustomers),
                nameof(IErpDbContext.AgencyServiceFeeAgreements),
                nameof(IErpDbContext.AgencyServiceFeeStatementLines)
            },
            counting.ReadProperties.Distinct().ToArray());

        // 再补 300 张（跨多页）：数据集访问次数必须保持不变（无逐行查库、无 N+1）
        for (var i = 0; i < 300; i++)
        {
            db.AgencyServiceFeeStatements.Add(new AgencyServiceFeeStatement
            {
                StatementNo = $"ASFS-BULK-{i:d4}",
                NormalizedStatementNo = $"ASFSBULK{i:d4}",
                CustomerId = customer.Id,
                CustomerCode = "C001",
                CustomerName = "义乌进出口",
                Currency = "USD",
                StatementDate = new DateTime(2026, 9, 20),
                ServicePeriodFrom = new DateTime(2026, 8, 1),
                ServicePeriodTo = new DateTime(2026, 8, 31),
                AgreementId = agreement.Id,
                AgreementNo = "ASF-2026-001",
                AgreementCurrency = "USD",
                AgreementCustomerId = customer.Id,
                AgreementFeeMethod = "比例费率",
                AgreementTermsText = "比例费率",
                TotalAmount = 10m,
                Status = AgencyServiceFeeStatementRules.StatusDraft,
                CreatedAt = DateTime.Now
            });
        }
        await db.SaveChangesAsync();

        var large = await AgencyServiceFeeStatementService.ListAsync(
            counting.Proxy, new AgencyServiceFeeStatementQuery { PageSize = 200 });
        var largeReads = counting.DatasetReads - singleReads;

        Assert.Equal(301, large.Total);
        Assert.Equal(AgencyServiceFeeStatementQuery.MaxPageSize, large.Items.Count);   // 单页有界
        Assert.Equal(singleReads, largeReads);                                        // 行数变化不改变访问次数
        Assert.Equal(0, counting.WriteCalls);                                         // 只读：不落库
        Assert.All(large.Items, r => Assert.Empty(r.Lines));                            // 列表只给行数摘要
    }

    [Fact]
    public async Task 详情读取行的数据集访问次数与行数无关_无逐行查库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);
        var list = SeedLoadingList(db, "LL-001", customer.Id);
        var controller = BuildController(db);

        // 一张只有 1 行的对账单
        var single = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id,
            new[] { LineDto(AgencyServiceFeeStatementRules.SourceTypeLoadingList, list.Id) })));

        // 另一张 120 行的对账单（同一来源类型、不同来源记录）
        var lines = new List<AgencyServiceFeeStatementLineSaveDto>();
        for (var i = 0; i < 120; i++)
        {
            var source = new ContainerLoadingList
            {
                LoadingListNo = $"LL-BULK-{i:d3}",
                LoadingDate = new DateTime(2026, 8, 20),
                CustomerId = customer.Id,
                Status = DocumentStatus.Approved,
                CreatedAt = DateTime.Now
            };
            db.ContainerLoadingLists.Add(source);
            await db.SaveChangesAsync();
            lines.Add(LineDto(AgencyServiceFeeStatementRules.SourceTypeLoadingList, source.Id, amount: 1m));
        }
        var many = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id, lines, statementNo: "ASFS-MANY-001")));

        var counting = StatementReadCounter.Wrap(db);
        await AgencyServiceFeeStatementService.GetAsync(counting.Proxy, single.Id);
        var singleReads = counting.DatasetReads;

        var loaded = await AgencyServiceFeeStatementService.GetAsync(counting.Proxy, many.Id);
        var manyReads = counting.DatasetReads - singleReads;

        Assert.Equal(120, loaded.Lines.Count);
        Assert.Equal(singleReads, manyReads);     // 行数变化不改变数据集访问次数（无逐行查库）
        Assert.Equal(0, counting.WriteCalls);
        Assert.Contains(nameof(IErpDbContext.AgencyServiceFeeStatementLines), counting.ReadProperties);
    }

    [Fact]
    public async Task 登记与作废不改写协议_客户_订单_装柜清单_发票_收款_费用与退税记录()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var order = SeedOrder(db, "SO-001", customer.Id, totalAmount: 1500m);
        var list = SeedLoadingList(db, "LL-001", customer.Id);
        var agreement = await SeedRecordedAgreementAsync(db, customer.Id);

        var receipt = new FinanceReceipt
        {
            ReceiptNo = "RC-001",
            ReceiptDate = new DateTime(2026, 9, 10),
            CustomerId = customer.Id,
            Amount = 500m,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved
        };
        var expense = new FinanceExpense
        {
            ExpenseNo = "EX-001",
            ExpenseDate = new DateTime(2026, 9, 11),
            ExpenseType = "报关费",
            Amount = 120m,
            Currency = "CNY"
        };
        var refund = new BaseTaxRefund
        {
            RefundNo = "TR-001",
            RefundPeriod = "2026-08",
            SalesOrderNo = "SO-001",
            CustomerId = customer.Id
        };
        db.FinanceReceipts.Add(receipt);
        db.FinanceExpenses.Add(expense);
        db.BaseTaxRefunds.Add(refund);
        await db.SaveChangesAsync();

        var snapshot = new
        {
            CustomerStatus = customer.Status,
            CustomerCreditLimit = customer.CreditLimit,
            CustomerCreditStatus = customer.CreditStatus,
            CustomerCreditDays = customer.CreditDays,
            CustomerUpdatedAt = customer.UpdatedAt,
            OrderStatus = order.Status,
            OrderTotal = order.TotalAmount,
            OrderUpdatedAt = order.UpdatedAt,
            ListStatus = list.Status,
            ListContainerNo = list.ContainerNo,
            ListUpdatedAt = list.UpdatedAt,
            AgreementStatus = agreement.Status,
            AgreementUpdatedAt = agreement.RecordedAt,
            ReceiptStatus = receipt.Status,
            ReceiptAmount = receipt.Amount,
            ReceiptUpdatedAt = receipt.UpdatedAt,
            ExpenseAmount = expense.Amount,
            ExpenseUpdatedAt = expense.UpdatedAt,
            RefundPeriod = refund.RefundPeriod,
            RefundUpdatedAt = refund.UpdatedAt
        };

        var controller = BuildController(db);
        var draft = AssertOk<AgencyServiceFeeStatementDto>(await controller.Create(SaveDto(
            customer.Id, agreement.Id, new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id, amount: 30m),
                LineDto(AgencyServiceFeeStatementRules.SourceTypeLoadingList, list.Id, amount: 20m)
            })));
        AssertOk<AgencyServiceFeeStatementDto>(await controller.Update(draft.Id, SaveDto(
            customer.Id, agreement.Id,
            new[]
            {
                LineDto(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id, amount: 35m),
                LineDto(AgencyServiceFeeStatementRules.SourceTypeLoadingList, list.Id, amount: 25m)
            })));
        var recorded = AssertOk<AgencyServiceFeeStatementDto>(await controller.Record(draft.Id));
        AssertOk<AgencyServiceFeeStatementDto>(await controller.Void(recorded.Id,
            new AgencyServiceFeeStatementVoidRequest { Reason = "对账口径更正" }));

        db.ChangeTracker.Clear();
        var reloadedCustomer = db.BaseCustomers.Single(c => c.Id == customer.Id);
        var reloadedOrder = db.SalesOrders.Single(o => o.Id == order.Id);
        var reloadedList = db.ContainerLoadingLists.Single(l => l.Id == list.Id);
        var reloadedAgreement = db.AgencyServiceFeeAgreements.Single(a => a.Id == agreement.Id);
        var reloadedReceipt = db.FinanceReceipts.Single(r => r.Id == receipt.Id);
        var reloadedExpense = db.FinanceExpenses.Single(x => x.Id == expense.Id);
        var reloadedRefund = db.BaseTaxRefunds.Single(x => x.Id == refund.Id);

        Assert.Equal(snapshot.CustomerStatus, reloadedCustomer.Status);
        Assert.Equal(snapshot.CustomerCreditLimit, reloadedCustomer.CreditLimit);
        Assert.Equal(snapshot.CustomerCreditStatus, reloadedCustomer.CreditStatus);
        Assert.Equal(snapshot.CustomerCreditDays, reloadedCustomer.CreditDays);
        Assert.Equal(snapshot.CustomerUpdatedAt, reloadedCustomer.UpdatedAt);
        Assert.Equal(snapshot.OrderStatus, reloadedOrder.Status);
        Assert.Equal(snapshot.OrderTotal, reloadedOrder.TotalAmount);
        Assert.Equal(snapshot.OrderUpdatedAt, reloadedOrder.UpdatedAt);
        Assert.Equal(snapshot.ListStatus, reloadedList.Status);
        Assert.Equal(snapshot.ListContainerNo, reloadedList.ContainerNo);
        Assert.Equal(snapshot.ListUpdatedAt, reloadedList.UpdatedAt);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusRecorded, reloadedAgreement.Status);   // 协议未被改写
        Assert.Equal(snapshot.AgreementUpdatedAt, reloadedAgreement.RecordedAt);
        Assert.Equal(snapshot.AgreementStatus, reloadedAgreement.Status);
        Assert.Equal(snapshot.ReceiptStatus, reloadedReceipt.Status);
        Assert.Equal(snapshot.ReceiptAmount, reloadedReceipt.Amount);
        Assert.Equal(snapshot.ReceiptUpdatedAt, reloadedReceipt.UpdatedAt);
        Assert.Equal(snapshot.ExpenseAmount, reloadedExpense.Amount);
        Assert.Equal(snapshot.ExpenseUpdatedAt, reloadedExpense.UpdatedAt);
        Assert.Equal(snapshot.RefundPeriod, reloadedRefund.RefundPeriod);
        Assert.Equal(snapshot.RefundUpdatedAt, reloadedRefund.UpdatedAt);

        // 本模块只写自己的两张表（对账单 + 有效行：修改时被替换的原草稿行只软删除）
        Assert.Equal(1, db.AgencyServiceFeeStatements.Count());
        Assert.Equal(2, db.AgencyServiceFeeStatementLines.Count(l => !l.IsDeleted));
        Assert.Equal(2, db.AgencyServiceFeeStatementLines.Count(l => l.IsDeleted));
    }

    // ==================== 11. 纯规则、模型、幂等结构与前端接线契约 ====================

    [Fact]
    public void 纯规则_身份规范化与来源资格与合计计算()
    {
        Assert.Equal("ASFS2026001", AgencyServiceFeeStatementRules.NormalizeIdentityPart("asfs 2026-001"));
        Assert.Equal("ASFS2026001", AgencyServiceFeeStatementRules.NormalizeIdentityPart("ASFS-2026_001"));
        Assert.Equal("(未填对账单号)", AgencyServiceFeeStatementRules.IdentityText("  "));

        Assert.True(AgencyServiceFeeStatementRules.SourceCarriesCurrency(
            AgencyServiceFeeStatementRules.SourceTypeSalesOrder));
        Assert.False(AgencyServiceFeeStatementRules.SourceCarriesCurrency(
            AgencyServiceFeeStatementRules.SourceTypeLoadingList));
        Assert.Equal("销售订单", AgencyServiceFeeStatementRules.SourceTypeText("sales-order"));
        Assert.Contains("未知来源类型", AgencyServiceFeeStatementRules.SourceTypeText("other"));

        // 合计：按币种精度求和（JPY 0 位 / USD 2 位）
        Assert.Equal(600.01m, AgencyServiceFeeStatementRules.ComputeTotal(
            new[] { 100.005m, 200.004m, 300.001m }, "USD"));
        Assert.Equal(1000m, AgencyServiceFeeStatementRules.ComputeTotal(
            new[] { 999.6m, 0.4m }, "JPY"));

        // 客户 / 协议资格（历史证据只读标注）
        Assert.Equal("客户可用", AgencyServiceFeeStatementRules.CustomerAvailabilityText(1, false));
        Assert.Contains("已停用", AgencyServiceFeeStatementRules.CustomerAvailabilityText(0, false));
        Assert.Contains("已删除", AgencyServiceFeeStatementRules.CustomerAvailabilityText(null, true));
        Assert.Contains("已作废", AgencyServiceFeeStatementRules.AgreementAvailabilityText(
            AgencyServiceFeeAgreementRules.StatusVoided, false));
        Assert.True(AgencyServiceFeeStatementRules.IsAgreementSelectable(
            AgencyServiceFeeAgreementRules.StatusRecorded, false));
        Assert.False(AgencyServiceFeeStatementRules.IsAgreementSelectable(
            AgencyServiceFeeAgreementRules.StatusDraft, false));

        // 状态机
        Assert.Equal("草稿", AgencyServiceFeeStatementRules.StatusText(0));
        Assert.Equal("已登记", AgencyServiceFeeStatementRules.StatusText(1));
        Assert.Equal("已作废", AgencyServiceFeeStatementRules.StatusText(2));
    }

    [Fact]
    public void 模型结构_表头长度精度_唯一身份_无外键且读取标注不落库()
    {
        using var db = TestDbFactory.Create();

        var statementType = db.Model.FindEntityType(typeof(AgencyServiceFeeStatement));
        Assert.NotNull(statementType);
        Assert.Equal(nameof(AgencyServiceFeeStatement), statementType!.GetTableName());
        Assert.Equal("db_owner", statementType.GetSchema());

        Assert.Equal(50, statementType.FindProperty(nameof(AgencyServiceFeeStatement.StatementNo))!.GetMaxLength());
        Assert.Equal(50, statementType.FindProperty(nameof(AgencyServiceFeeStatement.NormalizedStatementNo))!.GetMaxLength());
        Assert.Equal(50, statementType.FindProperty(nameof(AgencyServiceFeeStatement.CustomerCode))!.GetMaxLength());
        Assert.Equal(200, statementType.FindProperty(nameof(AgencyServiceFeeStatement.CustomerName))!.GetMaxLength());
        Assert.Equal(20, statementType.FindProperty(nameof(AgencyServiceFeeStatement.Currency))!.GetMaxLength());
        Assert.Equal(50, statementType.FindProperty(nameof(AgencyServiceFeeStatement.AgreementNo))!.GetMaxLength());
        Assert.Equal(200, statementType.FindProperty(nameof(AgencyServiceFeeStatement.AgreementTermsText))!.GetMaxLength());
        Assert.Equal(18, statementType.FindProperty(nameof(AgencyServiceFeeStatement.TotalAmount))!.GetPrecision());
        Assert.Equal(2, statementType.FindProperty(nameof(AgencyServiceFeeStatement.TotalAmount))!.GetScale());
        Assert.Equal(100, statementType.FindProperty(nameof(AgencyServiceFeeStatement.RecordedBy))!.GetMaxLength());
        Assert.Equal(500, statementType.FindProperty(nameof(AgencyServiceFeeStatement.VoidReason))!.GetMaxLength());

        var identity = statementType.GetIndexes().Single(i =>
            i.GetDatabaseName() == "UX_AgencyServiceFeeStatements_ActiveIdentity");
        Assert.True(identity.IsUnique);
        Assert.Equal("IsDeleted = 0 AND Status <> 2", identity.GetFilter());
        Assert.Equal(
            new[]
            {
                nameof(AgencyServiceFeeStatement.CustomerId),
                nameof(AgencyServiceFeeStatement.NormalizedStatementNo)
            },
            identity.Properties.Select(p => p.Name).ToArray());

        Assert.Contains(statementType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_AgencyServiceFeeStatements_CustomerId_Status");
        Assert.Contains(statementType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_AgencyServiceFeeStatements_Status_StatementDate");
        Assert.Contains(statementType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_AgencyServiceFeeStatements_AgreementId_Status");
        Assert.Contains(statementType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_AgencyServiceFeeStatements_NormalizedStatementNo");

        Assert.DoesNotContain(statementType.GetProperties(), p => p.Name
            is nameof(AgencyServiceFeeStatement.CustomerAvailable)
            or nameof(AgencyServiceFeeStatement.CustomerAvailabilityText)
            or nameof(AgencyServiceFeeStatement.AgreementAvailable)
            or nameof(AgencyServiceFeeStatement.AgreementAvailabilityText)
            or nameof(AgencyServiceFeeStatement.DueDateText)
            or nameof(AgencyServiceFeeStatement.ServicePeriodText)
            or nameof(AgencyServiceFeeStatement.CurrencyCompatibilityText)
            or nameof(AgencyServiceFeeStatement.LineCount)
            or nameof(AgencyServiceFeeStatement.SourceLinkRuleText)
            or nameof(AgencyServiceFeeStatement.AmountRuleText)
            or nameof(AgencyServiceFeeStatement.UniquenessRuleText)
            or nameof(AgencyServiceFeeStatement.SeparationText)
            or nameof(AgencyServiceFeeStatement.BoundaryText));

        // 刻意不建到客户 / 协议的任何外键与导航属性
        Assert.Empty(statementType.GetForeignKeys());
        Assert.Empty(statementType.GetNavigations());
    }

    [Fact]
    public void 模型结构_行长度精度_唯一来源_无外键且读取标注不落库()
    {
        using var db = TestDbFactory.Create();

        var lineType = db.Model.FindEntityType(typeof(AgencyServiceFeeStatementLine));
        Assert.NotNull(lineType);
        Assert.Equal(nameof(AgencyServiceFeeStatementLine), lineType!.GetTableName());
        Assert.Equal("db_owner", lineType.GetSchema());
        Assert.Equal(20, lineType.FindProperty(nameof(AgencyServiceFeeStatementLine.SourceType))!.GetMaxLength());
        Assert.Equal(50, lineType.FindProperty(nameof(AgencyServiceFeeStatementLine.SourceNo))!.GetMaxLength());
        Assert.Equal(200, lineType.FindProperty(nameof(AgencyServiceFeeStatementLine.SourceCustomerName))!.GetMaxLength());
        Assert.Equal(20, lineType.FindProperty(nameof(AgencyServiceFeeStatementLine.SourceCurrency))!.GetMaxLength());
        Assert.Equal(200, lineType.FindProperty(nameof(AgencyServiceFeeStatementLine.Description))!.GetMaxLength());
        Assert.Equal(18, lineType.FindProperty(nameof(AgencyServiceFeeStatementLine.BasisQuantity))!.GetPrecision());
        Assert.Equal(4, lineType.FindProperty(nameof(AgencyServiceFeeStatementLine.BasisQuantity))!.GetScale());
        Assert.Equal(18, lineType.FindProperty(nameof(AgencyServiceFeeStatementLine.Amount))!.GetPrecision());
        Assert.Equal(2, lineType.FindProperty(nameof(AgencyServiceFeeStatementLine.Amount))!.GetScale());

        // **防重复计费证据**：同一来源在未作废行内全局唯一
        var sourceIndex = lineType.GetIndexes().Single(i =>
            i.GetDatabaseName() == "UX_AgencyServiceFeeStatementLines_ActiveSource");
        Assert.True(sourceIndex.IsUnique);
        Assert.Equal("IsDeleted = 0 AND Status <> 2", sourceIndex.GetFilter());
        Assert.Equal(
            new[]
            {
                nameof(AgencyServiceFeeStatementLine.SourceType),
                nameof(AgencyServiceFeeStatementLine.SourceId)
            },
            sourceIndex.Properties.Select(p => p.Name).ToArray());

        Assert.Contains(lineType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_AgencyServiceFeeStatementLines_StatementId_LineNo");
        Assert.Contains(lineType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_AgencyServiceFeeStatementLines_Status_RecordedAt");

        Assert.DoesNotContain(lineType.GetProperties(), p => p.Name
            is nameof(AgencyServiceFeeStatementLine.SourceAvailable)
            or nameof(AgencyServiceFeeStatementLine.SourceAvailabilityText)
            or nameof(AgencyServiceFeeStatementLine.SourceCurrencyCompatibilityText)
            or nameof(AgencyServiceFeeStatementLine.SourceTypeText)
            or nameof(AgencyServiceFeeStatementLine.SourceIdentityText)
            or nameof(AgencyServiceFeeStatementLine.StatusText)
            or nameof(AgencyServiceFeeStatementLine.AmountText)
            or nameof(AgencyServiceFeeStatementLine.AmountRuleText));
        Assert.Empty(lineType.GetForeignKeys());
        Assert.Empty(lineType.GetNavigations());
    }

    [Fact]
    public void Schema_第43段幂等建表建索引且不含任何回填或账务语句()
    {
        var script = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.AgencyServiceFeeStatements') IS NULL", script);
        Assert.Contains("IF OBJECT_ID('db_owner.AgencyServiceFeeStatementLines') IS NULL", script);
        Assert.Contains("DueDate DATETIME2 NULL", script);
        Assert.Contains("TotalAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("Amount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("BasisQuantity DECIMAL(18,4) NULL", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_AgencyServiceFeeStatements_ActiveIdentity", script);
        Assert.Contains(
            "ON db_owner.AgencyServiceFeeStatements(CustomerId, NormalizedStatementNo)", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_AgencyServiceFeeStatementLines_ActiveSource", script);
        Assert.Contains("ON db_owner.AgencyServiceFeeStatementLines(SourceType, SourceId)", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status <> 2;", script);

        var start = script.IndexOf("// 43. 代理服务费对账单证据", StringComparison.Ordinal);
        Assert.True(start > 0);
        var segment = script[start..];
        // 本模块段落只建两张表 + 过滤索引：不改写任何既有表结构，也不做任何回填 / 发票 / 记账 / 收款语句
        Assert.DoesNotContain("ALTER TABLE", segment);
        Assert.DoesNotContain("UPDATE db_owner", segment);
        Assert.DoesNotContain("INSERT INTO db_owner", segment);
        Assert.DoesNotContain("DELETE FROM db_owner", segment);
        Assert.DoesNotContain("EXEC ", segment);
        Assert.DoesNotContain("FK_AgencyServiceFeeStatement", segment);
    }

    [Fact]
    public void 请求契约_不提供来源快照或账务字段_服务端权威字段全部由服务端写入()
    {
        // 请求 DTO 不接受来源快照 / 状态 / 客户快照 / 合计 / 发票 / 记账字段（客户端提交的同名字段不被采信）
        var lineNames = typeof(AgencyServiceFeeStatementLineSaveDto)
            .GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(lineNames, n => n.Contains("SourceNo", StringComparison.Ordinal)
            || n.Contains("SourceStatus", StringComparison.Ordinal)
            || n.Contains("SourceDate", StringComparison.Ordinal)
            || n.Contains("Currency", StringComparison.Ordinal)
            || n.Contains("LineNo", StringComparison.Ordinal)
            || n.Contains("RecordedBy", StringComparison.Ordinal)
            || n.Contains("Status", StringComparison.Ordinal));

        var requestNames = typeof(AgencyServiceFeeStatementSaveDto)
            .GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(requestNames, n => n.Contains("Invoice", StringComparison.Ordinal)
            || n.Contains("Ledger", StringComparison.Ordinal)
            || n.Contains("Voucher", StringComparison.Ordinal)
            || n.Contains("Receipt", StringComparison.Ordinal)
            || n.Contains("RecordedBy", StringComparison.Ordinal)
            || n.Contains("Status", StringComparison.Ordinal)
            || n.Contains("Total", StringComparison.Ordinal));
        Assert.Contains("Lines", requestNames);

        // 实体确实持久化「服务端合计」与「可选到期日」列（服务端权威字段而非仅 DTO 计算）
        Assert.Contains(typeof(AgencyServiceFeeStatement).GetProperties(), p => p.Name == "TotalAmount");
        Assert.Contains(typeof(AgencyServiceFeeStatement).GetProperties(), p => p.Name == "DueDate");
        Assert.Contains(typeof(AgencyServiceFeeStatement).GetProperties(), p => p.Name == "AgreementId");
    }

    [Fact]
    public void 前端与路由接线契约()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/agency-service-fee-statements.js", index);

        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules.js"));
        // 工具栏入口必须带括号（extraActions 直接注入 onclick 属性），行操作只写函数名（渲染时注入行 Id）
        Assert.Contains("onclick: 'openAgencyServiceFeeStatementRegister()'", modules);
        Assert.Contains("onclick: 'openAgencyServiceFeeStatementRegister'", modules);

        var js = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "agency-service-fee-statements.js"));
        Assert.Contains("async function openAgencyServiceFeeStatementRegister", js);
        Assert.Contains("'/api/agency-service-fee-statements'", js);
        Assert.Contains("'/api/agency-service-fee-statements?'", js);
        Assert.Contains("'/api/agency-service-fee-statements/source-options?", js);
        Assert.Contains("/record", js);
        Assert.Contains("/void", js);
        Assert.Contains("asfsSaveForm", js);
        Assert.Contains("asfsConfirmVoid", js);
        Assert.Contains("asfsChangeLineType", js);
        Assert.Contains("不是税务发票", js);
        Assert.Contains("不会按单号文本、金额或日期相似度", js);
        Assert.Contains("到期日（可选；留空 = 未知，系统不推算）", js);
        Assert.Contains("服务端计算", js);

        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "AgencyServiceFeeStatementController.cs"));
        Assert.Contains("[Route(\"api/agency-service-fee-statements\")]", controller);
        Assert.Contains("{id:long}/record", controller);
        Assert.Contains("{id:long}/void", controller);
        Assert.Contains("[HttpGet(\"metadata\")]", controller);
        Assert.Contains("[HttpGet(\"source-options\")]", controller);
    }

    /// <summary>
    /// 只读计数上下文代理（<see cref="DispatchProxy"/>）：记录访问的数据集（<c>DbSet</c> 属性）名称与写入次数，
    /// 用于断言「分页 / 有界查询」「无逐行查库」与「只读不写库」；不改动生产代码。
    /// </summary>
    public class StatementReadCounter : DispatchProxy
    {
        private IErpDbContext _inner = null!;

        /// <summary>包装后的上下文（服务按 <see cref="IErpDbContext"/> 使用）</summary>
        public IErpDbContext Proxy { get; private set; } = null!;

        /// <summary>数据集（<c>DbSet</c> 属性）访问次数：即本次查询实际发起的数据集访问次数</summary>
        public int DatasetReads => ReadProperties.Count;

        /// <summary>被访问的数据集属性名</summary>
        public List<string> ReadProperties { get; } = new();

        /// <summary><c>SaveChangesAsync</c> 调用次数：只读库恒为 0</summary>
        public int WriteCalls { get; private set; }

        /// <summary>包装一个真实上下文（计数从返回对象上读取）</summary>
        public static StatementReadCounter Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, StatementReadCounter>();
            var counting = (StatementReadCounter)(object)proxy;
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
