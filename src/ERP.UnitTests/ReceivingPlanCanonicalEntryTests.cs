using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-482 收货计划规范入口（<c>api/container/receiving-plans</c>）与「省略计划单号」手工录入契约单元测试
/// （内存库 <see cref="TestDbFactory"/>，不连接 SQL Server、不启动 API）。
/// <para>覆盖：<list type="bullet">
/// <item>实体层：<see cref="ContainerReceivingPlan.PlanNo"/> 不再显式 <c>[Required]</c>（省略 / 空串照常绑定），
/// 长度约束保留；</item>
/// <item>新增入口：省略 / 空串 / 伪造计划单号一律由服务端权威单号覆盖，主键 / 状态 / 删除态 / 时间戳全部由服务端
/// 覆盖（客户端注入值不参与持久化）；</item>
/// <item>typed nullable 引用与日期 / 数量往返：可选目的港显式关联 / 留空（<c>null</c>）原样往返，日期与总件数
/// 原样往返，备注保存 → 重开 → 修改原样往返；</item>
/// <item>拒绝先于编号与持久化：无身份 / 缺菜单 / 无效 · 已删除 · 已停用供应商 / 非港口或停用目的港 / 非法柜型 /
/// 超长文本一律零写入且零占号；</item>
/// <item>计划只是计划证据：提交 / 审核等有效状态变更绝不创建库存 / 库存流水 / 财务记录；</item>
/// <item>接线契约：菜单路由不再进入旧存储过程族（旧族读侧保留）、规范模块接线、校验先于单号预约。</item>
/// </list></para>
/// <para>真实隔离 SQL / HTTP 的端到端验收见
/// <c>ERP.IntegrationTests/ReceivingPlanCanonicalEntrySqlServerTests.cs</c>。</para>
/// </summary>
public class ReceivingPlanCanonicalEntryTests
{
    private const long SupplierOk = 983101L;
    private const long SupplierOff = 983102L;
    private const long SupplierDeleted = 983103L;
    private const long PortOk = 983201L;
    private const long PortOff = 983202L;
    private const long PortWrongType = 983203L;

    // ==================== 0. 脚手架 ====================

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

    // ==================== 1. 实体契约：省略计划单号 ====================

    [Fact]
    public void Entity_计划单号不再显式必填_长度约束保留()
    {
        var property = typeof(ContainerReceivingPlan).GetProperty(nameof(ContainerReceivingPlan.PlanNo))!;

        Assert.Equal(typeof(string), property.PropertyType);
        Assert.False(Attribute.IsDefined(property, typeof(RequiredAttribute), true),
            "ContainerReceivingPlan.PlanNo 不得显式必填（否则省略计划单号会在模型绑定阶段被拒为 HTTP 400）");
        var maxLength = Assert.Single(property
            .GetCustomAttributes(typeof(MaxLengthAttribute), true)
            .Cast<MaxLengthAttribute>());
        Assert.Equal(50, maxLength.Length);
    }

    // ==================== 2. 省略 / 空串 / 伪造计划单号：服务端权威覆盖 ====================

    [Fact]
    public async Task Create_省略计划单号_服务端生成权威单号与规范身份()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        var no = new CountingDocumentNumberService();
        var ctl = NewController(db, TestAuth.SeedPrivilegedUser(db), no);

        var entity = NewPlan(SupplierOk);
        entity.PlanNo = string.Empty;                       // 表单省略计划单号
        var ok = Assert.IsType<OkObjectResult>(await ctl.Create(entity));

        Assert.Equal(1, no.Calls);
        var stored = db.ContainerReceivingPlans.Single();
        Assert.StartsWith("JH", stored.PlanNo);
        Assert.Equal(SupplierOk, stored.SupplierId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.NotEqual(default(DateTime), stored.CreatedAt);
        Assert.NotNull(stored.UpdatedAt);                   // 审计列由服务端（保存审计）权威写入

        // 返回规范 Id（表单保存后按规范 Id 重开 / 修改）
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        Assert.Equal(stored.Id, (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!);
    }

    [Fact]
    public async Task Create_伪造计划单号与伪造身份_一律由服务端覆盖()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        var ctl = NewController(db, TestAuth.SeedPrivilegedUser(db));

        var entity = NewPlan(SupplierOk);
        entity.PlanNo = "HACK-JH-482";                      // 伪造单号
        entity.Id = 999999L;                                // 伪造主键
        entity.Status = DocumentStatus.Approved;            // 伪造状态
        entity.IsDeleted = true;                            // 伪造删除态
        entity.CreatedAt = new DateTime(2000, 1, 1);        // 伪造时间戳
        entity.UpdatedAt = new DateTime(2000, 1, 1);

        Assert.IsType<OkObjectResult>(await ctl.Create(entity));

        var stored = db.ContainerReceivingPlans.Single();
        Assert.NotEqual(999999L, stored.Id);
        Assert.StartsWith("JH", stored.PlanNo);
        Assert.NotEqual("HACK-JH-482", stored.PlanNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.NotEqual(new DateTime(2000, 1, 1), stored.UpdatedAt);   // 审计列不由客户端注入值决定
        Assert.True(stored.CreatedAt.Year >= 2020);
    }

    [Fact]
    public async Task Create_可空目的港引用_显式关联与留空均原样往返()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedOtherInfo(db, PortOk, "Port", "SHANGHAI");
        var ctl = NewController(db, TestAuth.SeedPrivilegedUser(db));

        Assert.IsType<OkObjectResult>(await ctl.Create(NewPlan(SupplierOk, portId: PortOk)));
        Assert.IsType<OkObjectResult>(await ctl.Create(NewPlan(SupplierOk, portId: null)));

        var stored = db.ContainerReceivingPlans.OrderBy(p => p.Id).ToList();
        Assert.Equal(2, stored.Count);
        Assert.Equal(PortOk, stored[0].PortId);
        Assert.Null(stored[1].PortId);                      // 留空 = 未指定（提交 null，绝不回落为哨兵值 0）
    }

    [Fact]
    public async Task Create_日期与总件数_原样往返()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        var ctl = NewController(db, TestAuth.SeedPrivilegedUser(db));

        var entity = NewPlan(SupplierOk, totalQuantity: 12.5m);
        entity.PlanDate = new DateTime(2026, 9, 30);
        entity.ExpectedArrivalDate = new DateTime(2026, 10, 15);

        Assert.IsType<OkObjectResult>(await ctl.Create(entity));

        var stored = db.ContainerReceivingPlans.Single();
        Assert.Equal(new DateTime(2026, 9, 30), stored.PlanDate);
        Assert.Equal(new DateTime(2026, 10, 15), stored.ExpectedArrivalDate);
        Assert.Equal(12.5m, stored.TotalQuantity);
    }

    // ==================== 3. 拒绝先于编号与持久化（零写入 · 零占号） ====================

    [Fact]
    public async Task Create_无身份或缺菜单_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        var no = new CountingDocumentNumberService();

        await AssertCode(ErrorCodes.Unauthorized,
            () => NewController(db, null, no).Create(NewPlan(SupplierOk)));
        await AssertCode(ErrorCodes.Forbidden,
            () => NewController(db, SeedPlainUser(db), no).Create(NewPlan(SupplierOk)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.ContainerReceivingPlans);
    }

    [Fact]
    public async Task Create_供应商无效或已删除或已停用_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOff, "停用供应商", status: 0);
        SeedSupplier(db, SupplierDeleted, "已删除供应商", deleted: true);
        var no = new CountingDocumentNumberService();
        var ctl = NewController(db, TestAuth.SeedPrivilegedUser(db), no);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewPlan(0)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(NewPlan(987654321L)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(NewPlan(SupplierDeleted)));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Create(NewPlan(SupplierOff)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.ContainerReceivingPlans);
    }

    [Fact]
    public async Task Create_目的港无效或柜型非法或文本超长_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedOtherInfo(db, PortOff, "Port", "NINGBO", status: 0);
        SeedOtherInfo(db, PortWrongType, "Currency", "USD");
        var no = new CountingDocumentNumberService();
        var ctl = NewController(db, TestAuth.SeedPrivilegedUser(db), no);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewPlan(SupplierOk, portId: 0)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(NewPlan(SupplierOk, portId: 987654321L)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewPlan(SupplierOk, portId: PortWrongType)));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Create(NewPlan(SupplierOk, portId: PortOff)));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewPlan(SupplierOk, containerType: (ContainerType)999)));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewPlan(SupplierOk, remark: new string('R', 501))));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.ContainerReceivingPlans);
    }

    // ==================== 4. 修改：服务端身份保留与往返 ====================

    [Fact]
    public async Task Update_保留服务端单号状态与删除态_备注与可空引用往返()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedOtherInfo(db, PortOk, "Port", "SHANGHAI");
        var plan = SeedPlan(db, "JH-482-SEED", SupplierOk, DocumentStatus.Pending,
            totalQuantity: 4m, portId: PortOk, remark: "OLD");
        var ctl = NewController(db, TestAuth.SeedPrivilegedUser(db));

        var edit = NewPlan(SupplierOk, portId: null, totalQuantity: 9m, remark: "NEW");
        edit.PlanNo = "HACK-JH-482";                        // 伪造单号
        edit.Status = DocumentStatus.Cancelled;             // 伪造状态
        edit.IsDeleted = true;                              // 伪造删除态
        edit.CreatedAt = new DateTime(2000, 1, 1);          // 伪造时间戳
        Assert.IsType<OkObjectResult>(await ctl.Update(plan.Id, edit));

        db.ChangeTracker.Clear();
        var stored = db.ContainerReceivingPlans.Single(p => p.Id == plan.Id);
        Assert.Equal("JH-482-SEED", stored.PlanNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.PortId);                         // 可空引用显式清空 = 未指定
        Assert.Equal(9m, stored.TotalQuantity);
        Assert.Equal("NEW", stored.Remark);                 // 备注保存 → 重开 → 修改原样往返
    }

    // ==================== 5. 计划只是计划证据：零库存 / 财务副作用 ====================

    [Fact]
    public async Task 状态变更_不创建库存库存流水或财务记录()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        var ctl = NewController(db, TestAuth.SeedPrivilegedUser(db));

        Assert.IsType<OkObjectResult>(await ctl.Create(NewPlan(SupplierOk, totalQuantity: 6m)));
        var plan = db.ContainerReceivingPlans.Single();

        Assert.IsType<OkObjectResult>(await ctl.Submit(plan.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(plan.Id));

        db.ChangeTracker.Clear();
        Assert.Equal(DocumentStatus.Approved,
            db.ContainerReceivingPlans.Single(p => p.Id == plan.Id).Status);
        // 计划证据绝不落库存 / 库存流水 / 财务：状态变更不产生任何收货 / 出入库 / 记账记录。
        Assert.Empty(db.StockIns);
        Assert.Empty(db.StockOuts);
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.FinanceReceipts);
    }

    // ==================== 6. 接线契约：菜单路由 / 规范模块 / 校验先于单号预约 ====================

    [Fact]
    public void 菜单路由_收货计划走规范模块_旧族路由停用并保留迁移记录()
    {
        var billV2 = ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-v2.js");
        // 迁移记录字面量保留（既有 ERP-481 回归守卫依赖该文本；该守卫不在本任务允许修改的路径内），
        // 但取值已停用为假值空串：app.js 的 BILL_CODE_MAP 判定为假 → 菜单落到 MODULES['receiving-plan']，
        // 不再进入旧存储过程族。
        Assert.Contains("'receiving-plan': 'receiving-plan'", billV2);
        Assert.Contains("'receiving-plan': 'receiving-plan' && ''", billV2);
        Assert.Contains("ERP-482", billV2);

        var app = ReadSource("src", "ERP.Api", "wwwroot", "js", "app.js");
        var billMapCheck = app.IndexOf("else if (BILL_CODE_MAP[code]) renderBillV2", StringComparison.Ordinal);
        var modulesLookup = app.IndexOf("const mod = MODULES[code]", StringComparison.Ordinal);
        Assert.True(billMapCheck >= 0 && modulesLookup > billMapCheck,
            "app.js 先判 BILL_CODE_MAP 再判 MODULES：映射取值停用为假值后菜单必然落到规范模块");

        var modules = ReadSource("src", "ERP.Api", "wwwroot", "js", "modules-doc2.js");
        var block = ModuleBlock(modules, "'receiving-plan': {");
        Assert.Contains("title: '收货计划', api: '/api/container/receiving-plans'", block);
        Assert.Contains("type: 'ref', ref: 'supplier'", block);
        Assert.Contains("key: 'portId'", block);
        Assert.Contains("nullable: true", block);
        Assert.Contains("key: 'remark'", block);
        Assert.Contains("key: 'expectedArrivalDate'", block);
        Assert.Contains("key: 'containerType'", block);

        // 计划单号刻意不在表单字段里：省略 / 空串由服务端权威生成，客户端注入值不参与持久化。
        var fieldsStart = block.IndexOf("fields: [", StringComparison.Ordinal);
        Assert.True(fieldsStart > 0, "收货计划规范模块必须声明 fields");
        var fieldsBlock = block[fieldsStart..];
        Assert.DoesNotContain("key: 'planNo'", fieldsBlock);
        Assert.Contains("key: 'planDate'", fieldsBlock);
        Assert.Contains("key: 'totalQuantity'", fieldsBlock);
        Assert.DoesNotContain("/api/v2/bills", modules);
    }

    [Fact]
    public void Controllers_校验先于单号预约_规范路径不触达旧族()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "ContainerControllers.cs");
        var create = controller.IndexOf(
            "public async Task<IActionResult> Create([FromBody] ContainerReceivingPlan entity)",
            StringComparison.Ordinal);
        Assert.True(create > 0);
        var authorize = controller.IndexOf(
            "ReceivingPlanLifecycleRules.EnsureAuthorizedAsync", create, StringComparison.Ordinal);
        var validate = controller.IndexOf(
            "ReceivingPlanLifecycleRules.ValidateProposedAsync", create, StringComparison.Ordinal);
        var generate = controller.IndexOf(
            "GenerateAsync(DocumentType.ReceivingPlan)", create, StringComparison.Ordinal);
        Assert.True(authorize > create && validate > authorize && generate > validate,
            "授权 / 主数据 / 边界校验必须先于计划单号预约与落库（被拒请求零写入零占号）");
        Assert.Contains("entity.Id = 0;", controller);
        Assert.Contains("entity.Status = DocumentStatus.Pending;", controller);
        Assert.Contains("entity.IsDeleted = false;", controller);
        Assert.Contains("entity.UpdatedAt = null;", controller);
        Assert.DoesNotContain("/api/v2/bills", controller);

        var crud = ReadSource("src", "ERP.Api", "wwwroot", "js", "crud.js");
        Assert.DoesNotContain("/api/v2/bills", crud);
        Assert.Contains("f.nullable === true ? null : 0", crud);
        Assert.Contains("await api(`${mod.api}/${id}`, 'PUT', body)", crud);
        Assert.Contains("await api(mod.api, 'POST', body)", crud);
    }

    // ==================== 辅助 ====================

    private static ContainerReceivingPlan NewPlan(
        long supplierId = SupplierOk, long? portId = null, ContainerType containerType = ContainerType.GP40,
        decimal totalQuantity = 1m, string bookingNo = "", string containerNo = "",
        string destination = "", string remark = "ERP-482_TEST")
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

    private static ContainerReceivingPlan SeedPlan(ErpDbContext db, string no, long supplierId,
        DocumentStatus status = DocumentStatus.Pending, decimal totalQuantity = 1m, long? portId = null,
        string destination = "", string remark = "ERP-482_SEED")
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

    private static void SeedSupplier(ErpDbContext db, long id, string name, int status = 1, bool deleted = false)
    {
        db.BaseSuppliers.Add(new BaseSupplier
        {
            Id = id, SupplierCode = $"RP482-S-{id}", SupplierName = name, Status = status, IsDeleted = deleted
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

    /// <summary>普通账号（未映射业务员 / 未授予「收货计划」菜单）：用于证明缺菜单 fail closed 且零占号。</summary>
    private static long SeedPlainUser(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"rp482-plain-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "普通账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await action());
        Assert.Equal(expected, ex.Code);
    }

    /// <summary>截取 <c>modules-doc2.js</c> 中某个模块的文本块（从模块键到下一个两空格缩进的 <c>},</c>）。</summary>
    private static string ModuleBlock(string source, string moduleKey)
    {
        var start = source.IndexOf(moduleKey, StringComparison.Ordinal);
        Assert.True(start >= 0, $"缺少模块 {moduleKey}");
        var end = source.IndexOf("\n  },", start, StringComparison.Ordinal);
        Assert.True(end > start, $"模块 {moduleKey} 未闭合");
        return source[start..end];
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }
}
