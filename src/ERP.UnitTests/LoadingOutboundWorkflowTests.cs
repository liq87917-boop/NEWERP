using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-373 装柜清单业务界面「出运证据」显式链接工作流单元测试。
/// <list type="number">
/// <item><b>业务界面接线</b>：装柜清单行操作「出运证据」→ <c>loading-outbound-links.js</c> 工作台
/// （脚本已在 <c>index.html</c> 注册；单据编辑器 <c>bill-edit.js</c> 暴露只读出运证据列并随行携带持久化链接）；</item>
/// <item><b>服务端权威往返</b>：打开 / 重新加载读取服务端持久化链接状态（历史 <c>null</c> = 显式「未链接」；
/// 来源已不可用 = 显式「来源不可用」且原链接原样保留）；指派 / 清除按**精确**销售出库明细 Id 往返；</item>
/// <item><b>批量原子性</b>：任一行非法则整批拒绝，库中所有行保持原样；</item>
/// <item><b>只读与保留</b>：已提交 / 已审核 / 已取消单据拒绝维护（服务端权威）；明细整体替换与**主表单独更新**都不得
/// 静默清空出运证据；</item>
/// <item><b>授权</b>：存在显式链接时读取 / 指派必须有既有「销售出库」菜单授权（fail closed，不降级）。</item>
/// </list>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不运行浏览器验收；
/// 真实 SQL Server 往返与两条独立连接竞争见 <c>ERP.IntegrationTests/LoadingOutboundWorkflowSqlServerTests.cs</c>。</para>
/// </summary>
public class LoadingOutboundWorkflowTests
{
    private const long CustomerA = 973001L;
    private const long CustomerB = 973002L;
    private const long ProductA = 973101L;
    private const long ProductB = 973102L;

    // ==================== 脚手架与种子数据 ====================

    private static ContainerLoadingListController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerLoadingListController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long id, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            Id = id, CustomerCode = $"C-{id}", CustomerName = name, EmpId = empId, Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static void SeedProduct(ErpDbContext db, long id, string name, string unit = "PCS")
    {
        db.BaseProducts.Add(new BaseProduct
        {
            Id = id, ProductCode = $"P-{id}", ProductName = name, Spec = "规格A", Unit = unit
        });
        db.SaveChanges();
    }

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = no, OrderDate = DateTime.Today, CustomerId = customerId, Status = status
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static StockOut SeedStockOut(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Approved, long? salesOrderId = null, bool deleted = false)
    {
        var stockOut = new StockOut
        {
            StockOutNo = no, StockOutDate = DateTime.Today, CustomerId = customerId,
            Status = status, SalesOrderId = salesOrderId, IsDeleted = deleted
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        return stockOut;
    }

    private static StockOutDetail AddStockOutDetail(ErpDbContext db, long stockOutId, long productId,
        decimal quantity, string unit = "PCS", bool deleted = false)
    {
        var detail = new StockOutDetail
        {
            StockOutId = stockOutId, ProductId = productId, ProductName = $"商品{productId}",
            Unit = unit, Quantity = quantity, IsDeleted = deleted
        };
        db.StockOutDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static ContainerLoadingList SeedLoadingList(ErpDbContext db, string no, long customerId,
        DocumentStatus status, params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no, LoadingDate = DateTime.Today, CustomerId = customerId, Status = status,
            Remark = "ERP-373_TEST"
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        foreach (var (productId, quantity, sourceDetailId) in lines)
        {
            db.ContainerLoadingDetails.Add(new ContainerLoadingDetail
            {
                LoadingListId = list.Id, ProductId = productId, ProductName = $"商品{productId}",
                Quantity = quantity, SourceStockOutDetailId = sourceDetailId
            });
        }
        db.SaveChanges();
        return list;
    }

    private static async Task<ContainerLoadingList> ReloadAsync(ErpDbContext db, long id)
    {
        db.ChangeTracker.Clear();
        return await db.ContainerLoadingLists.Include(o => o.Details).AsNoTracking()
            .SingleAsync(o => o.Id == id);
    }

    private static async Task<List<long>> DetailIdsAsync(ErpDbContext db, long loadingListId)
        => await db.ContainerLoadingDetails.AsNoTracking()
            .Where(d => d.LoadingListId == loadingListId && !d.IsDeleted)
            .OrderBy(d => d.Id).Select(d => d.Id).ToListAsync();

    private static LoadingStockOutLinkAssignRequest Assign(params (long DetailId, long? SourceDetailId)[] links)
        => new()
        {
            Links = links.Select(l => new LoadingStockOutLinkAssignmentDto
            {
                LoadingDetailId = l.DetailId,
                SourceStockOutDetailId = l.SourceDetailId,
            }).ToList()
        };

    /// <summary>播种受限（非特权）装柜操作员（业务员映射 + 既有菜单授权），返回其用户 Id 与员工 Id。</summary>
    private static (long UserId, long EmployeeId) SeedRestrictedOperator(ErpDbContext db, params string[] menuCodes)
    {
        var code = $"outbound-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = code, Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "出运证据操作员", RoleCode = $"OutboundOp-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        GrantMenus(db, role.Id, menuCodes);
        return (user.Id, employee.Id);
    }

    private static void GrantMenus(ErpDbContext db, long roleId, params string[] menuCodes)
    {
        foreach (var menuCode in menuCodes)
        {
            var menu = new SysMenu
            {
                MenuCode = menuCode,
                MenuName = menuCode == LoadingStockOutLinkRules.RequiredMenuCode
                    ? LoadingStockOutLinkRules.RequiredMenuText
                    : LoadingStockOutLinkRules.SourceRequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static string ReadJs(string fileName) => File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", fileName));

    // ==================== 1. 业务界面接线（保存后行操作 → 出运证据工作台） ====================

    [Fact]
    public void Frontend_saved_draft_row_action_opens_outbound_dialog_and_script_is_registered()
    {
        var modules = ReadJs("modules-doc2.js");
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        var dialog = ReadJs("loading-outbound-links.js");

        // 行操作 → 全局函数名成对存在（拼写漂移会让「更多」里的按钮点了没反应）
        Assert.Contains("label: '出运证据'", modules);
        Assert.Contains("onclick: 'openLoadingOutboundLinks'", modules);
        Assert.Contains("async function openLoadingOutboundLinks(loadingListId)", dialog);

        // 脚本必须注册，否则行操作找不到全局函数
        Assert.Contains("/js/loading-outbound-links.js", index);

        // 保存后行操作使用服务端持久化的行 Id 与两个既有接口
        Assert.Contains("/api/container/loading-lists/${LOL.loadingListId}", dialog);
        Assert.Contains("/stock-out-candidates?${params.join('&')}", dialog);
        Assert.Contains("stock-out-links`, 'POST'", dialog);
        Assert.Contains("stock-out-links`)", dialog);
    }

    [Fact]
    public void Frontend_dialog_requires_explicit_numeric_selection_and_never_invents_links()
    {
        var dialog = ReadJs("loading-outbound-links.js");

        // 只接受显式正整数选择（空 = 清除）；不解析文本为 Id
        Assert.Contains("function lolNormalizeSelection(value)", dialog);
        Assert.Contains("/^\\d+$/", dialog);
        Assert.Contains("sourceStockOutDetailId: next.value === '' ? null : Number(next.value)", dialog);

        // 未保存单据不得登记链接（绝不臆造服务端行 Id）
        Assert.Contains("LOL_UNSAVED_TEXT", dialog);
        Assert.Contains("lolGuardSavedId", dialog);

        // 历史未链接 / 来源不可用都是显式事实，绝不静默清除
        Assert.Contains("lolLineEvidenceText", dialog);
        Assert.Contains("LOL_UNLINKED_TEXT", dialog);
        Assert.Contains("LOL_UNAVAILABLE_TEXT", dialog);
        Assert.Contains("绝不回填", dialog);

        // 双击 / 重复提交与陈旧选择被阻断
        Assert.Contains("if (LOL.submitting) return;", dialog);
        Assert.Contains("lolStaleSelections", dialog);
        Assert.Contains("LOL.selectionTokens", dialog);

        // 失败保留草稿输入：出错分支只写 error，不清空 draft
        Assert.Contains("LOL.error = lolErrorMessage(err);", dialog);
        Assert.Contains("LOL.draft = applied.draft;", dialog);

        // 只读：仅「待提交」可维护
        Assert.Contains("lolIsEditableStatus", dialog);
        Assert.Contains("LOL_EDITABLE_STATUS = 'Pending'", dialog);
    }

    [Fact]
    public void Frontend_bill_editor_exposes_outbound_evidence_column_and_carries_persisted_link()
    {
        var billEdit = ReadJs("bill-edit.js");

        // 其它单据保持原列；只有装柜清单追加「出运证据」只读列
        Assert.Contains("function detailColumnsFor(code)", billEdit);
        Assert.Contains("if (code !== 'pre-loading') return DETAIL_COLUMNS;", billEdit);
        Assert.Contains("type: 'outbound-evidence'", billEdit);
        Assert.Contains("openLoadingOutboundLinks", billEdit);

        // 持久化链接随行携带并原样回传（明细编辑 / 保存不得静默清空）
        Assert.Contains("tr.dataset.sourceStockOutDetailId = persistedOutbound;", billEdit);
        Assert.Contains("const preserveOutboundEvidence = BILL_CODE === 'loading-list';", billEdit);
        Assert.Contains("d.SourceStockOutDetailId = Number(persisted);", billEdit);

        // 未保存单据不给入口（按钮 disabled），不为未保存单据臆造明细 Id
        Assert.Contains("请先保存该装柜清单，再登记出运证据", billEdit);
    }

    [Fact]
    public void Frontend_automation_interaction_test_exists_and_covers_core_guards()
    {
        var automation = RepoFile("tests", "automation", "loading-outbound-links.test.js");
        Assert.True(File.Exists(automation), "缺少可执行的 JS 交互单测：tests/automation/loading-outbound-links.test.js");

        var script = File.ReadAllText(automation);
        Assert.Contains("require(", script);
        Assert.Contains("lolGuardSavedId", script);
        Assert.Contains("lolBuildAssignments", script);
        Assert.Contains("lolStaleSelections", script);
        Assert.Contains("lolLineEvidenceText", script);
        Assert.Contains("lolIsEditableStatus", script);
    }

    [Fact]
    public void Controller_authorization_inherits_base_only()
    {
        var controllerType = typeof(ContainerLoadingListController);
        Assert.NotNull(controllerType.BaseType!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>().FirstOrDefault());
        Assert.Empty(controllerType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
    }

    // ==================== 2. 服务端权威读取（打开 / 重新加载） ====================

    [Fact]
    public async Task Server_links_endpoint_marks_historical_null_as_explicitly_unlinked()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        var list = SeedLoadingList(db, "ZQ-373-01", CustomerA, DocumentStatus.Pending, (ProductA, 6m, null));

        var lines = AssertOk<List<LoadingStockOutLinkLineDto>>(
            await NewController(db, userId).GetStockOutLinks(list.Id));

        var line = Assert.Single(lines);
        Assert.Null(line.SourceStockOutDetailId);
        Assert.False(line.SourceAvailable);
        Assert.Equal(LoadingStockOutLinkRules.UnlinkedEvidenceText, line.SourceAvailabilityText);
        Assert.Contains("绝不回填", line.SourceAvailabilityText);
    }

    [Fact]
    public async Task Server_links_endpoint_exposes_cancelled_source_without_clearing_it()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-373-02", CustomerA, DocumentStatus.Approved);
        var source = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-373-02", CustomerA, DocumentStatus.Pending, (ProductA, 6m, source.Id));

        // 来源出库单被取消（撤销审核）：历史装柜证据原样保留，只显式标注「来源不可用」
        stockOut.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        var lines = AssertOk<List<LoadingStockOutLinkLineDto>>(
            await NewController(db, userId).GetStockOutLinks(list.Id));

        var line = Assert.Single(lines);
        Assert.Equal(source.Id, line.SourceStockOutDetailId);
        Assert.Equal(stockOut.Id, line.StockOutId);
        Assert.Equal("CK-373-02", line.StockOutNo);
        Assert.False(line.SourceAvailable);
        Assert.Equal(LoadingStockOutLinkRules.UnavailableEvidenceText, line.SourceAvailabilityText);
        Assert.Contains("绝不静默清除", line.SourceAvailabilityText);
        Assert.Equal(source.Id, (await ReloadAsync(db, list.Id)).Details.Single().SourceStockOutDetailId);
    }

    [Fact]
    public async Task Server_links_endpoint_marks_deleted_source_detail_unavailable()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-373-03", CustomerA, DocumentStatus.Approved);
        var source = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-373-03", CustomerA, DocumentStatus.Pending, (ProductA, 6m, source.Id));

        source.IsDeleted = true;
        db.SaveChanges();

        var line = Assert.Single(AssertOk<List<LoadingStockOutLinkLineDto>>(
            await NewController(db, userId).GetStockOutLinks(list.Id)));

        Assert.Equal(source.Id, line.SourceStockOutDetailId);
        Assert.False(line.SourceAvailable);
        Assert.Equal(LoadingStockOutLinkRules.UnavailableEvidenceText, line.SourceAvailabilityText);
    }

    [Fact]
    public async Task Server_links_endpoint_marks_valid_source_available_with_stock_out_no()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        var order = SeedSalesOrder(db, "SO-373-04", CustomerA);
        var stockOut = SeedStockOut(db, "CK-373-04", CustomerA, DocumentStatus.Approved, order.Id);
        var source = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-373-04", CustomerA, DocumentStatus.Pending, (ProductA, 6m, source.Id));

        var line = Assert.Single(AssertOk<List<LoadingStockOutLinkLineDto>>(
            await NewController(db, userId).GetStockOutLinks(list.Id)));

        Assert.True(line.SourceAvailable);
        Assert.Equal(LoadingStockOutLinkRules.LinkedEvidenceText, line.SourceAvailabilityText);
        Assert.Equal(stockOut.Id, line.StockOutId);
        Assert.Equal("CK-373-04", line.StockOutNo);
    }

    [Fact]
    public void Evidence_availability_text_is_a_single_source()
    {
        Assert.Equal(LoadingStockOutLinkRules.UnlinkedEvidenceText,
            LoadingStockOutLinkRules.SourceAvailabilityTextOf(null, false));
        Assert.Equal(LoadingStockOutLinkRules.UnlinkedEvidenceText,
            LoadingStockOutLinkRules.SourceAvailabilityTextOf(0, false));
        Assert.Equal(LoadingStockOutLinkRules.LinkedEvidenceText,
            LoadingStockOutLinkRules.SourceAvailabilityTextOf(5, true));
        Assert.Equal(LoadingStockOutLinkRules.UnavailableEvidenceText,
            LoadingStockOutLinkRules.SourceAvailabilityTextOf(5, false));
    }

    // ==================== 3. 指派 / 清除 精确往返 ====================

    [Fact]
    public async Task Assign_then_reload_persists_exact_source_stock_out_detail_id()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-373-05", CustomerA, DocumentStatus.Approved);
        var source = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-373-05", CustomerA, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = (await DetailIdsAsync(db, list.Id)).Single();

        var result = AssertOk<LoadingStockOutLinkAssignResultDto>(
            await NewController(db, userId).AssignStockOutLinks(list.Id, Assign((detailId, source.Id))));
        Assert.Equal(1, result.LinkedCount);
        Assert.Equal(0, result.ClearedCount);
        Assert.Equal(source.Id, Assert.Single(result.Items).SourceStockOutDetailId);

        // 重新加载读取服务端持久化结果（不是本地草稿）
        var reloaded = Assert.Single(AssertOk<List<LoadingStockOutLinkLineDto>>(
            await NewController(db, userId).GetStockOutLinks(list.Id)));
        Assert.Equal(source.Id, reloaded.SourceStockOutDetailId);
        Assert.True(reloaded.SourceAvailable);
        Assert.Equal(source.Id, (await ReloadAsync(db, list.Id)).Details.Single().SourceStockOutDetailId);
    }

    [Fact]
    public async Task Assign_then_clear_returns_to_explicit_unlinked()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-373-06", CustomerA, DocumentStatus.Approved);
        var source = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-373-06", CustomerA, DocumentStatus.Pending, (ProductA, 6m, source.Id));
        var detailId = (await DetailIdsAsync(db, list.Id)).Single();

        var cleared = AssertOk<LoadingStockOutLinkAssignResultDto>(
            await NewController(db, userId).AssignStockOutLinks(list.Id, Assign((detailId, null))));
        Assert.Equal(0, cleared.LinkedCount);
        Assert.Equal(1, cleared.ClearedCount);
        Assert.Null(Assert.Single(cleared.Items).SourceStockOutDetailId);

        var line = Assert.Single(AssertOk<List<LoadingStockOutLinkLineDto>>(
            await NewController(db, userId).GetStockOutLinks(list.Id)));
        Assert.Null(line.SourceStockOutDetailId);
        Assert.Equal(LoadingStockOutLinkRules.UnlinkedEvidenceText, line.SourceAvailabilityText);
    }

    [Fact]
    public async Task Assign_rejected_proposal_leaves_all_rows_untouched()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        SeedProduct(db, ProductB, "商品B");
        var goodStockOut = SeedStockOut(db, "CK-373-07-GOOD", CustomerA, DocumentStatus.Approved);
        var goodSource = AddStockOutDetail(db, goodStockOut.Id, ProductA, 10m);
        var badStockOut = SeedStockOut(db, "CK-373-07-BAD", CustomerA, DocumentStatus.Approved);
        var badSource = AddStockOutDetail(db, badStockOut.Id, ProductB, 10m);
        var list = SeedLoadingList(db, "ZQ-373-07", CustomerA, DocumentStatus.Pending,
            (ProductA, 4m, goodSource.Id), (ProductA, 3m, null));
        var ids = await DetailIdsAsync(db, list.Id);

        // 一行合法 + 一行商品不匹配：整批拒绝，两行（含既有链接）保持原样
        await AssertCode(ErrorCodes.RuleConflict, () => NewController(db, userId)
            .AssignStockOutLinks(list.Id, Assign((ids[0], goodSource.Id), (ids[1], badSource.Id))));

        var reloaded = await ReloadAsync(db, list.Id);
        Assert.Equal(goodSource.Id, reloaded.Details.Single(d => d.Id == ids[0]).SourceStockOutDetailId);
        Assert.Null(reloaded.Details.Single(d => d.Id == ids[1]).SourceStockOutDetailId);
    }

    [Theory]
    [InlineData(DocumentStatus.Submitted)]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Cancelled)]
    public async Task Assign_rejects_non_pending_document_and_preserves_evidence(DocumentStatus status)
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, $"CK-373-08-{status}", CustomerA, DocumentStatus.Approved);
        var source = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, $"ZQ-373-08-{status}", CustomerA, status, (ProductA, 6m, null));
        var detailId = (await DetailIdsAsync(db, list.Id)).Single();

        await AssertCode(ErrorCodes.RuleConflict, () => NewController(db, userId)
            .AssignStockOutLinks(list.Id, Assign((detailId, source.Id))));

        Assert.Null((await ReloadAsync(db, list.Id)).Details.Single().SourceStockOutDetailId);
        Assert.Equal(status, (await ReloadAsync(db, list.Id)).Status);
    }

    // ==================== 4. 常规编辑 / 重开不得静默清空证据 ====================

    [Fact]
    public async Task Main_table_only_update_preserves_outbound_evidence()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-373-09", CustomerA, DocumentStatus.Approved);
        var source = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-373-09", CustomerA, DocumentStatus.Pending, (ProductA, 6m, source.Id));

        // 主表单独更新（未携带明细，如业务界面侧栏编辑主表）：既有明细与显式出运证据原样保留
        var result = await NewController(db, userId).Update(list.Id, new ContainerLoadingList
        {
            LoadingDate = DateTime.Today,
            CustomerId = CustomerA,
            ContainerNo = "CTN-373-09",
            Remark = "主表更新",
            Details = new List<ContainerLoadingDetail>(),
        });
        Assert.IsType<OkObjectResult>(result);

        var reloaded = await ReloadAsync(db, list.Id);
        var detail = Assert.Single(reloaded.Details);
        Assert.Equal(ProductA, detail.ProductId);
        Assert.Equal(source.Id, detail.SourceStockOutDetailId);
        Assert.Equal("CTN-373-09", reloaded.ContainerNo);
    }

    [Fact]
    public async Task Detail_update_carrying_same_source_detail_id_preserves_link()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-373-10", CustomerA, DocumentStatus.Approved);
        var source = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-373-10", CustomerA, DocumentStatus.Pending, (ProductA, 6m, source.Id));

        // 正常明细编辑：显式随行携带同一 SourceStockOutDetailId（来自出运证据工作台的显式选择）
        var result = await NewController(db, userId).Update(list.Id, new ContainerLoadingList
        {
            LoadingDate = DateTime.Today,
            CustomerId = CustomerA,
            ContainerNo = "CTN-373-10",
            Details = new List<ContainerLoadingDetail>
            {
                new()
                {
                    ProductId = ProductA, ProductName = "商品A", Quantity = 4m,
                    Cartons = 1m, Weight = 1m, Volume = 1m, SourceStockOutDetailId = source.Id
                }
            }
        });
        Assert.IsType<OkObjectResult>(result);

        var detail = Assert.Single((await ReloadAsync(db, list.Id)).Details);
        Assert.Equal(4m, detail.Quantity);
        Assert.Equal(source.Id, detail.SourceStockOutDetailId);

        // 服务端权威回显也认这条链接（有效证据）
        var line = Assert.Single(AssertOk<List<LoadingStockOutLinkLineDto>>(
            await NewController(db, userId).GetStockOutLinks(list.Id)));
        Assert.True(line.SourceAvailable);
        Assert.Equal(stockOut.Id, line.StockOutId);
    }

    // ==================== 5. 既有授权（真实登录账号 + 既有菜单，无管理员降级） ====================

    [Fact]
    public async Task Restricted_operator_with_existing_menus_completes_roundtrip_on_own_customer()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db,
            LoadingStockOutLinkRules.RequiredMenuCode, LoadingStockOutLinkRules.SourceRequiredMenuCode);
        SeedCustomer(db, CustomerA, "自有客户", employeeId);
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-373-11", CustomerA, DocumentStatus.Approved);
        var source = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-373-11", CustomerA, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = (await DetailIdsAsync(db, list.Id)).Single();

        var candidates = AssertOk<List<LoadingStockOutCandidateDto>>(
            await NewController(db, userId).GetStockOutCandidates(list.Id, null, 0));
        var candidate = Assert.Single(candidates);
        Assert.Equal(source.Id, candidate.StockOutDetailId);
        Assert.Equal("CK-373-11", candidate.StockOutNo);
        Assert.Equal(CustomerA, candidate.CustomerId);
        Assert.Equal(10m, candidate.RemainingBaseQuantity);

        var linked = AssertOk<LoadingStockOutLinkAssignResultDto>(
            await NewController(db, userId).AssignStockOutLinks(list.Id, Assign((detailId, source.Id))));
        Assert.Equal(1, linked.LinkedCount);
        Assert.Equal(source.Id, (await ReloadAsync(db, list.Id)).Details.Single().SourceStockOutDetailId);
    }

    [Fact]
    public async Task Unlinked_read_does_not_require_source_menu_but_linked_read_does()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, LoadingStockOutLinkRules.RequiredMenuCode);
        SeedCustomer(db, CustomerA, "自有客户", employeeId);
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-373-12", CustomerA, DocumentStatus.Approved);
        var source = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);

        // 未链接（历史 null）：只要求装柜清单菜单，可用于识别「未链接」显式事实
        var unlinked = SeedLoadingList(db, "ZQ-373-12-A", CustomerA, DocumentStatus.Pending, (ProductA, 6m, null));
        var unlinkedLine = Assert.Single(AssertOk<List<LoadingStockOutLinkLineDto>>(
            await NewController(db, userId).GetStockOutLinks(unlinked.Id)));
        Assert.Null(unlinkedLine.SourceStockOutDetailId);
        Assert.Equal(LoadingStockOutLinkRules.UnlinkedEvidenceText, unlinkedLine.SourceAvailabilityText);

        // 存在显式链接：读取链接状态额外要求既有「销售出库」菜单授权（fail closed，不降级）
        var linked = SeedLoadingList(db, "ZQ-373-12-B", CustomerA, DocumentStatus.Pending, (ProductA, 6m, source.Id));
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).GetStockOutLinks(linked.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains(LoadingStockOutLinkRules.SourceRequiredMenuText, ex.Message);
    }

    [Fact]
    public async Task Assign_without_source_menu_fails_closed_and_preserves_evidence()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, LoadingStockOutLinkRules.RequiredMenuCode);
        SeedCustomer(db, CustomerA, "自有客户", employeeId);
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-373-13", CustomerA, DocumentStatus.Approved);
        var source = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-373-13", CustomerA, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = (await DetailIdsAsync(db, list.Id)).Single();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db, userId)
            .AssignStockOutLinks(list.Id, Assign((detailId, source.Id))));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains(LoadingStockOutLinkRules.SourceRequiredMenuText, ex.Message);

        Assert.Null((await ReloadAsync(db, list.Id)).Details.Single().SourceStockOutDetailId);
    }

    [Fact]
    public async Task Assign_rejects_source_out_of_authoritative_customer_scope()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db,
            LoadingStockOutLinkRules.RequiredMenuCode, LoadingStockOutLinkRules.SourceRequiredMenuCode);
        SeedCustomer(db, CustomerA, "自有客户", employeeId);
        SeedCustomer(db, CustomerB, "其它客户", empId: null);
        SeedProduct(db, ProductA, "商品A");
        var otherStockOut = SeedStockOut(db, "CK-373-14", CustomerB, DocumentStatus.Approved);
        var otherSource = AddStockOutDetail(db, otherStockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-373-14", CustomerA, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = (await DetailIdsAsync(db, list.Id)).Single();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db, userId)
            .AssignStockOutLinks(list.Id, Assign((detailId, otherSource.Id))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("客户不属于本装柜清单的权威客户范围", ex.Message);

        Assert.Null((await ReloadAsync(db, list.Id)).Details.Single().SourceStockOutDetailId);
    }

}
