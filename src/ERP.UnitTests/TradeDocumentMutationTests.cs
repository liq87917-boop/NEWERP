using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 单证表头生命周期与明细行变更的共享父行锁 + 原子事务协议单元测试（ERP-395）。
/// 覆盖：状态已知 / 冻结口径与未知状态 fail closed、表头状态前进与回退拒绝、冻结状态商业字段不可改写、
/// 明细行写入与父单证删除 / 未知状态 / 冻结状态的一致性、批量删除整体拒绝与确定性锁序、
/// 以及「控制器表头与明细写路由都取同一把父单证行锁与同一事务」的接线契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本；真实 SQL 行锁竞态由
/// <c>ERP.IntegrationTests/TradeDocumentMutationSqlServerTests.cs</c> 在受控 localdb 上验证。
/// </summary>
public class TradeDocumentMutationTests
{
    // ==================== 0. 测试脚手架 ====================

    private static TradeDocumentController BuildController(ErpDbContext db)
        => new(new GenericService<TradeDocument>(db), db);

    private static TradeDocument SeedDocument(ErpDbContext db, string docNo,
        string docType = "商业发票", string status = "待制作", decimal amount = 0m,
        string currency = "USD", long? customerId = null)
    {
        var document = new TradeDocument
        {
            DocNo = docNo, DocType = docType, Status = status, Amount = amount,
            Currency = currency, IssueDate = new DateTime(2026, 9, 20),
            CustomerId = customerId, CustomerName = "义乌客户", Copies = 3,
        };
        db.TradeDocuments.Add(document);
        db.SaveChanges();
        return document;
    }

    private static TradeDocumentItemSaveDto Item(
        decimal quantity = 10m, decimal unitPrice = 2.5m, int? lineOrder = null)
        => new()
        {
            ProductCode = "P001", ProductNameCn = "毛巾", Quantity = quantity,
            UnitPrice = unitPrice, Unit = "箱", LineOrder = lineOrder,
        };

    /// <summary>以既有权威单证为蓝本构造「拟议」表头（只改要改的字段，其余保持原值）。</summary>
    private static TradeDocument Edit(TradeDocument stored, string? status = null,
        decimal? amount = null, string? customerName = null)
        => new()
        {
            Id = stored.Id, DocNo = stored.DocNo, DocType = stored.DocType,
            Status = status ?? stored.Status, Amount = amount ?? stored.Amount,
            Currency = stored.Currency, IssueDate = stored.IssueDate,
            CustomerId = stored.CustomerId, CustomerName = customerName ?? stored.CustomerName,
            Copies = stored.Copies, DeparturePort = stored.DeparturePort,
            DestinationPort = stored.DestinationPort, IssuedBy = stored.IssuedBy,
            SalesOrderNo = stored.SalesOrderNo, RefNo = stored.RefNo, DeclareNo = stored.DeclareNo,
            FileNote = stored.FileNote, Remark = stored.Remark,
        };

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static async Task<BusinessException> CodeOfAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static async Task<TradeDocumentItemDto> AddItemAsync(
        TradeDocumentController controller, long documentId, TradeDocumentItemSaveDto dto)
        => AssertOk<TradeDocumentItemDto>(await controller.CreateItem(documentId, dto));

    // ==================== 1. 状态口径（纯规则） ====================

    [Fact]
    public void 状态口径_已知与冻结集合_未知状态不属于已知()
    {
        Assert.Equal(
            new[] { "待制作", "已制作", "已提交客户", "已使用" },
            TradeDocumentItemRules.KnownStatuses);
        Assert.Equal(new[] { "已提交客户", "已使用" }, TradeDocumentItemRules.FrozenStatuses);

        Assert.True(TradeDocumentItemRules.IsKnownStatus("已制作"));
        Assert.True(TradeDocumentItemRules.IsKnownStatus(" 已使用 "));
        Assert.False(TradeDocumentItemRules.IsKnownStatus("已作废"));
        Assert.False(TradeDocumentItemRules.IsKnownStatus(""));

        Assert.True(TradeDocumentItemRules.IsFrozenStatus("已提交客户"));
        Assert.True(TradeDocumentItemRules.IsFrozenStatus("已使用"));
        Assert.False(TradeDocumentItemRules.IsFrozenStatus("待制作"));
        Assert.False(TradeDocumentItemRules.IsFrozenStatus("未知状态"));
    }

    [Fact]
    public void 状态归一化_空值按待制作_未知状态fail_closed()
    {
        Assert.Equal("待制作", TradeDocumentMutationRules.NormalizeStatus(null));
        Assert.Equal("待制作", TradeDocumentMutationRules.NormalizeStatus("  "));
        Assert.Equal("已制作", TradeDocumentMutationRules.NormalizeStatus(" 已制作 "));

        TradeDocumentMutationRules.EnsureKnownStatus("已使用");
        var ex = Assert.Throws<BusinessException>(
            () => TradeDocumentMutationRules.EnsureKnownStatus("已作废"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("已知口径", ex.Message);
    }

    // ==================== 2. 表头生命周期（未知 / 回退 / 冻结 fail closed） ====================

    [Fact]
    public async Task 表头修改_未知拟议状态_拒绝且不改写()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "MUT-A", status: "待制作");

        var ex = await CodeOfAsync(ErrorCodes.InvalidParameter,
            () => BuildController(db).Update(document.Id, Edit(document, status: "已作废")));

        Assert.Contains("已知口径", ex.Message);
        Assert.Equal("待制作", db.TradeDocuments.Single(d => d.Id == document.Id).Status);
    }

    [Fact]
    public async Task 表头修改_状态回退_拒绝且不改写()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "MUT-B", status: "已制作");

        var ex = await CodeOfAsync(ErrorCodes.RuleConflict,
            () => BuildController(db).Update(document.Id, Edit(document, status: "待制作")));

        Assert.Contains("不能回退", ex.Message);
        Assert.Equal("已制作", db.TradeDocuments.Single(d => d.Id == document.Id).Status);
    }

    [Fact]
    public async Task 表头修改_冻结状态改商业字段_拒绝且不改写()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "MUT-C", status: "已提交客户", amount: 100m);

        var ex = await CodeOfAsync(ErrorCodes.RuleConflict,
            () => BuildController(db).Update(document.Id,
                Edit(document, status: "已提交客户", amount: 999m)));

        Assert.Contains("冻结", ex.Message);
        Assert.Equal(100m, db.TradeDocuments.Single(d => d.Id == document.Id).Amount);
    }

    [Fact]
    public async Task 表头修改_冻结状态前进到已使用_允许()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "MUT-D", status: "已提交客户", amount: 100m);

        AssertOk<TradeDocument>(await BuildController(db)
            .Update(document.Id, Edit(document, status: "已使用")));

        Assert.Equal("已使用", db.TradeDocuments.Single(d => d.Id == document.Id).Status);
    }

    [Fact]
    public async Task 表头修改_待制作前进到已制作_允许_回归()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "MUT-E", status: "待制作", amount: 0m);

        AssertOk<TradeDocument>(await BuildController(db)
            .Update(document.Id, Edit(document, status: "已制作", amount: 800m)));

        var stored = db.TradeDocuments.Single(d => d.Id == document.Id);
        Assert.Equal("已制作", stored.Status);
        Assert.Equal(800m, stored.Amount);
    }

    [Fact]
    public async Task 新增单证_未知状态_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var ex = await CodeOfAsync(ErrorCodes.InvalidParameter,
            () => BuildController(db).Create(new TradeDocument
            {
                DocNo = "MUT-F", DocType = "商业发票", Status = "已作废",
            }));

        Assert.Contains("已知口径", ex.Message);
        Assert.Empty(db.TradeDocuments);
    }

    // ==================== 3. 明细行写入与父单证状态 / 删除一致性 ====================

    [Fact]
    public async Task 明细行_父单证未知状态_新增修改删除一律拒绝()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "MUT-G", status: "待制作");
        var controller = BuildController(db);
        var item = await AddItemAsync(controller, document.Id, Item());

        // 人为制造未知持久化状态（历史脏数据）：未知状态一律 fail closed。
        document.Status = "已作废";
        db.SaveChanges();

        await CodeOfAsync(ErrorCodes.RuleConflict, () => controller.CreateItem(document.Id, Item()));
        await CodeOfAsync(ErrorCodes.RuleConflict, () => controller.UpdateItem(item.Id, Item(quantity: 3m)));
        await CodeOfAsync(ErrorCodes.RuleConflict, () => controller.DeleteItem(item.Id));

        Assert.Equal(1, db.TradeDocumentItems.Count(i => !i.IsDeleted));
        Assert.Equal(10m, db.TradeDocumentItems.Single(i => i.Id == item.Id).Quantity);
    }

    [Fact]
    public async Task 明细行_父单证已删除_新增修改删除一律拒绝()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "MUT-H", status: "待制作");
        var controller = BuildController(db);
        var item = await AddItemAsync(controller, document.Id, Item());

        document.IsDeleted = true;
        db.SaveChanges();

        await CodeOfAsync(ErrorCodes.NotFound, () => controller.CreateItem(document.Id, Item()));
        await CodeOfAsync(ErrorCodes.NotFound, () => controller.UpdateItem(item.Id, Item(quantity: 3m)));
        await CodeOfAsync(ErrorCodes.NotFound, () => controller.DeleteItem(item.Id));

        Assert.False(db.TradeDocumentItems.Single(i => i.Id == item.Id).IsDeleted);
        Assert.Equal(10m, db.TradeDocumentItems.Single(i => i.Id == item.Id).Quantity);
    }

    [Fact]
    public async Task 明细行_重复显式行序_服务端先行拒绝()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "MUT-I", status: "待制作");
        var controller = BuildController(db);
        await AddItemAsync(controller, document.Id, Item(lineOrder: 1));

        await CodeOfAsync(ErrorCodes.Duplicate, () => controller.CreateItem(document.Id, Item(lineOrder: 1)));

        Assert.Equal(1, db.TradeDocumentItems.Count(i => !i.IsDeleted));
    }

    // ==================== 4. 批量删除（确定性锁序与整体拒绝） ====================

    [Fact]
    public async Task 批量删除_存在缺失_整体拒绝且无部分删除()
    {
        using var db = TestDbFactory.Create();
        var first = SeedDocument(db, "MUT-J-1");
        var second = SeedDocument(db, "MUT-J-2");

        await CodeOfAsync(ErrorCodes.NotFound, () => BuildController(db)
            .BatchDelete(new List<long> { first.Id, second.Id, 987654L }));

        Assert.Empty(db.TradeDocuments.Where(d => d.IsDeleted));
    }

    [Fact]
    public async Task 批量删除_空或非法Id_不写库且成功()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "MUT-K");

        var ok = Assert.IsType<OkObjectResult>(
            await BuildController(db).BatchDelete(new List<long> { 0, -1 }));
        Assert.Equal(ErrorCodes.Success, Assert.IsType<ApiResponse<object>>(ok.Value).Code);
        Assert.False(db.TradeDocuments.Single(d => d.Id == document.Id).IsDeleted);
    }

    [Fact]
    public async Task 批量删除_全部允许成功_倒序入参也生效()
    {
        using var db = TestDbFactory.Create();
        var first = SeedDocument(db, "MUT-L-1");
        var second = SeedDocument(db, "MUT-L-2");

        var ok = Assert.IsType<OkObjectResult>(
            await BuildController(db).BatchDelete(new List<long> { second.Id, first.Id }));
        Assert.Equal(ErrorCodes.Success, Assert.IsType<ApiResponse<object>>(ok.Value).Code);
        Assert.All(db.TradeDocuments.ToList(), d => Assert.True(d.IsDeleted));
    }

    // ==================== 5. 锁序与接线契约（源码级） ====================

    [Fact]
    public void 锁序_父单证行升序去重且仅正整数()
    {
        Assert.Equal(new long[] { 2, 5, 9 },
            TradeDocumentMutationRules.MergeDocumentLockIds(new long[] { 9, 5, 2, 5, 0, -3 }));
        Assert.Empty(TradeDocumentMutationRules.MergeDocumentLockIds(null));

        Assert.Contains("db_owner.TradeDocuments", TradeDocumentMutationRules.LockDocumentRowSql);
        Assert.Contains("UPDLOCK, HOLDLOCK", TradeDocumentMutationRules.LockDocumentRowSql);
    }

    [Fact]
    public void 规则类_父行锁为审计时间戳刷新式排它锁_共享事务且非关系型跳过()
    {
        var source = File.ReadAllText(
            RepoFile("src", "ERP.Application", "Services", "TradeDocumentMutationRules.cs"));

        Assert.Contains("UpdatedAt = DateTime.Now", source);          // 行锁 = 审计时间戳刷新 UPDATE（X 锁）
        Assert.Contains("DbUpdateConcurrencyException", source);      // 并发令牌过期后有界重试
        Assert.Contains("IsRelationalProvider", source);              // 内存库跳过行锁
        Assert.Contains("BeginMutationTransactionAsync", source);     // 共享原子事务
        Assert.Contains("MergeDocumentLockIds", source);              // 确定性锁序
        Assert.Contains("EnsureHeaderUpdateAllowed", source);         // 表头生命周期判定

        // 不新增权限模型：护栏只加锁与判定，不写菜单 / 角色 / 用户授权，也不匿名放行。
        Assert.DoesNotContain("SysRoleMenu", source);
        Assert.DoesNotContain("SysUserRole", source);
        Assert.DoesNotContain("AllowAnonymous", source);
    }

    [Fact]
    public void 控制器契约_表头与批量写路由都在事务与父行锁内()
    {
        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "TradeDocumentController.cs"));

        Assert.Contains("TradeDocumentMutationRules.BeginMutationTransactionAsync", controller);
        Assert.Contains("TradeDocumentMutationRules.LockDocumentRowAsync", controller);
        Assert.Contains("TradeDocumentMutationRules.LockDocumentRowsAsync", controller);
        Assert.Contains("TradeDocumentAuthorizationRules.LoadLockedDocumentAsync", controller);
        Assert.Contains("TradeDocumentMutationRules.EnsureHeaderUpdateAllowed", controller);
        Assert.Contains("RollbackAsync", controller);
        Assert.Contains("DiscardTrackedChanges", controller);
    }

    [Fact]
    public void 服务契约_明细行三式都先取父行锁再在锁内复核()
    {
        var service = File.ReadAllText(
            RepoFile("src", "ERP.Application", "Services", "TradeDocumentItemService.cs"));

        Assert.Contains("TradeDocumentMutationRules.BeginMutationTransactionAsync", service);
        Assert.Contains("TradeDocumentMutationRules.LockDocumentRowAsync", service);
        Assert.Contains("TradeDocumentAuthorizationRules.LoadLockedDocumentAsync", service);
        Assert.Contains("TradeDocumentMutationRules.ParentDeletedText", service);

        // 新增 / 修改 / 删除三条写路由各自取一次父行锁（三式齐全）。
        Assert.Equal(3, CountOccurrences(service, "await TradeDocumentMutationRules.LockDocumentRowAsync"));

        // 读取侧仍不写库、也不改动来源与表头。
        Assert.Contains("LoadProductsAsync", service);
        Assert.Contains("AsNoTracking()", service);
    }

    private static int CountOccurrences(string text, string token)
        => text.Split(token, StringSplitOptions.None).Length - 1;
}
