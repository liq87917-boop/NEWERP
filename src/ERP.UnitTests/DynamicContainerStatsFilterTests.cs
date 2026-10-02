using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-252 动态柜量与装柜利用率证据报表「字段 / 筛选 / 分页」聚焦单元测试。
/// <para>覆盖：字段规范化（未知 / 重复 / 空键拒绝、留空返回全部）、日期与分页边界（含结束日溢出防护）、
/// 可选应用筛选规范化（客户 Id 正整数 / 柜号关键字去首尾空白、80 字符上限、控制字符拒绝、字面 % _）、
/// 服务端在客户范围与既有谓词之后、501 装柜清单头上限探测之前用字面柜号谓词相交、空白关键字保留缺号证据桶、
/// 非空白关键字仅匹配持久化非空白原始柜号、与客户范围相交、字面通配符不被当作 SQL 通配符、筛选先于上限探测。</para>
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class DynamicContainerStatsFilterTests
{
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

    // ==================== 脚手架 ====================

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static ContainerLoadingList SeedList(
        ErpDbContext db, string loadingListNo, long customerId, string containerNo,
        DateTime? loadingDate = null, DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = loadingListNo,
            LoadingDate = loadingDate ?? new DateTime(2026, 9, 10),
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = status,
            IsDeleted = deleted,
            TotalCartons = 1m,
            TotalWeight = 2m,
            TotalVolume = 3m
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    private static DynamicContainerStatsReportRequest Request(
        List<string>? fields = null, int page = 1, int pageSize = 20,
        DynamicContainerStatsReportFilterDto? filter = null,
        DateTime? start = null, DateTime? end = null)
        => new()
        {
            Fields = fields,
            Start = start ?? Start,
            End = end ?? End,
            Page = page,
            PageSize = pageSize,
            Filter = filter,
        };

    private static async Task<DynamicContainerStatsReportPageDto> ServicePageAsync(
        ErpDbContext db, DynamicContainerStatsReportFilterDto? filter,
        List<string>? fields = null, SalespersonDataScope? scope = null)
        => await new ReportService(db).GetDynamicContainerStatsReportAsync(
            Request(fields: fields, filter: filter), scope ?? PrivilegedScope);

    // ==================== 1. 字段规范化（纯规则） ====================

    [Fact]
    public void 字段_留空返回全部白名单_保持目录顺序()
    {
        var keys = DynamicContainerStatsReportRules.NormalizeFields(null);
        Assert.Equal(
            new[] { "loadingDate", "containerNo", "loadingListCount", "authorizedCustomerCount", "totalCartons", "totalWeight", "totalVolume", "utilizationType", "reasons" },
            keys);
    }

    [Fact]
    public void 字段_未知重复空键_拒绝()
    {
        Assert.Throws<BusinessException>(() =>
            DynamicContainerStatsReportRules.NormalizeFields(new List<string> { "nope" }));
        Assert.Throws<BusinessException>(() =>
            DynamicContainerStatsReportRules.NormalizeFields(new List<string> { "loadingDate", "loadingDate" }));
        Assert.Throws<BusinessException>(() =>
            DynamicContainerStatsReportRules.NormalizeFields(new List<string> { "  " }));
    }

    [Fact]
    public void 字段_选定顺序保留()
    {
        var keys = DynamicContainerStatsReportRules.NormalizeFields(
            new List<string> { "totalVolume", "loadingDate", "containerNo" });
        Assert.Equal(new[] { "totalVolume", "loadingDate", "containerNo" }, keys);
    }

    // ==================== 2. 日期与分页边界（纯规则） ====================

    [Fact]
    public void 日期_结束早于开始_超过366天_最大日期_拒绝()
    {
        Assert.Throws<BusinessException>(() =>
            DynamicContainerStatsReportRules.ValidateDateRange(new DateTime(2026, 9, 30), new DateTime(2026, 9, 1)));
        Assert.Throws<BusinessException>(() =>
            DynamicContainerStatsReportRules.ValidateDateRange(new DateTime(2026, 1, 1), new DateTime(2027, 1, 2)));
        Assert.Throws<BusinessException>(() =>
            DynamicContainerStatsReportRules.ValidateDateRange(new DateTime(2026, 1, 1), DateTime.MaxValue));
    }

    [Fact]
    public void 日期_恰为366天_通过()
    {
        var (s, e) = DynamicContainerStatsReportRules.ValidateDateRange(new DateTime(2026, 1, 1), new DateTime(2027, 1, 1));
        Assert.Equal(new DateTime(2026, 1, 1), s);
        Assert.Equal(new DateTime(2027, 1, 1), e);
    }

    [Fact]
    public void 分页_页码与每页边界_拒绝()
    {
        Assert.Throws<BusinessException>(() => DynamicContainerStatsReportRules.ValidatePageBounds(0, 20));
        Assert.Throws<BusinessException>(() => DynamicContainerStatsReportRules.ValidatePageBounds(1, 0));
        Assert.Throws<BusinessException>(() => DynamicContainerStatsReportRules.ValidatePageBounds(1, 201));
    }

    // ==================== 3. 筛选规范化（纯规则） ====================

    [Fact]
    public void 筛选_全部留空返回null_客户Id必须正整数()
    {
        Assert.Null(DynamicContainerStatsReportRules.NormalizeFilter(null));
        Assert.Null(DynamicContainerStatsReportRules.NormalizeFilter(new DynamicContainerStatsReportFilterDto()));

        Assert.Throws<BusinessException>(() =>
            DynamicContainerStatsReportRules.NormalizeFilter(new DynamicContainerStatsReportFilterDto { CustomerId = 0 }));
        Assert.Throws<BusinessException>(() =>
            DynamicContainerStatsReportRules.NormalizeFilter(new DynamicContainerStatsReportFilterDto { CustomerId = -1 }));
    }

    [Fact]
    public void 关键字_留空返回null_去首尾空白_超长与控制字符拒绝()
    {
        Assert.Null(DynamicContainerStatsReportRules.NormalizeContainerNoKeyword(null));
        Assert.Null(DynamicContainerStatsReportRules.NormalizeContainerNoKeyword(""));
        Assert.Null(DynamicContainerStatsReportRules.NormalizeContainerNoKeyword("   "));

        Assert.Equal("TCLU-001", DynamicContainerStatsReportRules.NormalizeContainerNoKeyword("  TCLU-001  "));

        var over = new string('A', DynamicContainerStatsReportRules.MaxFilterKeywordLength + 1);
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicContainerStatsReportRules.NormalizeContainerNoKeyword(over));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        Assert.Throws<BusinessException>(() =>
            DynamicContainerStatsReportRules.NormalizeContainerNoKeyword("TCLU\u0000001"));
    }

    [Fact]
    public void 筛选_上下文文案_客户Id与柜号关键字()
    {
        Assert.Equal(string.Empty, DynamicContainerStatsReportRules.BuildFilterContext(null));
        Assert.Equal("客户 Id 7", DynamicContainerStatsReportRules.BuildFilterContext(
            new DynamicContainerStatsReportFilterDto { CustomerId = 7 }));
        Assert.Equal("客户 Id 7；柜号关键字 TCLU", DynamicContainerStatsReportRules.BuildFilterContext(
            new DynamicContainerStatsReportFilterDto { CustomerId = 7, ContainerNo = "TCLU" }));
    }

    // ==================== 4. 服务端关键字相交（内存 EF） ====================

    [Fact]
    public async Task 服务_空白关键字_保留缺号证据桶_非空白关键字仅匹配非空白柜号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-BLANK", customer.Id, "   ");
        SeedList(db, "LL-OK", customer.Id, "TCLU-001");

        // 空白关键字（不过滤）：缺号证据桶与非空白柜号桶都保留
        var noFilter = await ServicePageAsync(db, null, new List<string> { "containerNo" });
        Assert.Equal(2, noFilter.Total);
        Assert.Contains(noFilter.Rows, r => string.Equals(r["containerNo"] as string, "未填柜号（装柜清单 #1）"));

        // 非空白关键字：仅匹配持久化非空白原始柜号，缺号证据桶被排除
        var filtered = await ServicePageAsync(db,
            new DynamicContainerStatsReportFilterDto { ContainerNo = "TCLU" },
            new List<string> { "containerNo" });
        var row = Assert.Single(filtered.Rows);
        Assert.Equal("TCLU-001", row["containerNo"]);
    }

    [Fact]
    public async Task 服务_柜号关键字_字面通配符不被当作SQL通配符()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-A_B", customer.Id, "A_B");
        SeedList(db, "LL-AB", customer.Id, "AB");
        SeedList(db, "LL-PCT", customer.Id, "100%柜");
        SeedList(db, "LL-NOPCT", customer.Id, "100柜");

        var underscore = await ServicePageAsync(db,
            new DynamicContainerStatsReportFilterDto { ContainerNo = "_" },
            new List<string> { "containerNo" });
        var uRow = Assert.Single(underscore.Rows);
        Assert.Equal("A_B", uRow["containerNo"]);

        var percent = await ServicePageAsync(db,
            new DynamicContainerStatsReportFilterDto { ContainerNo = "%" },
            new List<string> { "containerNo" });
        var pRow = Assert.Single(percent.Rows);
        Assert.Equal("100%柜", pRow["containerNo"]);
    }

    [Fact]
    public async Task 服务_柜号关键字_与客户范围相交_不扩展权限()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C001", "我的客户");
        var other = SeedCustomer(db, "C002", "别人的客户");
        SeedList(db, "LL-MINE", mine.Id, "TCLU-001");
        SeedList(db, "LL-OTHER", other.Id, "TCLU-002");

        var scope = new SalespersonDataScope
        {
            IsPrivileged = false,
            SalesmanId = 1,
            AllowedCustomerIds = new HashSet<long> { mine.Id }
        };

        var page = await ServicePageAsync(db,
            new DynamicContainerStatsReportFilterDto { ContainerNo = "TCLU" },
            new List<string> { "containerNo" }, scope);

        var row = Assert.Single(page.Rows);
        Assert.Equal("TCLU-001", row["containerNo"]);
    }

    [Fact]
    public async Task 服务_客户Id筛选_作用于范围之内_不扩展权限()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C001", "我的客户");
        var other = SeedCustomer(db, "C002", "别人的客户");
        SeedList(db, "LL-MINE", mine.Id, "TCLU-001");
        SeedList(db, "LL-OTHER", other.Id, "TCLU-002");

        var scope = new SalespersonDataScope
        {
            IsPrivileged = false,
            SalesmanId = 1,
            AllowedCustomerIds = new HashSet<long> { mine.Id }
        };

        // 范围外客户 Id 筛选：即使显式指定其它客户，也不泄露范围外记录
        var excluded = await ServicePageAsync(db,
            new DynamicContainerStatsReportFilterDto { CustomerId = other.Id },
            new List<string> { "containerNo" }, scope);
        Assert.Empty(excluded.Rows);
        Assert.Equal(0, excluded.Total);
    }

    // ==================== 5. 筛选先于 500 上限探测 ====================

    [Fact]
    public async Task 服务_筛选先于上限探测_过滤后不超限()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-MATCH", customer.Id, "TCLU-001");

        for (var i = 0; i < 500; i++)
        {
            db.ContainerLoadingLists.Add(new ContainerLoadingList
            {
                LoadingListNo = $"LL-OTHER-{i:D4}",
                LoadingDate = new DateTime(2026, 9, 10),
                ContainerNo = $"OTHER-{i:D4}",
                CustomerId = customer.Id,
                Status = DocumentStatus.Approved
            });
        }
        db.SaveChanges();

        // 无筛选：501 张装柜清单头 → 超出上限 fail closed
        var service = new ReportService(db);
        var overflow = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetDynamicContainerStatsReportAsync(Request(), PrivilegedScope));
        Assert.Equal(ErrorCodes.RuleConflict, overflow.Code);

        // 柜号关键字筛选在 Take(501) 之前相交：过滤后仅 1 张匹配 → 正常返回
        var page = await service.GetDynamicContainerStatsReportAsync(
            Request(fields: new List<string> { "containerNo", "loadingListCount" },
                filter: new DynamicContainerStatsReportFilterDto { ContainerNo = "TCLU-001" }),
            PrivilegedScope);
        var row = Assert.Single(page.Rows);
        Assert.Equal("TCLU-001", row["containerNo"]);
        Assert.Equal(1, page.Total);
    }
}


