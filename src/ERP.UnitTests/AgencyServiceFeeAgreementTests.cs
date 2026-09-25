using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 代理服务费协议证据登记单元测试（ERP-069）。覆盖：新增前既有模型审计（客户 / 订单佣金比例、供应商返点、
/// 业务员提成报表都是各自独立的模型，本模块既不替换也不派生）、支持的计费方式（比例费率 / 固定金额）与必填条款、
/// 不兼容条款组合、币种口径、生效日期区间、协议号与身份规范化、客户权威资格（存在 / 未删除 / 启用）、
/// 唯一身份与重复拒绝、草稿编辑与登记冻结、显式作废与历史保留、台账过滤与分页有界、
/// 「费用条款只来自用户显式提交值」（不从 SalesCommissionRate、客户比例或历史订单推断）、
/// 与业务员提成报表的分离（登记 / 作废不改变提成报表结果）、相邻记录非变更边界、
/// 「无任何账外 / 不披露佣金字段」的模型契约，以及模型 / 幂等结构 / 前端接线契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本、不开票 / 不记账 / 不授权付款、
/// 不做任何浏览器 / UI 验收。
/// </summary>
public class AgencyServiceFeeAgreementTests
{
    // ==================== 0. 测试脚手架 ====================

    /// <summary>
    /// 构造控制器并注入测试身份（与其它登记册测试同一口径）：控制器从 ClaimsPrincipal 读取登记人，
    /// 没有身份时记「未知用户」。
    /// </summary>
    private static AgencyServiceFeeAgreementController BuildController(
        ErpDbContext db, string? userName = null)
    {
        var controller = new AgencyServiceFeeAgreementController(db);
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
        ErpDbContext db, string code, string name, int status = 1, bool deleted = false,
        decimal commissionRatio = 0m)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30,
            CommissionRatio = commissionRatio,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency = Currency.USD,
        decimal totalAmount = 1000m, decimal commissionRatio = 0m)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            CommissionRatio = commissionRatio,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static AgencyServiceFeeAgreementSaveDto SaveDto(
        long customerId, string agreementNo = "ASF-2026-001", string feeMethod = "比例费率",
        decimal? ratePercent = 1.5m, decimal? fixedAmount = null, string currency = "USD",
        string feeBasis = "按出口发票金额", DateTime? effectiveFrom = null, DateTime? effectiveTo = null,
        string remark = "")
        => new()
        {
            AgreementNo = agreementNo,
            CustomerId = customerId,
            EffectiveFrom = effectiveFrom ?? new DateTime(2026, 1, 1),
            EffectiveTo = effectiveTo,
            Currency = currency,
            FeeMethod = feeMethod,
            RatePercent = ratePercent,
            FixedAmount = fixedAmount,
            FeeBasis = feeBasis,
            Remark = remark
        };

    /// <summary>创建协议并登记（登记人由服务端按已认证身份写入；单元测试中控制器没有 ClaimsPrincipal，记「未知用户」）</summary>
    private static async Task<AgencyServiceFeeAgreementDto> CreateRecordedAsync(
        AgencyServiceFeeAgreementController controller, AgencyServiceFeeAgreementSaveDto dto)
    {
        var created = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Create(dto));
        return AssertOk<AgencyServiceFeeAgreementDto>(await controller.Record(created.Id));
    }

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

    // ==================== 1. 计费方式与显式费用条款 ====================

    [Fact]
    public async Task 比例费率草稿_写入显式条款与客户快照且默认草稿状态()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口", commissionRatio: 7.5m);
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, ratePercent: 1.25m, currency: "usd")));

        Assert.Equal("ASF-2026-001", created.AgreementNo);
        Assert.Equal(customer.Id, created.CustomerId);
        Assert.Equal("C001", created.CustomerCode);
        Assert.Equal("义乌进出口", created.CustomerName);
        Assert.Equal("USD", created.Currency);
        Assert.Equal("比例费率", created.FeeMethod);
        Assert.Equal(1.25m, created.RatePercent);
        Assert.Equal(0m, created.FixedAmount);
        Assert.Equal("按出口发票金额", created.FeeBasis);
        Assert.Equal(0, created.Status);
        Assert.True(created.IsDraft);
        Assert.False(created.IsRecorded);
        Assert.False(created.IsVoided);
        Assert.Null(created.RecordedAt);
        Assert.Equal(string.Empty, created.RecordedBy);
        Assert.Contains("比例费率", created.FeeTermsText);
        Assert.Contains("USD", created.FeeTermsText);
        Assert.Contains("1.25", created.FeeTermsText);
        // 客户主数据比例（7.5%）绝不参与：条款保持用户显式提交的 1.25%
        Assert.DoesNotContain("7.5", created.FeeTermsText);
        Assert.Contains("不是税务发票", created.BoundaryText);
        Assert.Contains("付款授权", created.BoundaryText);
    }

    [Fact]
    public async Task 比例费率_费率必须大于0且不得缺失()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, ratePercent: 0m)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, ratePercent: null)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, ratePercent: -1m)));
        Assert.Empty(db.AgencyServiceFeeAgreements);
    }

    [Fact]
    public async Task 比例费率_费率不得超过100且不自动截断()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, ratePercent: 100.0001m)));
        Assert.Empty(db.AgencyServiceFeeAgreements);

        // 边界值 100 允许
        var boundary = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, ratePercent: 100m)));
        Assert.Equal(100m, boundary.RatePercent);
    }

    [Fact]
    public async Task 比例费率_不得同时显式填写固定金额()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, ratePercent: 1.5m, fixedAmount: 500m)));
        Assert.Contains("不能同时填写固定金额", ex.Message);
        Assert.Empty(db.AgencyServiceFeeAgreements);
    }

    [Fact]
    public async Task 固定金额_按币种精度取整后写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var jpy = new AgencyServiceFeeAgreementSaveDto
        {
            AgreementNo = "ASF-JPY-001",
            CustomerId = customer.Id,
            EffectiveFrom = new DateTime(2026, 1, 1),
            Currency = "JPY",
            FeeMethod = "固定金额",
            FixedAmount = 1000.4m,
            FeeBasis = "按月固定"
        };
        var created = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Create(jpy));

        Assert.Equal(1000m, created.FixedAmount);
        Assert.Equal(0m, created.RatePercent);
        Assert.Equal(0, created.AmountDecimals);       // JPY = 0 位小数
        Assert.Contains("固定金额", created.FeeTermsText);

        var usd = SaveDto(customer.Id, agreementNo: "ASF-USD-002", feeMethod: "固定金额",
            ratePercent: null, fixedAmount: 12.345m, currency: "USD");
        var createdUsd = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Create(usd));
        Assert.Equal(12.35m, createdUsd.FixedAmount);   // USD = 2 位小数，0.5 进位
        Assert.Equal(2, createdUsd.AmountDecimals);
    }

    [Fact]
    public async Task 固定金额_取整后为0或负数被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var zeroAfterRound = SaveDto(customer.Id, feeMethod: "固定金额", ratePercent: null,
            fixedAmount: 0.4m, currency: "JPY");
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(zeroAfterRound));

        var negative = SaveDto(customer.Id, feeMethod: "固定金额", ratePercent: null, fixedAmount: -1m);
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(negative));
        Assert.Empty(db.AgencyServiceFeeAgreements);
    }

    [Fact]
    public async Task 固定金额_不得同时显式填写费率()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, feeMethod: "固定金额",
                ratePercent: 2m, fixedAmount: 500m)));
        Assert.Contains("不能同时填写费率", ex.Message);
        Assert.Empty(db.AgencyServiceFeeAgreements);
    }

    [Fact]
    public async Task 未知或空计费方式被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, feeMethod: "阶梯费率")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, feeMethod: string.Empty)));
        Assert.Empty(db.AgencyServiceFeeAgreements);
    }

    [Fact]
    public async Task 费率保留4位小数且0_5进位()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, ratePercent: 1.234567m)));
        Assert.Equal(1.2346m, created.RatePercent);

        var fiveUp = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, agreementNo: "ASF-2026-002",
                ratePercent: 0.56785m)));
        Assert.Equal(0.5679m, fiveUp.RatePercent);   // 0.5 进位，不使用银行家舍入
    }

    // ==================== 2. 币种、生效区间与有界文本 ====================

    [Fact]
    public async Task 币种必须受支持且规范化为大写()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, currency: " eur ")));
        Assert.Equal("EUR", created.Currency);

        // 留空按系统默认币种处理（CNY）
        var fallback = SaveDto(customer.Id, agreementNo: "ASF-2026-003", currency: string.Empty);
        var createdFallback = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Create(fallback));
        Assert.Equal("CNY", createdFallback.Currency);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, agreementNo: "ASF-2026-004", currency: "BTC")));
        Assert.Equal(2, db.AgencyServiceFeeAgreements.Count());
    }

    [Fact]
    public async Task 生效起始日期必须显式填写()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var dto = SaveDto(customer.Id);
        dto.EffectiveFrom = null;
        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(dto));
        Assert.Contains("生效起始日期", ex.Message);
        Assert.Empty(db.AgencyServiceFeeAgreements);
    }

    [Fact]
    public async Task 生效结束日期不得早于起始且留空为无固定结束日()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, effectiveFrom: new DateTime(2026, 3, 1),
                effectiveTo: new DateTime(2026, 2, 28))));

        var openEnded = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, effectiveFrom: new DateTime(2026, 3, 1))));
        Assert.Null(openEnded.EffectiveTo);
        Assert.Contains("无固定结束日", openEnded.EffectiveRangeText);

        var ranged = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, agreementNo: "ASF-2026-002",
                effectiveFrom: new DateTime(2026, 3, 1), effectiveTo: new DateTime(2026, 12, 31))));
        Assert.Equal(new DateTime(2026, 12, 31), ranged.EffectiveTo);
        Assert.Contains("2026-12-31", ranged.EffectiveRangeText);
    }

    [Fact]
    public async Task 计费依据说明必填且有界()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, feeBasis: "   ")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, feeBasis: new string('说', 201))));

        var trimmed = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, feeBasis: "  按订单 FOB 金额  ")));
        Assert.Equal("按订单 FOB 金额", trimmed.FeeBasis);
    }

    [Fact]
    public async Task 协议号必填且备注超长被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, agreementNo: string.Empty)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, agreementNo: new string('A', 51))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, remark: new string('备', 501))));
        Assert.Empty(db.AgencyServiceFeeAgreements);
    }

    // ==================== 3. 客户权威资格（存在 / 未删除 / 启用） ====================

    [Fact]
    public async Task 未选择客户或客户不存在被拒绝()
    {
        using var db = TestDbFactory.Create();
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(0)));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(SaveDto(9999)));
        Assert.Empty(db.AgencyServiceFeeAgreements);
    }

    [Fact]
    public async Task 客户已删除或已停用不能登记新协议()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedCustomer(db, "C-DEL", "已删除客户", deleted: true);
        var inactive = SeedCustomer(db, "C-OFF", "已停用客户", status: 0);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(SaveDto(deleted.Id)));
        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(inactive.Id)));
        Assert.Empty(db.AgencyServiceFeeAgreements);
    }

    [Fact]
    public async Task 客户停用或删除后历史证据仍可读并显式标注()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var recorded = await CreateRecordedAsync(controller, SaveDto(customer.Id));

        customer.Status = 0;
        await db.SaveChangesAsync();

        var afterDisabled = AssertOk<AgencyServiceFeeAgreementDto>(await controller.GetById(recorded.Id));
        Assert.Equal("义乌进出口", afterDisabled.CustomerName);
        Assert.False(afterDisabled.CustomerAvailable);
        Assert.Contains("已停用", afterDisabled.CustomerAvailabilityText);

        customer.IsDeleted = true;
        await db.SaveChangesAsync();

        var afterDeleted = AssertOk<AgencyServiceFeeAgreementDto>(await controller.GetById(recorded.Id));
        Assert.Equal("C001", afterDeleted.CustomerCode);
        Assert.False(afterDeleted.CustomerAvailable);
        Assert.Contains("已删除", afterDeleted.CustomerAvailabilityText);
        Assert.Equal(1.5m, afterDeleted.RatePercent);   // 原始条款保持登记当时口径
    }

    [Fact]
    public async Task 客户改名后历史证据保持登记当时快照()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Create(SaveDto(customer.Id)));

        customer.CustomerName = "义乌进出口（新名）";
        await db.SaveChangesAsync();

        var reloaded = AssertOk<AgencyServiceFeeAgreementDto>(await controller.GetById(created.Id));
        Assert.Equal("义乌进出口", reloaded.CustomerName);
        Assert.True(reloaded.CustomerAvailable);
    }

    // ==================== 4. 唯一身份与重复拒绝 ====================

    [Fact]
    public async Task 同一客户同一规范化协议号被视为重复并拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, agreementNo: "ASF-2026-001")));

        // 大小写、空白与连字符 / 下划线差异都归一为同一身份
        var ex = await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => controller.Create(SaveDto(customer.Id, agreementNo: " asf_2026 - 001 ")));
        Assert.Contains("重复协议被拒绝", ex.Message);
        Assert.Equal(1, db.AgencyServiceFeeAgreements.Count());
    }

    [Fact]
    public async Task 不同客户可以使用相同协议号()
    {
        using var db = TestDbFactory.Create();
        var first = SeedCustomer(db, "C001", "客户一");
        var second = SeedCustomer(db, "C002", "客户二");
        var controller = BuildController(db);

        AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(first.Id, agreementNo: "ASF-2026-001")));
        var other = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(second.Id, agreementNo: "ASF-2026-001")));

        Assert.Equal("客户二", other.CustomerName);
        Assert.Equal(2, db.AgencyServiceFeeAgreements.Count());
    }

    [Fact]
    public async Task 草稿占用身份_作废后可重新登记同一身份()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var draft = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, agreementNo: "ASF-2026-001")));
        await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => controller.Create(SaveDto(customer.Id, agreementNo: "ASF-2026-001")));

        AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Void(draft.Id, new AgencyServiceFeeAgreementVoidRequest { Reason = "登记错误" }));

        var reRegistered = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, agreementNo: "ASF-2026-001", ratePercent: 2m)));
        Assert.Equal(2m, reRegistered.RatePercent);
        Assert.Equal(2, db.AgencyServiceFeeAgreements.Count());
    }

    // ==================== 5. 草稿编辑、登记冻结与显式作废 ====================

    [Fact]
    public async Task 修改草稿成功并保留客户快照()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var created = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Create(SaveDto(customer.Id)));

        var updated = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Update(created.Id,
            SaveDto(customer.Id, feeMethod: "固定金额", ratePercent: null, fixedAmount: 800m,
                currency: "USD", feeBasis: "按票固定", remark: "已改为固定金额")));

        Assert.Equal("固定金额", updated.FeeMethod);
        Assert.Equal(800m, updated.FixedAmount);
        Assert.Equal(0m, updated.RatePercent);
        Assert.Equal("按票固定", updated.FeeBasis);
        Assert.Equal("已改为固定金额", updated.Remark);
        Assert.True(updated.IsDraft);
    }

    [Fact]
    public async Task 修改保持自身身份不算重复_占用他人身份则拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var first = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, agreementNo: "ASF-2026-001")));
        var second = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, agreementNo: "ASF-2026-002")));

        var same = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Update(first.Id,
            SaveDto(customer.Id, agreementNo: "ASF-2026-001", ratePercent: 2m)));
        Assert.Equal(2m, same.RatePercent);

        await AssertBusinessAsync(ErrorCodes.Duplicate, () => controller.Update(second.Id,
            SaveDto(customer.Id, agreementNo: "ASF-2026-001")));
    }

    [Fact]
    public async Task 已登记或已作废的协议不可修改()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var recorded = await CreateRecordedAsync(controller,
            SaveDto(customer.Id, agreementNo: "ASF-2026-001"));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Update(recorded.Id,
            SaveDto(customer.Id, agreementNo: "ASF-2026-001", ratePercent: 9m)));

        var draft = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, agreementNo: "ASF-2026-002")));
        AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Void(draft.Id, new AgencyServiceFeeAgreementVoidRequest { Reason = "作废原因" }));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Update(draft.Id,
            SaveDto(customer.Id, agreementNo: "ASF-2026-002", ratePercent: 9m)));
    }

    [Fact]
    public async Task 重复登记与已作废再登记都被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var recorded = await CreateRecordedAsync(controller, SaveDto(customer.Id));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Record(recorded.Id));

        var draft = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, agreementNo: "ASF-2026-002")));
        AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Void(draft.Id, new AgencyServiceFeeAgreementVoidRequest { Reason = "作废原因" }));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Record(draft.Id));
    }

    [Fact]
    public async Task 作废原因必填且有界_重复作废被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var recorded = await CreateRecordedAsync(controller, SaveDto(customer.Id));

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(recorded.Id, new AgencyServiceFeeAgreementVoidRequest { Reason = "  " }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Void(recorded.Id,
            new AgencyServiceFeeAgreementVoidRequest { Reason = new string('原', 501) }));

        var voided = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Void(recorded.Id,
            new AgencyServiceFeeAgreementVoidRequest { Reason = "客户协商终止" }));
        Assert.True(voided.IsVoided);
        Assert.Equal("客户协商终止", voided.VoidReason);
        Assert.NotNull(voided.VoidedAt);

        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Void(recorded.Id,
            new AgencyServiceFeeAgreementVoidRequest { Reason = "再次作废" }));
    }

    [Fact]
    public async Task 作废保留原始条款_客户快照_登记人与时间戳()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        var recorded = await CreateRecordedAsync(controller, SaveDto(customer.Id, ratePercent: 1.5m,
            effectiveFrom: new DateTime(2026, 1, 1), effectiveTo: new DateTime(2026, 12, 31)));
        Assert.NotNull(recorded.RecordedAt);
        Assert.Equal("未知用户", recorded.RecordedBy);   // 单元测试控制器无 ClaimsPrincipal

        var voided = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Void(recorded.Id,
            new AgencyServiceFeeAgreementVoidRequest { Reason = "协议提前终止" }));

        Assert.Equal("ASF-2026-001", voided.AgreementNo);
        Assert.Equal("C001", voided.CustomerCode);
        Assert.Equal("义乌进出口", voided.CustomerName);
        Assert.Equal(1.5m, voided.RatePercent);
        Assert.Equal("按出口发票金额", voided.FeeBasis);
        Assert.Equal(new DateTime(2026, 1, 1), voided.EffectiveFrom);
        Assert.Equal(new DateTime(2026, 12, 31), voided.EffectiveTo);
        Assert.Equal(recorded.RecordedAt, voided.RecordedAt);
        Assert.Equal(recorded.RecordedBy, voided.RecordedBy);
        Assert.Equal(2, voided.Status);
        Assert.Equal("已作废", voided.StatusText);

        // 作废不物理删除：记录仍在库中，只是状态为已作废
        Assert.Equal(1, db.AgencyServiceFeeAgreements.Count());
        Assert.Equal("协议提前终止", db.AgencyServiceFeeAgreements.Single().VoidReason);
    }

    [Fact]
    public async Task 登记人由服务端写入且缺失记未知用户()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");

        var draft = await AgencyServiceFeeAgreementService.CreateAsync(db, SaveDto(customer.Id));
        var recorded = await AgencyServiceFeeAgreementService.RecordAsync(db, draft.Id, null);
        Assert.Equal("未知用户", recorded.RecordedBy);

        var draft2 = await AgencyServiceFeeAgreementService.CreateAsync(db,
            SaveDto(customer.Id, agreementNo: "ASF-2026-002"));
        var recorded2 = await AgencyServiceFeeAgreementService.RecordAsync(db, draft2.Id, " 张业务 ");
        Assert.Equal("张业务", recorded2.RecordedBy);

        var draft3 = await AgencyServiceFeeAgreementService.CreateAsync(db,
            SaveDto(customer.Id, agreementNo: "ASF-2026-003"));
        var recorded3 = await AgencyServiceFeeAgreementService.RecordAsync(db, draft3.Id, new string('名', 150));
        Assert.Equal(100, recorded3.RecordedBy.Length);   // 有界：登记人长度上限 100

        // 控制器路径：登记人来自已认证身份（不采信客户端提交的值）
        var controller = BuildController(db, "李经理");
        var viaController = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, agreementNo: "ASF-2026-004")));
        var recordedByController = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Record(viaController.Id));
        Assert.Equal("李经理", recordedByController.RecordedBy);
    }

    // ==================== 6. 台账读取（过滤、分页与有界） ====================

    [Fact]
    public async Task 台账支持客户_状态_币种_计费方式_生效日期与关键字过滤()
    {
        using var db = TestDbFactory.Create();
        var first = SeedCustomer(db, "C001", "客户一");
        var second = SeedCustomer(db, "C002", "客户二");
        var controller = BuildController(db);

        var a = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Create(SaveDto(first.Id,
            agreementNo: "ASF-A", currency: "USD", effectiveFrom: new DateTime(2026, 1, 1))));
        AssertOk<AgencyServiceFeeAgreementDto>(await controller.Create(SaveDto(first.Id,
            agreementNo: "ASF-B", feeMethod: "固定金额", ratePercent: null, fixedAmount: 300m,
            currency: "CNY", effectiveFrom: new DateTime(2026, 6, 1))));
        var c = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Create(SaveDto(second.Id,
            agreementNo: "ASF-C", currency: "USD", effectiveFrom: new DateTime(2026, 9, 1),
            feeBasis: "按装柜批次")));
        await controller.Record(c.Id);

        var byCustomer = AssertOk<PagedResult<AgencyServiceFeeAgreementDto>>(
            await controller.GetPaged(new AgencyServiceFeeAgreementQuery { CustomerId = first.Id }));
        Assert.Equal(2, byCustomer.Total);

        var byStatus = AssertOk<PagedResult<AgencyServiceFeeAgreementDto>>(
            await controller.GetPaged(new AgencyServiceFeeAgreementQuery { Status = 1 }));
        Assert.Equal(1, byStatus.Total);
        Assert.Equal("ASF-C", byStatus.Items[0].AgreementNo);

        var byCurrency = AssertOk<PagedResult<AgencyServiceFeeAgreementDto>>(
            await controller.GetPaged(new AgencyServiceFeeAgreementQuery { Currency = "usd" }));
        Assert.Equal(2, byCurrency.Total);

        var byMethod = AssertOk<PagedResult<AgencyServiceFeeAgreementDto>>(
            await controller.GetPaged(new AgencyServiceFeeAgreementQuery { FeeMethod = "固定金额" }));
        Assert.Equal(1, byMethod.Total);

        var byRange = AssertOk<PagedResult<AgencyServiceFeeAgreementDto>>(
            await controller.GetPaged(new AgencyServiceFeeAgreementQuery
            {
                EffectiveFromFrom = new DateTime(2026, 5, 1),
                EffectiveFromTo = new DateTime(2026, 8, 1)
            }));
        Assert.Equal(1, byRange.Total);
        Assert.Equal("ASF-B", byRange.Items[0].AgreementNo);

        var byKeyword = AssertOk<PagedResult<AgencyServiceFeeAgreementDto>>(
            await controller.GetPaged(new AgencyServiceFeeAgreementQuery { Keyword = "装柜" }));
        Assert.Equal(1, byKeyword.Total);

        Assert.False(a.IsVoided);
    }

    [Fact]
    public async Task 台账未知筛选取值与超长关键字被拒绝()
    {
        using var db = TestDbFactory.Create();
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new AgencyServiceFeeAgreementQuery { Status = 9 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new AgencyServiceFeeAgreementQuery { FeeMethod = "阶梯费率" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new AgencyServiceFeeAgreementQuery { Currency = "BTC" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new AgencyServiceFeeAgreementQuery { Keyword = new string('长', 101) }));
    }

    [Fact]
    public async Task 台账分页有界且按生效起始日期倒序()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var controller = BuildController(db);

        for (var i = 1; i <= 5; i++)
        {
            AssertOk<AgencyServiceFeeAgreementDto>(await controller.Create(SaveDto(customer.Id,
                agreementNo: $"ASF-{i:000}", effectiveFrom: new DateTime(2026, i, 1))));
        }

        var pageOne = AssertOk<PagedResult<AgencyServiceFeeAgreementDto>>(await controller.GetPaged(
            new AgencyServiceFeeAgreementQuery { Page = 1, PageSize = 2 }));
        Assert.Equal(5, pageOne.Total);
        Assert.Equal(2, pageOne.Items.Count);
        Assert.Equal("ASF-005", pageOne.Items[0].AgreementNo);
        Assert.Equal("ASF-004", pageOne.Items[1].AgreementNo);

        // 分页参数有界：PageSize 超上限按上限截断，Page < 1 按第 1 页
        var bounded = AssertOk<PagedResult<AgencyServiceFeeAgreementDto>>(await controller.GetPaged(
            new AgencyServiceFeeAgreementQuery { Page = 0, PageSize = 9999 }));
        Assert.Equal(1, bounded.Page);
        Assert.Equal(AgencyServiceFeeAgreementQuery.MaxPageSize, bounded.PageSize);
        Assert.Equal(5, bounded.Items.Count);
    }

    [Fact]
    public async Task 详情_不存在或已删除的协议被拒绝()
    {
        using var db = TestDbFactory.Create();
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetById(0));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(12345));

        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var created = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Create(SaveDto(customer.Id)));

        var entity = db.AgencyServiceFeeAgreements.Single(x => x.Id == created.Id);
        entity.IsDeleted = true;
        await db.SaveChangesAsync();

        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(created.Id));
    }

    // ==================== 7. 与业务员提成报表的分离与不派生 ====================

    [Fact]
    public async Task 费用条款只来自显式提交值_不从提成设置或主数据比例派生()
    {
        using var db = TestDbFactory.Create();
        db.SysParameters.Add(new SysParameter
        {
            ParamKey = "SalesCommissionRate",
            ParamValue = "7",
            ParamName = "业务员提成比例",
            IsSystem = true
        });
        var customer = SeedCustomer(db, "C001", "义乌进出口", commissionRatio: 3.5m);
        SeedOrder(db, "SO-001", customer.Id, commissionRatio: 5m);
        await db.SaveChangesAsync();

        var controller = BuildController(db);

        // 显式 1.5% 保持 1.5%：既不被 7%（提成参数）覆盖，也不被 3.5%（客户比例）/ 5%（订单比例）覆盖
        var created = AssertOk<AgencyServiceFeeAgreementDto>(
            await controller.Create(SaveDto(customer.Id, ratePercent: 1.5m)));
        Assert.Equal(1.5m, created.RatePercent);
        Assert.DoesNotContain("7%", created.FeeTermsText);
        Assert.DoesNotContain("3.5", created.FeeTermsText);

        // 费率缺失时不从上述任一口径补齐：仍然拒绝
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(customer.Id, agreementNo: "ASF-2026-002", ratePercent: null)));

        // 事后修改提成参数 / 客户比例，协议条款保持不变（无任何派生关系）
        db.SysParameters.Single().ParamValue = "20";
        customer.CommissionRatio = 9.9m;
        await db.SaveChangesAsync();

        var reloaded = AssertOk<AgencyServiceFeeAgreementDto>(await controller.GetById(created.Id));
        Assert.Equal(1.5m, reloaded.RatePercent);

        // 客户侧比例按我们自己的显式改动变为 9.9%，协议条款仍是登记时的显式 1.5%（互不派生）
        Assert.Equal(9.9m, db.BaseCustomers.Single(c => c.Id == customer.Id).CommissionRatio);
        Assert.DoesNotContain("9.9", reloaded.FeeTermsText);
    }

    [Fact]
    public async Task 协议证据不改变业务员提成报表结果()
    {
        using var db = TestDbFactory.Create();
        db.SysParameters.Add(new SysParameter
        {
            ParamKey = "SalesCommissionRate",
            ParamValue = "10",
            ParamName = "业务员提成比例",
            IsSystem = true
        });
        var employee = new BaseEmployee { EmployeeCode = "E001", EmployeeName = "张三" };
        var product = new BaseProduct { ProductCode = "P001", ProductName = "保温杯", CostPrice = 60m };
        db.BaseEmployees.Add(employee);
        db.BaseProducts.Add(product);
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        await db.SaveChangesAsync();

        var order = new SalesOrder
        {
            OrderNo = "SO-001",
            OrderDate = new DateTime(2026, 9, 5),
            CustomerId = customer.Id,
            Currency = Currency.USD,
            TotalAmount = 2000m,
            SalesmanId = employee.Id,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id,
            ProductId = product.Id,
            ProductName = product.ProductName,
            Quantity = 20m,
            UnitPrice = 100m
        });
        await db.SaveChangesAsync();

        var report = new ReportService(db);
        var before = await report.GetSalesCommissionAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));
        Assert.Single(before);
        Assert.Equal(10m, before[0].CommissionRate);

        var controller = BuildController(db);
        var recorded = await CreateRecordedAsync(controller, SaveDto(customer.Id, ratePercent: 12.5m));
        AssertOk<AgencyServiceFeeAgreementDto>(await controller.Void(recorded.Id,
            new AgencyServiceFeeAgreementVoidRequest { Reason = "协议终止" }));

        var after = await report.GetSalesCommissionAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));
        Assert.Single(after);
        Assert.Equal(before[0].SalesmanName, after[0].SalesmanName);
        Assert.Equal(before[0].CommissionRate, after[0].CommissionRate);
        Assert.Equal(before[0].SalesAmount, after[0].SalesAmount);
        Assert.Equal(before[0].Profit, after[0].Profit);
        Assert.Equal(before[0].CommissionAmount, after[0].CommissionAmount);
    }

    [Fact]
    public async Task 登记与作废不改写客户_订单_收款_费用_退税与销项发票记录()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口", commissionRatio: 4m);
        var order = SeedOrder(db, "SO-001", customer.Id, totalAmount: 1500m, commissionRatio: 3m);

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
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = "普票",
            InvoiceNumber = "INV-001",
            NormalizedInvoiceNumber = "INV-001",
            InvoiceDate = new DateTime(2026, 9, 7),
            CustomerId = customer.Id,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            Currency = "USD",
            NetAmount = 900m,
            TaxAmount = 100m,
            GrossAmount = 1000m,
            Status = CustomerSalesInvoiceEvidenceRules.StatusRecorded
        };
        db.FinanceReceipts.Add(receipt);
        db.FinanceExpenses.Add(expense);
        db.BaseTaxRefunds.Add(refund);
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        await db.SaveChangesAsync();

        var snapshot = new
        {
            CustomerStatus = customer.Status,
            CustomerCreditLimit = customer.CreditLimit,
            CustomerCreditStatus = customer.CreditStatus,
            CustomerCommissionRatio = customer.CommissionRatio,
            CustomerUpdatedAt = customer.UpdatedAt,
            OrderStatus = order.Status,
            OrderTotal = order.TotalAmount,
            OrderCommissionRatio = order.CommissionRatio,
            OrderUpdatedAt = order.UpdatedAt,
            ReceiptStatus = receipt.Status,
            ReceiptAmount = receipt.Amount,
            ReceiptUpdatedAt = receipt.UpdatedAt,
            ExpenseAmount = expense.Amount,
            ExpenseUpdatedAt = expense.UpdatedAt,
            RefundPeriod = refund.RefundPeriod,
            RefundUpdatedAt = refund.UpdatedAt,
            InvoiceStatus = invoice.Status,
            InvoiceGross = invoice.GrossAmount,
            InvoiceUpdatedAt = invoice.UpdatedAt
        };

        var controller = BuildController(db);
        var draft = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Create(SaveDto(customer.Id)));
        AssertOk<AgencyServiceFeeAgreementDto>(await controller.Update(draft.Id,
            SaveDto(customer.Id, ratePercent: 2m)));
        var recorded = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Record(draft.Id));
        AssertOk<AgencyServiceFeeAgreementDto>(await controller.Void(recorded.Id,
            new AgencyServiceFeeAgreementVoidRequest { Reason = "协议终止" }));

        db.ChangeTracker.Clear();
        var reloadedCustomer = db.BaseCustomers.Single(c => c.Id == customer.Id);
        var reloadedOrder = db.SalesOrders.Single(o => o.Id == order.Id);
        var reloadedReceipt = db.FinanceReceipts.Single(r => r.Id == receipt.Id);
        var reloadedExpense = db.FinanceExpenses.Single(x => x.Id == expense.Id);
        var reloadedRefund = db.BaseTaxRefunds.Single(x => x.Id == refund.Id);
        var reloadedInvoice = db.CustomerSalesInvoiceEvidences.Single(x => x.Id == invoice.Id);

        Assert.Equal(snapshot.CustomerStatus, reloadedCustomer.Status);
        Assert.Equal(snapshot.CustomerCreditLimit, reloadedCustomer.CreditLimit);
        Assert.Equal(snapshot.CustomerCreditStatus, reloadedCustomer.CreditStatus);
        Assert.Equal(snapshot.CustomerCommissionRatio, reloadedCustomer.CommissionRatio);
        Assert.Equal(snapshot.CustomerUpdatedAt, reloadedCustomer.UpdatedAt);
        Assert.Equal(snapshot.OrderStatus, reloadedOrder.Status);
        Assert.Equal(snapshot.OrderTotal, reloadedOrder.TotalAmount);
        Assert.Equal(snapshot.OrderCommissionRatio, reloadedOrder.CommissionRatio);
        Assert.Equal(snapshot.OrderUpdatedAt, reloadedOrder.UpdatedAt);
        Assert.Equal(snapshot.ReceiptStatus, reloadedReceipt.Status);
        Assert.Equal(snapshot.ReceiptAmount, reloadedReceipt.Amount);
        Assert.Equal(snapshot.ReceiptUpdatedAt, reloadedReceipt.UpdatedAt);
        Assert.Equal(snapshot.ExpenseAmount, reloadedExpense.Amount);
        Assert.Equal(snapshot.ExpenseUpdatedAt, reloadedExpense.UpdatedAt);
        Assert.Equal(snapshot.RefundPeriod, reloadedRefund.RefundPeriod);
        Assert.Equal(snapshot.RefundUpdatedAt, reloadedRefund.UpdatedAt);
        Assert.Equal(snapshot.InvoiceStatus, reloadedInvoice.Status);
        Assert.Equal(snapshot.InvoiceGross, reloadedInvoice.GrossAmount);
        Assert.Equal(snapshot.InvoiceUpdatedAt, reloadedInvoice.UpdatedAt);

        // 本模块只写自己的一张表
        Assert.Equal(1, db.AgencyServiceFeeAgreements.Count());
    }

    // ==================== 8. 模型、幂等结构与前端接线契约 ====================

    [Fact]
    public void 模型结构_长度精度_唯一身份_无外键且读取标注不落库()
    {
        using var db = TestDbFactory.Create();
        var entityType = db.Model.FindEntityType(typeof(AgencyServiceFeeAgreement));
        Assert.NotNull(entityType);
        // 模型表名与仓库既有口径一致（EF 按实体类型名给出）；物理表 db_owner.AgencyServiceFeeAgreements
        // 与索引由 SchemaUpgrader 第 42 段幂等建表（见下方 Schema 契约测试），实体 / 索引 / 无外键口径在此断言。
        Assert.Equal(nameof(AgencyServiceFeeAgreement), entityType!.GetTableName());
        Assert.Equal("db_owner", entityType.GetSchema());

        Assert.Equal(50, entityType.FindProperty(nameof(AgencyServiceFeeAgreement.AgreementNo))!.GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(AgencyServiceFeeAgreement.NormalizedAgreementNo))!.GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(AgencyServiceFeeAgreement.CustomerCode))!.GetMaxLength());
        Assert.Equal(200, entityType.FindProperty(nameof(AgencyServiceFeeAgreement.CustomerName))!.GetMaxLength());
        Assert.Equal(20, entityType.FindProperty(nameof(AgencyServiceFeeAgreement.Currency))!.GetMaxLength());
        Assert.Equal(20, entityType.FindProperty(nameof(AgencyServiceFeeAgreement.FeeMethod))!.GetMaxLength());
        Assert.Equal(9, entityType.FindProperty(nameof(AgencyServiceFeeAgreement.RatePercent))!.GetPrecision());
        Assert.Equal(4, entityType.FindProperty(nameof(AgencyServiceFeeAgreement.RatePercent))!.GetScale());
        Assert.Equal(18, entityType.FindProperty(nameof(AgencyServiceFeeAgreement.FixedAmount))!.GetPrecision());
        Assert.Equal(200, entityType.FindProperty(nameof(AgencyServiceFeeAgreement.FeeBasis))!.GetMaxLength());
        Assert.Equal(100, entityType.FindProperty(nameof(AgencyServiceFeeAgreement.RecordedBy))!.GetMaxLength());
        Assert.Equal(500, entityType.FindProperty(nameof(AgencyServiceFeeAgreement.VoidReason))!.GetMaxLength());
        Assert.Equal(500, entityType.FindProperty(nameof(AgencyServiceFeeAgreement.Remark))!.GetMaxLength());

        var unique = entityType.GetIndexes().Single(i =>
            i.GetDatabaseName() == "UX_AgencyServiceFeeAgreements_ActiveIdentity");
        Assert.True(unique.IsUnique);
        Assert.Equal("IsDeleted = 0 AND Status <> 2", unique.GetFilter());
        Assert.Equal(
            new[]
            {
                nameof(AgencyServiceFeeAgreement.CustomerId),
                nameof(AgencyServiceFeeAgreement.NormalizedAgreementNo)
            },
            unique.Properties.Select(p => p.Name).ToArray());

        Assert.Contains(entityType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_AgencyServiceFeeAgreements_CustomerId_Status");
        Assert.Contains(entityType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_AgencyServiceFeeAgreements_Status_EffectiveFrom");
        Assert.Contains(entityType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_AgencyServiceFeeAgreements_NormalizedAgreementNo");

        // 读取侧标注是非持久化列（不落库）
        Assert.DoesNotContain(entityType.GetProperties(),
            p => p.Name is nameof(AgencyServiceFeeAgreement.CustomerAvailable)
                or nameof(AgencyServiceFeeAgreement.CustomerAvailabilityText)
                or nameof(AgencyServiceFeeAgreement.FeeTermsText)
                or nameof(AgencyServiceFeeAgreement.EffectiveRangeText)
                or nameof(AgencyServiceFeeAgreement.BoundaryText)
                or nameof(AgencyServiceFeeAgreement.CommissionSeparationText));

        // 刻意不建到客户的外键、也没有导航属性
        Assert.Empty(entityType.GetForeignKeys());
        Assert.Empty(entityType.GetNavigations());

        var customerType = db.Model.FindEntityType(typeof(BaseCustomer));
        Assert.DoesNotContain(customerType!.GetNavigations(),
            n => n.ClrType == typeof(AgencyServiceFeeAgreement));
    }

    [Fact]
    public void Schema_幂等建表建索引且不含任何回填或账务语句()
    {
        var script = File.ReadAllText(
            RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.AgencyServiceFeeAgreements') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.AgencyServiceFeeAgreements", script);
        Assert.Contains("RatePercent DECIMAL(9,4) NOT NULL DEFAULT 0", script);
        Assert.Contains("FixedAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_AgencyServiceFeeAgreements_ActiveIdentity", script);
        Assert.Contains("ON db_owner.AgencyServiceFeeAgreements(CustomerId, NormalizedAgreementNo)", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status <> 2;", script);

        var start = script.IndexOf("// 42. 代理服务费协议证据登记", StringComparison.Ordinal);
        Assert.True(start > 0);
        var segment = script[start..];
        // 本模块段落只建表 + 过滤索引：不改写任何既有表结构，也不做任何回填 / 发票 / 记账 / 付款语句
        Assert.DoesNotContain("ALTER TABLE", segment);
        Assert.DoesNotContain("UPDATE db_owner", segment);
        Assert.DoesNotContain("INSERT INTO db_owner", segment);
        Assert.DoesNotContain("DELETE FROM db_owner", segment);
        Assert.DoesNotContain("EXEC ", segment);
        Assert.DoesNotContain("FK_AgencyServiceFee", segment);
    }

    [Fact]
    public void 模型与请求契约不提供任何账外或不披露佣金字段()
    {
        var forbidden = new[]
        {
            "OffBook", "Concealed", "Hidden", "Undisclosed", "Secret",
            "不披露", "账外", "隐匿"
        };

        foreach (var type in new[]
                 {
                     typeof(AgencyServiceFeeAgreement),
                     typeof(AgencyServiceFeeAgreementSaveDto)
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();

            foreach (var bad in forbidden)
                Assert.DoesNotContain(names, n => n.Contains(bad, StringComparison.OrdinalIgnoreCase));

            // 不建模任何佣金取值字段：费率 / 金额只能是用户显式提交的协议条款
            Assert.DoesNotContain(names, n => n.Contains("CommissionRate", StringComparison.Ordinal)
                || n.Contains("CommissionAmount", StringComparison.Ordinal)
                || n.Contains("CommissionRatio", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void 前端与路由接线契约()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/agency-service-fee-agreements.js", index);

        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules.js"));
        // 工具栏入口必须带括号（extraActions 直接注入 onclick 属性），行操作只写函数名（渲染时注入行 Id）
        Assert.Contains("onclick: 'openAgencyServiceFeeAgreementRegister()'", modules);
        Assert.Contains("onclick: 'openAgencyServiceFeeAgreementRegister'", modules);

        var js = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "agency-service-fee-agreements.js"));
        Assert.Contains("async function openAgencyServiceFeeAgreementRegister", js);
        Assert.Contains("'/api/agency-service-fee-agreements'", js);
        Assert.Contains("'/api/agency-service-fee-agreements?'", js);
        Assert.Contains("/record", js);
        Assert.Contains("/void", js);
        Assert.Contains("asfaSaveForm", js);
        Assert.Contains("asfaConfirmVoid", js);
        Assert.Contains("不是税务发票", js);
        Assert.Contains("业务员提成", js);
        Assert.Contains("无固定结束日", js);

        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "AgencyServiceFeeAgreementController.cs"));
        Assert.Contains("[Route(\"api/agency-service-fee-agreements\")]", controller);
        Assert.Contains("{id:long}/record", controller);
        Assert.Contains("{id:long}/void", controller);
    }
}
