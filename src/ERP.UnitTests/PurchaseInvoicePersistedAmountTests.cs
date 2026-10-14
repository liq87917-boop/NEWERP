using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-510 供应商采购发票「标量金额与登记算术存储边界」单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item>纯规则：<see cref="PurchaseInvoiceRules.ValidateAmounts"/> 与
/// <see cref="PurchaseInvoiceRules.NormalizeAllocationAmount"/> 在既有「按币种精度取整 + 大于 0 / 等式」之后，
/// 复用 <see cref="FinancePersistedAmountRules"/> 追加金额列实际存储精度（<c>DECIMAL(18,2)</c>）判定；
/// 取整**进位后越过单列上限**、超过上限的极大十进制、净额 + 税额的 CLR 溢出、最小正数、
/// 零税额 / 零净额与零小数币种（<c>JPY</c>）取整口径全部保持既有语义（<c>AwayFromZero</c>）；</item>
/// <item>全局 <see cref="CurrencyAmountRules.RoundAmount"/> 与只读聚合求和口径**不加**单列上限；</item>
/// <item>控制器创建 / 修改 / 保存关联：受权账号提交超出单列精度的正金额在**任何表头 / 明细写入之前**被受控拒绝
/// （零表头 / 零关联行写入），恰好等于上限的金额照常落库；</item>
/// <item>登记（Record）在既有采购订单 / 发票行锁内只读复核**已持久化**金额
/// （<see cref="PurchaseInvoiceRules.EnsurePersistedAmountsConsistent"/>）：不一致 / 越界 / 未取整的历史值被受控拒绝，
/// 状态、字段、关联与审计不变，绝不静默改写；作废与读取**不新增**草稿有效性闸门。</item>
/// </list>
/// 说明：除「实际 EF 列精度」用例以 SQL Server 提供程序**仅构建模型**（不打开连接）外，全部使用内存数据库。
/// </summary>
public class PurchaseInvoicePersistedAmountTests
{
    private const decimal MaxAmount = FinancePersistedAmountRules.MaxDecimal18Scale2;   // 9_999_999_999_999_999.99
    private const decimal HalfUpToMax = 9_999_999_999_999_999.985m;                    // USD 半进位后恰好等于上限
    private const decimal RoundUpCrossing = 9_999_999_999_999_999.995m;                // USD 取整进位越过上限
    private const decimal OneE16 = 10_000_000_000_000_000m;                            // 1e16（超出上限）

    // ==================== 1. 纯规则：标量边界与全局取整口径 ====================

    [Fact]
    public void 金额规则_精确上限_半进位_零税额零净额与币种取整保持既有语义()
    {
        // 恰好等于上限（净额上限 + 零税额 = 含税总额上限）放行，零税额 / 零净额既有语义保留。
        Assert.Equal((MaxAmount, 0m, MaxAmount),
            PurchaseInvoiceRules.ValidateAmounts(MaxAmount, 0m, MaxAmount, "CNY"));
        Assert.Equal((0m, MaxAmount, MaxAmount),
            PurchaseInvoiceRules.ValidateAmounts(0m, MaxAmount, MaxAmount, "CNY"));

        // 半进位进位到上限（USD）放行：9_999_999_999_999_999.985 → 9_999_999_999_999_999.99
        var half = PurchaseInvoiceRules.ValidateAmounts(HalfUpToMax, 0m, HalfUpToMax, "USD");
        Assert.Equal((MaxAmount, 0m, MaxAmount), half);

        // 币种取整（AwayFromZero）既有口径原样保留。
        Assert.Equal((100.40m, 0.10m, 100.50m),
            PurchaseInvoiceRules.ValidateAmounts(100.4m, 0.1m, 100.5m, "CNY"));
        Assert.Equal((100m, 1m, 101m),
            PurchaseInvoiceRules.ValidateAmounts(100.4m, 0.5m, 100.5m, "JPY"));

        // 关联金额：精确上限 / 半进位到上限 / 零小数币种取整。
        Assert.Equal(MaxAmount, PurchaseInvoiceRules.NormalizeAllocationAmount(MaxAmount, "CNY"));
        Assert.Equal(MaxAmount, PurchaseInvoiceRules.NormalizeAllocationAmount(HalfUpToMax, "USD"));
        Assert.Equal(100m, PurchaseInvoiceRules.NormalizeAllocationAmount(100.4m, "JPY"));
        Assert.Equal(101m, PurchaseInvoiceRules.NormalizeAllocationAmount(100.5m, "JPY"));
        Assert.Equal(0.01m, PurchaseInvoiceRules.NormalizeAllocationAmount(0.005m, "USD"));
    }

    [Fact]
    public void 金额规则_越界标量先于等式与差额受控拒绝_含CLR溢出防护()
    {
        // 取整进位越过单列上限（USD）：净额 / 含税总额都在等式与差额计算之前被拒绝。
        var netOver = Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.ValidateAmounts(RoundUpCrossing, 0m, RoundUpCrossing, "USD"));
        Assert.Equal(ErrorCodes.InvalidParameter, netOver.Code);
        Assert.Contains("DECIMAL(18,2)", netOver.Message);

        // 零小数币种取整进位越过上限（JPY）。
        var jpyOver = Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.ValidateAmounts(9_999_999_999_999_999.6m, 0m, 9_999_999_999_999_999.6m, "JPY"));
        Assert.Equal(ErrorCodes.InvalidParameter, jpyOver.Code);
        Assert.Contains("DECIMAL(18,2)", jpyOver.Message);

        // 单看每项都在等式内、但含税总额越界（净额 + 税额会得到 1.8e16）。
        var grossOver = Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.ValidateAmounts(
                9_000_000_000_000_000m, 9_000_000_000_000_000m, OneE16, "CNY"));
        Assert.Equal(ErrorCodes.InvalidParameter, grossOver.Code);
        Assert.Contains("DECIMAL(18,2)", grossOver.Message);

        // 1e16 与极大十进制：绝不穿透到 SQL，也绝不以 CLR 溢出崩溃。
        Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.ValidateAmounts(OneE16, 0m, OneE16, "CNY"));
        var overflow = Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.ValidateAmounts(decimal.MaxValue, decimal.MaxValue, decimal.MaxValue, "CNY"));
        Assert.Equal(ErrorCodes.InvalidParameter, overflow.Code);
        Assert.Contains("DECIMAL(18,2)", overflow.Message);

        // 负数与「取整后为 0 的含税总额」既有语义保留。
        Assert.Throws<BusinessException>(() => PurchaseInvoiceRules.ValidateAmounts(-1m, 0m, -1m, "CNY"));
        Assert.Throws<BusinessException>(() => PurchaseInvoiceRules.ValidateAmounts(0m, 0m, 0m, "CNY"));

        // 等式不成立（合法标量）仍按既有文案拒绝。
        var equation = Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.ValidateAmounts(100m, 1m, 100m, "CNY"));
        Assert.Contains("金额等式不成立", equation.Message);
    }

    [Fact]
    public void 关联金额规则_取整后越界_受控拒绝且非正语义不变()
    {
        var roundUp = Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.NormalizeAllocationAmount(RoundUpCrossing, "USD"));
        Assert.Equal(ErrorCodes.InvalidParameter, roundUp.Code);
        Assert.Contains("DECIMAL(18,2)", roundUp.Message);

        Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.NormalizeAllocationAmount(OneE16, "CNY"));
        Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.NormalizeAllocationAmount(decimal.MaxValue, "JPY"));
        Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.NormalizeAllocationAmount(-1m, "CNY"));
        Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.NormalizeAllocationAmount(0.001m, "CNY"));
    }

    [Fact]
    public void 已持久化金额复核_一致放行_不一致受控拒绝且不取整不改写()
    {
        // 一致（含零税额 / 零净额）放行。
        PurchaseInvoiceRules.EnsurePersistedAmountsConsistent(100m, 0m, 100m, "CNY", "INV-OK");
        PurchaseInvoiceRules.EnsurePersistedAmountsConsistent(0m, 100m, 100m, "CNY", "INV-OK");
        PurchaseInvoiceRules.EnsurePersistedAmountsConsistent(MaxAmount, 0m, MaxAmount, "CNY", "INV-OK");

        // 等式不成立 → 规则冲突。
        var equation = Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.EnsurePersistedAmountsConsistent(100m, 1m, 100m, "CNY", "INV-EQ"));
        Assert.Equal(ErrorCodes.RuleConflict, equation.Code);

        // 超出实际存储精度（旧 / 被外部改写的快照）。
        var overLimit = Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.EnsurePersistedAmountsConsistent(OneE16, 0m, OneE16, "CNY", "INV-BIG"));
        Assert.Equal(ErrorCodes.RuleConflict, overLimit.Code);
        Assert.Contains("DECIMAL(18,2)", overLimit.Message);

        // 未按币种精度取整（JPY 存了小数）→ 拒绝而不是静默取整。
        var unrounded = Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.EnsurePersistedAmountsConsistent(100.5m, 0m, 100.5m, "JPY", "INV-JPY"));
        Assert.Equal(ErrorCodes.RuleConflict, unrounded.Code);
        Assert.Contains("取整", unrounded.Message);

        // 负数 / 含税总额为 0 / 极大值（不得以 CLR 溢出崩溃）。
        Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.EnsurePersistedAmountsConsistent(-1m, 0m, -1m, "CNY", "INV-NEG"));
        Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.EnsurePersistedAmountsConsistent(0m, 0m, 0m, "CNY", "INV-ZERO"));
        Assert.Throws<BusinessException>(() =>
            PurchaseInvoiceRules.EnsurePersistedAmountsConsistent(
                decimal.MaxValue, decimal.MaxValue, decimal.MaxValue, "CNY", "INV-MAX"));
    }

    [Fact]
    public void 全局取整与只读聚合口径_不施加单列上限()
    {
        // 全局币种取整与只读求和保持无上限：单列边界只存在于标量守卫。
        Assert.Equal(OneE16, CurrencyAmountRules.RoundAmount(RoundUpCrossing, "USD"));
        Assert.Equal(decimal.MaxValue, CurrencyAmountRules.RoundAmount(decimal.MaxValue, "USD"));

        // 两个合法上限金额的求和（只读聚合口径）合法地超过单个存储列：不得被单列边界限制。
        Assert.Equal(19_999_999_999_999_999.98m, MaxAmount + MaxAmount);
    }

    // ==================== 2. 实际 EF 列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void 实际EF列精度与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP510_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        Assert.Equal(18, FinancePersistedAmountRules.DecimalPrecision);
        Assert.Equal(2, FinancePersistedAmountRules.DecimalScale);
        AssertScalarAmountColumn(db.Model, typeof(PurchaseInvoice), nameof(PurchaseInvoice.NetAmount));
        AssertScalarAmountColumn(db.Model, typeof(PurchaseInvoice), nameof(PurchaseInvoice.TaxAmount));
        AssertScalarAmountColumn(db.Model, typeof(PurchaseInvoice), nameof(PurchaseInvoice.GrossAmount));
        AssertScalarAmountColumn(db.Model, typeof(PurchaseInvoiceAllocation),
            nameof(PurchaseInvoiceAllocation.AllocatedAmount));
    }

    private static void AssertScalarAmountColumn(IModel model, Type entityType, string propertyName)
    {
        var property = model.FindEntityType(entityType)!.FindProperty(propertyName)!;

        // 采购发票的金额列显式 HasPrecision(18,2)，规则常量必须与该实际列类型逐字同源。
        Assert.Equal(FinancePersistedAmountRules.DecimalPrecision, property.GetPrecision());
        Assert.Equal(FinancePersistedAmountRules.DecimalScale, property.GetScale());
        Assert.Equal(
            $"decimal({FinancePersistedAmountRules.DecimalPrecision},{FinancePersistedAmountRules.DecimalScale})",
            property.GetColumnType());
        Assert.Equal("decimal(18,2)", property.GetColumnType());
    }

    // ==================== 3. 创建 / 修改：越界拒绝且零写入 ====================

    [Fact]
    public async Task 创建发票_越界标量_拒绝且零表头写入_边界放行()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-PA1");
        var controller = BuildController(db);

        // 取整进位越过上限（USD）。
        var roundUp = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            InvoiceDto(supplier.Id, "A-1", RoundUpCrossing, 0m, RoundUpCrossing, "USD")));
        Assert.Equal(ErrorCodes.InvalidParameter, roundUp.Code);
        Assert.Contains("DECIMAL(18,2)", roundUp.Message);

        // 声称等式成立（1e16 + 0 = 1e16）但标量越界，且不得以 CLR 溢出崩溃。
        var big = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            InvoiceDto(supplier.Id, "A-2", OneE16, 0m, OneE16)));
        Assert.Equal(ErrorCodes.InvalidParameter, big.Code);
        Assert.Contains("DECIMAL(18,2)", big.Message);

        var huge = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            InvoiceDto(supplier.Id, "A-3", decimal.MaxValue, 0m, decimal.MaxValue)));
        Assert.Equal(ErrorCodes.InvalidParameter, huge.Code);

        // 被拒请求零表头写入（发票身份即其业务单号语义，系统不生成发票号码）。
        Assert.Empty(db.PurchaseInvoices);

        // 恰好等于存储上限的金额照常落库。
        var ok = AssertOk<PurchaseInvoiceDto>(await controller.Create(
            InvoiceDto(supplier.Id, "A-MAX", MaxAmount, 0m, MaxAmount)));
        Assert.Equal(MaxAmount, ok.NetAmount);
        Assert.Equal(0m, ok.TaxAmount);
        Assert.Equal(MaxAmount, ok.GrossAmount);
        Assert.Equal(PurchaseInvoiceRules.StatusDraft, ok.Status);
        Assert.Equal(1, await db.PurchaseInvoices.CountAsync());
    }

    [Fact]
    public async Task 修改发票_越界标量_拒绝且已存储字段不变()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-PA2");
        var controller = BuildController(db);
        var invoice = AssertOk<PurchaseInvoiceDto>(await controller.Create(
            InvoiceDto(supplier.Id, "B-1", 100m, 13m, 113m)));
        var before = await db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);

        var request = InvoiceDto(supplier.Id, "B-1", OneE16, 0m, OneE16);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(invoice.Id, request));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var after = await db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        Assert.Equal(before.NetAmount, after.NetAmount);
        Assert.Equal(before.TaxAmount, after.TaxAmount);
        Assert.Equal(before.GrossAmount, after.GrossAmount);
        Assert.Equal(before.InvoiceNumber, after.InvoiceNumber);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task 保存关联_越界金额_拒绝且零关联行_边界金额放行()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-PA3");
        var controller = BuildController(db);
        var invoice = AssertOk<PurchaseInvoiceDto>(await controller.Create(
            InvoiceDto(supplier.Id, "C-1", 100m, 13m, 113m)));
        var order = SeedOrder(db, "PO-PA3", supplier.Id, totalAmount: MaxAmount);
        var before = await db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.SaveAllocations(invoice.Id, Lines((order.Id, OneE16))));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);

        // 被拒请求零关联行写入，发票自身逐字段不变。
        Assert.Empty(await db.PurchaseInvoiceAllocations.ToListAsync());
        var after = await db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        Assert.Equal(before.GrossAmount, after.GrossAmount);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);

        // 恰好等于上限且不超订单容量的关联金额照常落库（需发票含税总额同样达到上限）。
        var maxInvoice = AssertOk<PurchaseInvoiceDto>(await controller.Create(
            InvoiceDto(supplier.Id, "C-MAX", MaxAmount, 0m, MaxAmount)));
        var saved = AssertOk<PurchaseInvoiceDto>(await controller.SaveAllocations(
            maxInvoice.Id, Lines((order.Id, MaxAmount))));
        Assert.Equal(MaxAmount, Assert.Single(saved.Allocations).AllocatedAmount);
        Assert.Equal(MaxAmount, saved.LinkedAmount);
    }

    // ==================== 4. 登记：锁内复核已持久化金额（撤销 / 读取不新增闸门） ====================

    [Fact]
    public async Task 登记_已持久化金额不一致或越界_拒绝且状态字段关联不变_读取与作废仍可用()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-PA4");
        var controller = BuildController(db);

        // 1) 等式不成立的历史快照。
        var equationBroken = SeedInvoice(db, "D-EQ", supplier.Id, 100m, 1m, 100m);
        // 2) 超出实际存储精度的历史快照。
        var overLimit = SeedInvoice(db, "D-BIG", supplier.Id, OneE16, 0m, OneE16);
        // 3) 未按币种精度取整的历史快照（JPY 存了小数）。
        var unrounded = SeedInvoice(db, "D-JPY", supplier.Id, 100.5m, 0m, 100.5m, currency: "JPY");
        var ids = new[] { equationBroken.Id, overLimit.Id, unrounded.Id };
        var before = await db.PurchaseInvoices.AsNoTracking()
            .Where(i => ids.Contains(i.Id)).OrderBy(i => i.Id).ToListAsync();

        var eqEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Record(equationBroken.Id));
        var bigEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Record(overLimit.Id));
        var jpyEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Record(unrounded.Id));
        Assert.Equal(ErrorCodes.RuleConflict, eqEx.Code);
        Assert.Equal(ErrorCodes.RuleConflict, bigEx.Code);
        Assert.Equal(ErrorCodes.RuleConflict, jpyEx.Code);

        // 拒绝发生在改状态之前：状态 / 金额 / 单号 / 审计逐字段不变，绝不静默改写快照。
        var after = await db.PurchaseInvoices.AsNoTracking()
            .Where(i => ids.Contains(i.Id)).OrderBy(i => i.Id).ToListAsync();
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(PurchaseInvoiceRules.StatusDraft, after[i].Status);
            Assert.Equal(before[i].Status, after[i].Status);
            Assert.Equal(before[i].NetAmount, after[i].NetAmount);
            Assert.Equal(before[i].TaxAmount, after[i].TaxAmount);
            Assert.Equal(before[i].GrossAmount, after[i].GrossAmount);
            Assert.Equal(before[i].InvoiceNumber, after[i].InvoiceNumber);
            Assert.Equal(before[i].UpdatedAt, after[i].UpdatedAt);
            Assert.Null(after[i].RecordedAt);
        }

        // 读取（历史证据照常可读）与作废（既有撤销语义，不新增草稿有效性闸门）仍可用。
        var detail = AssertOk<PurchaseInvoiceDto>(await controller.GetById(equationBroken.Id));
        Assert.Equal(100m, detail.GrossAmount);
        var voided = AssertOk<PurchaseInvoiceDto>(await controller.Void(
            equationBroken.Id, new PurchaseInvoiceVoidRequest { Reason = "历史快照更正" }));
        Assert.Equal(PurchaseInvoiceRules.StatusVoided, voided.Status);
        Assert.Equal("历史快照更正", voided.VoidReason);
    }

    [Fact]
    public async Task 登记_合法边界已持久化金额_放行()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-PA5");
        var controller = BuildController(db);
        var invoice = SeedInvoice(db, "D-OK", supplier.Id, MaxAmount, 0m, MaxAmount);

        var recorded = AssertOk<PurchaseInvoiceDto>(await controller.Record(invoice.Id));

        Assert.Equal(PurchaseInvoiceRules.StatusRecorded, recorded.Status);
        Assert.Equal(MaxAmount, recorded.GrossAmount);
        Assert.NotNull(recorded.RecordedAt);
        var stored = await db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        Assert.Equal(PurchaseInvoiceRules.StatusRecorded, stored.Status);
        Assert.Equal(MaxAmount, stored.NetAmount);
    }

    [Fact]
    public async Task 登记_手工登记无关联的合法发票_放行并冻结证据()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S-PA6");
        var controller = BuildController(db);

        // 手工登记（不关联任何采购订单）的合法发票照常「创建 → 登记」。
        var invoice = AssertOk<PurchaseInvoiceDto>(await controller.Create(
            InvoiceDto(supplier.Id, "E-1", 100.40m, 0.10m, 100.50m)));
        Assert.Equal(PurchaseInvoiceRules.LinkageUnlinked, invoice.LinkageStatus);

        var recorded = AssertOk<PurchaseInvoiceDto>(await controller.Record(invoice.Id));

        Assert.Equal(PurchaseInvoiceRules.StatusRecorded, recorded.Status);
        Assert.True(recorded.IsRecorded);
        Assert.Empty(recorded.Allocations);
        Assert.Equal(100.50m, recorded.GrossAmount);
        Assert.NotNull(recorded.RecordedAt);
        // 已登记证据不可改写：再次登记被既有状态门拒绝且状态不变。
        await Assert.ThrowsAsync<BusinessException>(() => controller.Record(invoice.Id));
        Assert.Equal(PurchaseInvoiceRules.StatusRecorded,
            (await db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id)).Status);
    }

    // ==================== 5. 测试脚手架 ====================

    private static PurchaseInvoiceController BuildController(ErpDbContext db)
        => PurchaseInvoiceTestIdentities.ForUser(
            db, PurchaseInvoiceTestIdentities.SeedAuthorizedUser(db, privileged: true));

    private static BaseSupplier SeedSupplier(ErpDbContext db, string code)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = $"供应商-{code}", Status = 1 };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, long supplierId, decimal totalAmount = 1000m)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            SupplierId = supplierId,
            Currency = Currency.CNY,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved,
            ArrivalProgress = "未到货",
            SettlementProgress = "未结算"
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static PurchaseInvoice SeedInvoice(
        ErpDbContext db, string number, long supplierId, decimal net, decimal tax, decimal gross,
        string currency = "CNY", int status = PurchaseInvoiceRules.StatusDraft)
    {
        var invoice = new PurchaseInvoice
        {
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceCode = string.Empty,
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = new DateTime(2026, 9, 20),
            SupplierId = supplierId,
            SupplierCode = "S-PA",
            SupplierName = "供应商-PA",
            Currency = currency,
            NetAmount = net,
            TaxAmount = tax,
            GrossAmount = gross,
            Status = status,
            UpdatedAt = new DateTime(2026, 10, 1, 8, 0, 0)
        };
        db.PurchaseInvoices.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static PurchaseInvoiceSaveDto InvoiceDto(
        long supplierId, string number, decimal net, decimal tax, decimal gross, string currency = "CNY")
        => new()
        {
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
            InvoiceCode = null,
            InvoiceNumber = number,
            InvoiceDate = new DateTime(2026, 9, 20),
            SupplierId = supplierId,
            Currency = currency,
            NetAmount = net,
            TaxAmount = tax,
            GrossAmount = gross,
            DueDate = null,
            PaymentTerms = string.Empty,
            Remark = string.Empty
        };

    private static PurchaseInvoiceAllocationSaveRequest Lines(params (long OrderId, decimal Amount)[] lines)
        => new()
        {
            Lines = lines.Select(l => new PurchaseInvoiceAllocationSaveDto
            {
                PurchaseOrderId = l.OrderId,
                AllocatedAmount = l.Amount
            }).ToList()
        };

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }
}
