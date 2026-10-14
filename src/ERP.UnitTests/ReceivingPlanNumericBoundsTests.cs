using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
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
/// ERP-513 收货计划总件数（<see cref="ContainerReceivingPlan.TotalQuantity"/>）实际列精度存储边界单元测试
/// （内存库 + 计数型单号服务，不连接 SQL Server、不启动 API）：
/// 覆盖纯规则正负边界与过精度 / 溢出、新增 / 修改草稿拒绝先于单号与写入、草稿可表示 <c>0</c> / 负数照常落库、
/// 提交 / 审核承诺复核（正数原样保存、零 / 负与不可存储拒绝）以及历史读取 / 取消不新增草稿有效性闸门。
/// </summary>
public class ReceivingPlanNumericBoundsTests
{
    private const decimal MaxQuantity = ReceivingPlanLifecycleRules.MaxQuantity;   // 9_999_999_999_999_999.99
    private const decimal MinQuantity = ReceivingPlanLifecycleRules.MinQuantity;   // -9_999_999_999_999_999.99
    private const decimal OneE16 = 10_000_000_000_000_000m;                        // 1e16（超出 DECIMAL(18,2) 上限）
    private const decimal OverPrecision = 12.345m;                                 // 过精度（3 位小数）
    private const long SupplierOk = 513101L;
    private const long PortOk = 513201L;

    // ==================== 1. 纯规则：精度常量与表示区间 ====================

    [Fact]
    public void 数量边界常量与实际列精度_与EF默认decimal18_2同源()
    {
        Assert.Equal(18, ReceivingPlanLifecycleRules.QuantityPrecision);
        Assert.Equal(2, ReceivingPlanLifecycleRules.QuantityScale);
        Assert.Equal(9_999_999_999_999_999.99m, ReceivingPlanLifecycleRules.MaxQuantity);
        Assert.Equal(-9_999_999_999_999_999.99m, ReceivingPlanLifecycleRules.MinQuantity);

        // 实际 EF 列精度：ContainerReceivingPlan.TotalQuantity 未显式 HasPrecision → 提供程序默认 decimal(18,2)。
        Assert.Equal("decimal(18,2)", ModelColumnType(
            typeof(ContainerReceivingPlan), nameof(ContainerReceivingPlan.TotalQuantity)));
    }

    [Fact]
    public void 可表示判定_正负边界与草稿零负数放行_过精度与溢出拒绝()
    {
        // 恰好边界（含负向）与草稿可寄存的 0 / 负数 / 2 位小数负值。
        Assert.True(ReceivingPlanLifecycleRules.IsQuantityRepresentable(MaxQuantity));
        Assert.True(ReceivingPlanLifecycleRules.IsQuantityRepresentable(MinQuantity));
        Assert.True(ReceivingPlanLifecycleRules.IsQuantityRepresentable(0m));
        Assert.True(ReceivingPlanLifecycleRules.IsQuantityRepresentable(-5m));
        Assert.True(ReceivingPlanLifecycleRules.IsQuantityRepresentable(-7.25m));
        Assert.True(ReceivingPlanLifecycleRules.IsQuantityRepresentable(12.5m));
        Assert.True(ReceivingPlanLifecycleRules.IsQuantityRepresentable(12345.67m));

        // 过精度 / 溢出一律不可原样表示。
        Assert.False(ReceivingPlanLifecycleRules.IsQuantityRepresentable(OverPrecision));
        Assert.False(ReceivingPlanLifecycleRules.IsQuantityRepresentable(-OverPrecision));
        Assert.False(ReceivingPlanLifecycleRules.IsQuantityRepresentable(0.005m));
        Assert.False(ReceivingPlanLifecycleRules.IsQuantityRepresentable(MaxQuantity + 0.01m));
        Assert.False(ReceivingPlanLifecycleRules.IsQuantityRepresentable(MinQuantity - 0.01m));
        Assert.False(ReceivingPlanLifecycleRules.IsQuantityRepresentable(OneE16));
        Assert.False(ReceivingPlanLifecycleRules.IsQuantityRepresentable(-OneE16));
        Assert.False(ReceivingPlanLifecycleRules.IsQuantityRepresentable(decimal.MaxValue));
        Assert.False(ReceivingPlanLifecycleRules.IsQuantityRepresentable(decimal.MinValue));

        // EnsureQuantityRepresentable：可表示原样返回；不可表示受控 1001，绝不静默取整。
        Assert.Equal(-7.25m, ReceivingPlanLifecycleRules.EnsureQuantityRepresentable(-7.25m));
        Assert.Equal(MaxQuantity, ReceivingPlanLifecycleRules.EnsureQuantityRepresentable(MaxQuantity));

        var overPrecision = Assert.Throws<BusinessException>(
            () => ReceivingPlanLifecycleRules.EnsureQuantityRepresentable(OverPrecision));
        Assert.Equal(ErrorCodes.InvalidParameter, overPrecision.Code);
        Assert.Contains("DECIMAL(18,2)", overPrecision.Message, StringComparison.Ordinal);
        Assert.Contains(ReceivingPlanLifecycleRules.QuantityStorageText, overPrecision.Message, StringComparison.Ordinal);

        var huge = Assert.Throws<BusinessException>(
            () => ReceivingPlanLifecycleRules.EnsureQuantityRepresentable(OneE16));
        Assert.Equal(ErrorCodes.InvalidParameter, huge.Code);
        Assert.Contains("DECIMAL(18,2)", huge.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 承诺正数判定_可存储正数通过_零负与不可存储拒绝()
    {
        ReceivingPlanLifecycleRules.EnsureQuantityPositive(1m);
        ReceivingPlanLifecycleRules.EnsureQuantityPositive(12.5m);
        ReceivingPlanLifecycleRules.EnsureQuantityPositive(MaxQuantity);

        var zero = Assert.Throws<BusinessException>(
            () => ReceivingPlanLifecycleRules.EnsureQuantityPositive(0m));
        Assert.Equal(ErrorCodes.InvalidParameter, zero.Code);
        Assert.Contains("TotalQuantity > 0", zero.Message, StringComparison.Ordinal);

        var negative = Assert.Throws<BusinessException>(
            () => ReceivingPlanLifecycleRules.EnsureQuantityPositive(-5m));
        Assert.Equal(ErrorCodes.InvalidParameter, negative.Code);
        Assert.Contains("TotalQuantity > 0", negative.Message, StringComparison.Ordinal);

        // 不可存储值优先以存储边界文案拒绝（绝非静默取整 / 溢出）。
        var overPrecision = Assert.Throws<BusinessException>(
            () => ReceivingPlanLifecycleRules.EnsureQuantityPositive(OverPrecision));
        Assert.Equal(ErrorCodes.InvalidParameter, overPrecision.Code);
        Assert.Contains("DECIMAL(18,2)", overPrecision.Message, StringComparison.Ordinal);

        var huge = Assert.Throws<BusinessException>(
            () => ReceivingPlanLifecycleRules.EnsureQuantityPositive(OneE16));
        Assert.Equal(ErrorCodes.InvalidParameter, huge.Code);
        Assert.Contains("DECIMAL(18,2)", huge.Message, StringComparison.Ordinal);
    }

    // ==================== 2. 新增（草稿）：拒绝先于编号与写入 ====================

    [Theory]
    [InlineData("over-precision")]
    [InlineData("huge-positive")]
    [InlineData("huge-negative")]
    [InlineData("decimal-max")]
    public async Task 新增_过精度或溢出总件数_受控拒绝且零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        var no = new CountingDocumentNumberService();
        var ctl = NewController(db, TestAuth.SeedPrivilegedUser(db), no);

        var quantity = scenario switch
        {
            "over-precision" => OverPrecision,
            "huge-positive" => OneE16,
            "huge-negative" => -OneE16,
            _ => decimal.MaxValue
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Create(NewPlan(SupplierOk, totalQuantity: quantity)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message, StringComparison.Ordinal);

        Assert.Equal(0, no.Calls);                                  // 零单号消耗
        db.ChangeTracker.Clear();
        Assert.Empty(db.ContainerReceivingPlans);                   // 零写入
    }

    [Fact]
    public async Task 新增_草稿_可表示的零与负数照常落库_并被服务端权威覆盖()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        var no = new CountingDocumentNumberService();
        var ctl = NewController(db, TestAuth.SeedPrivilegedUser(db), no);

        AssertOkObject(await ctl.Create(NewPlan(SupplierOk, totalQuantity: 0m)));
        AssertOkObject(await ctl.Create(NewPlan(SupplierOk, totalQuantity: -7.25m)));
        Assert.Equal(2, no.Calls);

        db.ChangeTracker.Clear();
        var zeroRow = db.ContainerReceivingPlans.Single(p => p.TotalQuantity == 0m);
        var negativeRow = db.ContainerReceivingPlans.Single(p => p.TotalQuantity == -7.25m);
        Assert.False(string.IsNullOrWhiteSpace(zeroRow.PlanNo));     // 服务端权威单号
        Assert.False(string.IsNullOrWhiteSpace(negativeRow.PlanNo));
        Assert.Equal(DocumentStatus.Pending, zeroRow.Status);
        Assert.Equal(DocumentStatus.Pending, negativeRow.Status);
    }

    // ==================== 3. 修改（草稿）：拒绝不改动库中原单 ====================

    [Fact]
    public async Task 修改_过精度或溢出总件数_拒绝且库中原单完全不变()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        var plan = SeedPlan(db, "RP513-EDIT", SupplierOk, DocumentStatus.Pending,
            totalQuantity: 10m, destination: "OLD-DEST");
        var ctl = NewController(db, TestAuth.SeedPrivilegedUser(db));

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(plan.Id,
            NewPlan(SupplierOk, totalQuantity: OverPrecision, destination: "NEW-DEST")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(plan.Id,
            NewPlan(SupplierOk, totalQuantity: OneE16, destination: "NEW-DEST")));

        db.ChangeTracker.Clear();
        var stored = db.ContainerReceivingPlans.Single(p => p.Id == plan.Id);
        Assert.Equal(10m, stored.TotalQuantity);
        Assert.Equal("OLD-DEST", stored.Destination);

        // 可表示的负数草稿照常可写（既有草稿口径不变）。
        AssertOkObject(await ctl.Update(plan.Id, NewPlan(SupplierOk, totalQuantity: -9.99m, destination: "NEG-DEST")));
        db.ChangeTracker.Clear();
        var edited = db.ContainerReceivingPlans.Single(p => p.Id == plan.Id);
        Assert.Equal(-9.99m, edited.TotalQuantity);
        Assert.Equal("NEG-DEST", edited.Destination);
    }

    // ==================== 4. 提交 / 审核（承诺）：锁内复核数量 ====================

    [Fact]
    public async Task 提交审核_可存储正数照常流转并原样保存数量()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedOtherInfo(db, PortOk, "Port", "SHANGHAI", status: 1);
        var plan = SeedPlan(db, "RP513-FLOW", SupplierOk, DocumentStatus.Pending,
            totalQuantity: 12345.67m, portId: PortOk);
        var ctl = NewController(db, TestAuth.SeedPrivilegedUser(db));

        AssertOkObject(await ctl.Submit(plan.Id));
        db.ChangeTracker.Clear();
        var submitted = db.ContainerReceivingPlans.Single(p => p.Id == plan.Id);
        Assert.Equal(DocumentStatus.Submitted, submitted.Status);
        Assert.Equal(12345.67m, submitted.TotalQuantity);

        AssertOkObject(await ctl.Approve(plan.Id));
        db.ChangeTracker.Clear();
        var approved = db.ContainerReceivingPlans.Single(p => p.Id == plan.Id);
        Assert.Equal(DocumentStatus.Approved, approved.Status);
        Assert.Equal(12345.67m, approved.TotalQuantity);
    }

    [Fact]
    public async Task 提交审核_零负与不可存储值_拒绝且状态数量不变()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        var zero = SeedPlan(db, "RP513-ZERO", SupplierOk, DocumentStatus.Pending, totalQuantity: 0m);
        var negative = SeedPlan(db, "RP513-NEG", SupplierOk, DocumentStatus.Pending, totalQuantity: -5m);
        var huge = SeedPlan(db, "RP513-HUGE", SupplierOk, DocumentStatus.Pending, totalQuantity: OneE16);
        var zeroSubmitted = SeedPlan(db, "RP513-APP-ZERO", SupplierOk, DocumentStatus.Submitted, totalQuantity: 0m);
        var ctl = NewController(db, TestAuth.SeedPrivilegedUser(db));

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Submit(zero.Id));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Submit(negative.Id));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Submit(huge.Id));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Approve(zeroSubmitted.Id));

        db.ChangeTracker.Clear();
        Assert.Equal(DocumentStatus.Pending, db.ContainerReceivingPlans.Single(p => p.Id == zero.Id).Status);
        Assert.Equal(DocumentStatus.Pending, db.ContainerReceivingPlans.Single(p => p.Id == negative.Id).Status);
        Assert.Equal(DocumentStatus.Pending, db.ContainerReceivingPlans.Single(p => p.Id == huge.Id).Status);
        Assert.Equal(DocumentStatus.Submitted, db.ContainerReceivingPlans.Single(p => p.Id == zeroSubmitted.Id).Status);
        Assert.Equal(-5m, db.ContainerReceivingPlans.Single(p => p.Id == negative.Id).TotalQuantity);
        Assert.Equal(OneE16, db.ContainerReceivingPlans.Single(p => p.Id == huge.Id).TotalQuantity);
    }

    // ==================== 5. 历史读取 / 取消：不新增草稿有效性闸门 ====================

    [Fact]
    public async Task 历史读取与取消_不新增草稿有效性闸门_数量原样保留()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        var overPrecision = SeedPlan(db, "RP513-READ", SupplierOk, DocumentStatus.Pending, totalQuantity: OverPrecision);
        var zero = SeedPlan(db, "RP513-CANCEL", SupplierOk, DocumentStatus.Submitted, totalQuantity: 0m);
        var ctl = NewController(db, TestAuth.SeedPrivilegedUser(db));

        // 只读照常返回（数量字段原样，不做任何回填 / 取整）。
        var detail = AssertOk<ContainerReceivingPlan>(await ctl.GetById(overPrecision.Id));
        Assert.Equal(OverPrecision, detail.TotalQuantity);
        var page = AssertOk<PagedResult<ContainerReceivingPlan>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));
        Assert.Contains(page.Items, p => p.Id == overPrecision.Id);

        // 取消沿用既有状态机（不要求正数、不新增数量闸门），数量保持原样。
        AssertOkObject(await ctl.Cancel(zero.Id));
        db.ChangeTracker.Clear();
        var cancelled = db.ContainerReceivingPlans.Single(p => p.Id == zero.Id);
        Assert.Equal(DocumentStatus.Cancelled, cancelled.Status);
        Assert.Equal(0m, cancelled.TotalQuantity);
    }

    // ==================== 6. 源码契约：守卫纯函数且先于单号 / 写入 ====================

    [Fact]
    public void 源码契约_数量守卫纯函数先于单号生成_规则仍不落库()
    {
        var rules = ReadSource("src/ERP.Application/Services/ReceivingPlanLifecycleRules.cs");

        Assert.Contains("public static bool IsQuantityRepresentable(decimal totalQuantity)", rules);
        Assert.Contains("public static decimal EnsureQuantityRepresentable(decimal totalQuantity)", rules);
        Assert.Contains("decimal.Round(totalQuantity, QuantityScale) == totalQuantity", rules);
        Assert.Contains("TotalQuantity = EnsureQuantityRepresentable(proposed.TotalQuantity)", rules);
        Assert.Contains("EnsureQuantityRepresentable(totalQuantity);", rules);   // EnsureQuantityPositive 先判可存储
        Assert.DoesNotContain("SaveChanges", rules);                             // 纯判定，不落库
        Assert.DoesNotContain(".Update(", rules);
        Assert.DoesNotContain("HttpClient", rules);
        // 不借用财务金额归一化改写物流数量。
        Assert.DoesNotContain("FinancePersistedAmountRules", rules);
        Assert.DoesNotContain("CurrencyAmountRules", rules);

        var controller = ReadSource("src/ERP.Api/Controllers/ContainerControllers.cs");
        var create = controller.IndexOf(
            "public async Task<IActionResult> Create([FromBody] ContainerReceivingPlan entity)", StringComparison.Ordinal);
        var validate = controller.IndexOf("ReceivingPlanLifecycleRules.ValidateProposedAsync", create, StringComparison.Ordinal);
        var generate = controller.IndexOf("GenerateAsync(DocumentType.ReceivingPlan)", create, StringComparison.Ordinal);
        Assert.True(validate > create && generate > validate, "总件数守卫必须在单号生成之前");
    }

    // ==================== 脚手架与种子数据 ====================

    private sealed class CountingDocumentNumberService : IDocumentNumberService
    {
        private int _seq;

        /// <summary>已被请求生成的计划单号次数（用于证明被拒请求绝不消耗计划单号）。</summary>
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
        {
            Calls++;
            Assert.Equal(DocumentType.ReceivingPlan, documentType);
            return Task.FromResult($"JH{DateTime.Now:yyyyMMdd}{++_seq:D4}");
        }
    }

    private static ContainerReceivingPlanController NewController(
        ErpDbContext db, long? userId, IDocumentNumberService? noService = null)
    {
        var ctl = new ContainerReceivingPlanController(db, noService ?? new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static string ModelColumnType(Type entityType, string propertyName)
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP513_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);
        return db.Model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;
    }

    private static ContainerReceivingPlan NewPlan(
        long supplierId = SupplierOk, long? portId = null, ContainerType containerType = ContainerType.GP40,
        decimal totalQuantity = 1m, string bookingNo = "", string containerNo = "",
        string destination = "", string remark = "ERP-513_TEST")
        => new()
        {
            PlanDate = DateTime.Today,
            SupplierId = supplierId,
            PortId = portId,
            ContainerType = containerType,
            TotalQuantity = totalQuantity,
            BookingNo = bookingNo,
            ContainerNo = containerNo,
            Destination = destination,
            Remark = remark
        };

    private static void SeedSupplier(ErpDbContext db, long id, string name, int status = 1, bool deleted = false)
    {
        db.BaseSuppliers.Add(new BaseSupplier
        {
            Id = id, SupplierCode = $"RP513-S-{id}", SupplierName = name, Status = status, IsDeleted = deleted
        });
        db.SaveChanges();
    }

    private static void SeedOtherInfo(ErpDbContext db, long id, string infoType, string code,
        int status = 1, bool deleted = false)
    {
        db.BaseOtherInfos.Add(new BaseOtherInfo
        {
            Id = id, InfoType = infoType, InfoCode = code, InfoName = $"{infoType}-{code}",
            Status = status, IsDeleted = deleted
        });
        db.SaveChanges();
    }

    private static ContainerReceivingPlan SeedPlan(ErpDbContext db, string no, long supplierId,
        DocumentStatus status = DocumentStatus.Pending, decimal totalQuantity = 1m, long? portId = null,
        string destination = "", string remark = "ERP-513_SEED")
    {
        var plan = new ContainerReceivingPlan
        {
            PlanNo = no, PlanDate = DateTime.Today, SupplierId = supplierId, Status = status,
            TotalQuantity = totalQuantity, PortId = portId, Destination = destination, Remark = remark
        };
        db.ContainerReceivingPlans.Add(plan);
        db.SaveChanges();
        return plan;
    }

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await action());
        Assert.Equal(expected, ex.Code);
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static void AssertOkObject(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
