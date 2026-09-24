using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-035 报价单版本链（多轮议价版本留痕）单元测试：
/// 复制隔离（源版本一行不改）、合计服务端权威复算、版本号服务端分配与链内单调递增、
/// 并发唯一索引冲突的错误映射、历史报价单兼容（无版本元数据 = 初始版本 V1，不回填）、
/// 历史版本只读（不可改 / 提交 / 审核 / 销审 / 取消 / 删除）、
/// 下游转 PI / 销售订单只认被显式选中的版本、以及前端接线与结构脚本契约。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed。</para>
/// </summary>
public class QuotationRevisionTests
{
    private const string RootNo = "QT2609240001";

    // ==================== 1. 复制隔离与合计权威复算 ====================

    [Fact]
    public async Task CreateRevision_copies_negotiable_values_and_recalculates_totals_without_touching_source()
    {
        using var db = TestDbFactory.Create();
        var source = SeedSource(db);
        var sourceLines = SourceLines(db, source.Id);

        var result = GetData<QuotationRevisionResult>(await Controller(db).CreateRevision(source.Id));

        var revision = db.Quotations.AsNoTracking().Include(q => q.Details).Single(q => q.Id == result.Id);
        Assert.NotEqual(source.Id, revision.Id);
        Assert.Equal($"{RootNo}-R2", revision.QuotationNo);
        Assert.Equal(2, revision.RevisionNumber);
        Assert.Equal(DocumentStatus.Pending, revision.Status);              // 审核状态不复制 → 新版本一律草稿
        Assert.Equal(source.Id, revision.PreviousRevisionId);
        Assert.Equal(RootNo, revision.PreviousRevisionNo);
        Assert.Equal(source.Id, revision.RootQuotationId);
        Assert.Equal(RootNo, revision.RootQuotationNo);
        Assert.Equal(DateTime.Today, revision.QuotationDate);
        Assert.Equal(source.ValidUntil, revision.ValidUntil);               // 有效期是可议价条款：原样继承

        // 可议价主表字段整体复制（客户 / 联系 / 来源询价 / 贸易条款 / 港口 / 付款 / 交期 / 币种汇率 / 业务员 / 备注）
        Assert.Equal(source.CustomerId, revision.CustomerId);
        Assert.Equal(source.CustomerName, revision.CustomerName);
        Assert.Equal(source.ContactPerson, revision.ContactPerson);
        Assert.Equal(source.ContactPhone, revision.ContactPhone);
        Assert.Equal(source.ContactEmail, revision.ContactEmail);
        Assert.Equal(source.InquiryId, revision.InquiryId);
        Assert.Equal(source.InquiryNo, revision.InquiryNo);
        Assert.Equal(source.TradeTerms, revision.TradeTerms);
        Assert.Equal(source.PortOfLoading, revision.PortOfLoading);
        Assert.Equal(source.PortOfDestination, revision.PortOfDestination);
        Assert.Equal(source.PaymentTerms, revision.PaymentTerms);
        Assert.Equal(source.LeadTime, revision.LeadTime);
        Assert.Equal(source.Currency, revision.Currency);
        Assert.Equal(source.ExchangeRate, revision.ExchangeRate);
        Assert.Equal(source.SalesmanId, revision.SalesmanId);
        Assert.Equal(source.SalesmanName, revision.SalesmanName);
        Assert.Equal(source.Remark, revision.Remark);

        // 明细：只复制未删除行、行号重排 1..n、金额与合计由服务端复算（源行的被篡改金额不被继承）
        Assert.Equal(2, revision.Details.Count);
        Assert.Equal(new[] { "P001", "P002" }, revision.Details.OrderBy(d => d.SortNo).Select(d => d.ProductCode));
        Assert.Equal(new[] { 1, 2 }, revision.Details.OrderBy(d => d.SortNo).Select(d => d.SortNo));
        Assert.Equal(new[] { 250m, 500m }, revision.Details.OrderBy(d => d.SortNo).Select(d => d.Amount));
        Assert.Equal(750m, revision.TotalAmount);
        Assert.Equal(5400m, revision.TotalAmountCny);
        Assert.All(revision.Details, d => Assert.Equal(revision.QuotationNo, d.QuotationNo));
        Assert.All(revision.Details, d => Assert.Equal(revision.Id, d.QuotationId));
        Assert.DoesNotContain(revision.Details, d => d.ProductCode == "P003");       // 源版本的软删除行不复制

        // 源版本（含明细主键 / 行号 / 被篡改的金额）一行未变
        var sourceAfter = db.Quotations.AsNoTracking().Single(q => q.Id == source.Id);
        Assert.Equal(DocumentStatus.Approved, sourceAfter.Status);
        Assert.Equal(1, sourceAfter.RevisionNumber);
        Assert.Null(sourceAfter.RootQuotationId);
        Assert.Equal(string.Empty, sourceAfter.RootQuotationNo);
        Assert.Equal(string.Empty, sourceAfter.PreviousRevisionNo);
        Assert.Equal(750m, sourceAfter.TotalAmount);
        Assert.Equal(5400m, sourceAfter.TotalAmountCny);
        Assert.Equal(sourceLines, SourceLines(db, source.Id));
    }

    [Fact]
    public async Task CreateRevision_rejects_missing_soft_deleted_and_cancelled_sources_without_writing()
    {
        using var db = TestDbFactory.Create();
        var cancelled = SeedSource(db, "QT-REV-CANCELLED", DocumentStatus.Cancelled);
        var deleted = SeedSource(db, "QT-REV-DELETED", DocumentStatus.Pending);
        deleted.IsDeleted = true;
        db.SaveChanges();
        var ctl = Controller(db);

        var missing = await Assert.ThrowsAsync<BusinessException>(() => ctl.CreateRevision(999999));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var softDeleted = await Assert.ThrowsAsync<BusinessException>(() => ctl.CreateRevision(deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, softDeleted.Code);

        var badStatus = await Assert.ThrowsAsync<BusinessException>(() => ctl.CreateRevision(cancelled.Id));
        Assert.Equal(ErrorCodes.RuleConflict, badStatus.Code);
        Assert.Contains("已作废", badStatus.Message);

        Assert.Equal(2, db.Quotations.Count());                 // 未新增任何版本
    }

    // ==================== 2. 版本号服务端分配：链内单调递增 + 历史根单兼容 ====================

    [Fact]
    public async Task CreateRevision_assigns_monotonic_chain_numbers_from_a_legacy_root_without_rewriting_it()
    {
        using var db = TestDbFactory.Create();
        // 历史报价单：新增版本列之前创建 → RevisionNumber 未赋值（0）、无任何版本元数据
        var legacyRoot = SeedSource(db, RootNo, DocumentStatus.Pending, revisionNumber: 0);
        legacyRoot.UpdatedAt = new DateTime(2026, 9, 2, 8, 0, 0);
        db.SaveChanges();
        var rootUpdatedAt = legacyRoot.UpdatedAt;
        var ctl = Controller(db);

        var v2 = GetData<QuotationRevisionResult>(await ctl.CreateRevision(legacyRoot.Id));
        var v3 = GetData<QuotationRevisionResult>(await ctl.CreateRevision(v2.Id));           // 从最新版本继续
        var v4 = GetData<QuotationRevisionResult>(await ctl.CreateRevision(legacyRoot.Id));   // 从历史根分支：仍取链内最大值 + 1

        Assert.Equal(new[] { 2, 3, 4 }, new[] { v2.RevisionNumber, v3.RevisionNumber, v4.RevisionNumber });
        Assert.Equal(new[] { $"{RootNo}-R2", $"{RootNo}-R3", $"{RootNo}-R4" },
            new[] { v2.QuotationNo, v3.QuotationNo, v4.QuotationNo });
        Assert.All(new[] { v2, v3, v4 }, r => Assert.Equal(legacyRoot.Id, r.RootQuotationId));
        Assert.All(new[] { v2, v3, v4 }, r => Assert.Equal(RootNo, r.RootQuotationNo));
        Assert.Equal(legacyRoot.Id, v2.PreviousRevisionId);
        Assert.Equal(v2.Id, v3.PreviousRevisionId);
        Assert.Equal(v2.QuotationNo, v3.PreviousRevisionNo);
        Assert.Equal(legacyRoot.Id, v4.PreviousRevisionId);

        // 历史根单保持原样：不回填版本号、不写根单、不更新时间戳、状态不变
        var rootAfter = db.Quotations.AsNoTracking().Single(q => q.Id == legacyRoot.Id);
        Assert.Equal(0, rootAfter.RevisionNumber);
        Assert.Null(rootAfter.RootQuotationId);
        Assert.Equal(string.Empty, rootAfter.RootQuotationNo);
        Assert.Equal(string.Empty, rootAfter.PreviousRevisionNo);
        Assert.Equal(DocumentStatus.Pending, rootAfter.Status);
        Assert.Equal(rootUpdatedAt, rootAfter.UpdatedAt);

        // 版本链：根单 + 全部历史版本（不隐藏历史），按版本号升序，历史根按初始版本 V1 呈现
        var chain = GetData<List<QuotationRevisionChainItem>>(await ctl.GetRevisions(legacyRoot.Id));
        Assert.Equal(new[] { 1, 2, 3, 4 }, chain.Select(c => c.RevisionNumber));
        Assert.All(chain, c => Assert.Equal(RootNo, c.RootQuotationNo));
        Assert.All(chain, c => Assert.Equal(legacyRoot.Id, c.RootQuotationId));
        Assert.True(chain[0].IsInitialRevision);
        Assert.Null(chain[0].PreviousRevisionId);
        Assert.Equal(string.Empty, chain[0].PreviousRevisionNo);
        Assert.True(chain[0].IsSelected);                        // 本次显式选中的版本
        Assert.False(chain[0].IsLatest);
        Assert.True(chain[0].Superseded);                        // 已有后续版本 → 只读历史
        Assert.True(chain[1].Superseded);
        Assert.False(chain[2].Superseded);                       // V3 没有直接后续版本（V4 从根单分支）
        Assert.False(chain[3].Superseded);
        Assert.True(chain[3].IsLatest);
        Assert.Equal($"{RootNo}-R2", chain[2].PreviousRevisionNo);
        Assert.Equal($"{RootNo}-R4", chain[3].QuotationNo);
        Assert.Contains("版本链共 4 个版本", OkMessage(await ctl.GetRevisions(legacyRoot.Id)));
    }

    [Fact]
    public async Task CreateRevision_skips_numbers_taken_outside_the_chain_and_rejects_overlong_numbers()
    {
        using var db = TestDbFactory.Create();
        var root = SeedSource(db);
        // 链外单据（手工建单）占用根单号的 -R2 → 新版本必须跳过它，仍保持单调递增且不重复
        db.Quotations.Add(new Quotation { QuotationNo = $"{RootNo}-R2", QuotationDate = DateTime.Today, CustomerName = "手工占用" });
        db.SaveChanges();
        var ctl = Controller(db);

        var revision = GetData<QuotationRevisionResult>(await ctl.CreateRevision(root.Id));
        Assert.Equal(3, revision.RevisionNumber);
        Assert.Equal($"{RootNo}-R3", revision.QuotationNo);
        Assert.Equal(1, db.Quotations.Count(q => q.QuotationNo == $"{RootNo}-R2"));

        // 单号长度上限（QuotationNo 为 nvarchar(50)）：超长直接拒绝，不做静默截断
        var overlong = SeedSource(db, new string('Q', 50), DocumentStatus.Pending);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.CreateRevision(overlong.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("50", ex.Message);
        Assert.Equal(0, db.Quotations.Count(q => q.RootQuotationId == overlong.Id));   // 未产生任何版本
    }

    // ==================== 3. 历史版本只读（不可变历史） ====================

    [Fact]
    public async Task Superseded_revision_is_read_only_while_the_latest_revision_stays_operable()
    {
        using var db = TestDbFactory.Create();
        var root = SeedSource(db);
        var ctl = Controller(db);
        var v2 = GetData<QuotationRevisionResult>(await ctl.CreateRevision(root.Id));

        Assert.True(await QuotationRevisionService.IsSupersededAsync(db, root.Id));
        Assert.False(await QuotationRevisionService.IsSupersededAsync(db, v2.Id));

        var update = new Quotation
        {
            QuotationDate = DateTime.Today,
            CustomerName = "改名客户",
            Currency = Currency.USD,
            ExchangeRate = 1m,
            Details = new List<QuotationDetail> { Detail("NEW-1", 2m, 3m, 1) }
        };
        var blocked = new (string Action, Func<Task<IActionResult>> Call)[]
        {
            ("修改", () => ctl.Update(root.Id, update)),
            ("提交", () => ctl.Submit(root.Id)),
            ("审核", () => ctl.Approve(root.Id)),
            ("销审", () => ctl.Unaudit(root.Id)),
            ("取消", () => ctl.Cancel(root.Id)),
            ("删除", () => ctl.Delete(root.Id))
        };
        foreach (var (action, call) in blocked)
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(call);
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains("历史版本只读", ex.Message);
            Assert.False(string.IsNullOrWhiteSpace(action));
        }

        // 源版本数据与状态一行未变（客户名、状态、软删除标记、合计、明细行数）
        var rootAfter = db.Quotations.AsNoTracking().Single(q => q.Id == root.Id);
        Assert.Equal("义乌客户（版本测试）", rootAfter.CustomerName);
        Assert.Equal(DocumentStatus.Approved, rootAfter.Status);
        Assert.False(rootAfter.IsDeleted);
        Assert.Equal(750m, rootAfter.TotalAmount);
        Assert.Equal(2, db.QuotationDetails.Count(d => d.QuotationId == root.Id && !d.IsDeleted));

        // 最新版本不受影响：可修改、可审核（不是「整条链冻结」，也不是「静默改到别的版本」）
        Assert.IsType<OkObjectResult>(await ctl.Update(v2.Id, update));
        Assert.IsType<OkObjectResult>(await ctl.Approve(v2.Id));
        var latest = db.Quotations.AsNoTracking().Single(q => q.Id == v2.Id);
        Assert.Equal("改名客户", latest.CustomerName);
        Assert.Equal(DocumentStatus.Approved, latest.Status);
        Assert.Equal($"{RootNo}-R2", latest.QuotationNo);                    // 单号不可被客户端改掉
        Assert.Equal(6m, latest.TotalAmount);                                // 服务端复算：2 × 3
    }

    // ==================== 4. 下游转换只认被显式选中的版本 ====================

    [Fact]
    public async Task Conversion_uses_the_explicitly_selected_revision_and_never_switches_to_another_one()
    {
        using var db = TestDbFactory.Create();
        var root = SeedSource(db);                                            // 已审核
        var ctl = Controller(db);

        // 根单（V1）转 PI：PI 只挂 V1，V1 变「已完成」
        Assert.IsType<OkObjectResult>(await ctl.ToProformaInvoice(root.Id));
        var rootPi = db.ProformaInvoices.AsNoTracking().Single(p => p.QuotationId == root.Id);
        Assert.Equal(DocumentStatus.Completed, db.Quotations.AsNoTracking().Single(q => q.Id == root.Id).Status);

        // 以已转 PI 的版本创建新版本：新版本是独立草稿，不复制下游转换状态与单据链接
        var v2 = GetData<QuotationRevisionResult>(await ctl.CreateRevision(root.Id));
        Assert.Equal(DocumentStatus.Pending, db.Quotations.AsNoTracking().Single(q => q.Id == v2.Id).Status);
        Assert.Null(db.ProformaInvoices.AsNoTracking().FirstOrDefault(p => p.QuotationId == v2.Id));
        var draftConversion = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToProformaInvoice(v2.Id));
        Assert.Contains("未审核", draftConversion.Message);                    // 绝不静默改用 V1 的审核状态
        Assert.Equal(1, db.ProformaInvoices.Count());

        // 审核 V2 后转 PI：新增一张只挂 V2 的 PI，V1 的 PI 与状态保持不变
        // （每个接口请求在服务端都是独立的 DbContext 作用域，这里清空跟踪器以模拟真实请求边界：
        //   审核接口只会加载主表、不加载明细，因此「审核后明细仍在」正是生产行为）
        FreshRequest(db);
        Assert.IsType<OkObjectResult>(await ctl.Approve(v2.Id));
        Assert.IsType<OkObjectResult>(await ctl.ToProformaInvoice(v2.Id));
        Assert.Equal(2, db.ProformaInvoices.Count());
        Assert.Equal(rootPi.PiNo, db.ProformaInvoices.AsNoTracking().Single(p => p.QuotationId == root.Id).PiNo);
        Assert.Equal(DocumentStatus.Completed, db.Quotations.AsNoTracking().Single(q => q.Id == v2.Id).Status);
        Assert.Equal(2, db.QuotationDetails.Count(d => d.QuotationId == v2.Id && !d.IsDeleted));   // 审核不清空明细

        // 转销售订单同理：来源留痕指向被选中的版本，不会挂到根单
        var v3 = GetData<QuotationRevisionResult>(await ctl.CreateRevision(v2.Id));
        FreshRequest(db);
        Assert.IsType<OkObjectResult>(await ctl.Approve(v3.Id));
        Assert.IsType<OkObjectResult>(await ctl.ToSalesOrder(v3.Id));
        var order = db.SalesOrders.AsNoTracking().Single();
        Assert.Equal(v3.Id, order.SourceQuotationId);
        Assert.Equal(v3.QuotationNo, order.SourceQuotationNo);
        Assert.Null(db.SalesOrders.AsNoTracking().FirstOrDefault(o => o.SourceQuotationId == root.Id || o.SourceQuotationId == v2.Id));
        Assert.Equal(2, db.SalesOrderDetails.Count(d => d.SalesOrderId == order.Id));

        // 版本链上的「已转出」只标记被转换的那一张；状态与金额独立
        var chain = GetData<List<QuotationRevisionChainItem>>(await ctl.GetRevisions(root.Id));
        Assert.Equal(3, chain.Count);
        Assert.All(chain, c => Assert.True(c.Converted));
        Assert.Equal(DocumentStatus.Completed, chain.Single(c => c.Id == v3.Id).Status);
        Assert.Equal(750m, chain.Single(c => c.Id == v2.Id).TotalAmount);
    }

    // ==================== 5. 并发保护：唯一索引冲突的错误映射 ====================

    [Fact]
    public async Task CreateRevision_maps_duplicate_revision_number_conflicts_to_a_retryable_business_error()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new FailingContext(options)
        {
            // 与 SQL Server 唯一索引冲突同形状：2601 duplicate key（另一个请求已插入同版本号）
            Failure = new DbUpdateException("Violation of UNIQUE KEY constraint 'UX_Quotations_RevisionChain'. 2601 duplicate key row")
        };
        var root = SeedSource(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => Controller(db).CreateRevision(root.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(QuotationRevisionService.ConcurrencyConflictMessage, ex.Message);
        Assert.Equal(1, db.Quotations.Count());                     // 链内不会出现重复版本号（本次插入被拒绝）
        Assert.Equal(3, db.QuotationDetails.Count());                // 也没有半成品明细
    }

    [Fact]
    public async Task CreateRevision_does_not_mask_non_unique_database_failures()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new FailingContext(options)
        {
            Failure = new DbUpdateException("Invalid column name 'RevisionNumber'.")   // 结构缺失等真实故障
        };
        var root = SeedSource(db);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => Controller(db).CreateRevision(root.Id));

        Assert.Contains("Invalid column name", ex.Message);         // 原样上抛，不误报成「并发冲突」
    }

    [Fact]
    public void Model_declares_the_filtered_unique_index_protecting_chain_revision_numbers()
    {
        using var db = TestDbFactory.Create();
        var entity = db.Model.FindEntityType(typeof(Quotation));
        Assert.NotNull(entity);

        var index = Assert.Single(entity!.GetIndexes(), i =>
            i.Properties.Any(p => p.Name == nameof(Quotation.RootQuotationId))
            && i.Properties.Any(p => p.Name == nameof(Quotation.RevisionNumber)));

        Assert.True(index.IsUnique);
        Assert.Equal("UX_Quotations_RevisionChain", index.GetDatabaseName());
        Assert.NotNull(index.GetFilter());
        Assert.Contains("IsDeleted = 0", index.GetFilter());
        Assert.Contains("RootQuotationId IS NOT NULL", index.GetFilter());
        // 软删除行与根单（RootQuotationId 为 NULL）不占用版本号
        Assert.False(index.GetFilter()!.Contains("RevisionNumber > 0", StringComparison.Ordinal));
    }

    [Fact]
    public void Schema_upgrade_adds_revision_metadata_and_the_chain_unique_index_idempotently()
    {
        var script = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF COL_LENGTH('db_owner.Quotations', 'RevisionNumber') IS NULL", script);
        Assert.Contains("ADD RevisionNumber INT NOT NULL DEFAULT 1", script);        // 历史数据默认初始版本 V1
        Assert.Contains("IF COL_LENGTH('db_owner.Quotations', 'RootQuotationId') IS NULL", script);
        Assert.Contains("ADD RootQuotationId BIGINT NULL", script);
        Assert.Contains("ADD RootQuotationNo NVARCHAR(50) NOT NULL DEFAULT N''", script);
        Assert.Contains("ADD PreviousRevisionId BIGINT NULL", script);
        Assert.Contains("ADD PreviousRevisionNo NVARCHAR(50) NOT NULL DEFAULT N''", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_Quotations_RevisionChain", script);
        Assert.Contains("WHERE IsDeleted = 0 AND RootQuotationId IS NOT NULL", script);
        Assert.DoesNotContain("UPDATE db_owner.Quotations SET RevisionNumber", script);   // 不做历史回填
    }

    // ==================== 6. 历史报价单兼容（只读、不回填）与接口 / 前端接线 ====================

    [Fact]
    public async Task Legacy_quotations_without_metadata_stay_readable_as_initial_revision_and_are_not_rewritten()
    {
        using var db = TestDbFactory.Create();
        var legacy = SeedSource(db, "QT-LEGACY-1", DocumentStatus.Approved, revisionNumber: 0);
        legacy.UpdatedAt = new DateTime(2026, 9, 2, 8, 0, 0);
        db.SaveChanges();
        var updatedAt = legacy.UpdatedAt;
        var ctl = Controller(db);

        // 详情 / 列表照常可读：库中原值原样返回（历史报价单未被改写）
        var detail = GetData<Quotation>(await ctl.GetById(legacy.Id));
        Assert.Equal(0, detail.RevisionNumber);
        Assert.Null(detail.RootQuotationId);
        Assert.Empty(detail.RootQuotationNo);
        Assert.Equal(string.Empty, detail.PreviousRevisionNo);
        Assert.Equal(DocumentStatus.Approved, detail.Status);

        var page = GetData<PagedResult<Quotation>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null, null, null));
        Assert.Equal("QT-LEGACY-1", Assert.Single(page.Items).QuotationNo);

        // 版本链：无版本元数据 = 初始版本 V1（读取口径），根单即自身
        var only = Assert.Single(GetData<List<QuotationRevisionChainItem>>(await ctl.GetRevisions(legacy.Id)));
        Assert.Equal(1, only.RevisionNumber);
        Assert.Equal("QT-LEGACY-1", only.RootQuotationNo);
        Assert.Equal(legacy.Id, only.RootQuotationId);
        Assert.True(only.IsInitialRevision);
        Assert.True(only.IsLatest);
        Assert.True(only.IsSelected);
        Assert.False(only.Superseded);
        Assert.False(only.Converted);

        // 读取不写库：不回填版本号 / 根单，也不刷新时间戳
        var after = db.Quotations.AsNoTracking().Single(q => q.Id == legacy.Id);
        Assert.Equal(0, after.RevisionNumber);
        Assert.Null(after.RootQuotationId);
        Assert.Equal(string.Empty, after.RootQuotationNo);
        Assert.Equal(updatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task Revision_endpoints_and_frontend_actions_are_wired_end_to_end()
    {
        using var db = TestDbFactory.Create();
        var root = SeedSource(db);
        var ctl = Controller(db);

        // 后端路由契约：POST / {id}/revisions（创建版本）+ GET / {id}/revisions（版本链）
        Assert.Contains(GetTemplates(typeof(QuotationController), nameof(QuotationController.CreateRevision)),
            t => t == "{id:long}/revisions");
        Assert.Contains(GetTemplates(typeof(QuotationController), nameof(QuotationController.GetRevisions)),
            t => t == "{id:long}/revisions");

        var missing = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetRevisions(999999));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        var softDeletedId = await SoftDeleteAsync(db, root.Id);
        var softDeleted = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetRevisions(softDeletedId));
        Assert.Equal(ErrorCodes.NotFound, softDeleted.Code);

        // 前端接线：列表派生列 + 行操作 + 接口路径（拼写漂移会让按钮点了没反应）
        var modules = File.ReadAllText(Path.Combine(JsDirectory(), "modules.js"));
        var salesPi = File.ReadAllText(Path.Combine(JsDirectory(), "sales-pi.js"));
        Assert.Contains("onclick: 'quotationCreateRevision'", modules);
        Assert.Contains("onclick: 'quotationRevisionHistory'", modules);
        Assert.Contains("render: row => quotationRevisionBadge(row)", modules);
        Assert.Contains("render: row => quotationRootNo(row)", modules);
        Assert.Contains("render: row => quotationPreviousNo(row)", modules);
        // 详情表单（只读展示版本号 / 版本链根单号 / 上一版本）
        Assert.Contains("label: '版本号（只读，服务端分配）'", modules);
        Assert.Contains("label: '版本链根单号（只读）'", modules);
        Assert.Contains("label: '上一版本（只读）'", modules);
        foreach (var function in new[] { "quotationRevisionNumberOf", "quotationRevisionBadge", "quotationRootNo", "quotationPreviousNo", "quotationCreateRevision", "quotationRevisionHistory" })
            Assert.Contains($"function {function}(", salesPi);
        Assert.Contains("`/api/sales/quotations/${id}/revisions`", salesPi);
        Assert.Contains("`/api/sales/quotations/${id}/revisions`, 'POST'", salesPi);
        Assert.Contains("历史只读", salesPi);                                   // 版本链弹窗标记已取代的历史版本
    }

    // ==================== 工厂与种子数据 ====================

    private static QuotationController Controller(ErpDbContext db) => new(db, new DocumentNumberService(db));

    /// <summary>模拟「新的接口请求」= 新的 DbContext 作用域（清空变更跟踪器）</summary>
    private static void FreshRequest(ErpDbContext db) => db.ChangeTracker.Clear();

    private static async Task<long> SoftDeleteAsync(ErpDbContext db, long id)
    {
        var quotation = await db.Quotations.SingleAsync(q => q.Id == id);
        quotation.IsDeleted = true;
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>读取控制器返回的统一响应数据</summary>
    private static T GetData<T>(IActionResult action)
    {
        var response = Assert.IsType<ApiResponse<T>>(Assert.IsType<OkObjectResult>(action).Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        return response.Data!;
    }

    /// <summary>读取版本链响应中的提示文案（版本链长度也在其中）</summary>
    private static string OkMessage(IActionResult action)
        => Assert.IsType<ApiResponse<List<QuotationRevisionChainItem>>>(Assert.IsType<OkObjectResult>(action).Value).Message ?? string.Empty;

    /// <summary>源版本明细的持久化快照（主键 / 行号 / 金额 / 软删除标记 / 商品编码），用于断言「源版本一行未改」</summary>
    private static string SourceLines(ErpDbContext db, long quotationId) => string.Join(";", db.QuotationDetails.AsNoTracking()
        .Where(d => d.QuotationId == quotationId).OrderBy(d => d.Id)
        .Select(d => $"{d.Id}|{d.SortNo}|{d.Amount}|{d.IsDeleted}|{d.ProductCode}").ToList());

    /// <summary>读取控制器方法上的 HTTP 路由模板</summary>
    private static List<string> GetTemplates(Type controller, string methodName)
    {
        var method = controller.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        return method!.GetCustomAttributes<HttpMethodAttribute>(true)
            .Select(a => a.Template ?? string.Empty).ToList();
    }

    /// <summary>前端脚本目录（沿测试程序集输出目录上溯到仓库根，与 UiTestFixture 同一约定）</summary>
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    /// <summary>仓库根下的文件绝对路径</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static QuotationDetail Detail(string code, decimal quantity, decimal price, int sortNo) => new()
    {
        ProductId = sortNo, ProductCode = code, ProductName = "商品 " + code, Spec = "大", Unit = "PCS",
        Quantity = quantity, UnitPrice = price, Amount = 0m, SortNo = sortNo, Moq = "500 pcs/款", Remark = "红色"
    };

    /// <summary>
    /// 源报价单：可议价字段齐备；两行有效明细（来源金额故意写错，用于验证服务端复算）+
    /// 一行软删除明细（不参与复制）。<paramref name="revisionNumber"/> 传 0 可模拟「新增版本列之前创建」的历史报价单。
    /// </summary>
    private static Quotation SeedSource(ErpDbContext db, string no = RootNo,
        DocumentStatus status = DocumentStatus.Approved, int revisionNumber = 1)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            QuotationDate = new DateTime(2026, 9, 1),
            ValidUntil = new DateTime(2026, 10, 1),
            CustomerId = 7L,
            CustomerName = "义乌客户（版本测试）",
            ContactPerson = "Mr. Smith",
            ContactPhone = "138-0000-0000",
            ContactEmail = "smith@example.com",
            InquiryId = 3L,
            InquiryNo = "INQ-REV-1",
            TradeTerms = "FOB",
            PortOfLoading = "NINGBO",
            PortOfDestination = "HAMBURG",
            PaymentTerms = "T/T 30% deposit",
            LeadTime = "35 days after deposit",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            SalesmanId = 9L,
            SalesmanName = "验收业务员",
            Status = status,
            Remark = "REVISION_TEST",
            RevisionNumber = revisionNumber,
            TotalAmount = 750m,                       // 100 × 2.5 + 200 × 2.5（不是明细里被篡改的金额）
            TotalAmountCny = 5400m,
            Details = new List<QuotationDetail>
            {
                new() { ProductId = 11L, ProductCode = "P001", ProductName = "商品 A", Spec = "大", Unit = "PCS",
                        Quantity = 100m, UnitPrice = 2.5m, Amount = 1m, SortNo = 1, Moq = "500 pcs/款", Remark = "红色" },
                new() { ProductId = 12L, ProductCode = "P002", ProductName = "商品 B", Spec = "小", Unit = "PCS",
                        Quantity = 200m, UnitPrice = 2.5m, Amount = 999m, SortNo = 2, Moq = "1000 pcs/款", Remark = "蓝色" },
                new() { ProductId = 13L, ProductCode = "P003", ProductName = "已删除行", Unit = "PCS",
                        Quantity = 1m, UnitPrice = 1m, Amount = 1m, SortNo = 3, IsDeleted = true }
            }
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    /// <summary>测试上下文：让 <c>SaveChangesAsync</c> 按需抛出数据库异常（模拟唯一索引并发冲突 / 结构缺失），
    /// 用于验证服务的错误映射范围；不影响 InMemory 存储与同步保存。</summary>
    private sealed class FailingContext : ErpDbContext
    {
        public FailingContext(DbContextOptions<ErpDbContext> options) : base(options) { }

        /// <summary>为 null 时行为与生产上下文一致；否则每次异步保存都失败</summary>
        public Exception? Failure { get; init; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => Failure is null ? base.SaveChangesAsync(cancellationToken) : Task.FromException<int>(Failure);
    }
}
