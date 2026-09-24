using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 装柜清单多客户参与方单元测试（ERP-041）。覆盖：
/// 一柜多客户的新增 / 修改 / 停用 / 启用 / 删除、客户引用校验（不存在 / 已删除 / 已停用）、
/// 同一清单同一客户不重复、主参与方唯一与其显式转移（与列表顺序无关）、
/// 主参与方对装柜清单兼容客户字段（CustomerId）的同步、历史单客户视图的只读兼容与「读取不写库」、
/// 参与方计数与主参与方的列表 / 详情标注、有界上限，
/// 以及「维护参与方不改写装柜明细 / 单证 / 费用 / 库存 / 客户主数据」的边界与幂等结构契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class ContainerLoadingParticipantTests
{
    // ==================== 0. 测试脚手架 ====================

    private static ContainerLoadingListController BuildLoadingListController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static ContainerLoadingList SeedLoadingList(
        ErpDbContext db, string loadingListNo, long customerId,
        DocumentStatus status = DocumentStatus.Pending)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = loadingListNo,
            LoadingDate = new DateTime(2026, 9, 25),
            ContainerNo = "TCLU-001",
            CustomerId = customerId,
            Status = status
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    private static ContainerLoadingDetail SeedLoadingDetail(
        ErpDbContext db, long loadingListId, decimal quantity = 100m, decimal cartons = 10m)
    {
        var detail = new ContainerLoadingDetail
        {
            LoadingListId = loadingListId,
            ProductId = 9001,
            ProductName = "保温杯",
            Quantity = quantity,
            Cartons = cartons,
            Weight = 500m,
            Volume = 3.5m
        };
        db.ContainerLoadingDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    /// <summary>直接落库一条参与方（构造历史 / 停用 / 主参与方等场景，绕过服务端校验）</summary>
    private static ContainerLoadingListParticipant SeedParticipant(
        ErpDbContext db, long loadingListId, long customerId, string code = "", string name = "",
        bool primary = false, int status = 1, bool deleted = false, int sortOrder = 0)
    {
        var participant = new ContainerLoadingListParticipant
        {
            LoadingListId = loadingListId,
            CustomerId = customerId,
            CustomerCode = code,
            CustomerName = name,
            IsPrimary = primary,
            Status = status,
            SortOrder = sortOrder,
            IsDeleted = deleted
        };
        db.ContainerLoadingListParticipants.Add(participant);
        db.SaveChanges();
        return participant;
    }

    private static ContainerLoadingParticipantSaveDto Save(
        long customerId, bool? primary = null, int? status = null, int? sortOrder = null, string remark = "")
        => new()
        {
            CustomerId = customerId,
            IsPrimary = primary,
            Status = status,
            SortOrder = sortOrder,
            Remark = remark
        };

    /// <summary>断言成功响应并取出数据（业务码必须为 0）</summary>
    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>断言成功响应（更新 / 删除类接口的 Data 为 null，只校验业务码）</summary>
    private static void AssertOkEmpty(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
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

    // ==================== 1. 新增：一柜多客户与主参与方 ====================

    [Fact]
    public async Task Create_一柜多客户_可维护多个参与方并显式指定主参与方()
    {
        using var db = TestDbFactory.Create();
        var legacyCustomer = SeedCustomer(db, "C001", "老客户");
        var customerA = SeedCustomer(db, "C002", "拼柜客户A");
        var customerB = SeedCustomer(db, "C003", "拼柜客户B");
        var list = SeedLoadingList(db, "ZG20260925001", legacyCustomer.Id);
        var ctl = BuildLoadingListController(db);

        var first = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerA.Id, primary: true, remark: "主客户")));
        var second = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerB.Id, sortOrder: 2)));

        // 客户编码 / 名称快照由服务端按客户主数据写入
        Assert.Equal(customerA.Id, first.CustomerId);
        Assert.Equal("C002", first.CustomerCode);
        Assert.Equal("拼柜客户A", first.CustomerName);
        Assert.True(first.IsPrimary);
        Assert.True(first.Selectable);
        Assert.False(second.IsPrimary);
        Assert.Equal("启用", second.StatusText);

        // 显式置主同步兼容客户字段（装柜清单原有 CustomerId）
        Assert.Equal(customerA.Id, db.ContainerLoadingLists.Single(o => o.Id == list.Id).CustomerId);

        var rows = AssertOk<List<ContainerLoadingParticipantDto>>(await ctl.GetParticipants(list.Id));
        Assert.Equal(2, rows.Count);
        var primary = Assert.Single(rows, r => r.IsPrimary);
        Assert.Equal(customerA.Id, primary.CustomerId);
    }

    [Fact]
    public async Task Create_同一客户重复参与被拒绝_停用记录同样占用唯一性()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "拼柜客户");
        var list = SeedLoadingList(db, "ZG20260925002", customer.Id);
        var ctl = BuildLoadingListController(db);

        AssertOk<ContainerLoadingParticipantDto>(await ctl.CreateParticipant(list.Id, Save(customer.Id)));

        var duplicate = await AssertBusinessAsync(
            ErrorCodes.Duplicate, () => ctl.CreateParticipant(list.Id, Save(customer.Id)));
        Assert.Contains("已存在参与方记录", duplicate.Message);

        // 停用后的记录仍然占用唯一性（恢复参与应直接启用原参与方）
        var rows = AssertOk<List<ContainerLoadingParticipantDto>>(await ctl.GetParticipants(list.Id));
        AssertOk<ContainerLoadingParticipantDto>(
            await ctl.DisableParticipant(list.Id, rows.Single().Id));

        await AssertBusinessAsync(
            ErrorCodes.Duplicate, () => ctl.CreateParticipant(list.Id, Save(customer.Id)));
    }

    [Fact]
    public async Task Create_客户不存在已删除或已停用_都不能新增参与方()
    {
        using var db = TestDbFactory.Create();
        var active = SeedCustomer(db, "C001", "在用客户");
        var disabled = SeedCustomer(db, "C002", "已停用客户", status: 0);
        var deleted = SeedCustomer(db, "C003", "已删除客户", deleted: true);
        var list = SeedLoadingList(db, "ZG20260925003", active.Id);
        var ctl = BuildLoadingListController(db);

        await AssertBusinessAsync(
            ErrorCodes.InvalidParameter, () => ctl.CreateParticipant(list.Id, Save(0)));
        await AssertBusinessAsync(
            ErrorCodes.NotFound, () => ctl.CreateParticipant(list.Id, Save(999999)));
        await AssertBusinessAsync(
            ErrorCodes.NotFound, () => ctl.CreateParticipant(list.Id, Save(deleted.Id)));

        var inactive = await AssertBusinessAsync(
            ErrorCodes.InvalidParameter, () => ctl.CreateParticipant(list.Id, Save(disabled.Id)));
        Assert.Contains("已停用", inactive.Message);

        // 拒绝后不产生任何参与方，也不占用提示中的历史行
        Assert.Empty(db.ContainerLoadingListParticipants.Where(x => x.LoadingListId == list.Id));
    }

    [Fact]
    public async Task Create_已存在启用主参与方时_不能新增第二个主参与方()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925005", customerA.Id);
        var ctl = BuildLoadingListController(db);

        AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerA.Id, primary: true)));

        var conflict = await AssertBusinessAsync(ErrorCodes.Duplicate, () =>
            ctl.CreateParticipant(list.Id, Save(customerB.Id, primary: true)));
        Assert.Contains("主参与方", conflict.Message);
        Assert.Equal(1, db.ContainerLoadingListParticipants.Count(x => x.LoadingListId == list.Id));
    }

    // ==================== 2. 主参与方：显式转移与兼容字段同步 ====================

    [Fact]
    public async Task SetPrimary_显式转移主参与方_与列表顺序无关且始终只有一条()
    {
        using var db = TestDbFactory.Create();
        var legacy = SeedCustomer(db, "C001", "老客户");
        var customerA = SeedCustomer(db, "C002", "客户A");
        var customerB = SeedCustomer(db, "C003", "客户B");
        var customerC = SeedCustomer(db, "C004", "客户C");
        var list = SeedLoadingList(db, "ZG20260925006", legacy.Id);
        var ctl = BuildLoadingListController(db);

        var participantA = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerA.Id, primary: true, sortOrder: 30)));
        var participantB = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerB.Id, sortOrder: 10)));
        var participantC = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerC.Id, sortOrder: 20)));

        // 转移到「排在中间」的参与方：与排序号 / 展示顺序无关
        var promotedB = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.SetPrimaryParticipant(list.Id, participantB.Id));
        Assert.True(promotedB.IsPrimary);
        Assert.Equal(customerB.Id, db.ContainerLoadingLists.Single(o => o.Id == list.Id).CustomerId);

        var afterB = AssertOk<List<ContainerLoadingParticipantDto>>(await ctl.GetParticipants(list.Id));
        Assert.Equal(customerB.Id, Assert.Single(afterB, r => r.IsPrimary).CustomerId);
        Assert.False(afterB.Single(r => r.CustomerId == customerA.Id).IsPrimary);

        // 再转移到第三个参与方：旧主参与方被释放，结果不依赖提交先后
        AssertOk<ContainerLoadingParticipantDto>(await ctl.SetPrimaryParticipant(list.Id, participantC.Id));
        var afterC = AssertOk<List<ContainerLoadingParticipantDto>>(await ctl.GetParticipants(list.Id));
        Assert.Equal(customerC.Id, Assert.Single(afterC, r => r.IsPrimary).CustomerId);
        Assert.Equal(customerC.Id, db.ContainerLoadingLists.Single(o => o.Id == list.Id).CustomerId);

        // 对已是主参与方的行重复置主是幂等的
        AssertOk<ContainerLoadingParticipantDto>(await ctl.SetPrimaryParticipant(list.Id, participantC.Id));
        var afterRepeat = AssertOk<List<ContainerLoadingParticipantDto>>(await ctl.GetParticipants(list.Id));
        Assert.Equal(customerC.Id, Assert.Single(afterRepeat, r => r.IsPrimary).CustomerId);
    }

    [Fact]
    public async Task Update_主参与方不能被直接取消_且停用或删除前必须先改指他人()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925007", customerA.Id);
        var ctl = BuildLoadingListController(db);

        var participantA = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerA.Id, primary: true)));
        var participantB = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerB.Id)));

        // 非主参与方显式 isPrimary=false：无变化，允许
        var unchanged = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.UpdateParticipant(list.Id, participantB.Id, Save(customerB.Id, primary: false, remark: "备用")));
        Assert.False(unchanged.IsPrimary);
        Assert.Equal("备用", unchanged.Remark);

        // 主参与方不能直接取消
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () =>
            ctl.UpdateParticipant(list.Id, participantA.Id, Save(customerA.Id, primary: false)));

        // 有别的启用参与方时，主参与方不能被停用 / 删除
        var disableBlocked = await AssertBusinessAsync(ErrorCodes.RuleConflict, () =>
            ctl.DisableParticipant(list.Id, participantA.Id));
        Assert.Contains("主参与方", disableBlocked.Message);
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () =>
            ctl.DeleteParticipant(list.Id, participantA.Id));
        Assert.Equal(customerA.Id, db.ContainerLoadingLists.Single(o => o.Id == list.Id).CustomerId);

        // 显式改指后即可停用原主参与方
        AssertOk<ContainerLoadingParticipantDto>(await ctl.SetPrimaryParticipant(list.Id, participantB.Id));
        var disabledA = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.DisableParticipant(list.Id, participantA.Id));
        Assert.Equal("停用", disabledA.StatusText);
        Assert.False(disabledA.Selectable);
        Assert.Equal(customerB.Id, db.ContainerLoadingLists.Single(o => o.Id == list.Id).CustomerId);
    }

    [Fact]
    public async Task Update_请求主参与方为真_视为显式置主并释放旧主()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925008", customerA.Id);
        var ctl = BuildLoadingListController(db);

        AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerA.Id, primary: true)));
        var participantB = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerB.Id)));

        var updated = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.UpdateParticipant(list.Id, participantB.Id, Save(customerB.Id, primary: true)));
        Assert.True(updated.IsPrimary);
        Assert.Equal(customerB.Id, db.ContainerLoadingLists.Single(o => o.Id == list.Id).CustomerId);

        var rows = AssertOk<List<ContainerLoadingParticipantDto>>(await ctl.GetParticipants(list.Id));
        Assert.Equal(customerB.Id, Assert.Single(rows, r => r.IsPrimary).CustomerId);

        // 停用状态的参与方不能被置主：先把落选的非主参与方停用，再请求置主
        var participantAId = rows.Single(r => r.CustomerId == customerA.Id).Id;
        AssertOk<ContainerLoadingParticipantDto>(await ctl.DisableParticipant(list.Id, participantAId));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.UpdateParticipant(list.Id, participantAId, Save(customerA.Id, primary: true, status: 0)));
    }

    // ==================== 3. 停用 / 删除与历史单客户视图 ====================

    [Fact]
    public async Task SetStatus_停用最后一个启用参与方_回退历史单客户视图且不清空兼容字段()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var list = SeedLoadingList(db, "ZG20260925009", customer.Id);
        var ctl = BuildLoadingListController(db);

        var participant = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customer.Id, primary: true)));
        Assert.Equal(customer.Id, db.ContainerLoadingLists.Single(o => o.Id == list.Id).CustomerId);

        // 唯一启用参与方可以停用（此后清单回到「没有启用参与方」的历史单客户视图）
        var disabled = AssertOk<ContainerLoadingParticipantDto>(await ctl.DisableParticipant(list.Id, participant.Id));
        Assert.False(disabled.IsPrimary);                                 // 停用释放主标记
        Assert.Equal(customer.Id, db.ContainerLoadingLists.Single(o => o.Id == list.Id).CustomerId);

        var active = AssertOk<List<ContainerLoadingParticipantDto>>(await ctl.GetParticipants(list.Id, true));
        Assert.Empty(active);

        // 客户后来停用：重新启用参与方被拒绝（重新启用等同于重新选用该客户）
        var stored = db.BaseCustomers.Single(c => c.Id == customer.Id);
        stored.Status = 0;
        db.SaveChanges();
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => ctl.EnableParticipant(list.Id, participant.Id));

        // 客户恢复后可重新启用，但不会自动恢复主参与方标记
        stored.Status = 1;
        db.SaveChanges();
        var enabled = AssertOk<ContainerLoadingParticipantDto>(await ctl.EnableParticipant(list.Id, participant.Id));
        Assert.False(enabled.IsPrimary);
        Assert.True(enabled.Selectable);
    }

    [Fact]
    public async Task Delete_软删除保留历史行_且不影响主参与方与兼容客户字段()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925010", customerA.Id);
        var ctl = BuildLoadingListController(db);

        AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerA.Id, primary: true)));
        var participantB = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerB.Id)));

        AssertOkEmpty(await ctl.DeleteParticipant(list.Id, participantB.Id));

        // 软删除：行保留（历史可读、不物理删除），并释放标记
        var deletedRow = db.ContainerLoadingListParticipants.Single(x => x.Id == participantB.Id);
        Assert.True(deletedRow.IsDeleted);
        Assert.False(deletedRow.IsPrimary);

        var rows = AssertOk<List<ContainerLoadingParticipantDto>>(await ctl.GetParticipants(list.Id));
        Assert.Equal(customerA.Id, Assert.Single(rows).CustomerId);
        Assert.Equal(customerA.Id, db.ContainerLoadingLists.Single(o => o.Id == list.Id).CustomerId);

        // 软删除行不占用唯一性：同一客户可以重新新增为参与方（历史行仍保留）
        var readded = AssertOk<ContainerLoadingParticipantDto>(await ctl.CreateParticipant(list.Id, Save(customerB.Id)));
        Assert.False(readded.IsPrimary);
        Assert.Equal(2, db.ContainerLoadingListParticipants
            .Count(x => x.LoadingListId == list.Id && !x.IsDeleted));       // A + 重新新增的 B
        Assert.Equal(3, db.ContainerLoadingListParticipants
            .Count(x => x.LoadingListId == list.Id));                       // 含已软删除的历史行
    }

    [Fact]
    public async Task Legacy_无参与方行的历史清单_按兼容字段只读为单客户视图且读取不写库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "历史客户");
        var list = SeedLoadingList(db, "ZG20260925011", customer.Id);
        SeedLoadingDetail(db, list.Id);                       // 历史装柜明细不受参与方逻辑影响
        var ctl = BuildLoadingListController(db);

        var detail = AssertOk<ContainerLoadingList>(await ctl.GetById(list.Id));
        Assert.True(detail.LegacySingleCustomer);             // 显式标记为历史单客户视图
        Assert.Equal(0, detail.ParticipantCount);
        Assert.Equal(0, detail.ParticipantTotalCount);
        Assert.Null(detail.PrimaryParticipantCustomerId);
        Assert.Equal(string.Empty, detail.PrimaryParticipantCustomerName);
        Assert.Empty(detail.Participants);
        Assert.Equal(customer.Id, detail.CustomerId);         // 客户身份仍来自兼容字段
        Assert.Single(detail.Details);

        var page = AssertOk<PagedResult<ContainerLoadingList>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 20 }, null));
        var row = Assert.Single(page.Items);
        Assert.True(row.LegacySingleCustomer);
        Assert.Equal(0, row.ParticipantCount);

        // 读取路径不写库：不产生任何参与方行、不改写兼容客户字段（无自动回填、无请求时写入）
        Assert.Empty(db.ContainerLoadingListParticipants);
        Assert.Equal(customer.Id, db.ContainerLoadingLists.Single(o => o.Id == list.Id).CustomerId);

        var rows = AssertOk<List<ContainerLoadingParticipantDto>>(await ctl.GetParticipants(list.Id));
        Assert.Empty(rows);
    }

    // ==================== 4. 读取标注、不可用引用与有界列表 ====================

    [Fact]
    public async Task Annotate_列表与详情标注参与方计数_主参与方与主客户不可用()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var customerC = SeedCustomer(db, "C003", "客户C");
        var list = SeedLoadingList(db, "ZG20260925012", customerA.Id);
        var ctl = BuildLoadingListController(db);

        AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerA.Id, primary: true)));
        AssertOk<ContainerLoadingParticipantDto>(await ctl.CreateParticipant(list.Id, Save(customerB.Id)));
        var participantC = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerC.Id)));
        AssertOk<ContainerLoadingParticipantDto>(await ctl.DisableParticipant(list.Id, participantC.Id));

        var row = Assert.Single(AssertOk<PagedResult<ContainerLoadingList>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 20 }, null)).Items);
        Assert.False(row.LegacySingleCustomer);
        Assert.Equal(2, row.ParticipantCount);                 // 启用中的参与方
        Assert.Equal(3, row.ParticipantTotalCount);            // 含停用
        Assert.Equal(customerA.Id, row.PrimaryParticipantCustomerId);
        Assert.Equal("客户A", row.PrimaryParticipantCustomerName);
        Assert.True(row.PrimaryParticipantAvailable);

        var detail = AssertOk<ContainerLoadingList>(await ctl.GetById(list.Id));
        Assert.Equal(3, detail.Participants.Count);
        Assert.All(detail.Participants, p => Assert.True(p.CustomerAvailable));
        Assert.Equal(2, detail.ParticipantCount);

        // 主参与方客户后来被停用：名称快照照常显示，但显式标注不可用（不静默替换）
        db.BaseCustomers.Single(c => c.Id == customerA.Id).Status = 0;
        db.SaveChanges();

        var afterDisable = Assert.Single(AssertOk<PagedResult<ContainerLoadingList>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 20 }, null)).Items);
        Assert.Equal("客户A", afterDisable.PrimaryParticipantCustomerName);
        Assert.False(afterDisable.PrimaryParticipantAvailable);
    }

    [Fact]
    public async Task 客户改名或删除_历史参与方仍可读且快照不被静默改写()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "旧名称");
        var list = SeedLoadingList(db, "ZG20260925013", customer.Id);
        var ctl = BuildLoadingListController(db);

        var participant = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customer.Id)));

        // 客户改名：快照保留原名称，当前名称单独暴露并标注差异
        customer.CustomerName = "新名称";
        db.SaveChanges();

        var renamed = Assert.Single(AssertOk<List<ContainerLoadingParticipantDto>>(await ctl.GetParticipants(list.Id)));
        Assert.Equal("旧名称", renamed.CustomerName);          // 快照不被静默改写
        Assert.Equal("新名称", renamed.CustomerCurrentName);
        Assert.True(renamed.CustomerRenamed);
        Assert.True(renamed.CustomerAvailable);

        // 引用未变更时仍可编辑备注（历史参与方保持可维护，快照保持不动）
        var edited = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.UpdateParticipant(list.Id, participant.Id, Save(customer.Id, remark: "仍在合作")));
        Assert.Equal("旧名称", edited.CustomerName);
        Assert.Equal("仍在合作", edited.Remark);

        // 客户被软删除：历史参与方仍可读并显式标注不可用，但不能被设为主参与方
        customer.IsDeleted = true;
        db.SaveChanges();

        var unavailable = Assert.Single(
            AssertOk<List<ContainerLoadingParticipantDto>>(await ctl.GetParticipants(list.Id)));
        Assert.False(unavailable.CustomerAvailable);
        Assert.Equal("旧名称", unavailable.CustomerName);
        Assert.Contains("已停用", unavailable.AvailabilityText);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.SetPrimaryParticipant(list.Id, participant.Id));

        // 更换到不存在的客户被拒绝（新增 / 更换必须是可用客户）
        await AssertBusinessAsync(ErrorCodes.NotFound, () =>
            ctl.UpdateParticipant(list.Id, participant.Id, Save(999999)));
    }

    [Fact]
    public async Task Bound_超过参与方上限被拒绝_且读取按上限收敛()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "在用客户");
        var list = SeedLoadingList(db, "ZG20260925014", customer.Id);
        var ctl = BuildLoadingListController(db);

        for (var i = 0; i < ContainerLoadingParticipantRules.MaxParticipantsPerLoadingList; i++)
            SeedParticipant(db, list.Id, 100000 + i, name: $"历史客户{i + 1}");

        var extra = SeedCustomer(db, "C900", "第 101 个客户");
        var bound = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            ctl.CreateParticipant(list.Id, Save(extra.Id)));
        Assert.Contains("上限", bound.Message);

        var rows = AssertOk<List<ContainerLoadingParticipantDto>>(await ctl.GetParticipants(list.Id));
        Assert.Equal(ContainerLoadingParticipantRules.MaxParticipantsPerLoadingList, rows.Count);
    }

    [Fact]
    public async Task Status_已作废清单拒绝维护但可读_非作废状态仍可维护()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925015", customer.Id);
        var ctl = BuildLoadingListController(db);

        var participant = SeedParticipant(db, list.Id, customer.Id, "C001", "客户A", primary: true);

        // 已审核的历史柜仍可维护参与方（拼柜客户可能事后补充 / 纠正），唯一同步的是兼容客户字段
        list.Status = DocumentStatus.Approved;
        db.SaveChanges();

        var created = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerB.Id)));
        Assert.Equal(customerB.Id, created.CustomerId);
        Assert.True(AssertOk<ContainerLoadingParticipantDto>(
            await ctl.SetPrimaryParticipant(list.Id, created.Id)).IsPrimary);
        Assert.Equal(customerB.Id, db.ContainerLoadingLists.Single(o => o.Id == list.Id).CustomerId);

        // 已作废：客户归属冻结，拒绝维护，但历史参与方仍可读
        list.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        var rows = AssertOk<List<ContainerLoadingParticipantDto>>(await ctl.GetParticipants(list.Id));
        Assert.Equal(2, rows.Count);

        var blocked = await AssertBusinessAsync(ErrorCodes.RuleConflict, () =>
            ctl.CreateParticipant(list.Id, Save(customerB.Id)));
        Assert.Contains("已作废", blocked.Message);
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () =>
            ctl.UpdateParticipant(list.Id, participant.Id, Save(customer.Id, remark: "x")));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () =>
            ctl.SetPrimaryParticipant(list.Id, participant.Id));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () =>
            ctl.DisableParticipant(list.Id, created.Id));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () =>
            ctl.DeleteParticipant(list.Id, created.Id));

        var detail = AssertOk<ContainerLoadingList>(await ctl.GetById(list.Id));
        Assert.Equal(2, detail.Participants.Count);
    }

    // ==================== 5. 边界：不改写相邻数据 ====================

    [Fact]
    public async Task NotMutating_维护参与方不改写装柜明细_单证_费用_库存与客户主数据()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "客户A");
        var customerB = SeedCustomer(db, "C002", "客户B");
        var list = SeedLoadingList(db, "ZG20260925016", customerA.Id);
        var detail = SeedLoadingDetail(db, list.Id);

        db.TradeDocuments.Add(new TradeDocument
        {
            DocNo = "DOC-001",
            DocType = "装箱单",
            RefNo = list.ContainerNo,
            CustomerId = customerA.Id,
            CustomerName = "客户A",
            Amount = 1000m
        });
        db.FinanceExpenses.Add(new FinanceExpense
        {
            ExpenseNo = "FY-001",
            ExpenseType = "报关费",
            Amount = 500m,
            RefType = "整柜",
            RefNo = list.ContainerNo,
            CustomerId = customerA.Id,
            CustomerName = "客户A",
            AllocationBase = "体积",
            AllocatedAmount = 500m
        });
        db.Stocks.Add(new Stock
        {
            WarehouseId = 1,
            ProductId = 9001,
            Quantity = 10m,
            AvailableQuantity = 10m,
            AverageCost = 5m,
            TotalCost = 50m
        });
        db.SaveChanges();

        var ctl = BuildLoadingListController(db);
        var participantA = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerA.Id, primary: true)));
        var participantB = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(customerB.Id)));
        AssertOk<ContainerLoadingParticipantDto>(await ctl.SetPrimaryParticipant(list.Id, participantB.Id));
        AssertOk<ContainerLoadingParticipantDto>(
            await ctl.UpdateParticipant(list.Id, participantB.Id, Save(customerB.Id, remark: "改主客户")));
        AssertOk<ContainerLoadingParticipantDto>(await ctl.DisableParticipant(list.Id, participantA.Id));
        AssertOk<ContainerLoadingParticipantDto>(await ctl.EnableParticipant(list.Id, participantA.Id));
        AssertOkEmpty(await ctl.DeleteParticipant(list.Id, participantA.Id));

        // 装柜明细、单证、费用、库存与客户主数据全部保持不变
        var storedDetail = db.ContainerLoadingDetails.Single(x => x.Id == detail.Id);
        Assert.Equal(100m, storedDetail.Quantity);
        Assert.Equal(10m, storedDetail.Cartons);
        Assert.Equal(500m, storedDetail.Weight);
        Assert.Equal(3.5m, storedDetail.Volume);

        Assert.Equal("DOC-001", db.TradeDocuments.Single().DocNo);
        Assert.Equal(1000m, db.TradeDocuments.Single().Amount);
        Assert.Equal(500m, db.FinanceExpenses.Single().AllocatedAmount);
        Assert.Equal("体积", db.FinanceExpenses.Single().AllocationBase);
        Assert.Equal(10m, db.Stocks.Single().Quantity);
        Assert.Equal(50m, db.Stocks.Single().TotalCost);
        Assert.Empty(db.StockMovements);
        Assert.Equal("客户A", db.BaseCustomers.Single(c => c.Id == customerA.Id).CustomerName);
        Assert.Equal("C002", db.BaseCustomers.Single(c => c.Id == customerB.Id).CustomerCode);

        // 装柜清单的容器 / 单据身份与其他字段不变，只有兼容客户字段按显式置主同步
        var storedList = db.ContainerLoadingLists.Single(o => o.Id == list.Id);
        Assert.Equal("TCLU-001", storedList.ContainerNo);
        Assert.Equal("ZG20260925016", storedList.LoadingListNo);
        Assert.Equal(DocumentStatus.Pending, storedList.Status);
        Assert.Equal(customerB.Id, storedList.CustomerId);
    }

    // ==================== 6. 模型 / 幂等结构 / 前端接线契约 ====================

    [Fact]
    public void Model_参与方索引与过滤条件与建表脚本口径一致()
    {
        using var db = TestDbFactory.Create();
        var entity = db.Model.FindEntityType(typeof(ContainerLoadingListParticipant));
        Assert.NotNull(entity);

        var listCustomer = Assert.Single(entity!.GetIndexes(), i =>
            i.IsUnique && i.Properties.Count == 2
            && i.Properties.Any(p => p.Name == nameof(ContainerLoadingListParticipant.LoadingListId))
            && i.Properties.Any(p => p.Name == nameof(ContainerLoadingListParticipant.CustomerId)));
        Assert.Equal("UX_ContainerLoadingListParticipants_ListCustomer", listCustomer.GetDatabaseName());
        Assert.Contains("IsDeleted = 0", listCustomer.GetFilter());

        var listPrimary = Assert.Single(entity.GetIndexes(), i =>
            i.IsUnique && i.Properties.Count == 1
            && i.Properties[0].Name == nameof(ContainerLoadingListParticipant.LoadingListId));
        Assert.Equal("UX_ContainerLoadingListParticipants_ListPrimary", listPrimary.GetDatabaseName());
        Assert.Contains("Status = 1", listPrimary.GetFilter());              // 停用行不占用主参与方位
        Assert.Contains("IsPrimary = 1", listPrimary.GetFilter());

        var customerIndex = Assert.Single(entity.GetIndexes(), i =>
            i.Properties.Count == 1
            && i.Properties[0].Name == nameof(ContainerLoadingListParticipant.CustomerId));
        Assert.False(customerIndex.IsUnique);
        Assert.Equal("IX_ContainerLoadingListParticipants_CustomerId", customerIndex.GetDatabaseName());

        // 只有「装柜清单子表」这一条关系（级联清理）；**刻意不建客户外键**——
        // 客户软删除 / 停用后历史参与方必须继续可读
        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(nameof(ContainerLoadingListParticipant.LoadingListId), foreignKey.Properties.Single().Name);
        Assert.Equal(nameof(ContainerLoadingList), foreignKey.PrincipalEntityType.ClrType.Name);

        // 快照列长度与实体 / 建表脚本一致
        Assert.Equal(50, entity.FindProperty(nameof(ContainerLoadingListParticipant.CustomerCode))!.GetMaxLength());
        Assert.Equal(200, entity.FindProperty(nameof(ContainerLoadingListParticipant.CustomerName))!.GetMaxLength());
        Assert.Equal(500, entity.FindProperty(nameof(ContainerLoadingListParticipant.Remark))!.GetMaxLength());
    }

    [Fact]
    public void Schema_upgrade_幂等建表建索引且不做任何回填()
    {
        var script = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.ContainerLoadingListParticipants') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.ContainerLoadingListParticipants", script);
        Assert.Contains("LoadingListId BIGINT NOT NULL", script);
        Assert.Contains("CustomerCode NVARCHAR(50) NOT NULL DEFAULT N''", script);
        Assert.Contains("CustomerName NVARCHAR(200) NOT NULL DEFAULT N''", script);
        Assert.Contains("IsPrimary BIT NOT NULL DEFAULT 0", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_ContainerLoadingListParticipants_ListCustomer", script);
        Assert.Contains("ON db_owner.ContainerLoadingListParticipants(LoadingListId, CustomerId)", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_ContainerLoadingListParticipants_ListPrimary", script);
        Assert.Contains("ON db_owner.ContainerLoadingListParticipants(LoadingListId)", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status = 1 AND IsPrimary = 1;", script);
        Assert.Contains("CREATE INDEX IX_ContainerLoadingListParticipants_CustomerId", script);

        // 幂等结构：不得出现任何按参与方回填 / 改写装柜清单、明细、单证、费用或库存的语句
        Assert.DoesNotContain("UPDATE db_owner.ContainerLoadingListParticipants", script);
        Assert.DoesNotContain("INSERT INTO db_owner.ContainerLoadingListParticipants", script);
        Assert.DoesNotContain("UPDATE db_owner.ContainerLoadingLists", script);
        Assert.DoesNotContain("UPDATE db_owner.ContainerLoadingDetail", script);
        Assert.DoesNotContain("UPDATE db_owner.TradeDocuments", script);
        Assert.DoesNotContain("UPDATE db_owner.FinanceExpenses", script);
        Assert.DoesNotContain("UPDATE db_owner.Stocks", script);
    }

    [Fact]
    public void Frontend_列表列与行操作已接线且脚本已注册()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/loading-list-participants.js", index);

        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc2.js"));
        Assert.Contains("label: '客户参与方'", modules);
        Assert.Contains("loadingListParticipantCellHtml", modules);
        Assert.Contains("openLoadingListParticipants", modules);

        var script = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "loading-list-participants.js"));
        Assert.Contains("async function openLoadingListParticipants", script);
        Assert.Contains("/participants", script);
        Assert.Contains("/primary", script);
        // 界面明确声明边界：只维护客户归属，不按数量 / 体积 / 金额分摊费用，也不改动装柜明细等相邻数据
        Assert.Contains("分摊费用", script);
        Assert.Contains("不改动装柜明细", script);

        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "ContainerLoadingListController.cs"));
        Assert.Contains("[HttpPost(\"{id:long}/participants\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/participants/{participantId:long}/primary\")]", controller);
        Assert.Contains("[HttpDelete(\"{id:long}/participants/{participantId:long}\")]", controller);
    }

    // ==================== 7. 纯规则 ====================

    [Fact]
    public void Rules_规范化与校验边界()
    {
        Assert.Equal(ContainerLoadingParticipantRules.ActiveStatus,
            ContainerLoadingParticipantRules.NormalizeStatus(null));
        Assert.Equal(ContainerLoadingParticipantRules.DisabledStatus,
            ContainerLoadingParticipantRules.NormalizeStatus(0));
        Assert.Throws<BusinessException>(() => ContainerLoadingParticipantRules.NormalizeStatus(2));
        Assert.Throws<BusinessException>(() => ContainerLoadingParticipantRules.NormalizeStatus(-1));

        Assert.Equal(0, ContainerLoadingParticipantRules.NormalizeSortOrder(null));
        Assert.Equal(5, ContainerLoadingParticipantRules.NormalizeSortOrder(5));
        Assert.Throws<BusinessException>(() => ContainerLoadingParticipantRules.NormalizeSortOrder(-1));
        Assert.Throws<BusinessException>(() => ContainerLoadingParticipantRules.NormalizeSortOrder(
            ContainerLoadingParticipantRules.MaxSortOrder + 1));

        Assert.Equal("主 客户", ContainerLoadingParticipantRules.NormalizeRemark("  主   客户  "));
        Assert.Throws<BusinessException>(() => ContainerLoadingParticipantRules.NormalizeRemark(
            new string('x', ContainerLoadingParticipantRules.MaxRemarkLength + 1)));

        ContainerLoadingParticipantRules.EnsureParticipantBound(0);          // 未达上限不抛
        Assert.Throws<BusinessException>(() => ContainerLoadingParticipantRules.EnsureParticipantBound(
            ContainerLoadingParticipantRules.MaxParticipantsPerLoadingList));

        Assert.Throws<BusinessException>(() =>
            ContainerLoadingParticipantRules.EnsurePrimaryCompatible(isPrimary: true, status: 0));
        ContainerLoadingParticipantRules.EnsurePrimaryCompatible(isPrimary: false, status: 0);
    }

    [Fact]
    public void Rules_可用性与展示文案_停用或缺引用都显式标注()
    {
        var disabled = new ContainerLoadingListParticipant { Id = 1, Status = 0, IsPrimary = true };
        Assert.False(ContainerLoadingParticipantRules.IsSelectable(disabled));
        Assert.False(ContainerLoadingParticipantRules.IsPrimaryActive(disabled));   // 停用行不占用主参与方位

        var active = new ContainerLoadingListParticipant { Id = 2, Status = 1 };
        Assert.True(ContainerLoadingParticipantRules.IsSelectable(active));
        Assert.False(ContainerLoadingParticipantRules.IsPrimaryActive(active));
        active.IsPrimary = true;
        Assert.True(ContainerLoadingParticipantRules.IsPrimaryActive(active));

        var deleted = new ContainerLoadingListParticipant { Id = 3, Status = 1, IsPrimary = true, IsDeleted = true };
        Assert.False(ContainerLoadingParticipantRules.IsSelectable(deleted));

        Assert.Equal(ContainerLoadingParticipantRules.UnavailableMark,
            ContainerLoadingParticipantRules.MarkUnavailable("  "));
        Assert.Equal($"义乌客户{ContainerLoadingParticipantRules.UnavailableMark}",
            ContainerLoadingParticipantRules.MarkUnavailable(" 义乌客户 "));

        Assert.Equal("可选用", ContainerLoadingParticipantRules.AvailabilityText(true, true));
        Assert.Contains("已停用", ContainerLoadingParticipantRules.AvailabilityText(false, true));
        Assert.Contains(ContainerLoadingParticipantRules.UnavailableMark,
            ContainerLoadingParticipantRules.AvailabilityText(true, false));

        Assert.Equal("客户#7", ContainerLoadingParticipantRules.DisplayName("", "", 7));
        Assert.Equal("C007", ContainerLoadingParticipantRules.DisplayName(null, " C007 ", 7));
        Assert.Equal(ContainerLoadingParticipantRules.PrimaryText, ContainerLoadingParticipantRules.PrimaryTextOf(true));
        Assert.Equal("—", ContainerLoadingParticipantRules.PrimaryTextOf(false));
    }
}
