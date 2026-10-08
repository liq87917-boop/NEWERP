using System.Security.Claims;
using System.Text.Json;
using ERP.Api.Controllers;
using ERP.Api.Services;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-406 旧单据操作历史（<c>GET api/v2/bills/{billType}/{oid}/logs</c>）数据授权修复单元测试
/// （内存库 + 真实既有身份 / 菜单授权 + 既有范围语义的实现替身）。
/// <list type="bullet">
/// <item><b>先授权后读取</b>：缺失 / 已删除身份、已禁用 / 无菜单 / 仅导出菜单、撤销授权一律不读取任何权威行；</item>
/// <item><b>精确文档身份</b>：族既有模块标题 + 权威单号 + 有限精确路径交叉匹配，同单号跨族 / 跨客户与前缀碰撞 / 非有限动作段 /
/// 无记录标识的历史行一律不返回；</item>
/// <item><b>零 / 负数 Oid</b> 拒绝全局历史；缺失 / 越权 Oid 返回同一结果且不泄露单号与历史；</item>
/// <item><b>有界分页</b>与检查运算；统一只返回业务时间线字段，绝不返回请求体等原始载荷；全部零写入、零通知。</item>
/// </list>
/// </summary>
public class LegacyBillHistoryTests
{
    private const string UnusedConnectionString =
        "Server=(localdb)\\NEWERP_UnitTests_Gate;Database=UNUSED;Integrated Security=true;Connect Timeout=1";

    private const long VisibleOid = 5001;
    private const long HiddenOid = 5101;

    // ==================== 脚手架 ====================

    private static StoredProcedureService StoredProcedures()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = UnusedConnectionString
            })
            .Build();
        return new StoredProcedureService(configuration);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static BillProcController NewController(ErpDbContext db, long? userId, ILegacyBillReadService reads)
    {
        var controller = new BillProcController(StoredProcedures(), db,
            new DingTalkService(db, new StubHttpClientFactory(), NullLogger<DingTalkService>.Instance), reads);

        var http = new DefaultHttpContext();
        if (userId.HasValue)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));
        }

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static ApiResponse<object> Envelope(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<object>>(ok.Value);
    }

    private static JsonElement Payload(ApiResponse<object> envelope)
        => JsonDocument.Parse(JsonSerializer.Serialize(envelope.Data)).RootElement.Clone();

    private static string ModuleOf(string billType) => BillProcController.BillTitles[billType];

    /// <summary>
    /// 既有范围语义的实现替身：<c>ReadAuthoritativeHeaderAsync</c> 只返回调用方范围内的权威行（越权 / 不存在 = <c>null</c>，
    /// 缺结构抛受控 environment-blocked）；其余读方法一律证明「不被调用」。
    /// </summary>
    private sealed class StubLegacyReadService : ILegacyBillReadService
    {
        private readonly Dictionary<(string Family, long Oid), (long CustId, string BillNo)> _rows = new();
        private readonly HashSet<string> _unavailable = new(StringComparer.OrdinalIgnoreCase);

        public int HeaderCalls { get; private set; }
        public int PageCalls { get; private set; }
        public int DetailCalls { get; private set; }
        public int NavigateCalls { get; private set; }
        public SalespersonDataScope? LastScope { get; private set; }

        public StubLegacyReadService Add(string family, long oid, long custId, string billNo)
        {
            _rows[(family, oid)] = (custId, billNo);
            return this;
        }

        public StubLegacyReadService Unavailable(string family)
        {
            _unavailable.Add(family);
            return this;
        }

        public Task<Dictionary<string, object?>?> ReadAuthoritativeHeaderAsync(
            string familyKey, long oid, SalespersonDataScope scope, CancellationToken cancellationToken = default)
        {
            HeaderCalls++;
            LastScope = scope;
            var family = LegacyBillReadCatalog.Resolve(familyKey);
            var clause = LegacyBillReadService.ResolveScopeClause(family, scope);

            if (_unavailable.Contains(familyKey))
            {
                throw new BusinessException(
                    $"旧单据表 {family.TableName} 不存在或列结构不兼容（environment-blocked，拒绝读取）",
                    ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported);
            }

            if (clause.Sql == "1=0") return Task.FromResult<Dictionary<string, object?>?>(null);
            if (!_rows.TryGetValue((familyKey, oid), out var row))
                return Task.FromResult<Dictionary<string, object?>?>(null);
            if (scope.AllowedCustomerIds is not null && !scope.AllowedCustomerIds.Contains(row.CustId))
                return Task.FromResult<Dictionary<string, object?>?>(null);

            return Task.FromResult<Dictionary<string, object?>?>(new(StringComparer.Ordinal)
            {
                ["Oid"] = oid,
                ["BillNo"] = row.BillNo,
                ["CustId"] = row.CustId,
            });
        }

        public Task<LegacyBillReadPage> ReadPageAsync(string familyKey, LegacyBillReadQuery query,
            SalespersonDataScope scope, CancellationToken cancellationToken = default)
        {
            PageCalls++;
            throw new NotSupportedException();
        }

        public Task<LegacyBillReadDetail?> ReadDetailAsync(string familyKey, long oid,
            SalespersonDataScope scope, CancellationToken cancellationToken = default)
        {
            DetailCalls++;
            throw new NotSupportedException();
        }

        public Task<LegacyBillNavigateResult> NavigateAsync(string familyKey, long oid, string? direction,
            SalespersonDataScope scope, CancellationToken cancellationToken = default)
        {
            NavigateCalls++;
            throw new NotSupportedException();
        }
    }

    // ==================== 播种（真实既有身份 / 菜单 / 客户范围） ====================

    private static (SysUser User, SysRole Role) SeedRestrictedOperator(ErpDbContext db, params string[] menuCodes)
    {
        var user = new SysUser
        {
            UserName = $"legacy-hist-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "受限历史操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "受限历史角色", RoleCode = $"HR-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        GrantMenus(db, role.Id, menuCodes);
        return (user, role);
    }

    private static void GrantMenus(ErpDbContext db, long roleId, params string[] menuCodes)
    {
        foreach (var menuCode in menuCodes)
        {
            var menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    private static (BaseCustomer Visible, BaseCustomer Hidden) SeedScopedSalesman(ErpDbContext db, long userId)
    {
        var userName = db.SysUsers.AsNoTracking().First(u => u.Id == userId).UserName;
        var employee = new BaseEmployee
        {
            EmployeeCode = userName,
            EmployeeName = "受限历史业务员",
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var visible = new BaseCustomer
        {
            CustomerCode = $"C-{Guid.NewGuid():N}",
            CustomerName = "可见客户",
            EmpId = employee.Id,
            Status = 1,
            CreditStatus = "正常"
        };
        var hidden = new BaseCustomer
        {
            CustomerCode = $"C-{Guid.NewGuid():N}",
            CustomerName = "隐藏客户",
            EmpId = null,
            Status = 1,
            CreditStatus = "正常"
        };
        db.BaseCustomers.AddRange(visible, hidden);
        db.SaveChanges();
        return (visible, hidden);
    }

    private static SysUser SeedUser(ErpDbContext db, UserStatus status)
    {
        var user = new SysUser
        {
            UserName = $"legacy-hist-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SysOperationLog AddLog(ErpDbContext db, string module, string path, string billNo,
        string action = "保存", bool deleted = false, DateTime? createdAt = null)
    {
        var log = new SysOperationLog
        {
            UserName = "操作员",
            Module = module,
            Action = action,
            Method = "POST",
            Path = path,
            BillNo = billNo,
            IpAddress = "127.0.0.1",
            StatusCode = 200,
            DurationMs = 1,
            CreatedAt = createdAt ?? DateTime.Now,
            IsDeleted = deleted,
        };
        db.SysOperationLogs.Add(log);
        db.SaveChanges();
        return log;
    }

    // ==================== 1. 精确模块 / 路径边界：同单号跨族跨客户与碰撞路径不得混入 ====================

    [Fact]
    public async Task 精确模块与路径边界_同单号跨族跨客户与碰撞路径不得混入()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order");
        var (visible, hidden) = SeedScopedSalesman(db, user.Id);
        var reads = new StubLegacyReadService()
            .Add("sales-order", VisibleOid, visible.Id, "SO-DUP")
            .Add("sales-order", HiddenOid, hidden.Id, "SO-DUP");
        var controller = NewController(db, user.Id, reads);

        var title = ModuleOf("sales-order");
        var basePath = $"/api/v2/bills/sales-order/{VisibleOid}";
        AddLog(db, title, basePath, "SO-DUP");
        AddLog(db, title, basePath + "/audit", "SO-DUP");
        AddLog(db, title, basePath + "0", "SO-DUP");                 // 前缀碰撞（…/5001 vs …/50010）→ 排除
        AddLog(db, title, basePath + "/audit-extra", "SO-DUP");      // 非有限动作段 → 排除
        AddLog(db, title, "/api/v2/bills/sales-order/save", "SO-DUP"); // 无记录标识 → 排除
        AddLog(db, title, basePath, "SO-OTHER");                     // 单号不符 → 排除
        AddLog(db, ModuleOf("stock-out"), "/api/v2/bills/stock-out/7001", "SO-DUP"); // 其它族 → 排除
        AddLog(db, title, $"/api/v2/bills/sales-order/{HiddenOid}", "SO-DUP");       // 其它客户文档 → 排除
        AddLog(db, title, basePath, "SO-DUP", deleted: true);        // 已删除 → 排除

        var envelope = Envelope(await controller.GetBillLogs("sales-order", VisibleOid));
        Assert.Equal(ErrorCodes.Success, envelope.Code);

        var root = Payload(envelope);
        Assert.Equal(2, root.GetProperty("total").GetInt32());
        var paths = root.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("Path").GetString() ?? string.Empty).ToList();
        Assert.Equal(2, paths.Count);
        Assert.DoesNotContain(basePath + "0", paths);
        Assert.DoesNotContain(basePath + "/audit-extra", paths);
        Assert.DoesNotContain("/api/v2/bills/stock-out/7001", paths);
        Assert.DoesNotContain($"/api/v2/bills/sales-order/{HiddenOid}", paths);
        Assert.All(paths, p => Assert.Contains(p, new[] { basePath, basePath + "/audit" }));

        Assert.Equal(0, reads.PageCalls);
        Assert.Equal(0, reads.NavigateCalls);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Empty(db.SysDingTalkLogs);
    }

    // ==================== 2. 零 / 负数 / 缺失 / 越权 Oid：不泄露单号与客户提示 ====================

    [Fact]
    public async Task 零与负数Oid拒绝_缺失与越权Oid不泄漏单号客户提示与历史()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order");
        var (visible, hidden) = SeedScopedSalesman(db, user.Id);
        var reads = new StubLegacyReadService()
            .Add("sales-order", VisibleOid, visible.Id, "SO-DUP")
            .Add("sales-order", HiddenOid, hidden.Id, "SO-DUP");
        var controller = NewController(db, user.Id, reads);
        var title = ModuleOf("sales-order");
        AddLog(db, title, $"/api/v2/bills/sales-order/{VisibleOid}", "SO-DUP");
        AddLog(db, title, $"/api/v2/bills/sales-order/{HiddenOid}", "SO-DUP");

        foreach (var badOid in new long[] { 0, -1 })
        {
            var denied = Envelope(await controller.GetBillLogs("sales-order", badOid));
            Assert.Equal(ErrorCodes.InvalidParameter, denied.Code);
            Assert.Null(denied.Data);
            Assert.Contains("全局历史", denied.Message, StringComparison.Ordinal);
        }

        // 零 / 负数 Oid 在读取任何权威行之前拒绝（零读取）。
        Assert.Equal(0, reads.HeaderCalls);

        var missing = Envelope(await controller.GetBillLogs("sales-order", 999999));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Null(missing.Data);

        var foreign = Envelope(await controller.GetBillLogs("sales-order", HiddenOid));
        Assert.Equal(ErrorCodes.NotFound, foreign.Code);
        Assert.Null(foreign.Data);
        Assert.DoesNotContain("SO-DUP", JsonSerializer.Serialize(foreign));

        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Empty(db.SysDingTalkLogs);
    }

    // ==================== 3. 未授权 / 禁用 / 删除 / 撤销授权：绝不读取历史 ====================

    [Fact]
    public async Task 未授权身份与权限撤销禁用不读取任何历史()
    {
        await using var db = TestDbFactory.Create();
        var reads = new StubLegacyReadService().Add("sales-order", VisibleOid, 1, "SO-DUP");

        var anonymous = Envelope(await NewController(db, null, reads).GetBillLogs("sales-order", VisibleOid));
        Assert.Equal(ErrorCodes.Unauthorized, anonymous.Code);
        Assert.Null(anonymous.Data);

        var (noMenu, _) = SeedRestrictedOperator(db);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, noMenu.Id, reads).GetBillLogs("sales-order", VisibleOid)).Code);

        var (exportOnly, _) = SeedRestrictedOperator(db, "sales-order-export");
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, exportOnly.Id, reads).GetBillLogs("sales-order", VisibleOid)).Code);

        var disabled = SeedUser(db, UserStatus.Disabled);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, disabled.Id, reads).GetBillLogs("sales-order", VisibleOid)).Code);

        var deleted = SeedUser(db, UserStatus.Enabled);
        deleted.IsDeleted = true;
        db.SaveChanges();
        Assert.Equal(ErrorCodes.Unauthorized,
            Envelope(await NewController(db, deleted.Id, reads).GetBillLogs("sales-order", VisibleOid)).Code);

        // 以上拒绝路径一律没有读取任何权威行，也没有任何写入 / 通知。
        Assert.Equal(0, reads.HeaderCalls);
        Assert.Empty(db.SysDingTalkLogs);

        // 撤销既有功能菜单后下一次请求立即收敛为拒绝；恢复授权后恢复读取（不缓存）。
        var (scopedUser, scopedRole) = SeedRestrictedOperator(db, "sales-order");
        var (visible, _) = SeedScopedSalesman(db, scopedUser.Id);
        var scopedReads = new StubLegacyReadService().Add("sales-order", VisibleOid, visible.Id, "SO-DUP");

        var granted = Envelope(await NewController(db, scopedUser.Id, scopedReads).GetBillLogs("sales-order", VisibleOid));
        Assert.Equal(ErrorCodes.Success, granted.Code);

        db.SysRoleMenus.RemoveRange(db.SysRoleMenus.Where(rm => rm.RoleId == scopedRole.Id));
        db.SaveChanges();
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, scopedUser.Id, scopedReads).GetBillLogs("sales-order", VisibleOid)).Code);

        GrantMenus(db, scopedRole.Id, "sales-order");
        Assert.Equal(ErrorCodes.Success,
            Envelope(await NewController(db, scopedUser.Id, scopedReads).GetBillLogs("sales-order", VisibleOid)).Code);

        Assert.Empty(db.SysDingTalkLogs);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 4. 有界分页与稳定时间线排序 ====================

    [Fact]
    public async Task 分页有界与计数_稳定时间线排序且无原始载荷()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order");
        var (visible, _) = SeedScopedSalesman(db, user.Id);
        var reads = new StubLegacyReadService().Add("sales-order", VisibleOid, visible.Id, "SO-DUP");
        var controller = NewController(db, user.Id, reads);
        var title = ModuleOf("sales-order");
        var basePath = $"/api/v2/bills/sales-order/{VisibleOid}";

        var seededIds = new List<long>();
        for (var i = 0; i < 5; i++)
            seededIds.Add(AddLog(db, title, basePath, "SO-DUP", createdAt: DateTime.Now.AddMinutes(i)).Id);

        var first = Payload(Envelope(await controller.GetBillLogs("sales-order", VisibleOid, 1, 2)));
        var second = Payload(Envelope(await controller.GetBillLogs("sales-order", VisibleOid, 2, 2)));
        var third = Payload(Envelope(await controller.GetBillLogs("sales-order", VisibleOid, 3, 2)));

        Assert.Equal(5, first.GetProperty("total").GetInt32());
        Assert.Equal(5, second.GetProperty("total").GetInt32());
        Assert.Equal(2, first.GetProperty("pageSize").GetInt32());

        var firstIds = first.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("Id").GetInt64()).ToList();
        var secondIds = second.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("Id").GetInt64()).ToList();
        var thirdIds = third.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("Id").GetInt64()).ToList();

        Assert.Equal(2, firstIds.Count);
        Assert.Equal(2, secondIds.Count);
        Assert.Single(thirdIds);
        Assert.Empty(firstIds.Intersect(secondIds));
        Assert.Empty(firstIds.Intersect(thirdIds));
        Assert.Equal(seededIds.OrderByDescending(x => x).Take(2).ToArray(), firstIds.ToArray());

        // 业务时间线字段保留；绝不含请求体等原始载荷（无敏感列泄露）。
        var item = first.GetProperty("items")[0];
        Assert.True(item.TryGetProperty("CreatedAt", out _));
        Assert.True(item.TryGetProperty("UserName", out _));
        Assert.False(item.TryGetProperty("RequestBody", out _));

        // 页大小上界收敛 + 无效参数收敛为默认值。
        var bounded = Payload(Envelope(await controller.GetBillLogs("sales-order", VisibleOid, 1, 100000)));
        Assert.Equal(LegacyBillHistoryRules.MaxPageSize, bounded.GetProperty("pageSize").GetInt32());

        var defaults = Payload(Envelope(await controller.GetBillLogs("sales-order", VisibleOid, 0, 0)));
        Assert.Equal(1, defaults.GetProperty("page").GetInt32());
        Assert.Equal(LegacyBillHistoryRules.DefaultPageSize, defaults.GetProperty("pageSize").GetInt32());

        // 偏移溢出（检查运算）以 1001 拒绝，且不返回任何数据。
        var overflow = Envelope(await controller.GetBillLogs("sales-order", VisibleOid, int.MaxValue, 200));
        Assert.Equal(ErrorCodes.InvalidParameter, overflow.Code);
        Assert.Null(overflow.Data);

        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Empty(db.SysDingTalkLogs);
    }

    // ==================== 5. 不可用历史源显式 environment-blocked（零写入 / 零通知） ====================

    [Fact]
    public async Task 不可用历史源显式environment_blocked且零写入()
    {
        await using var db = TestDbFactory.Create();
        var privilegedId = TestAuth.SeedPrivilegedUser(db);
        var reads = new StubLegacyReadService().Unavailable("payment");
        var controller = NewController(db, privilegedId, reads);

        var blocked = Envelope(await controller.GetBillLogs("payment", 1));
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, blocked.Code);
        Assert.Contains("environment-blocked", blocked.Message, StringComparison.Ordinal);
        Assert.Null(blocked.Data);

        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 6. 精确路径 / 有限边界 / 有界分页纯函数 ====================

    [Fact]
    public void 规则_精确路径有限边界与有界分页纯函数()
    {
        var paths = LegacyBillHistoryRules.BuildDocumentPaths("sales-order", 12);
        Assert.Contains("/api/v2/bills/sales-order/12", paths);
        Assert.Contains("/api/v2/bills/sales-order/12/audit", paths);
        Assert.DoesNotContain("/api/v2/bills/sales-order/120", paths);          // 前缀碰撞绝不匹配
        Assert.DoesNotContain("/api/v2/bills/sales-order/12/audit-extra", paths); // 非有限动作段绝不匹配
        Assert.Equal(LegacyBillHistoryRules.ActionSegments.Count + 1, paths.Count);

        foreach (var badOid in new long[] { 0, -3 })
        {
            var denied = Assert.Throws<BusinessException>(
                () => LegacyBillHistoryRules.BuildDocumentPaths("sales-order", badOid));
            Assert.Equal(ErrorCodes.InvalidParameter, denied.Code);
        }

        Assert.Throws<BusinessException>(() => LegacyBillHistoryRules.DocumentBasePath("bad type", 1));
        Assert.Throws<BusinessException>(() => LegacyBillHistoryRules.DocumentBasePath(string.Empty, 1));

        var (page1, size1, offset1) = LegacyBillHistoryRules.ResolvePaging(0, 0);
        Assert.Equal(1, page1);
        Assert.Equal(LegacyBillHistoryRules.DefaultPageSize, size1);
        Assert.Equal(0, offset1);

        var (page2, size2, offset2) = LegacyBillHistoryRules.ResolvePaging(2, 100000);
        Assert.Equal(2, page2);
        Assert.Equal(LegacyBillHistoryRules.MaxPageSize, size2);
        Assert.Equal(offset2, size2); // (2-1) * 200

        var overflow = Assert.Throws<BusinessException>(
            () => LegacyBillHistoryRules.ResolvePaging(int.MaxValue, LegacyBillHistoryRules.MaxPageSize));
        Assert.Equal(ErrorCodes.InvalidParameter, overflow.Code);
        Assert.Contains("偏移", overflow.Message, StringComparison.Ordinal);
    }
}
