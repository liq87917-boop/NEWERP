using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-515 散货结算单「标量金额存储边界」单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item>纯规则：<see cref="FinanceBulkSettlementLifecycleRules.NormalizeTotalAmount"/> /
/// <see cref="FinanceBulkSettlementLifecycleRules.NormalizeCost"/> 在**既有两位小数取整之后**复用既有
/// <see cref="FinancePersistedAmountRules"/> 判定可在实际列精度 <c>DECIMAL(18,2)</c> 内原样存储；恰好等于上限
/// （含半进位进位到上限）照常放行，取整进位越过上限 / <c>1e16</c> / 极大十进制受控拒绝，零海运费照常放行，
/// 且绝不发明 <c>total = freight</c> 公式、绝不把单列边界施加到聚合求和；</item>
/// <item>实际 EF 列精度：以 SQL Server 提供程序**仅构建模型**（不打开连接）断言两个金额列未显式 <c>HasPrecision</c>
/// 且列类型为 <c>decimal(18,2)</c>，与规则常量逐字同源；</item>
/// <item>创建（手工 / 未关联客户）：受权账号提交过精度 / 溢出金额在**单号预约与任何写入之前**受控拒绝
/// （零表头写入、零单号消耗），恰好等于上限照常落库；散货结算单没有来源单据，绝不按文本猜测来源；</item>
/// <item>修改：被拒请求既不写金额也不写备注（含 <c>UpdatedAt</c> 审计时间戳）；</item>
/// <item>提交 / 审核：在既有行锁内只读复核**已持久化**金额（
/// <see cref="FinanceBulkSettlementLifecycleRules.EnsurePersistedAmountsConsistent"/>），越界 / 精度不一致一律拒绝且
/// 状态 / 字段 / 审计不变；取消 / 删除等撤销路径**不新增**草稿有效性闸门。</item>
/// </list>
/// 真实 SQL 列精度 / 越界写入拒绝 / 号码序列见
/// <c>ERP.IntegrationTests/BulkSettlementPersistedAmountSqlServerTests.cs</c>。
/// </summary>
public class BulkSettlementPersistedAmountTests
{
    private const decimal MaxAmount = FinancePersistedAmountRules.MaxDecimal18Scale2;
    private const decimal HalfUpToMax = 9_999_999_999_999_999.985m;      // 半进位后恰好等于上限
    private const decimal JustOver = 9_999_999_999_999_999.999m;         // 取整进位后越过上限
    private const decimal RoundUpCrossing = 9_999_999_999_999_999.995m;  // 取整进位越过上限
    private const decimal OneE16 = 10_000_000_000_000_000m;               // 1e16（超出上限）

    // ==================== 0. 测试脚手架 ====================

    private sealed class CountingNumberService : IDocumentNumberService
    {
        /// <summary>已被请求生成单号的次数（证明被拒请求绝不消耗结算单号）。</summary>
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
        {
            Calls++;
            return Task.FromResult($"SJ{Guid.NewGuid():N}"[..14]);
        }
    }

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, long? empId = null, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static FinanceBulkSettlement SeedSettlement(
        ErpDbContext db, string settlementNo, long customerId,
        decimal totalAmount = 1000m, decimal freightCost = 0m,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var settlement = new FinanceBulkSettlement
        {
            SettlementNo = settlementNo,
            SettlementDate = new DateTime(2026, 10, 1),
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            Remark = "原始备注",
            Status = status,
            UpdatedAt = new DateTime(2026, 10, 2, 9, 0, 0),
            IsDeleted = deleted
        };
        db.FinanceBulkSettlements.Add(settlement);
        db.SaveChanges();
        return settlement;
    }

    /// <summary>播种一个具备既有「散货结算单」菜单授权的启用账号（系统内置角色 → 不受客户数据范围限制）。</summary>
    private static long SeedOperator(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = "散货结算金额边界角色",
            RoleCode = $"BulkSettlementAmount-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"bulk-settlement-amount-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "散货结算金额边界操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = FinanceBulkSettlementLifecycleRules.RequiredMenuCode,
            MenuName = FinanceBulkSettlementLifecycleRules.RequiredMenuText,
            Path = "/finance/bulk-settlement",
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static FinanceBulkSettlementController BuildController(ErpDbContext db, IDocumentNumberService no)
        => new(db, no);

    private static FinanceBulkSettlement NewRequest(
        long customerId, decimal totalAmount = 1000m, decimal freightCost = 0m)
        => new()
        {
            SettlementDate = new DateTime(2026, 10, 3),
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            Remark = "请求备注"
        };

    private static async Task<BusinessException> AssertBusinessCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static FinanceBulkSettlement StoredSettlement(ErpDbContext db, long settlementId)
        => db.FinanceBulkSettlements.AsNoTracking().Single(s => s.Id == settlementId);

    private static void AssertSettlementUnchanged(ErpDbContext db, FinanceBulkSettlement expected)
    {
        var stored = StoredSettlement(db, expected.Id);
        Assert.Equal(expected.Status, stored.Status);
        Assert.Equal(expected.CustomerId, stored.CustomerId);
        Assert.Equal(expected.TotalAmount, stored.TotalAmount);
        Assert.Equal(expected.FreightCost, stored.FreightCost);
        Assert.Equal(expected.SettlementNo, stored.SettlementNo);
        Assert.Equal(expected.Remark, stored.Remark);
        Assert.Equal(expected.IsDeleted, stored.IsDeleted);
        Assert.Equal(expected.UpdatedAt, stored.UpdatedAt);
    }

    private static decimal OverLimit(string scenario) => scenario switch
    {
        "just-over" => JustOver,
        "round-up-cross" => RoundUpCrossing,
        "1e16" => OneE16,
        _ => decimal.MaxValue
    };

    // ==================== 1. 纯规则：既有取整 + 标量存储边界 ====================

    [Fact]
    public void 标量边界常量与标签同源()
    {
        Assert.Equal(18, FinancePersistedAmountRules.DecimalPrecision);
        Assert.Equal(2, FinancePersistedAmountRules.DecimalScale);
        Assert.Equal(2, FinanceBulkSettlementLifecycleRules.AmountDecimals);
        Assert.Equal(9_999_999_999_999_999.99m, MaxAmount);

        Assert.Equal("结算总金额", FinanceBulkSettlementLifecycleRules.TotalAmountLabel);
        Assert.Equal("海运费", FinanceBulkSettlementLifecycleRules.FreightCostLabel);

        Assert.Contains("DECIMAL(18,2)", FinanceBulkSettlementLifecycleRules.PersistedAmountStorageText);
        Assert.Contains("绝不静默截断", FinanceBulkSettlementLifecycleRules.PersistedAmountStorageText);

        // 规则 / 边界文案同步既有权威口径（接口 / 文档同源）。
        Assert.Contains("DECIMAL(18,2)", FinanceBulkSettlementLifecycleRules.RuleText);
        Assert.Contains("撤销", FinanceBulkSettlementLifecycleRules.RuleText);
        Assert.Contains("DECIMAL(18,2)", FinanceBulkSettlementLifecycleRules.BoundaryText);
    }

    [Fact]
    public void 总金额与海运费_精确上限与半进位进位_放行()
    {
        Assert.Equal(MaxAmount, FinanceBulkSettlementLifecycleRules.NormalizeTotalAmount(MaxAmount));
        Assert.Equal(MaxAmount, FinanceBulkSettlementLifecycleRules.NormalizeTotalAmount(HalfUpToMax));
        Assert.Equal(MaxAmount, FinanceBulkSettlementLifecycleRules.NormalizeCost(
            MaxAmount, FinanceBulkSettlementLifecycleRules.FreightCostLabel));
        Assert.Equal(MaxAmount, FinanceBulkSettlementLifecycleRules.NormalizeCost(HalfUpToMax, "海运费"));

        // 既有两位小数取整（0.5 进位）原样保留。
        Assert.Equal(100.46m, FinanceBulkSettlementLifecycleRules.NormalizeTotalAmount(100.456m));
        Assert.Equal(10.56m, FinanceBulkSettlementLifecycleRules.NormalizeCost(10.555m, "海运费"));
        Assert.Equal(0m, FinanceBulkSettlementLifecycleRules.NormalizeCost(-0.001m, "海运费"));
    }

    [Theory]
    [InlineData("just-over")]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public void 总金额_超出单列上限_受控拒绝(string scenario)
    {
        var ex = Assert.Throws<BusinessException>(
            () => FinanceBulkSettlementLifecycleRules.NormalizeTotalAmount(OverLimit(scenario)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
    }

    [Theory]
    [InlineData("just-over")]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public void 海运费_超出单列上限_受控拒绝(string scenario)
    {
        var freight = Assert.Throws<BusinessException>(
            () => FinanceBulkSettlementLifecycleRules.NormalizeCost(OverLimit(scenario), "海运费"));
        Assert.Equal(ErrorCodes.InvalidParameter, freight.Code);
        Assert.Contains("DECIMAL(18,2)", freight.Message);

        var aggregate = Assert.Throws<BusinessException>(
            () => FinanceBulkSettlementLifecycleRules.NormalizeAmounts(100m, OverLimit(scenario)));
        Assert.Equal(ErrorCodes.InvalidParameter, aggregate.Code);
        Assert.Contains("DECIMAL(18,2)", aggregate.Message);
    }

    [Fact]
    public void 金额_不发明合计公式_零费用与聚合求和不受单列边界约束()
    {
        // 明确不校验 total == freight：零海运费与远大于总金额的合法海运费照常放行。
        var (total, freight) = FinanceBulkSettlementLifecycleRules.NormalizeAmounts(100m, MaxAmount);
        Assert.Equal(100m, total);
        Assert.Equal(MaxAmount, freight);

        var (zeroCostTotal, zeroFreight) = FinanceBulkSettlementLifecycleRules.NormalizeAmounts(100m, 0m);
        Assert.Equal(100m, zeroCostTotal);
        Assert.Equal(0m, zeroFreight);

        // 聚合 / 报告合计口径合法地超过单个存储列：单列边界绝不施加到求和。
        Assert.Equal(19_999_999_999_999_999.98m, MaxAmount + MaxAmount);
    }

    [Fact]
    public void 已持久化金额一致性_合法边界放行_过精度或越界拒绝()
    {
        // 合法（含零海运费与恰好上限）：只读复核不取整、不改写。
        FinanceBulkSettlementLifecycleRules.EnsurePersistedAmountsConsistent(MaxAmount, 0m);
        FinanceBulkSettlementLifecycleRules.EnsurePersistedAmountsConsistent(1000m, 100.55m);

        // 精度不一致（3 位小数）：拒绝而不是静默取整。
        var precision = Assert.Throws<BusinessException>(
            () => FinanceBulkSettlementLifecycleRules.EnsurePersistedAmountsConsistent(12.345m, 0m));
        Assert.Equal(ErrorCodes.InvalidParameter, precision.Code);
        Assert.Contains("DECIMAL(18,2)", precision.Message);

        // 越界（1e16 / 极大十进制）：拒绝。
        Assert.Throws<BusinessException>(
            () => FinanceBulkSettlementLifecycleRules.EnsurePersistedAmountsConsistent(OneE16, 0m));
        Assert.Throws<BusinessException>(
            () => FinanceBulkSettlementLifecycleRules.EnsurePersistedAmountsConsistent(decimal.MaxValue, 0m));

        // 既有正负口径原样保留：总金额 > 0、海运费 >= 0（零海运费放行）。
        Assert.Throws<BusinessException>(
            () => FinanceBulkSettlementLifecycleRules.EnsurePersistedAmountsConsistent(0m, 0m));
        Assert.Throws<BusinessException>(
            () => FinanceBulkSettlementLifecycleRules.EnsurePersistedAmountsConsistent(-1m, 0m));
        Assert.Throws<BusinessException>(
            () => FinanceBulkSettlementLifecycleRules.EnsurePersistedAmountsConsistent(100m, -0.01m));

        // 精度不一致的海运费同样拒绝（不是只有总金额受边界保护）。
        Assert.Throws<BusinessException>(
            () => FinanceBulkSettlementLifecycleRules.EnsurePersistedAmountsConsistent(100m, 0.123m));
    }

    // ==================== 2. 实际 EF 列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void 实际EF列精度与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_ERP515_MODEL_ONLY;"
                + "Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        foreach (var propertyName in new[]
                 {
                     nameof(FinanceBulkSettlement.TotalAmount),
                     nameof(FinanceBulkSettlement.FreightCost)
                 })
        {
            var property = db.Model.FindEntityType(typeof(FinanceBulkSettlement))!
                .FindProperty(propertyName)!;
            // 金额列**未**显式 HasPrecision：EF SQL Server 提供程序按默认精度给出 decimal(18,2)；
            // 规则常量必须与该实际列类型逐字同源（若将来显式声明精度，本断言会立即失败并要求同步规则常量）。
            Assert.Null(property.GetPrecision());
            Assert.Null(property.GetScale());
            Assert.Equal(
                $"decimal({FinancePersistedAmountRules.DecimalPrecision},{FinancePersistedAmountRules.DecimalScale})",
                property.GetColumnType());
            Assert.Equal("decimal(18,2)", property.GetColumnType());
        }
    }


    // ==================== 3. 创建：越界拒绝（零写入 / 零占号）与精确上限放行 ====================

    [Theory]
    [InlineData("just-over")]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task 创建_金额超出存储精度_拒绝且零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-515-A", "客户515A");
        var no = new CountingNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        var ex = await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewRequest(customer.Id, totalAmount: OverLimit(scenario))));
        Assert.Contains("DECIMAL(18,2)", ex.Message);
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceBulkSettlements.AsNoTracking().ToList());

        // 海运费越界同样在单号预约与任何写入之前拒绝（散货结算单没有其他费用字段）。
        var freightEx = await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewRequest(customer.Id, totalAmount: 100m,
                freightCost: OverLimit(scenario))));
        Assert.Contains("DECIMAL(18,2)", freightEx.Message);
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceBulkSettlements.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 创建_恰好等于存储上限_照常落库()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-515-MAX", "客户515MAX");
        var no = new CountingNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Create(
            NewRequest(customer.Id, totalAmount: MaxAmount, freightCost: MaxAmount)));

        var stored = Assert.Single(db.FinanceBulkSettlements.AsNoTracking().ToList());
        Assert.Equal(MaxAmount, stored.TotalAmount);
        Assert.Equal(MaxAmount, stored.FreightCost);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(string.IsNullOrWhiteSpace(stored.SettlementNo));
        Assert.Equal(1, no.Calls);
    }

    [Fact]
    public async Task 创建_手工未关联客户_仍走同一既有边界且不推断来源()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-515-MAN", "客户515MAN");
        var no = new CountingNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        // 散货结算单只有手工口径（没有来源单据字段）：金额仍走同一既有边界，越界零写入零占号。
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewRequest(customer.Id, totalAmount: OneE16)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewRequest(customer.Id, totalAmount: 100m,
                freightCost: RoundUpCrossing)));
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceBulkSettlements.AsNoTracking().ToList());

        // 合法零海运费照常落库（不发明 total == freight 等式）。
        Assert.IsType<OkObjectResult>(await controller.Create(
            NewRequest(customer.Id, totalAmount: 123.456m, freightCost: 0m)));
        var stored = Assert.Single(db.FinanceBulkSettlements.AsNoTracking().ToList());
        Assert.Equal(123.46m, stored.TotalAmount);
        Assert.Equal(0m, stored.FreightCost);
        Assert.Equal(1, no.Calls);
    }

    [Fact]
    public async Task 未认证_越界金额_仍按未认证拒绝且零写入零占号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-515-ANON", "客户515ANON");
        var no = new CountingNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, null);

        // 实时授权先于任何金额判定：越界金额也按未认证拒绝，绝不先泄露金额口径。
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized,
            () => controller.Create(NewRequest(customer.Id, totalAmount: OneE16)));
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceBulkSettlements.AsNoTracking().ToList());
    }


    // ==================== 4. 修改：被拒请求不改写金额与备注（含审计时间戳） ====================

    [Fact]
    public async Task 修改_金额超出存储精度_拒绝且原始字段审计不变()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-515-UPD", "客户515UPD");
        var settlement = SeedSettlement(db, "SJ-515-UPD", customer.Id, 1000m, 10m);
        var before = StoredSettlement(db, settlement.Id);
        var no = new CountingNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        var request = NewRequest(customer.Id, totalAmount: OneE16);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Update(settlement.Id, request));
        Assert.Contains("DECIMAL(18,2)", ex.Message);
        AssertSettlementUnchanged(db, before);
        Assert.Equal("原始备注", StoredSettlement(db, settlement.Id).Remark);
        Assert.Equal(0, no.Calls);

        // 海运费越界同样拒绝，仍零改写。
        var costRequest = NewRequest(customer.Id, totalAmount: 1000m, freightCost: RoundUpCrossing);
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Update(settlement.Id, costRequest));
        AssertSettlementUnchanged(db, before);
    }

    [Fact]
    public async Task 修改_恰好等于存储上限_允许()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-515-UPD-MAX", "客户515UPDMAX");
        var settlement = SeedSettlement(db, "SJ-515-UPD-MAX", customer.Id, 1000m);
        var controller = BuildController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Update(settlement.Id,
            NewRequest(customer.Id, totalAmount: MaxAmount, freightCost: MaxAmount)));
        var stored = StoredSettlement(db, settlement.Id);
        Assert.Equal(MaxAmount, stored.TotalAmount);
        Assert.Equal(MaxAmount, stored.FreightCost);
    }


    // ==================== 5. 提交 / 审核：既有行锁内只读复核已持久化金额 ====================

    [Fact]
    public async Task 提交审核_已持久化金额越界或精度不一致_拒绝且状态审计不变()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-515-SUB", "客户515SUB");
        var legacyOverflow = SeedSettlement(db, "SJ-515-FLOW-1", customer.Id, OneE16);
        var legacyPrecision = SeedSettlement(db, "SJ-515-FLOW-2", customer.Id, 12.345m,
            status: DocumentStatus.Submitted);
        var legacyNegativeCost = SeedSettlement(db, "SJ-515-FLOW-3", customer.Id, 100m, -0.01m);
        var overflowBefore = StoredSettlement(db, legacyOverflow.Id);
        var precisionBefore = StoredSettlement(db, legacyPrecision.Id);
        var costBefore = StoredSettlement(db, legacyNegativeCost.Id);
        var controller = BuildController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        var submitEx = await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Submit(legacyOverflow.Id));
        Assert.Contains("DECIMAL(18,2)", submitEx.Message);
        AssertSettlementUnchanged(db, overflowBefore);
        Assert.Equal(DocumentStatus.Pending, StoredSettlement(db, legacyOverflow.Id).Status);

        var approveEx = await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Approve(legacyPrecision.Id));
        Assert.Contains("DECIMAL(18,2)", approveEx.Message);
        AssertSettlementUnchanged(db, precisionBefore);
        Assert.Equal(DocumentStatus.Submitted, StoredSettlement(db, legacyPrecision.Id).Status);

        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Submit(legacyNegativeCost.Id));
        AssertSettlementUnchanged(db, costBefore);
    }

    [Fact]
    public async Task 提交审核_恰好等于存储上限_放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-515-FLOW", "客户515FLOW");
        var settlement = SeedSettlement(db, "SJ-515-FLOW-MAX", customer.Id, MaxAmount, MaxAmount);
        var controller = BuildController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Submit(settlement.Id));
        Assert.IsType<OkObjectResult>(await controller.Approve(settlement.Id));
        var stored = StoredSettlement(db, settlement.Id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(MaxAmount, stored.TotalAmount);
        Assert.Equal(MaxAmount, stored.FreightCost);
    }

    [Fact]
    public async Task 取消与删除_已持久化金额越界_仍按既有撤销语义执行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-515-UNDO", "客户515UNDO");
        var cancelTarget = SeedSettlement(db, "SJ-515-CAN", customer.Id, OneE16);
        var deleteTarget = SeedSettlement(db, "SJ-515-DEL", customer.Id, 12.345m);
        var controller = BuildController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        // 取消 / 删除等撤销路径不新增草稿有效性闸门：历史（或异常）金额不阻止既有撤销语义。
        Assert.IsType<OkObjectResult>(await controller.Cancel(cancelTarget.Id));
        var cancelled = StoredSettlement(db, cancelTarget.Id);
        Assert.Equal(DocumentStatus.Cancelled, cancelled.Status);
        Assert.Equal(OneE16, cancelled.TotalAmount);

        Assert.IsType<OkObjectResult>(await controller.Delete(deleteTarget.Id));
        var deleted = StoredSettlement(db, deleteTarget.Id);
        Assert.True(deleted.IsDeleted);
        Assert.Equal(12.345m, deleted.TotalAmount);
    }


    // ==================== 6. 控制器源码契约：边界先于单号、提交 / 审核锁内只读 ====================

    [Fact]
    public void 控制器源码契约_散货路径先边界后单号且提交审核锁内只读复核()
    {
        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "FinanceSettlementControllers.cs"));

        // 只检查共享控制器的散货结算单区段（装柜结算单路径字面保持原样，其回归由各自套件覆盖）。
        var bulk = controller[controller.IndexOf(
            "public class FinanceBulkSettlementController", StringComparison.Ordinal)..];

        Assert.Contains("NormalizeAmounts", bulk);
        Assert.Contains("EnsurePersistedAmountsConsistent", bulk);
        // 散货结算单没有币种 / 汇率字段与来源单据：绝不引入币种换算或装柜清单行锁。
        Assert.DoesNotContain("Currency", bulk);
        Assert.DoesNotContain("ExchangeRate", bulk);
        Assert.DoesNotContain("LockLoadingListRowsAsync(Db", bulk);

        // 创建 / 修改的归一化（已含实际列精度边界）先于单号预约与任何写入。
        var normalize = bulk.IndexOf("NormalizeAmounts", StringComparison.Ordinal);
        var numbering = bulk.IndexOf("GenerateAsync(DocumentType.BulkSettlement)", StringComparison.Ordinal);
        var add = bulk.IndexOf("Db.FinanceBulkSettlements.Add", StringComparison.Ordinal);
        Assert.True(normalize >= 0 && numbering >= 0 && add >= 0);
        Assert.True(normalize < numbering, "创建必须在单号预约之前完成金额存储边界校验");
        Assert.True(normalize < add, "创建必须在任何写入之前完成金额存储边界校验");

        // 提交 / 审核在同一事务的散货结算单行锁内只读复核已持久化金额。
        var rowLock = bulk.IndexOf("LockSettlementRowAsync", StringComparison.Ordinal);
        var consistent = bulk.IndexOf("EnsurePersistedAmountsConsistent", StringComparison.Ordinal);
        Assert.True(rowLock >= 0 && consistent >= 0);
        Assert.True(rowLock < consistent, "提交 / 审核必须在既有行锁内复核已持久化金额");
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NEWERP.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }
}
