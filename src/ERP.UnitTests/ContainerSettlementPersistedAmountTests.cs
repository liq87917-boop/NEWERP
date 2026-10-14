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
/// ERP-514 装柜结算单「标量金额存储边界」单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item>纯规则：<see cref="FinanceContainerSettlementLifecycleRules.NormalizeTotalAmount"/> /
/// <see cref="FinanceContainerSettlementLifecycleRules.NormalizeCost"/> 在**既有两位小数取整之后**复用既有
/// <see cref="FinancePersistedAmountRules"/> 判定可在实际列精度 <c>DECIMAL(18,2)</c> 内原样存储；恰好等于上限
/// （含半进位进位到上限）照常放行，取整进位越过上限 / <c>1e16</c> / 极大十进制受控拒绝，零费用照常放行，
/// 且绝不发明 <c>total = sum(costs)</c> 公式、绝不把单列边界施加到聚合求和；</item>
/// <item>实际 EF 列精度：以 SQL Server 提供程序**仅构建模型**（不打开连接）断言三个金额列未显式 <c>HasPrecision</c>
/// 且列类型为 <c>decimal(18,2)</c>，与规则常量逐字同源；</item>
/// <item>创建（手工 / 显式链接来源）：受权账号提交过精度 / 溢出金额在**单号预约与任何写入之前**受控拒绝
/// （零表头写入、零单号消耗、来源清单零改写），恰好等于上限照常落库；</item>
/// <item>修改：被拒请求既不写金额也不写备注（含 <c>UpdatedAt</c> 审计时间戳）；</item>
/// <item>提交 / 审核：在既有行锁内只读复核**已持久化**金额（
/// <see cref="FinanceContainerSettlementLifecycleRules.EnsurePersistedAmountsConsistent"/>），越界 / 精度不一致一律拒绝且
/// 状态 / 字段 / 审计不变；取消 / 删除等撤销路径**不新增**草稿有效性闸门。</item>
/// </list>
/// 真实 SQL 列精度 / 越界写入拒绝 / 号码序列见
/// <c>ERP.IntegrationTests/ContainerSettlementPersistedAmountSqlServerTests.cs</c>。
/// </summary>
public class ContainerSettlementPersistedAmountTests
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
            return Task.FromResult($"ZJ{Guid.NewGuid():N}"[..14]);
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

    private static ContainerLoadingList SeedLoadingList(
        ErpDbContext db, string listNo, string containerNo, long customerId,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = listNo,
            LoadingDate = new DateTime(2026, 10, 1),
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = status,
            IsDeleted = deleted
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    private static FinanceContainerSettlement SeedSettlement(
        ErpDbContext db, string settlementNo, long? loadingListId, long customerId,
        decimal totalAmount = 1000m, decimal freightCost = 0m, decimal otherCost = 0m,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var settlement = new FinanceContainerSettlement
        {
            SettlementNo = settlementNo,
            SettlementDate = new DateTime(2026, 10, 1),
            LoadingListId = loadingListId,
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            OtherCost = otherCost,
            Remark = "原始备注",
            Status = status,
            UpdatedAt = new DateTime(2026, 10, 2, 9, 0, 0),
            IsDeleted = deleted
        };
        db.FinanceContainerSettlements.Add(settlement);
        db.SaveChanges();
        return settlement;
    }

    /// <summary>播种一个具备既有「装柜结算单」菜单授权的启用账号（系统内置角色 → 不受客户数据范围限制）。</summary>
    private static long SeedOperator(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = "装柜结算金额边界角色",
            RoleCode = $"SettlementAmount-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"settlement-amount-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "装柜结算金额边界操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = FinanceContainerSettlementLifecycleRules.RequiredMenuCode,
            MenuName = FinanceContainerSettlementLifecycleRules.RequiredMenuText,
            Path = "/finance/container-settlement",
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static FinanceContainerSettlementController BuildController(ErpDbContext db, IDocumentNumberService no)
        => new(db, no);

    private static FinanceContainerSettlement NewSettlementRequest(
        long customerId, long? loadingListId = null, decimal totalAmount = 1000m,
        decimal freightCost = 0m, decimal otherCost = 0m)
        => new()
        {
            SettlementDate = new DateTime(2026, 10, 3),
            LoadingListId = loadingListId,
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            OtherCost = otherCost,
            Remark = "请求备注"
        };

    private static async Task<BusinessException> AssertBusinessCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static FinanceContainerSettlement StoredSettlement(ErpDbContext db, long settlementId)
        => db.FinanceContainerSettlements.AsNoTracking().Single(s => s.Id == settlementId);

    /// <summary>被拒操作必须逐字段（含审计时间戳）保持不变。</summary>
    private static void AssertSettlementUnchanged(ErpDbContext db, FinanceContainerSettlement expected)
    {
        var stored = StoredSettlement(db, expected.Id);
        Assert.Equal(expected.Status, stored.Status);
        Assert.Equal(expected.CustomerId, stored.CustomerId);
        Assert.Equal(expected.LoadingListId, stored.LoadingListId);
        Assert.Equal(expected.TotalAmount, stored.TotalAmount);
        Assert.Equal(expected.FreightCost, stored.FreightCost);
        Assert.Equal(expected.OtherCost, stored.OtherCost);
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
        Assert.Equal(2, FinanceContainerSettlementLifecycleRules.AmountDecimals);
        Assert.Equal(9_999_999_999_999_999.99m, MaxAmount);

        Assert.Equal("结算总金额", FinanceContainerSettlementLifecycleRules.TotalAmountLabel);
        Assert.Equal("海运费", FinanceContainerSettlementLifecycleRules.FreightCostLabel);
        Assert.Equal("其他费用", FinanceContainerSettlementLifecycleRules.OtherCostLabel);

        Assert.Contains("DECIMAL(18,2)", FinanceContainerSettlementLifecycleRules.PersistedAmountStorageText);
        Assert.Contains("绝不静默截断", FinanceContainerSettlementLifecycleRules.PersistedAmountStorageText);
        Assert.Contains("DECIMAL(18,2)", ContainerSettlementLoadingSourceService.SettlementAmountBoundaryText);
        Assert.Contains("既有边界", ContainerSettlementLoadingSourceService.SettlementAmountBoundaryText);

        // 规则 / 边界文案同步既有权威口径（接口 / 文档同源）。
        Assert.Contains("DECIMAL(18,2)", FinanceContainerSettlementLifecycleRules.RuleText);
        Assert.Contains("撤销", FinanceContainerSettlementLifecycleRules.RuleText);
    }

    [Fact]
    public void 总金额与费用_精确上限与半进位进位_放行()
    {
        Assert.Equal(MaxAmount, FinanceContainerSettlementLifecycleRules.NormalizeTotalAmount(MaxAmount));
        Assert.Equal(MaxAmount, FinanceContainerSettlementLifecycleRules.NormalizeTotalAmount(HalfUpToMax));
        Assert.Equal(MaxAmount, FinanceContainerSettlementLifecycleRules.NormalizeCost(MaxAmount, "海运费"));
        Assert.Equal(MaxAmount, FinanceContainerSettlementLifecycleRules.NormalizeCost(HalfUpToMax, "其他费用"));

        // 既有两位小数取整（0.5 进位）原样保留。
        Assert.Equal(100.46m, FinanceContainerSettlementLifecycleRules.NormalizeTotalAmount(100.456m));
        Assert.Equal(10.56m, FinanceContainerSettlementLifecycleRules.NormalizeCost(10.555m, "海运费"));
        Assert.Equal(0m, FinanceContainerSettlementLifecycleRules.NormalizeCost(-0.001m, "其他费用"));
    }

    [Theory]
    [InlineData("just-over")]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public void 总金额_超出单列上限_受控拒绝(string scenario)
    {
        var ex = Assert.Throws<BusinessException>(
            () => FinanceContainerSettlementLifecycleRules.NormalizeTotalAmount(OverLimit(scenario)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
    }

    [Theory]
    [InlineData("just-over")]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public void 海运费与其他费用_超出单列上限_受控拒绝(string scenario)
    {
        var freight = Assert.Throws<BusinessException>(
            () => FinanceContainerSettlementLifecycleRules.NormalizeCost(OverLimit(scenario), "海运费"));
        Assert.Equal(ErrorCodes.InvalidParameter, freight.Code);
        Assert.Contains("DECIMAL(18,2)", freight.Message);

        var other = Assert.Throws<BusinessException>(
            () => FinanceContainerSettlementLifecycleRules.NormalizeAmounts(100m, 0m, OverLimit(scenario)));
        Assert.Equal(ErrorCodes.InvalidParameter, other.Code);
        Assert.Contains("DECIMAL(18,2)", other.Message);
    }

    [Fact]
    public void 金额_不发明合计公式_零费用与聚合求和不受单列边界约束()
    {
        // 明确不校验 total == freight + other：零费用与远大于总金额的合法费用照常放行。
        var (total, freight, other) = FinanceContainerSettlementLifecycleRules.NormalizeAmounts(
            100m, MaxAmount, 0m);
        Assert.Equal(100m, total);
        Assert.Equal(MaxAmount, freight);
        Assert.Equal(0m, other);

        // 聚合 / 报告合计口径合法地超过单个存储列：单列边界绝不施加到求和。
        Assert.Equal(19_999_999_999_999_999.98m, MaxAmount + MaxAmount);
    }


    [Fact]
    public void 已持久化金额一致性_合法边界放行_过精度或越界拒绝()
    {
        // 合法（含零费用与恰好上限）：只读复核不取整、不改写。
        FinanceContainerSettlementLifecycleRules.EnsurePersistedAmountsConsistent(MaxAmount, 0m, 0m);
        FinanceContainerSettlementLifecycleRules.EnsurePersistedAmountsConsistent(1000m, 100.55m, 0m);

        // 精度不一致（3 位小数）：拒绝而不是静默取整。
        var precision = Assert.Throws<BusinessException>(
            () => FinanceContainerSettlementLifecycleRules.EnsurePersistedAmountsConsistent(12.345m, 0m, 0m));
        Assert.Equal(ErrorCodes.InvalidParameter, precision.Code);
        Assert.Contains("DECIMAL(18,2)", precision.Message);

        // 越界（1e16 / 极大十进制）：拒绝。
        Assert.Throws<BusinessException>(
            () => FinanceContainerSettlementLifecycleRules.EnsurePersistedAmountsConsistent(OneE16, 0m, 0m));
        Assert.Throws<BusinessException>(
            () => FinanceContainerSettlementLifecycleRules.EnsurePersistedAmountsConsistent(decimal.MaxValue, 0m, 0m));

        // 既有正负口径原样保留：总金额 > 0、费用 >= 0（零费用放行）。
        Assert.Throws<BusinessException>(
            () => FinanceContainerSettlementLifecycleRules.EnsurePersistedAmountsConsistent(0m, 0m, 0m));
        Assert.Throws<BusinessException>(
            () => FinanceContainerSettlementLifecycleRules.EnsurePersistedAmountsConsistent(-1m, 0m, 0m));
        Assert.Throws<BusinessException>(
            () => FinanceContainerSettlementLifecycleRules.EnsurePersistedAmountsConsistent(100m, -0.01m, 0m));
        Assert.Throws<BusinessException>(
            () => FinanceContainerSettlementLifecycleRules.EnsurePersistedAmountsConsistent(100m, 0m, -1m));

        // 精度不一致的费用同样拒绝（不是只有总金额受边界保护）。
        Assert.Throws<BusinessException>(
            () => FinanceContainerSettlementLifecycleRules.EnsurePersistedAmountsConsistent(100m, 0.123m, 0m));
    }

    // ==================== 2. 实际 EF 列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void 实际EF列精度与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_ERP514_MODEL_ONLY;"
                + "Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        foreach (var propertyName in new[]
                 {
                     nameof(FinanceContainerSettlement.TotalAmount),
                     nameof(FinanceContainerSettlement.FreightCost),
                     nameof(FinanceContainerSettlement.OtherCost)
                 })
        {
            var property = db.Model.FindEntityType(typeof(FinanceContainerSettlement))!
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


    // ==================== 3. 创建：手工 / 显式链接来源——越界拒绝（零写入 / 零占号）与精确上限放行 ====================

    [Theory]
    [InlineData("just-over")]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task 创建_金额超出存储精度_拒绝且零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-514-A", "客户514A");
        var no = new CountingNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        var ex = await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewSettlementRequest(customer.Id, totalAmount: OverLimit(scenario))));
        Assert.Contains("DECIMAL(18,2)", ex.Message);
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceContainerSettlements.AsNoTracking().ToList());

        // 费用越界同样在单号预约与任何写入之前拒绝。
        var freightEx = await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewSettlementRequest(customer.Id, totalAmount: 100m,
                freightCost: OverLimit(scenario))));
        Assert.Contains("DECIMAL(18,2)", freightEx.Message);
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceContainerSettlements.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 创建_恰好等于存储上限_照常落库()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-514-MAX", "客户514MAX");
        var no = new CountingNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Create(
            NewSettlementRequest(customer.Id, totalAmount: MaxAmount, freightCost: 0m, otherCost: MaxAmount)));

        var stored = Assert.Single(db.FinanceContainerSettlements.AsNoTracking().ToList());
        Assert.Equal(MaxAmount, stored.TotalAmount);
        Assert.Equal(MaxAmount, stored.OtherCost);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(string.IsNullOrWhiteSpace(stored.SettlementNo));
        Assert.Equal(1, no.Calls);
    }

    [Fact]
    public async Task 创建_显式链接来源_金额仍走同一既有边界()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-514-LNK", "客户514LNK");
        var list = SeedLoadingList(db, "ZG-514-LNK", "TCLU-514", customer.Id);
        var listBefore = db.ContainerLoadingLists.AsNoTracking().Single(l => l.Id == list.Id);
        var no = new CountingNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        // 来源可精确解析，但金额过精度 / 溢出仍在单号预约与任何写入之前拒绝：零写入、零占号、来源清单零改写。
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewSettlementRequest(customer.Id, list.Id, totalAmount: OneE16)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewSettlementRequest(customer.Id, list.Id, totalAmount: 100m, otherCost: OneE16)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceContainerSettlements.AsNoTracking().ToList());
        var listAfter = db.ContainerLoadingLists.AsNoTracking().Single(l => l.Id == list.Id);
        Assert.Equal(listBefore.Status, listAfter.Status);
        Assert.Equal(listBefore.IsDeleted, listAfter.IsDeleted);

        // 同一显式来源 + 合法边界金额照常落库并只消耗一次单号。
        Assert.IsType<OkObjectResult>(await controller.Create(
            NewSettlementRequest(customer.Id, list.Id, totalAmount: MaxAmount)));
        var stored = Assert.Single(db.FinanceContainerSettlements.AsNoTracking().ToList());
        Assert.Equal(list.Id, stored.LoadingListId);
        Assert.Equal(MaxAmount, stored.TotalAmount);
        Assert.Equal(1, no.Calls);
    }

    [Fact]
    public async Task 未认证_越界金额_仍按未认证拒绝且零写入零占号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-514-ANON", "客户514ANON");
        var no = new CountingNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, null);

        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized,
            () => controller.Create(NewSettlementRequest(customer.Id, totalAmount: OneE16)));
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceContainerSettlements.AsNoTracking().ToList());
    }


    // ==================== 4. 修改：被拒请求不改写金额与备注（含审计时间戳） ====================

    [Fact]
    public async Task 修改_金额超出存储精度_拒绝且原始字段审计不变()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-514-UPD", "客户514UPD");
        var settlement = SeedSettlement(db, "ZGS-514-UPD", null, customer.Id, 1000m, 10m, 5m);
        var before = StoredSettlement(db, settlement.Id);
        var no = new CountingNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        var request = NewSettlementRequest(customer.Id, totalAmount: OneE16);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Update(settlement.Id, request));
        Assert.Contains("DECIMAL(18,2)", ex.Message);
        AssertSettlementUnchanged(db, before);
        Assert.Equal("原始备注", StoredSettlement(db, settlement.Id).Remark);
        Assert.Equal(0, no.Calls);

        // 费用越界同样拒绝（含其他费用），仍零改写。
        var costRequest = NewSettlementRequest(customer.Id, totalAmount: 1000m, freightCost: 10m, otherCost: 5m);
        costRequest.OtherCost = RoundUpCrossing;
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Update(settlement.Id, costRequest));
        AssertSettlementUnchanged(db, before);
    }

    [Fact]
    public async Task 修改_恰好等于存储上限_允许()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-514-UPD-MAX", "客户514UPDMAX");
        var settlement = SeedSettlement(db, "ZGS-514-UPD-MAX", null, customer.Id, 1000m);
        var controller = BuildController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Update(settlement.Id,
            NewSettlementRequest(customer.Id, totalAmount: MaxAmount, freightCost: 0m, otherCost: MaxAmount)));
        var stored = StoredSettlement(db, settlement.Id);
        Assert.Equal(MaxAmount, stored.TotalAmount);
        Assert.Equal(MaxAmount, stored.OtherCost);
    }


    // ==================== 5. 提交 / 审核：既有行锁内只读复核已持久化金额 ====================

    [Fact]
    public async Task 提交审核_已持久化金额越界或精度不一致_拒绝且状态审计不变()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-514-SUB", "客户514SUB");
        var legacyOverflow = SeedSettlement(db, "ZGS-514-FLOW-1", null, customer.Id, OneE16);
        var legacyPrecision = SeedSettlement(db, "ZGS-514-FLOW-2", null, customer.Id, 12.345m,
            status: DocumentStatus.Submitted);
        var legacyNegativeCost = SeedSettlement(db, "ZGS-514-FLOW-3", null, customer.Id, 100m, -0.01m);
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
        var customer = SeedCustomer(db, "C-514-FLOW", "客户514FLOW");
        var settlement = SeedSettlement(db, "ZGS-514-FLOW-MAX", null, customer.Id, MaxAmount, 0m, MaxAmount);
        var controller = BuildController(db, new CountingNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Submit(settlement.Id));
        Assert.IsType<OkObjectResult>(await controller.Approve(settlement.Id));
        var stored = StoredSettlement(db, settlement.Id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(MaxAmount, stored.TotalAmount);
        Assert.Equal(MaxAmount, stored.OtherCost);
    }

    [Fact]
    public async Task 取消与删除_已持久化金额越界_仍按既有撤销语义执行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-514-UNDO", "客户514UNDO");
        var cancelTarget = SeedSettlement(db, "ZGS-514-CAN", null, customer.Id, OneE16);
        var deleteTarget = SeedSettlement(db, "ZGS-514-DEL", null, customer.Id, 12.345m);
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

}
