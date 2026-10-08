using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 供应商比价价格审批与供应商选择历史（ERP-095）单元测试：
/// 批准 / 拒绝决定、append-only（重复 / 陈旧 / 跨批次拒绝）、批次审批状态（pending / approved / rejected）、
/// 订单转换只接受「已批准」比价行并保留审批参考号、接口路由契约。
/// 说明：全部使用内存数据库，不连接 SQL Server、不启动 API（browser_deferred）。
/// </summary>
public class PurchaseQuoteApprovalTests
{
    [Fact]
    public async Task 批准比价行_记录选中供应商_决定人_时间_理由与审批参考()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-APV-1");

        var decision = await DecideAsync(db, new PurchaseQuoteDecisionRequest
        {
            QuoteId = quote.Id,
            QuoteNo = quote.QuoteNo,
            Decision = PurchaseQuoteApproval.Approved,
            DecisionBasis = "单价最低且交期可控",
            DecidedBy = 7L,
            DecidedByName = "采购主管"
        });

        Assert.Equal(quote.Id, decision.QuoteId);
        Assert.Equal("PQ-APV-1", decision.QuoteNo);
        Assert.Equal(PurchaseQuoteApproval.Approved, decision.Decision);
        Assert.Equal(88L, decision.SelectedSupplierId);
        Assert.Equal("供应商88", decision.SelectedSupplierName);
        Assert.Equal("单价最低且交期可控", decision.DecisionBasis);
        Assert.Equal(7L, decision.DecidedBy);
        Assert.Equal("采购主管", decision.DecidedByName);
        Assert.Equal("APV-PQ-APV-1-#" + quote.Id, decision.DecisionRef);
        Assert.NotEqual(default, decision.DecidedAt);
        Assert.Single(db.PurchaseQuoteDecisions);
    }

    [Fact]
    public async Task 拒绝比价行_记录拒绝且不选中供应商()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-REJ-1");

        var decision = await DecideAsync(db, new PurchaseQuoteDecisionRequest
        {
            QuoteId = quote.Id,
            Decision = PurchaseQuoteApproval.Rejected,
            DecisionBasis = "交期过长",
            DecidedByName = "采购员"
        });

        Assert.Equal(PurchaseQuoteApproval.Rejected, decision.Decision);
        Assert.Null(decision.SelectedSupplierId);
        Assert.Equal(string.Empty, decision.SelectedSupplierName);
        Assert.Equal("交期过长", decision.DecisionBasis);
    }

    [Fact]
    public async Task 重复决定_抛RuleConflict_且不追加记录()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-DUP-D");

        await DecideAsync(db, new PurchaseQuoteDecisionRequest
        {
            QuoteId = quote.Id,
            Decision = PurchaseQuoteApproval.Approved,
            DecisionBasis = "第一次"
        });

        var ex = await Assert.ThrowsAsync<BusinessException>(() => DecideAsync(db,
            new PurchaseQuoteDecisionRequest { QuoteId = quote.Id, Decision = PurchaseQuoteApproval.Rejected }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不能重复决定", ex.Message);
        Assert.Single(db.PurchaseQuoteDecisions);
    }

    [Fact]
    public async Task 陈旧决定_已转采购订单_抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-STALE-C", status: PurchaseQuoteConversion.ConvertedStatus);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => DecideAsync(db,
            new PurchaseQuoteDecisionRequest { QuoteId = quote.Id, Decision = PurchaseQuoteApproval.Approved }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已转采购订单", ex.Message);
        Assert.Empty(db.PurchaseQuoteDecisions);
    }

    [Fact]
    public async Task 陈旧决定_已放弃_抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-STALE-D", status: PurchaseQuoteConversion.DiscardedStatus);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => DecideAsync(db,
            new PurchaseQuoteDecisionRequest { QuoteId = quote.Id, Decision = PurchaseQuoteApproval.Approved }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已放弃", ex.Message);
        Assert.Empty(db.PurchaseQuoteDecisions);
    }

    [Fact]
    public async Task 跨批次决定_抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-A");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => DecideAsync(db,
            new PurchaseQuoteDecisionRequest
            {
                QuoteId = quote.Id,
                QuoteNo = "PQ-B",
                Decision = PurchaseQuoteApproval.Approved
            }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不能在批次 PQ-B 下审批", ex.Message);
        Assert.Empty(db.PurchaseQuoteDecisions);
    }

    [Fact]
    public async Task 无效决定类型_抛InvalidParameter()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-BADTYPE");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => DecideAsync(db,
            new PurchaseQuoteDecisionRequest { QuoteId = quote.Id, Decision = "Maybe" }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 批次审批状态_暴露待审批_已批准_已拒绝与历史()
    {
        using var db = TestDbFactory.Create();
        var pending = SeedQuote(db, "PQ-BATCH-A");
        var approved = SeedQuote(db, "PQ-BATCH-A", supplierId: 88L);
        var rejected = SeedQuote(db, "PQ-BATCH-A", supplierId: 99L);

        await DecideAsync(db, new PurchaseQuoteDecisionRequest
        {
            QuoteId = approved.Id,
            Decision = PurchaseQuoteApproval.Approved,
            DecisionBasis = "最优",
            DecidedByName = "张三"
        });
        await DecideAsync(db, new PurchaseQuoteDecisionRequest
        {
            QuoteId = rejected.Id,
            Decision = PurchaseQuoteApproval.Rejected,
            DecisionBasis = "价高",
            DecidedByName = "李四"
        });

        var batch = await BatchStatusAsync(db, "PQ-BATCH-A");

        Assert.Equal(3, batch.LineCount);
        Assert.Equal(1, batch.PendingCount);
        Assert.Equal(1, batch.ApprovedCount);
        Assert.Equal(1, batch.RejectedCount);
        Assert.Equal(2, batch.History.Count);

        Assert.Equal(PurchaseQuoteApproval.Pending, batch.Lines.Single(l => l.QuoteId == pending.Id).State);
        Assert.Null(batch.Lines.Single(l => l.QuoteId == pending.Id).Decision);

        var approvedLine = batch.Lines.Single(l => l.QuoteId == approved.Id);
        Assert.Equal(PurchaseQuoteApproval.Approved, approvedLine.State);
        Assert.Equal("最优", approvedLine.Decision!.DecisionBasis);
        Assert.Equal("张三", approvedLine.Decision.DecidedByName);

        var rejectedLine = batch.Lines.Single(l => l.QuoteId == rejected.Id);
        Assert.Equal(PurchaseQuoteApproval.Rejected, rejectedLine.State);
        Assert.Equal("价高", rejectedLine.Decision!.DecisionBasis);
    }

    [Fact]
    public async Task 转换需批准_未审批行抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-NOAPV");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewQuoteController(db).ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("尚未审批通过", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task 转换需批准_已拒绝行抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-REJCONV");
        await DecideAsync(db, new PurchaseQuoteDecisionRequest
        {
            QuoteId = quote.Id,
            Decision = PurchaseQuoteApproval.Rejected,
            DecisionBasis = "交期太长"
        });

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewQuoteController(db).ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("拒绝", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task 转换保留审批参考_订单备注含审批参考号()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-REF");
        var decision = await DecideAsync(db, new PurchaseQuoteDecisionRequest
        {
            QuoteId = quote.Id,
            Decision = PurchaseQuoteApproval.Approved,
            DecisionBasis = "综合最优"
        });

        var ok = Assert.IsType<OkObjectResult>(await NewQuoteController(db).ToPurchaseOrder(quote.Id));
        Assert.Equal(0, Assert.IsType<ApiResponse<PurchaseOrderConversionResult>>(ok.Value).Code);

        var order = db.PurchaseOrders.Single();
        Assert.Contains($"审批参考 {decision.DecisionRef}", order.Remark);
        Assert.Contains("APPROVAL_TEST", order.Remark);
    }

    [Fact]
    public void 审批接口路由_与前端调用路径一致()
    {
        AssertRoute(nameof(PurchaseQuoteDecisionController.BatchStatus), "batch");
        AssertRoute(nameof(PurchaseQuoteDecisionController.Decide), "decide");

        var route = typeof(PurchaseQuoteDecisionController)
            .GetCustomAttribute<RouteAttribute>(true)!.Template;
        Assert.Equal("api/purchase/quote-decisions", route);
    }

    // ==================== ERP-417：原子决定 + 实时可信决定人 + 批准供应商一致性 ====================

    [Fact]
    public async Task 审批决定_实时认证请求_伪造决定人被忽略_取自登录账号()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-APV-ACTOR");
        var userId = SeedPrivilegedUser(db, "ERP417 审批人");

        var decision = await DecideLiveAsync(db, userId, new PurchaseQuoteDecisionRequest
        {
            QuoteId = quote.Id,
            QuoteNo = quote.QuoteNo,
            Decision = PurchaseQuoteApproval.Approved,
            DecidedBy = 9_999_999L,
            DecidedByName = "伪造决定人"
        });

        Assert.Equal(userId, decision.DecidedBy);
        Assert.Equal("ERP417 审批人", decision.DecidedByName);
        Assert.Equal(userId, decision.CreatedBy); // 归属操作人：实时生命周期追加的决定冻结软删除
        Assert.Single(db.PurchaseQuoteDecisions);
    }

    [Fact]
    public async Task 已批准决定后_供应商被改写_转采购订单_拒绝()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedQuote(db, "PQ-APV-MISMATCH");
        await DecideAsync(db, new PurchaseQuoteDecisionRequest
        {
            QuoteId = quote.Id, Decision = PurchaseQuoteApproval.Approved
        });

        // 模拟绕过生命周期的直接改写（历史数据 / 直连 SQL）：批准 88，却被改成 99。
        var stored = db.PurchaseQuotes.Single(q => q.Id == quote.Id);
        stored.SupplierId = 99L;
        stored.SupplierName = "别家供应商";
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewQuoteController(db).ToPurchaseOrder(quote.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("与已批准决定的选中供应商", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    // ==================== 工厂与种子数据 ====================

    private static PurchaseQuoteDecisionController NewLiveController(ErpDbContext db, long userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/purchase/quote-decisions";
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));
        return new PurchaseQuoteDecisionController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static async Task<PurchaseQuoteDecision> DecideLiveAsync(ErpDbContext db, long userId,
        PurchaseQuoteDecisionRequest request)
    {
        var ok = Assert.IsType<OkObjectResult>(await NewLiveController(db, userId).Decide(request));
        var response = Assert.IsType<ApiResponse<PurchaseQuoteDecision>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        return response.Data!;
    }

    private static long SeedPrivilegedUser(ErpDbContext db, string displayName)
    {
        var code = $"erp417-a-{Guid.NewGuid():N}";
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = displayName, Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static PurchaseQuoteDecisionController NewController(ErpDbContext db) => new(db);

    private static PurchaseQuoteController NewQuoteController(ErpDbContext db)
        => new(new GenericService<PurchaseQuote>(db), db, new DocumentNumberService(db));

    private static async Task<PurchaseQuoteDecision> DecideAsync(ErpDbContext db,
        PurchaseQuoteDecisionRequest request)
    {
        var ok = Assert.IsType<OkObjectResult>(await NewController(db).Decide(request));
        var response = Assert.IsType<ApiResponse<PurchaseQuoteDecision>>(ok.Value);
        Assert.Equal(0, response.Code);
        return response.Data!;
    }

    private static async Task<PurchaseQuoteDecisionBatch> BatchStatusAsync(ErpDbContext db, string quoteNo)
    {
        var ok = Assert.IsType<OkObjectResult>(await NewController(db).BatchStatus(quoteNo));
        var response = Assert.IsType<ApiResponse<PurchaseQuoteDecisionBatch>>(ok.Value);
        Assert.Equal(0, response.Code);
        return response.Data!;
    }

    private static void AssertRoute(string methodName, string template)
    {
        var method = typeof(PurchaseQuoteDecisionController).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        var templates = method!.GetCustomAttributes<HttpMethodAttribute>(true)
            .Select(a => a.Template ?? string.Empty)
            .ToList();
        Assert.Contains(template, templates);
    }

    private static PurchaseQuote SeedQuote(ErpDbContext db, string quoteNo, long supplierId = 88L,
        string status = PurchaseQuoteConversion.SelectedStatus, bool selected = true)
    {
        var quote = new PurchaseQuote
        {
            QuoteNo = quoteNo,
            QuoteDate = DateTime.Today,
            ProductId = 310L,
            ProductName = "审批商品",
            Spec = "大号",
            Unit = "PCS",
            Quantity = 100m,
            SupplierId = supplierId,
            SupplierName = $"供应商{supplierId}",
            SupplierType = "档口",
            QuotePrice = 2m,
            TotalAmount = 200m,
            Currency = "CNY",
            TaxIncluded = false,
            DeliveryDays = 10,
            MinOrderQty = 1,
            PaymentTerms = "现结",
            IsSelected = selected,
            Status = status,
            Remark = "APPROVAL_TEST"
        };
        db.PurchaseQuotes.Add(quote);
        db.SaveChanges();
        return quote;
    }
}
