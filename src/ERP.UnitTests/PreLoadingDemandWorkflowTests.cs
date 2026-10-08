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
/// ERP-372 预装柜单业务界面「需求来源」显式链接工作流单元测试。
/// <list type="number">
/// <item><b>业务界面接线</b>：预装柜单行操作「需求来源」→ <c>preloading-demand-links.js</c> 工作台
/// （脚本已在 <c>index.html</c> 注册；单据编辑器 <c>bill-edit.js</c> 暴露只读需求来源列并随行携带持久化链接）；</item>
/// <item><b>服务端权威往返</b>：打开 / 重新加载读取服务端持久化链接状态（历史 <c>null</c> = 显式「未链接」；
/// 来源已不可用 = 显式「来源不可用」且原链接原样保留）；指派 / 清除按**精确**销售订单明细 Id 往返；</item>
/// <item><b>批量原子性</b>：任一行非法则整批拒绝，库中所有行保持原样；</item>
/// <item><b>只读与保留</b>：已审核 / 已取消单据拒绝维护（服务端权威）；明细整体替换与**主表单独更新**都不得
/// 静默清空需求来源证据；</item>
/// <item><b>授权</b>：存在显式链接时读取链接状态必须有既有「销售订单」菜单授权（fail closed，不降级）。</item>
/// </list>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不运行浏览器验收；
/// 真实 SQL Server 往返与两条独立连接竞争见 <c>ERP.IntegrationTests/PreLoadingDemandWorkflowSqlServerTests.cs</c>。</para>
/// </summary>
public class PreLoadingDemandWorkflowTests
{
    private const long CustomerA = 972001L;
    private const long CustomerB = 972002L;
    private const long ProductA = 972101L;
    private const long ProductB = 972102L;

    // ==================== 脚手架与种子数据 ====================

    private static ContainerPreLoadingController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerPreLoadingController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static void SeedCustomer(ErpDbContext db, long id, string name, long? empId = null)
    {
        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = id, CustomerCode = $"C-{id}", CustomerName = name, EmpId = empId, Status = 1
        });
        db.SaveChanges();
    }

    private static void SeedProduct(ErpDbContext db, long id, string name, string unit = "PCS")
    {
        db.BaseProducts.Add(new BaseProduct
        {
            Id = id, ProductCode = $"P-{id}", ProductName = name, Spec = "规格A", Unit = unit
        });
        db.SaveChanges();
    }

    private static ContainerBooking SeedBooking(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var booking = new ContainerBooking
        {
            BookingNo = no, BookingDate = DateTime.Today, CustomerId = customerId,
            Status = status, IsDeleted = deleted
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string no, long customerId,
        DocumentStatus status, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = no, OrderDate = DateTime.Today, CustomerId = customerId,
            Status = status, IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SalesOrderDetail AddOrderDetail(ErpDbContext db, long salesOrderId, long productId,
        decimal quantity, string unit = "PCS", bool deleted = false)
    {
        var detail = new SalesOrderDetail
        {
            SalesOrderId = salesOrderId, ProductId = productId, ProductName = $"商品{productId}",
            Unit = unit, Quantity = quantity, UnitPrice = 10m, Amount = quantity * 10m, IsDeleted = deleted
        };
        db.SalesOrderDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static ContainerPreLoading SeedPreLoading(ErpDbContext db, string no, long? bookingId,
        DocumentStatus status, params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
    {
        var pre = new ContainerPreLoading
        {
            PreLoadingNo = no, LoadingDate = DateTime.Today, BookingId = bookingId, Status = status
        };
        db.ContainerPreLoadings.Add(pre);
        db.SaveChanges();
        foreach (var (productId, quantity, sourceDetailId) in lines)
        {
            db.ContainerPreLoadingDetails.Add(new ContainerPreLoadingDetail
            {
                PreLoadingId = pre.Id, ProductId = productId, ProductName = $"商品{productId}",
                Quantity = quantity, SourceSalesOrderDetailId = sourceDetailId
            });
        }
        db.SaveChanges();
        return pre;
    }

    private static async Task<ContainerPreLoading> ReloadAsync(ErpDbContext db, long id)
        => await db.ContainerPreLoadings.Include(o => o.Details).AsNoTracking().SingleAsync(o => o.Id == id);

    private static async Task<List<long>> DetailIdsAsync(ErpDbContext db, long preLoadingId)
        => await db.ContainerPreLoadingDetails.AsNoTracking()
            .Where(d => d.PreLoadingId == preLoadingId && !d.IsDeleted)
            .OrderBy(d => d.Id).Select(d => d.Id).ToListAsync();

    private static PreLoadingSalesOrderLinkAssignRequest Assign(
        params (long DetailId, long? SourceDetailId)[] links)
        => new()
        {
            Links = links.Select(l => new PreLoadingSalesOrderLinkAssignmentDto
            {
                PreLoadingDetailId = l.DetailId,
                SourceSalesOrderDetailId = l.SourceDetailId,
            }).ToList()
        };

    /// <summary>播种受限制的装柜操作员（业务员映射 + 既有菜单授权），返回其用户 Id 与员工 Id。</summary>
    private static (long UserId, long EmployeeId) SeedRestrictedOperator(ErpDbContext db, params string[] menuCodes)
    {
        var code = $"demand-op-{Guid.NewGuid():N}";
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

        var role = new SysRole { RoleName = "需求来源操作员", RoleCode = $"DemandOp-{Guid.NewGuid():N}", IsSystem = false };
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
                MenuName = menuCode == PreLoadingSalesOrderLinkRules.RequiredMenuCode
                    ? PreLoadingSalesOrderLinkRules.RequiredMenuText
                    : PreLoadingSalesOrderLinkRules.SourceRequiredMenuText,
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

    // ==================== 1. 业务界面接线（保存后行操作 → 需求来源工作台） ====================

    [Fact]
    public void Frontend_saved_draft_row_action_opens_demand_dialog_and_script_is_registered()
    {
        var modules = ReadJs("modules-doc2.js");
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        var dialog = ReadJs("preloading-demand-links.js");

        // 行操作 → 全局函数名成对存在（拼写漂移会让「更多」里的按钮点了没反应）
        Assert.Contains("label: '需求来源'", modules);
        Assert.Contains("onclick: 'openPreLoadingDemandLinks'", modules);
        Assert.Contains("async function openPreLoadingDemandLinks(preLoadingId)", dialog);

        // 脚本必须注册，否则行操作找不到全局函数
        Assert.Contains("/js/preloading-demand-links.js", index);

        // 保存后行操作使用服务端持久化的行 Id 与两个既有接口
        Assert.Contains("/api/container/pre-loadings/${PDL.preLoadingId}", dialog);
        Assert.Contains("/sales-order-candidates?${params.join('&')}", dialog);
        Assert.Contains("sales-order-links`, 'POST'", dialog);
        Assert.Contains("sales-order-links`)", dialog);
    }

    [Fact]
    public void Frontend_dialog_requires_explicit_numeric_selection_and_never_invents_links()
    {
        var dialog = ReadJs("preloading-demand-links.js");

        // 只接受显式正整数选择（空 = 清除）；不解析文本为 Id
        Assert.Contains("function pdlNormalizeSelection(value)", dialog);
        Assert.Contains("/^\\d+$/", dialog);
        Assert.Contains("sourceSalesOrderDetailId: next.value === '' ? null : Number(next.value)", dialog);

        // 未保存单据不得登记链接（绝不臆造服务端行 Id）
        Assert.Contains("PDL_UNSAVED_TEXT", dialog);
        Assert.Contains("pdlGuardSavedId", dialog);

        // 历史未链接 / 来源不可用都是显式事实，绝不静默清除
        Assert.Contains("pdlLineEvidenceText", dialog);
        Assert.Contains("PDL_UNLINKED_TEXT", dialog);
        Assert.Contains("PDL_UNAVAILABLE_TEXT", dialog);
        Assert.Contains("绝不回填", dialog);

        // 双击 / 重复提交与陈旧选择被阻断
        Assert.Contains("if (PDL.submitting) return;", dialog);
        Assert.Contains("pdlStaleSelections", dialog);
        Assert.Contains("PDL.selectionTokens", dialog);

        // 失败保留草稿输入：出错分支只写 error，不清空 draft
        Assert.Contains("PDL.error = pdlErrorMessage(err);", dialog);
        Assert.Contains("PDL.draft = applied.draft;", dialog);

        // 只读：仅「待提交」可维护
        Assert.Contains("pdlIsEditableStatus", dialog);
        Assert.Contains("PDL_EDITABLE_STATUS = 'Pending'", dialog);
    }

    [Fact]
    public void Frontend_bill_editor_exposes_demand_source_column_and_carries_persisted_link()
    {
        var billEdit = ReadJs("bill-edit.js");

        // 其它单据保持原列；只有预装柜单追加「需求来源」只读列
        Assert.Contains("function detailColumnsFor(code)", billEdit);
        Assert.Contains("if (code !== 'pre-loading') return DETAIL_COLUMNS;", billEdit);
        Assert.Contains("type: 'demand-source'", billEdit);
        Assert.Contains("openPreLoadingDemandLinks", billEdit);

        // 持久化链接随行携带并原样回传（明细编辑 / 保存不得静默清空）
        Assert.Contains("tr.dataset.sourceSalesOrderDetailId = persistedSource;", billEdit);
        Assert.Contains("const preserveDemandSource = BILL_CODE === 'pre-loading';", billEdit);
        Assert.Contains("d.SourceSalesOrderDetailId = Number(persisted);", billEdit);

        // 未保存单据不给入口（按钮 disabled），不为未保存单据臆造明细 Id
        Assert.Contains("请先保存该预装柜单，再登记需求来源", billEdit);
    }

    [Fact]
    public void Frontend_automation_interaction_test_exists_and_covers_core_guards()
    {
        var automation = RepoFile("tests", "automation", "preloading-demand-links.test.js");
        Assert.True(File.Exists(automation), "缺少可执行的 JS 交互单测：tests/automation/preloading-demand-links.test.js");

        var script = File.ReadAllText(automation);
        Assert.Contains("require(", script);
        Assert.Contains("pdlGuardSavedId", script);
        Assert.Contains("pdlBuildAssignments", script);
        Assert.Contains("pdlStaleSelections", script);
        Assert.Contains("pdlLineEvidenceText", script);
        Assert.Contains("pdlIsEditableStatus", script);
    }

    [Fact]
    public void Controller_authorization_inherits_base_only()
    {
        var controllerType = typeof(ContainerPreLoadingController);
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
        var booking = SeedBooking(db, "DG-372-01", CustomerA);
        var pre = SeedPreLoading(db, "YZ-372-01", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));

        var lines = AssertOk<List<PreLoadingSalesOrderLinkLineDto>>(
            await NewController(db, userId).GetSalesOrderLinks(pre.Id));

        var line = Assert.Single(lines);
        Assert.Null(line.SourceSalesOrderDetailId);
        Assert.False(line.SourceAvailable);
        Assert.Equal(PreLoadingSalesOrderLinkRules.UnlinkedEvidenceText, line.SourceAvailabilityText);
        Assert.Contains("绝不回填", line.SourceAvailabilityText);
    }

    [Fact]
    public async Task Server_links_endpoint_exposes_unavailable_source_without_clearing_it()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-372-02", CustomerA);

        // 场景一：来源订单被取消（明细仍在，链接必须原样保留）
        var order = SeedSalesOrder(db, "SO-372-02", CustomerA, DocumentStatus.Approved);
        var sourceDetail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var pre = SeedPreLoading(db, "YZ-372-02", booking.Id, DocumentStatus.Pending,
            (ProductA, 6m, sourceDetail.Id));
        order.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var line = Assert.Single(AssertOk<List<PreLoadingSalesOrderLinkLineDto>>(
            await NewController(db, userId).GetSalesOrderLinks(pre.Id)));
        Assert.Equal(sourceDetail.Id, line.SourceSalesOrderDetailId);   // 原链接原样保留
        Assert.False(line.SourceAvailable);
        Assert.Equal(order.Id, line.SalesOrderId);                      // 来源仍可解析（只是不再可用）
        Assert.Equal(PreLoadingSalesOrderLinkRules.UnavailableEvidenceText, line.SourceAvailabilityText);
        Assert.Contains("绝不静默清除", line.SourceAvailabilityText);

        // 场景二：来源明细被删除 —— 仍必须显式暴露「来源不可用」，绝不静默清除
        var deletedOrder = SeedSalesOrder(db, "SO-372-02B", CustomerA, DocumentStatus.Approved);
        var deletedDetail = AddOrderDetail(db, deletedOrder.Id, ProductA, 10m);
        var pre2 = SeedPreLoading(db, "YZ-372-02B", booking.Id, DocumentStatus.Pending,
            (ProductA, 4m, deletedDetail.Id));
        deletedDetail.IsDeleted = true;
        await db.SaveChangesAsync();

        var line2 = Assert.Single(AssertOk<List<PreLoadingSalesOrderLinkLineDto>>(
            await NewController(db, userId).GetSalesOrderLinks(pre2.Id)));
        Assert.Equal(deletedDetail.Id, line2.SourceSalesOrderDetailId);
        Assert.False(line2.SourceAvailable);
        Assert.Equal(PreLoadingSalesOrderLinkRules.UnavailableEvidenceText, line2.SourceAvailabilityText);

        // 服务端持久化的链接编号始终未被清除
        Assert.Equal(sourceDetail.Id, (await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
        Assert.Equal(deletedDetail.Id, (await ReloadAsync(db, pre2.Id)).Details.Single().SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Server_links_endpoint_requires_sales_order_menu_when_link_exists()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, PreLoadingSalesOrderLinkRules.RequiredMenuCode);
        SeedCustomer(db, CustomerA, "自有客户", employeeId);
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-372-03", CustomerA);
        var order = SeedSalesOrder(db, "SO-372-03", CustomerA, DocumentStatus.Approved);
        var sourceDetail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var linked = SeedPreLoading(db, "YZ-372-03", booking.Id, DocumentStatus.Pending,
            (ProductA, 6m, sourceDetail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).GetSalesOrderLinks(linked.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains(PreLoadingSalesOrderLinkRules.SourceRequiredMenuText, ex.Message);

        // 未链接单据未暴露任何销售订单数据：无需销售订单菜单即可读取（仍只显示「未链接」事实）
        var unlinked = SeedPreLoading(db, "YZ-372-03U", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));
        var lines = AssertOk<List<PreLoadingSalesOrderLinkLineDto>>(
            await NewController(db, userId).GetSalesOrderLinks(unlinked.Id));
        Assert.Null(Assert.Single(lines).SourceSalesOrderDetailId);
    }

    [Fact]
    public void Rules_availability_text_helper_is_single_source_of_truth()
    {
        Assert.Equal(PreLoadingSalesOrderLinkRules.UnlinkedEvidenceText,
            PreLoadingSalesOrderLinkRules.SourceAvailabilityTextOf(null, false));
        Assert.Equal(PreLoadingSalesOrderLinkRules.UnlinkedEvidenceText,
            PreLoadingSalesOrderLinkRules.SourceAvailabilityTextOf(0, false));
        Assert.Equal(PreLoadingSalesOrderLinkRules.LinkedEvidenceText,
            PreLoadingSalesOrderLinkRules.SourceAvailabilityTextOf(5, true));
        Assert.Equal(PreLoadingSalesOrderLinkRules.UnavailableEvidenceText,
            PreLoadingSalesOrderLinkRules.SourceAvailabilityTextOf(5, false));
        Assert.Contains("绝不回填", PreLoadingSalesOrderLinkRules.UnlinkedEvidenceText);
        Assert.Contains("原样保留", PreLoadingSalesOrderLinkRules.UnavailableEvidenceText);
    }

    // ==================== 3. 指派 / 清除 精确往返 ====================

    [Fact]
    public async Task Assign_then_reload_roundtrip_links_and_clears_exact_source_detail_ids()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-372-04", CustomerA);
        var order = SeedSalesOrder(db, "SO-372-04", CustomerA, DocumentStatus.Approved);
        var consumed = AddOrderDetail(db, order.Id, ProductA, 10m);
        var available = AddOrderDetail(db, order.Id, ProductA, 10m);

        // 另一张已审核预装柜已链接 consumed 10：该来源剩余为 0，候选必须不再提供它（剩余数量口径）
        SeedPreLoading(db, "YZ-372-04A", booking.Id, DocumentStatus.Approved, (ProductA, 10m, consumed.Id));
        var pre = SeedPreLoading(db, "YZ-372-04", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = (await DetailIdsAsync(db, pre.Id)).Single();

        var candidates = AssertOk<List<PreLoadingSalesOrderCandidateDto>>(
            await NewController(db, userId).GetSalesOrderCandidates(pre.Id, null, 0));
        var candidate = Assert.Single(candidates);
        Assert.Equal(available.Id, candidate.SalesOrderDetailId);
        Assert.Equal(10m, candidate.RemainingBaseQuantity);
        Assert.Equal(order.OrderNo, candidate.OrderNo);

        // 显式指派精确来源销售订单明细 Id
        var linked = AssertOk<PreLoadingSalesOrderLinkAssignResultDto>(
            await NewController(db, userId).AssignSalesOrderLinks(pre.Id, Assign((detailId, available.Id))));
        Assert.Equal(1, linked.LinkedCount);
        Assert.Equal(0, linked.ClearedCount);
        var linkedLine = Assert.Single(linked.Items);
        Assert.Equal(available.Id, linkedLine.SourceSalesOrderDetailId);
        Assert.True(linkedLine.SourceAvailable);
        Assert.Equal(order.OrderNo, linkedLine.OrderNo);
        Assert.Equal(detailId, linkedLine.PreLoadingDetailId);

        // 重新加载服务端持久化结果
        var reloaded = AssertOk<List<PreLoadingSalesOrderLinkLineDto>>(
            await NewController(db, userId).GetSalesOrderLinks(pre.Id));
        Assert.Equal(available.Id, Assert.Single(reloaded).SourceSalesOrderDetailId);
        Assert.Equal(available.Id, (await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);

        // 显式清除（null = 显式未链接）
        var cleared = AssertOk<PreLoadingSalesOrderLinkAssignResultDto>(
            await NewController(db, userId).AssignSalesOrderLinks(pre.Id, Assign((detailId, null))));
        Assert.Equal(0, cleared.LinkedCount);
        Assert.Equal(1, cleared.ClearedCount);
        var clearedLine = Assert.Single(cleared.Items);
        Assert.Null(clearedLine.SourceSalesOrderDetailId);
        Assert.False(clearedLine.SourceAvailable);
        Assert.Equal(PreLoadingSalesOrderLinkRules.UnlinkedEvidenceText, clearedLine.SourceAvailabilityText);
        Assert.Null((await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Assign_rejects_whole_batch_when_any_line_invalid_and_leaves_all_unchanged()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        SeedProduct(db, ProductB, "商品B");
        var booking = SeedBooking(db, "DG-372-05", CustomerA);
        var orderA = SeedSalesOrder(db, "SO-372-05A", CustomerA, DocumentStatus.Approved);
        var detailA = AddOrderDetail(db, orderA.Id, ProductA, 10m);
        var orderB = SeedSalesOrder(db, "SO-372-05B", CustomerA, DocumentStatus.Approved);
        var detailB = AddOrderDetail(db, orderB.Id, ProductB, 10m);
        var pre = SeedPreLoading(db, "YZ-372-05", booking.Id, DocumentStatus.Pending,
            (ProductA, 5m, null), (ProductB, 5m, null));
        var ids = await DetailIdsAsync(db, pre.Id);

        // 第一行合法（商品 A → 订单明细 A），第二行非法（商品 B → 订单明细 A，商品不一致）
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).AssignSalesOrderLinks(pre.Id,
                Assign((ids[0], detailA.Id), (ids[1], detailA.Id))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        // 整批拒绝：库中**所有**行保持原样（一行都不许写入）
        Assert.All((await ReloadAsync(db, pre.Id)).Details, d => Assert.Null(d.SourceSalesOrderDetailId));
        Assert.Equal(ids.Count, (await DetailIdsAsync(db, pre.Id)).Count);
        Assert.Equal(DocumentStatus.Pending, (await ReloadAsync(db, pre.Id)).Status);

        // 两份候选仍可用（未因失败批次被消耗 / 污染）
        var candidates = AssertOk<List<PreLoadingSalesOrderCandidateDto>>(
            await NewController(db, userId).GetSalesOrderCandidates(pre.Id, null, 0));
        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, c => c.SalesOrderDetailId == detailA.Id);
        Assert.Contains(candidates, c => c.SalesOrderDetailId == detailB.Id);
    }

    // ==================== 4. 只读与保留 ====================

    [Fact]
    public async Task Approved_and_cancelled_documents_are_read_only_for_link_maintenance()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-372-06", CustomerA);
        var order = SeedSalesOrder(db, "SO-372-06", CustomerA, DocumentStatus.Approved);
        var sourceDetail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var approved = SeedPreLoading(db, "YZ-372-06A", booking.Id, DocumentStatus.Approved, (ProductA, 6m, null));
        var cancelled = SeedPreLoading(db, "YZ-372-06C", booking.Id, DocumentStatus.Cancelled, (ProductA, 6m, null));

        foreach (var doc in new[] { approved, cancelled })
        {
            var detailId = (await DetailIdsAsync(db, doc.Id)).Single();
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                NewController(db, userId).AssignSalesOrderLinks(doc.Id, Assign((detailId, sourceDetail.Id))));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Null((await ReloadAsync(db, doc.Id)).Details.Single().SourceSalesOrderDetailId);
        }

        // 只读查看仍可用（证据可读，写入被服务端权威拒绝）
        var approvedLines = AssertOk<List<PreLoadingSalesOrderLinkLineDto>>(
            await NewController(db, userId).GetSalesOrderLinks(approved.Id));
        Assert.False(Assert.Single(approvedLines).SourceAvailable);
    }

    [Fact]
    public async Task Header_only_update_preserves_details_and_demand_links()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-372-07", CustomerA);
        var order = SeedSalesOrder(db, "SO-372-07", CustomerA, DocumentStatus.Approved);
        var sourceDetail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var pre = SeedPreLoading(db, "YZ-372-07", booking.Id, DocumentStatus.Pending,
            (ProductA, 6m, sourceDetail.Id));
        var originalDetailId = (await DetailIdsAsync(db, pre.Id)).Single();

        // 主表单独更新（不带明细，模拟业务界面侧栏编辑主表）：明细与需求来源证据必须原样保留
        var result = await NewController(db, userId).Update(pre.Id, new ContainerPreLoading
        {
            LoadingDate = DateTime.Today,
            BookingId = booking.Id,
            ContainerNo = "CTN-372-UPD",
            SealNo = "SEAL-372",
            Remark = "仅更新主表",
            Details = new List<ContainerPreLoadingDetail>()
        });
        Assert.IsType<OkObjectResult>(result);

        var reloaded = await ReloadAsync(db, pre.Id);
        Assert.Equal("CTN-372-UPD", reloaded.ContainerNo);
        Assert.Equal("SEAL-372", reloaded.SealNo);
        Assert.Equal("仅更新主表", reloaded.Remark);
        var detail = Assert.Single(reloaded.Details);
        Assert.Equal(originalDetailId, detail.Id);
        Assert.Equal(6m, detail.Quantity);
        Assert.Equal(sourceDetail.Id, detail.SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Detail_update_carrying_same_source_detail_id_preserves_link()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-372-08", CustomerA);
        var order = SeedSalesOrder(db, "SO-372-08", CustomerA, DocumentStatus.Approved);
        var sourceDetail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var pre = SeedPreLoading(db, "YZ-372-08", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));

        // 正常明细编辑：显式随行携带同一 SourceSalesOrderDetailId（来自需求来源工作台的显式选择）
        var result = await NewController(db, userId).Update(pre.Id, new ContainerPreLoading
        {
            LoadingDate = DateTime.Today,
            BookingId = booking.Id,
            ContainerNo = "CTN-372-DTL",
            Details = new List<ContainerPreLoadingDetail>
            {
                new()
                {
                    ProductId = ProductA, ProductName = "商品A", Quantity = 4m,
                    Cartons = 1m, Weight = 1m, Volume = 1m, SourceSalesOrderDetailId = sourceDetail.Id
                }
            }
        });
        Assert.IsType<OkObjectResult>(result);

        var reloaded = await ReloadAsync(db, pre.Id);
        var detail = Assert.Single(reloaded.Details);
        Assert.Equal(ProductA, detail.ProductId);
        Assert.Equal(4m, detail.Quantity);
        Assert.Equal(sourceDetail.Id, detail.SourceSalesOrderDetailId);

        // 服务端权威回显也认这条链接（有效证据）
        var line = Assert.Single(AssertOk<List<PreLoadingSalesOrderLinkLineDto>>(
            await NewController(db, userId).GetSalesOrderLinks(pre.Id)));
        Assert.True(line.SourceAvailable);
        Assert.Equal(order.OrderNo, line.OrderNo);
    }

    // ==================== 5. 既有授权（真实登录账号 + 既有菜单，无管理员降级） ====================

    [Fact]
    public async Task Restricted_operator_with_existing_menus_completes_roundtrip_on_own_customer()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db,
            PreLoadingSalesOrderLinkRules.RequiredMenuCode, PreLoadingSalesOrderLinkRules.SourceRequiredMenuCode);
        SeedCustomer(db, CustomerA, "自有客户", employeeId);
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-372-10", CustomerA);
        var order = SeedSalesOrder(db, "SO-372-10", CustomerA, DocumentStatus.Approved);
        var sourceDetail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var pre = SeedPreLoading(db, "YZ-372-10", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = (await DetailIdsAsync(db, pre.Id)).Single();

        var candidates = AssertOk<List<PreLoadingSalesOrderCandidateDto>>(
            await NewController(db, userId).GetSalesOrderCandidates(pre.Id, null, 0));
        Assert.Equal(sourceDetail.Id, Assert.Single(candidates).SalesOrderDetailId);

        var linked = AssertOk<PreLoadingSalesOrderLinkAssignResultDto>(
            await NewController(db, userId).AssignSalesOrderLinks(pre.Id, Assign((detailId, sourceDetail.Id))));
        Assert.Equal(1, linked.LinkedCount);
        Assert.Equal(sourceDetail.Id, (await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }
}






