using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 客户「指定货代」单元测试（ERP-036）。
/// 覆盖：新增 / 更换 / 清空、无效引用（不存在 / 已删除 / 已停用 / 类型不符）、
/// 字典项停用或删除后历史引用的可读性与显式标注、老客户兼容，以及「不触达其他单据」的边界。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何部署 / SQL 脚本。
/// </summary>
public class CustomerForwarderTests
{
    // ============ 新增 ============

    [Fact]
    public async Task Create_指定启用的货代_服务端写入名称快照且忽略客户端自由文本()
    {
        using var db = TestDbFactory.Create();
        var forwarder = SeedOtherInfo(db, "Forwarder", "FD001", "宁波海通货代");
        var ctl = BuildController(db);

        var result = await ctl.Create(new BaseCustomer
        {
            CustomerCode = "C001",
            CustomerName = "测试客户",
            ForwarderId = forwarder.Id,
            ForwarderName = "客户端随手填的货代名"     // 自由文本不得被采信
        });

        var data = AssertOk<BaseCustomer>(result);
        Assert.Equal(forwarder.Id, data.ForwarderId);
        Assert.Equal("宁波海通货代", data.ForwarderName);
        Assert.True(data.ForwarderAvailable);

        var stored = db.BaseCustomers.Single();
        Assert.Equal(forwarder.Id, stored.ForwarderId);
        Assert.Equal("宁波海通货代", stored.ForwarderName);
    }

    [Fact]
    public async Task Create_只提交自由文本货代名_不构成权威引用()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db);

        var result = await ctl.Create(new BaseCustomer
        {
            CustomerCode = "C002",
            CustomerName = "客户2",
            ForwarderName = "只有名字、没有引用 Id"
        });

        var data = AssertOk<BaseCustomer>(result);
        Assert.Null(data.ForwarderId);
        Assert.Equal(string.Empty, data.ForwarderName);
        Assert.Equal(string.Empty, db.BaseCustomers.Single().ForwarderName);
    }

    [Fact]
    public async Task Create_货代Id不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new BaseCustomer
        {
            CustomerCode = "C003",
            CustomerName = "客户3",
            ForwarderId = 999999L
        }));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.BaseCustomers);
    }

    [Fact]
    public async Task Create_货代已被软删除_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var forwarder = SeedOtherInfo(db, "Forwarder", "FD001", "已删除货代", deleted: true);
        var ctl = BuildController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new BaseCustomer
        {
            CustomerCode = "C004",
            CustomerName = "客户4",
            ForwarderId = forwarder.Id
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("已被删除", ex.Message);
        Assert.Empty(db.BaseCustomers);
    }

    [Fact]
    public async Task Create_货代已停用_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var forwarder = SeedOtherInfo(db, "Forwarder", "FD001", "已停用货代", status: 0);
        var ctl = BuildController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new BaseCustomer
        {
            CustomerCode = "C005",
            CustomerName = "客户5",
            ForwarderId = forwarder.Id
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("已停用", ex.Message);
        Assert.Empty(db.BaseCustomers);
    }

    [Fact]
    public async Task Create_字典项类型不是Forwarder_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var port = SeedOtherInfo(db, "Port", "PT001", "宁波港");
        var ctl = BuildController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new BaseCustomer
        {
            CustomerCode = "C006",
            CustomerName = "客户6",
            ForwarderId = port.Id
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("不是货代", ex.Message);
        Assert.Empty(db.BaseCustomers);
    }

    // ============ 更新 / 更换 / 清空 ============

    [Fact]
    public async Task Update_更换货代_写入新名称快照()
    {
        using var db = TestDbFactory.Create();
        var old = SeedOtherInfo(db, "Forwarder", "FD001", "原货代");
        var fresh = SeedOtherInfo(db, "Forwarder", "FD002", "新货代");
        var customer = SeedCustomer(db, "C010", "客户10", old.Id, "原货代");
        var ctl = BuildController(db);

        var result = await ctl.Update(customer.Id, new BaseCustomer
        {
            CustomerCode = "C010",
            CustomerName = "客户10",
            ForwarderId = fresh.Id,
            ForwarderName = "企图覆盖的名称"
        });

        var data = AssertOk<BaseCustomer>(result);
        Assert.Equal(fresh.Id, data.ForwarderId);
        Assert.Equal("新货代", data.ForwarderName);
        Assert.True(data.ForwarderAvailable);

        var stored = db.BaseCustomers.Single();
        Assert.Equal(fresh.Id, stored.ForwarderId);
        Assert.Equal("新货代", stored.ForwarderName);
    }

    [Fact]
    public async Task Update_更换为已停用货代_拒绝且原引用不变()
    {
        using var db = TestDbFactory.Create();
        var active = SeedOtherInfo(db, "Forwarder", "FD001", "在用货代");
        var inactive = SeedOtherInfo(db, "Forwarder", "FD002", "停用货代", status: 0);
        var customer = SeedCustomer(db, "C011", "客户11", active.Id, "在用货代");
        var ctl = BuildController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(customer.Id, new BaseCustomer
        {
            CustomerCode = "C011",
            CustomerName = "客户11",
            ForwarderId = inactive.Id
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("已停用", ex.Message);

        var stored = db.BaseCustomers.Single();
        Assert.Equal(active.Id, stored.ForwarderId);
        Assert.Equal("在用货代", stored.ForwarderName);
    }

    [Fact]
    public async Task Update_更换为类型不符字典项_拒绝()
    {
        using var db = TestDbFactory.Create();
        var forwarder = SeedOtherInfo(db, "Forwarder", "FD001", "在用货代");
        var mark = SeedOtherInfo(db, "ShippingMark", "SM001", "唛头模板");
        var customer = SeedCustomer(db, "C012", "客户12", forwarder.Id, "在用货代");
        var ctl = BuildController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(customer.Id, new BaseCustomer
        {
            CustomerCode = "C012",
            CustomerName = "客户12",
            ForwarderId = mark.Id
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(forwarder.Id, db.BaseCustomers.Single().ForwarderId);
    }

    [Fact]
    public async Task Update_清空货代_两字段清空且不触达其他单据()
    {
        using var db = TestDbFactory.Create();
        var forwarder = SeedOtherInfo(db, "Forwarder", "FD001", "货代A");
        var customer = SeedCustomer(db, "C013", "客户13", forwarder.Id, "货代A");
        var ctl = BuildController(db);

        var result = await ctl.Update(customer.Id, new BaseCustomer
        {
            CustomerCode = "C013",
            CustomerName = "客户13",
            ForwarderId = null,
            ForwarderName = "清空时也不许写自由文本"
        });

        var data = AssertOk<BaseCustomer>(result);
        Assert.Null(data.ForwarderId);
        Assert.Equal(string.Empty, data.ForwarderName);
        Assert.True(data.ForwarderAvailable);

        var stored = db.BaseCustomers.Single();
        Assert.Null(stored.ForwarderId);
        Assert.Equal(string.Empty, stored.ForwarderName);

        // 边界：指定货代只写客户资料自身两列，不清、不建任何订舱 / 装柜 / 费用 / 单证记录
        Assert.Empty(db.ContainerBookings);
        Assert.Empty(db.ContainerLoadingLists);
        Assert.Empty(db.FinanceExpenses);
        Assert.Empty(db.TradeDocuments);
    }

    [Fact]
    public async Task Update_货代Id传0_等同清空()
    {
        using var db = TestDbFactory.Create();
        var forwarder = SeedOtherInfo(db, "Forwarder", "FD001", "货代B");
        var customer = SeedCustomer(db, "C014", "客户14", forwarder.Id, "货代B");
        var ctl = BuildController(db);

        var result = await ctl.Update(customer.Id, new BaseCustomer
        {
            CustomerCode = "C014",
            CustomerName = "客户14",
            ForwarderId = 0
        });

        var data = AssertOk<BaseCustomer>(result);
        Assert.Null(data.ForwarderId);
        Assert.Equal(string.Empty, data.ForwarderName);
        Assert.Null(db.BaseCustomers.Single().ForwarderId);
    }

    [Fact]
    public async Task Update_客户不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var forwarder = SeedOtherInfo(db, "Forwarder", "FD001", "货代C");
        var ctl = BuildController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(999999L, new BaseCustomer
        {
            CustomerCode = "C015",
            CustomerName = "客户15",
            ForwarderId = forwarder.Id
        }));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.BaseCustomers);
    }

    // ============ 字典项停用 / 删除后的历史引用 ============

    [Fact]
    public async Task Update_引用未变更但字典项已停用_保留历史快照且标注不可用()
    {
        using var db = TestDbFactory.Create();
        var forwarder = SeedOtherInfo(db, "Forwarder", "FD001", "历史货代");
        var customer = SeedCustomer(db, "C020", "客户20", forwarder.Id, "历史货代");

        // 字典项被停用（历史客户资料不许被动清空、改写，也不许因此读不出来）
        forwarder.Status = 0;
        db.SaveChanges();

        var ctl = BuildController(db);
        var result = await ctl.Update(customer.Id, new BaseCustomer
        {
            CustomerCode = "C020",
            CustomerName = "客户20",
            Remark = "只改备注",
            ForwarderId = forwarder.Id,
            ForwarderName = "借机改写历史名称"
        });

        var data = AssertOk<BaseCustomer>(result);
        Assert.Equal(forwarder.Id, data.ForwarderId);
        Assert.Equal("历史货代", data.ForwarderName);
        Assert.False(data.ForwarderAvailable);

        var read = AssertOk<BaseCustomer>(await ctl.GetById(customer.Id));
        Assert.Equal("历史货代", read.ForwarderName);
        Assert.False(read.ForwarderAvailable);
        Assert.Equal("只改备注", read.Remark);
        Assert.Equal("历史货代", db.BaseCustomers.Single().ForwarderName);
    }

    [Fact]
    public async Task GetById_引用字典项已软删除_读取不报错且标注不可用()
    {
        using var db = TestDbFactory.Create();
        var forwarder = SeedOtherInfo(db, "Forwarder", "FD001", "已删字典货代");
        var customer = SeedCustomer(db, "C021", "客户21", forwarder.Id, "已删字典货代");

        forwarder.IsDeleted = true;
        db.SaveChanges();

        var ctl = BuildController(db);
        var read = AssertOk<BaseCustomer>(await ctl.GetById(customer.Id));

        Assert.Equal(forwarder.Id, read.ForwarderId);
        Assert.Equal("已删字典货代", read.ForwarderName);
        Assert.False(read.ForwarderAvailable);

        // 已删除的字典项不能再被新指定
        var other = SeedCustomer(db, "C022", "客户22");
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(other.Id, new BaseCustomer
        {
            CustomerCode = "C022",
            CustomerName = "客户22",
            ForwarderId = forwarder.Id
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("已被删除", ex.Message);
    }

    // ============ 老客户兼容与列表标注 ============

    [Fact]
    public async Task GetById_未指定货代的老客户_读取与保存均正常()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C030", "老客户");    // 历史数据：无指定货代
        var ctl = BuildController(db);

        var read = AssertOk<BaseCustomer>(await ctl.GetById(customer.Id));
        Assert.Null(read.ForwarderId);
        Assert.Equal(string.Empty, read.ForwarderName);
        Assert.True(read.ForwarderAvailable);

        var updated = AssertOk<BaseCustomer>(await ctl.Update(customer.Id, new BaseCustomer
        {
            CustomerCode = "C030",
            CustomerName = "老客户改名"
        }));

        Assert.Null(updated.ForwarderId);
        Assert.Equal(string.Empty, updated.ForwarderName);
        var stored = db.BaseCustomers.Single();
        Assert.Equal("老客户改名", stored.CustomerName);
        Assert.Null(stored.ForwarderId);
    }

    [Fact]
    public async Task GetPaged_按行标注货代可用性且不写库()
    {
        using var db = TestDbFactory.Create();
        var active = SeedOtherInfo(db, "Forwarder", "FD001", "可用货代");
        var inactive = SeedOtherInfo(db, "Forwarder", "FD002", "不可用货代", status: 0);
        SeedCustomer(db, "C031", "客户31", active.Id, "可用货代");
        SeedCustomer(db, "C032", "客户32", inactive.Id, "不可用货代");
        SeedCustomer(db, "C033", "客户33");
        var ctl = BuildController(db);

        var ok = Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }));
        var resp = Assert.IsType<ApiResponse<PagedResult<BaseCustomer>>>(ok.Value);
        var items = resp.Data!.Items;

        Assert.Equal(3, items.Count);
        var c31 = items.Single(x => x.CustomerCode == "C031");
        Assert.Equal("可用货代", c31.ForwarderName);
        Assert.True(c31.ForwarderAvailable);

        var c32 = items.Single(x => x.CustomerCode == "C032");
        Assert.Equal("不可用货代", c32.ForwarderName);
        Assert.False(c32.ForwarderAvailable);

        var c33 = items.Single(x => x.CustomerCode == "C033");
        Assert.Null(c33.ForwarderId);
        Assert.True(c33.ForwarderAvailable);

        // 标注只影响返回对象，不写库（历史引用仍保留原值）
        Assert.Equal(inactive.Id, db.BaseCustomers.Single(x => x.CustomerCode == "C032").ForwarderId);
    }

    // ============ 下拉选项 ============

    [Fact]
    public async Task GetForwarderOptions_只返回启用未删除的货代字典项()
    {
        using var db = TestDbFactory.Create();
        var second = SeedOtherInfo(db, "Forwarder", "FD001", "宁波海通货代", sortOrder: 2);
        var first = SeedOtherInfo(db, "Forwarder", "FD002", "上海远洋货代", sortOrder: 1);
        SeedOtherInfo(db, "Forwarder", "FD003", "已停用货代", status: 0);
        SeedOtherInfo(db, "Forwarder", "FD004", "已删除货代", deleted: true);
        SeedOtherInfo(db, "Port", "PT001", "宁波港");
        var caseVariant = SeedOtherInfo(db, "forwarder", "FD005", "类型大小写差异货代", sortOrder: 3);
        var ctl = BuildController(db);

        var ok = Assert.IsType<OkObjectResult>(await ctl.GetForwarderOptions());
        var resp = Assert.IsType<ApiResponse<List<OtherInfoOptionDto>>>(ok.Value);
        var options = resp.Data!;

        Assert.Equal(3, options.Count);
        Assert.All(options, o => Assert.True(o.Selectable));
        Assert.Equal(
            new[] { "上海远洋货代", "宁波海通货代", "类型大小写差异货代" },
            options.Select(o => o.InfoName).ToArray());
        Assert.Equal(first.Id, options[0].Id);
        Assert.Equal(second.Id, options[1].Id);
        Assert.Equal(caseVariant.Id, options[2].Id);
    }

    // ============ 纯规则 ============

    [Fact]
    public void Rules_资料类型比较忽略大小写与首尾空白()
    {
        Assert.True(CustomerForwarderRules.IsType(" forwarder ", CustomerForwarderRules.ForwarderInfoType));
        Assert.False(CustomerForwarderRules.IsType("Port", CustomerForwarderRules.ForwarderInfoType));
        Assert.False(CustomerForwarderRules.IsType(null, CustomerForwarderRules.ForwarderInfoType));
    }

    [Fact]
    public void Rules_不可用标注文案与空名称处理()
    {
        Assert.Equal("XX 货代（已停用/不可用）", CustomerForwarderRules.MarkUnavailable("XX 货代"));
        Assert.Equal(CustomerForwarderRules.UnavailableMark, CustomerForwarderRules.MarkUnavailable("   "));
        Assert.Equal(CustomerForwarderRules.UnavailableMark, CustomerForwarderRules.MarkUnavailable(null));
    }

    // ============ 测试辅助 ============

    /// <summary>构造客户资料控制器（内存库 + 通用 CRUD 服务）</summary>
    private static CustomerController BuildController(ErpDbContext db)
        => new(new GenericService<BaseCustomer>(db), db);

    /// <summary>断言成功响应并取出数据（业务码必须为 0）</summary>
    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>造一条「其他资料」字典项</summary>
    private static BaseOtherInfo SeedOtherInfo(ErpDbContext db, string infoType, string infoCode, string infoName,
        int status = 1, bool deleted = false, int sortOrder = 0)
    {
        var entry = new BaseOtherInfo
        {
            InfoType = infoType,
            InfoCode = infoCode,
            InfoName = infoName,
            Status = status,
            IsDeleted = deleted,
            SortOrder = sortOrder
        };
        db.BaseOtherInfos.Add(entry);
        db.SaveChanges();
        return entry;
    }

    /// <summary>造一条客户资料（模拟历史数据：可直接带已失效的货代引用）</summary>
    private static BaseCustomer SeedCustomer(ErpDbContext db, string customerCode, string customerName,
        long? forwarderId = null, string forwarderName = "")
    {
        var customer = new BaseCustomer
        {
            CustomerCode = customerCode,
            CustomerName = customerName,
            Status = 1,
            ForwarderId = forwarderId,
            ForwarderName = forwarderName
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }
}
