using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-476 供应商比价「实时主数据引用」护栏单元测试（纯内存库 + 直接实例化控制器，不连接 SQL Server、
/// 不执行任何 DDL / 部署脚本）：
/// <list type="number">
/// <item><b>唯一规则</b>：归属客户（填写必须为正整数的实时引用：存在 / 未删除 / 启用，悬空一律拒绝）、
/// 可选供应商 / 商品（留空保持既有可空草稿语义；填写必须为正整数，解析到既有档案时不得已删除 / 已停用，
/// 解析不到档案的历史悬空弱引用保持既有可写 / 可转换口径，绝不改派 / 回填）；</item>
/// <item><b>采购承诺级复核</b>：供应商与商品引用必须显式存在且为正整数（绝不把缺失 / <c>0</c> 静默当作
/// 实时采购引用），归属客户必须解析到实时客户；去重集合一次有界批量查询，任一行不合格即整批受控拒绝；</item>
/// <item><b>控制器行为</b>：新增 / 修改 / 「选中」选择在单号预约与任何写入之前拒绝失效引用（零写入、零占号）；
/// 带入预填 / 单行转采购订单 / 批次转采购订单在既有来源行锁内、映射草稿与采购单号预约之前复核，
/// 一行不合格即整批拒绝且来源状态与单号流水不变；合法实时主数据照常正向生成；</item>
/// <item><b>授权前置与非披露</b>：既有身份 / 菜单 / 客户范围授权先于主数据复核；进程内直调同样执行实时授权与
/// 实时主数据复核（ERP-491：与调用形状完全无关，空 <c>Request.Path</c> / 无 <c>HttpContext</c> 不再构成旁路）。</item>
/// </list>
/// <para>安全口径：全部使用内存库 <see cref="TestDbFactory"/>，不读取 appsettings / .env / 生产凭据，
/// 不执行 drop / reset，也不使用生产数据。</para>
/// </summary>
public class PurchaseQuoteMasterReferenceTests
{
    private const string QuotePath = "/api/purchase/quotes";

    // ==================== 1. 唯一规则：归属客户（硬引用） ====================

    [Theory]
    [InlineData(0L)]
    [InlineData(-3L)]
    public async Task 客户_非正整数_按参数错误拒绝(long customerId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureCustomerAvailableAsync(db, customerId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseQuoteMasterReferenceRules.CustomerRequiredText, ex.Message);
    }

    [Fact]
    public async Task 客户_悬空或已删除_按不存在拒绝_已停用_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedCustomer(db, deleted: true);
        var disabled = SeedCustomer(db, status: 0);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureCustomerAvailableAsync(db, 4_760_001L));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Contains("不存在或已删除", missing.Message);

        var softDeleted = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureCustomerAvailableAsync(db, deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, softDeleted.Code);

        var stopped = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureCustomerAvailableAsync(db, disabled.Id));
        Assert.Equal(ErrorCodes.RuleConflict, stopped.Code);
        Assert.Contains("已停用", stopped.Message);
    }

    [Fact]
    public async Task 客户_留空_保持未指定语义_合法时返回实时实体()
    {
        using var db = TestDbFactory.Create();
        Assert.Null(await PurchaseQuoteMasterReferenceRules.EnsureCustomerAvailableAsync(db, null));

        var live = SeedCustomer(db);
        var resolved = await PurchaseQuoteMasterReferenceRules.EnsureCustomerAvailableAsync(db, live.Id);
        Assert.Equal(live.Id, resolved!.Id);
    }

    // ==================== 2. 唯一规则：可选供应商 / 商品（可空 + 弱引用口径） ====================

    [Theory]
    [InlineData(0L)]
    [InlineData(-8L)]
    public async Task 供应商与商品_非正整数_按参数错误拒绝(long id)
    {
        using var db = TestDbFactory.Create();

        var supplierEx = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureSupplierAvailableAsync(db, id));
        Assert.Equal(ErrorCodes.InvalidParameter, supplierEx.Code);
        Assert.Equal(PurchaseQuoteMasterReferenceRules.SupplierRequiredText, supplierEx.Message);

        var productEx = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureProductAvailableAsync(db, id));
        Assert.Equal(ErrorCodes.InvalidParameter, productEx.Code);
        Assert.Equal(PurchaseQuoteMasterReferenceRules.ProductRequiredText, productEx.Message);
    }

    [Fact]
    public async Task 供应商与商品_已删除_按不存在拒绝_已停用_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var deletedSupplier = SeedSupplier(db, deleted: true);
        var disabledSupplier = SeedSupplier(db, status: 0);
        var deletedProduct = SeedProduct(db, deleted: true);
        var disabledProduct = SeedProduct(db, status: 0);

        Assert.Equal(ErrorCodes.NotFound, (await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureSupplierAvailableAsync(db, deletedSupplier.Id))).Code);
        Assert.Equal(ErrorCodes.RuleConflict, (await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureSupplierAvailableAsync(db, disabledSupplier.Id))).Code);
        Assert.Equal(ErrorCodes.NotFound, (await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureProductAvailableAsync(db, deletedProduct.Id))).Code);
        Assert.Equal(ErrorCodes.RuleConflict, (await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureProductAvailableAsync(db, disabledProduct.Id))).Code);
    }

    [Fact]
    public async Task 供应商与商品_留空或未引用档案_保持既有可空与弱引用口径()
    {
        using var db = TestDbFactory.Create();

        // 留空 = 未维护供应商 / 手工（未引用档案）的比价草稿：保持既有可空语义。
        Assert.Null(await PurchaseQuoteMasterReferenceRules.EnsureSupplierAvailableAsync(db, null));
        Assert.Null(await PurchaseQuoteMasterReferenceRules.EnsureProductAvailableAsync(db, null));

        // 解析不到档案：既有「未引用档案」弱引用（档口 / 现货比价）保持可写 / 可转换，绝不臆造档案。
        Assert.Null(await PurchaseQuoteMasterReferenceRules.EnsureSupplierAvailableAsync(db, 4_760_002L));
        Assert.Null(await PurchaseQuoteMasterReferenceRules.EnsureProductAvailableAsync(db, 4_760_003L));

        // 解析到既有档案：返回实时实体（不改写、不回填）。
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        Assert.Equal(supplier.Id,
            (await PurchaseQuoteMasterReferenceRules.EnsureSupplierAvailableAsync(db, supplier.Id))!.Id);
        Assert.Equal(product.Id,
            (await PurchaseQuoteMasterReferenceRules.EnsureProductAvailableAsync(db, product.Id))!.Id);
    }

    [Fact]
    public async Task 草稿写入_可空引用与弱引用_整体放行_选中判定同源()
    {
        using var db = TestDbFactory.Create();

        var draft = NewQuote("PQ-476-NULLABLE");
        draft.SupplierId = null;
        draft.ProductId = null;
        draft.CustomerId = null;
        await PurchaseQuoteMasterReferenceRules.EnsureComparisonReferencesAsync(db, draft);

        var weak = NewQuote("PQ-476-WEAK");
        weak.SupplierId = 4_760_004L;
        weak.ProductId = 4_760_005L;
        await PurchaseQuoteMasterReferenceRules.EnsureComparisonReferencesAsync(db, weak);

        Assert.False(PurchaseQuoteMasterReferenceRules.IsSelectionCommitment(draft));
        draft.Status = PurchaseQuoteMutationRules.SelectedStatus;
        draft.IsSelected = true;
        Assert.True(PurchaseQuoteMasterReferenceRules.IsSelectionCommitment(draft));
    }

    // ==================== 3. 采购承诺级复核（绝不把缺失 / 0 静默当作实时采购引用） ====================

    [Fact]
    public async Task 承诺_供应商引用缺失_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var quote = NewQuote("PQ-476-COMMIT-SUPPLIER");
        quote.SupplierId = null;
        quote.Status = PurchaseQuoteMutationRules.SelectedStatus;
        quote.IsSelected = true;

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureCommitmentReferencesAsync(db, new[] { quote }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentSupplierText, ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-2L)]
    public async Task 承诺_商品引用缺失或非正_按规则冲突拒绝且绝不静默零占位(long? productId)
    {
        using var db = TestDbFactory.Create();
        var quote = NewQuote("PQ-476-COMMIT-PRODUCT");
        quote.SupplierId = 4_760_014L;                   // 供应商引用有效 / 弱引用，聚焦商品引用缺失
        quote.ProductId = productId;
        quote.Status = PurchaseQuoteMutationRules.SelectedStatus;
        quote.IsSelected = true;

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureCommitmentReferencesAsync(db, new[] { quote }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentProductText, ex.Message);
    }

    [Fact]
    public async Task 承诺_归属客户零或悬空或已停用_受控拒绝()
    {
        using var db = TestDbFactory.Create();
        var disabled = SeedCustomer(db, status: 0);

        var zero = NewQuote("PQ-476-OWNER-0");
        zero.SupplierId = 4_760_015L;                    // 供应商 / 商品引用有效，聚焦归属客户校验
        zero.ProductId = 4_760_016L;
        zero.CustomerId = 0;
        Assert.Equal(ErrorCodes.InvalidParameter, (await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureCommitmentReferencesAsync(db, new[] { zero }))).Code);

        var dangling = NewQuote("PQ-476-OWNER-MISS");
        dangling.SupplierId = 4_760_015L;
        dangling.ProductId = 4_760_016L;
        dangling.CustomerId = 4_760_006L;
        Assert.Equal(ErrorCodes.NotFound, (await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureCommitmentReferencesAsync(db, new[] { dangling }))).Code);

        var stopped = NewQuote("PQ-476-OWNER-OFF");
        stopped.SupplierId = 4_760_015L;
        stopped.ProductId = 4_760_016L;
        stopped.CustomerId = disabled.Id;
        Assert.Equal(ErrorCodes.RuleConflict, (await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureCommitmentReferencesAsync(db, new[] { stopped }))).Code);
    }

    [Fact]
    public async Task 承诺_档案已删除或已停用_一行不合格即整批拒绝()
    {
        using var db = TestDbFactory.Create();
        var liveSupplier = SeedSupplier(db);
        var liveProduct = SeedProduct(db);
        var disabledSupplier = SeedSupplier(db, status: 0);

        var ok = NewQuote("PQ-476-BATCH-OK");
        ok.SupplierId = liveSupplier.Id;
        ok.ProductId = liveProduct.Id;
        var bad = NewQuote("PQ-476-BATCH-BAD");
        bad.SupplierId = disabledSupplier.Id;
        bad.ProductId = liveProduct.Id;

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteMasterReferenceRules.EnsureCommitmentReferencesAsync(db, new[] { ok, bad }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteConversionMutationRules.SupplierMasterText, ex.Message);
        Assert.Contains($"比价行 #{bad.Id}", ex.Message);
    }

    [Fact]
    public async Task 承诺_弱引用与合法实时档案_放行且不臆造档案()
    {
        using var db = TestDbFactory.Create();
        var liveSupplier = SeedSupplier(db);
        var liveProduct = SeedProduct(db);
        var liveCustomer = SeedCustomer(db);

        // 合法实时主数据：正向放行。
        var live = NewQuote("PQ-476-COMMIT-LIVE");
        live.SupplierId = liveSupplier.Id;
        live.ProductId = liveProduct.Id;
        live.CustomerId = liveCustomer.Id;
        await PurchaseQuoteMasterReferenceRules.EnsureCommitmentReferencesAsync(db, new[] { live });

        // 既有弱引用（未引用档案的档口 / 现货比价）保持既有可转换口径，绝不回填 / 改派。
        var weak = NewQuote("PQ-476-COMMIT-WEAK");
        weak.SupplierId = 4_760_007L;
        weak.ProductId = 4_760_008L;
        await PurchaseQuoteMasterReferenceRules.EnsureCommitmentReferencesAsync(db, new[] { weak });

        Assert.Empty(db.BaseSuppliers.Where(s => s.Id == 4_760_007L));
        Assert.Empty(db.BaseProducts.Where(p => p.Id == 4_760_008L));
    }

    // ==================== 4. 控制器：新增 / 修改 / 选择（HTTP 绑定 + 既有授权） ====================

    [Fact]
    public async Task 新增_失效引用_受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUserWithMenus(db);
        var deletedProduct = SeedProduct(db, deleted: true);
        var disabledSupplier = SeedSupplier(db, status: 0);
        var ctl = HttpController(db, user);

        var zeroSupplier = NewQuote("PQ-476-CREATE-0S");
        zeroSupplier.SupplierId = 0;
        Assert.Equal(ErrorCodes.InvalidParameter,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(zeroSupplier))).Code);

        var zeroProduct = NewQuote("PQ-476-CREATE-0P");
        zeroProduct.ProductId = 0;
        Assert.Equal(ErrorCodes.InvalidParameter,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(zeroProduct))).Code);

        var danglingCustomer = NewQuote("PQ-476-CREATE-CUST");
        danglingCustomer.CustomerId = 4_760_009L;
        Assert.Equal(ErrorCodes.NotFound,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(danglingCustomer))).Code);

        var stoppedSupplier = NewQuote("PQ-476-CREATE-S");
        stoppedSupplier.SupplierId = disabledSupplier.Id;
        Assert.Equal(ErrorCodes.RuleConflict,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(stoppedSupplier))).Code);

        var removedProduct = NewQuote("PQ-476-CREATE-P");
        removedProduct.ProductId = deletedProduct.Id;
        Assert.Equal(ErrorCodes.NotFound,
            (await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(removedProduct))).Code);

        Assert.Empty(db.PurchaseQuotes);        // 被拒绝的新增零写入
    }

    [Fact]
    public async Task 新增_可空供应商商品与客户_保持既有草稿语义_正向保存()
    {
        using var db = TestDbFactory.Create();
        var ctl = HttpController(db, SeedPrivilegedUserWithMenus(db));
        var draft = NewQuote("PQ-476-CREATE-NULLABLE");
        draft.SupplierId = null;
        draft.ProductId = null;
        draft.CustomerId = null;

        var ok = Assert.IsType<OkObjectResult>(await ctl.Create(draft));

        Assert.IsType<ApiResponse<PurchaseQuote>>(ok.Value);
        var stored = db.PurchaseQuotes.Single();
        Assert.Null(stored.SupplierId);
        Assert.Null(stored.ProductId);
        Assert.Null(stored.CustomerId);
    }

    [Fact]
    public async Task 新增_选中承诺_商品引用缺失_按承诺级拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var ctl = HttpController(db, SeedPrivilegedUserWithMenus(db));
        var selected = NewQuote("PQ-476-CREATE-SELECTED");
        selected.SupplierId = SeedSupplier(db).Id;
        selected.ProductId = null;                       // 绝不把缺失 / 0 静默当作实时采购引用
        selected.IsSelected = true;
        selected.Status = PurchaseQuoteMutationRules.SelectedStatus;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(selected));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentProductText, ex.Message);
        Assert.Empty(db.PurchaseQuotes);
    }

    [Fact]
    public async Task 修改_草稿后供应商被停用_锁内原子拒绝且原行不变()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUserWithMenus(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var customer = SeedCustomer(db);
        var ctl = HttpController(db, user);
        var seeded = SeedQuote(db, "PQ-476-UPDATE-STOP", customer.Id, supplier.Id, product.Id);

        supplier.Status = 0;                             // 草稿保存后主数据失效
        db.SaveChanges();

        var change = NewQuote("PQ-476-UPDATE-STOP", customer.Id, supplier.Id, product.Id);
        change.Quantity = 999m;
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(seeded.Id, change));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);

        var stored = db.PurchaseQuotes.AsNoTracking().Single();
        Assert.Equal(100m, stored.Quantity);              // 锁内权威行一行不改
        Assert.Equal(PurchaseQuoteMutationRules.PendingStatus, stored.Status);
        Assert.Equal(supplier.Id, stored.SupplierId);
    }

    [Fact]
    public async Task 修改_选择承诺_商品档案已删除_原子拒绝且不进入已选中()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUserWithMenus(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var customer = SeedCustomer(db);
        var ctl = HttpController(db, user);
        var seeded = SeedQuote(db, "PQ-476-SELECT-BAD", customer.Id, supplier.Id, product.Id);

        product.IsDeleted = true;                        // 草稿保存后商品档案失效
        db.SaveChanges();

        var selection = NewQuote("PQ-476-SELECT-BAD", customer.Id, supplier.Id, product.Id);
        selection.IsSelected = true;
        selection.Status = PurchaseQuoteMutationRules.SelectedStatus;
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(seeded.Id, selection));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(PurchaseQuoteMutationRules.PendingStatus,
            db.PurchaseQuotes.AsNoTracking().Single().Status);   // 绝不进入已选中承诺
    }

    [Fact]
    public async Task 修改_选择承诺_合法实时主数据_正向进入已选中()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUserWithMenus(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var customer = SeedCustomer(db);
        var ctl = HttpController(db, user);
        var seeded = SeedQuote(db, "PQ-476-SELECT-OK", customer.Id, supplier.Id, product.Id);

        var selection = NewQuote("PQ-476-SELECT-OK", customer.Id, supplier.Id, product.Id);
        selection.IsSelected = true;
        selection.Status = PurchaseQuoteMutationRules.SelectedStatus;
        Assert.IsType<OkObjectResult>(await ctl.Update(seeded.Id, selection));

        var stored = db.PurchaseQuotes.AsNoTracking().Single();
        Assert.Equal(PurchaseQuoteMutationRules.SelectedStatus, stored.Status);
        Assert.True(stored.IsSelected);
    }

    [Fact]
    public async Task 进程内直调_缺失身份_受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        // ERP-486：不再有「无请求管线免授权」旁路 —— 未绑定任何身份 / 请求的直接调用 fail closed。
        var ctl = new PurchaseQuoteController(new GenericService<PurchaseQuote>(db), db,
            new DocumentNumberService(db));
        var draft = NewQuote("PQ-486-INPROCESS-NOID");
        draft.SupplierId = SeedSupplier(db).Id;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(draft));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Equal(PurchaseQuoteAuthorizationRules.UnauthorizedText, ex.Message);
        Assert.Empty(db.PurchaseQuotes);
    }

    [Fact]
    public async Task 进程内直调_真实启用身份_空请求路径_实时主数据失效_同样受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        // ERP-491：既有进程内夹具改为真实启用身份（既有菜单 / 不受限范围），刻意不设置 Request.Path；
        // 授权判定与请求形状无关，实时主数据复核同样与调用形状无关 —— 失效显式引用（悬空客户）一律
        // 受控拒绝、零写入，绝不把「无请求管线」当作主数据引用豁免。
        var ctl = PurchaseQuoteTestAuthority.NewQuoteController(db);
        var draft = NewQuote("PQ-491-INPROCESS");
        draft.SupplierId = 4_760_010L;
        draft.ProductId = 4_760_011L;
        draft.CustomerId = 4_760_012L;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(draft));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("不存在或已删除", ex.Message);
        Assert.Empty(db.PurchaseQuotes);
    }

    [Fact]
    public async Task 进程内直调_真实启用身份_空请求路径_实时主数据齐备_与真实请求一致放行()
    {
        using var db = TestDbFactory.Create();
        // ERP-491：同为「空 Request.Path / 无 MVC 请求绑定」的调用形状，只要引用真实存在 / 未删除 / 启用的
        // 主数据行，就与真实 HTTP 写入请求给出完全一致的放行结论（绝不按调用形状区别对待）。
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var customer = SeedCustomer(db);
        var ctl = PurchaseQuoteTestAuthority.NewQuoteController(db);
        var draft = NewQuote("PQ-491-INPROCESS-LIVE", customer.Id, supplier.Id, product.Id);

        Assert.IsType<OkObjectResult>(await ctl.Create(draft));

        var stored = db.PurchaseQuotes.AsNoTracking().Single();
        Assert.Equal(supplier.Id, stored.SupplierId);
        Assert.Equal(product.Id, stored.ProductId);
        Assert.Equal(customer.Id, stored.CustomerId);
    }

    [Fact]
    public async Task 授权先于主数据_无菜单受限账号_同一非披露拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedRestrictedUserWithoutMenu(db);
        var ctl = HttpController(db, userId);
        var draft = NewQuote("PQ-476-AUTH-FIRST");
        draft.CustomerId = 4_760_013L;                    // 悬空客户：授权仍最先、非披露

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(draft));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(PurchaseQuoteAuthorizationRules.MenuDeniedText, ex.Message);
        Assert.Empty(db.PurchaseQuotes);
    }

    // ==================== 5. 采购承诺：带入预填 / 单行 / 批次转采购订单 ====================

    [Fact]
    public async Task 带入预填_商品引用缺失_拒绝且不落库不改来源()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUserWithMenus(db);
        var supplier = SeedSupplier(db);
        var customer = SeedCustomer(db);
        var ctl = HttpController(db, user);
        var quote = SeedSelectedApprovedQuote(db, "PQ-476-PREFILL-NULL", customer.Id, supplier.Id, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.OrderPrefill(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentProductText, ex.Message);
        Assert.Empty(db.PurchaseOrders);
        var source = db.PurchaseQuotes.AsNoTracking().Single();
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
        Assert.Equal(string.Empty, source.RefOrderNo ?? string.Empty);
    }

    [Fact]
    public async Task 单行转采购订单_商品引用缺失_在发号与写入之前受控拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUserWithMenus(db);
        var supplier = SeedSupplier(db);
        var customer = SeedCustomer(db);
        var rule = SeedNumberRule(db, DocumentType.PurchaseOrder, "PO", 7);
        var ctl = HttpController(db, user);
        var quote = SeedSelectedApprovedQuote(db, "PQ-476-ORDER-NULL", customer.Id, supplier.Id, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentProductText, ex.Message);
        Assert.Empty(db.PurchaseOrders);                               // 零写入
        Assert.Equal(7L, Sequence(db, rule.Id));                       // 零发号
        var source = db.PurchaseQuotes.AsNoTracking().Single();
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
        Assert.Equal(string.Empty, source.RefOrderNo ?? string.Empty); // 来源状态 / 留痕不变
    }

    [Fact]
    public async Task 单行转采购订单_主数据失效_受控拒绝且零写入零发号()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUserWithMenus(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var customer = SeedCustomer(db);
        var rule = SeedNumberRule(db, DocumentType.PurchaseOrder, "PO", 7);
        var ctl = HttpController(db, user);
        var quote = SeedSelectedApprovedQuote(db, "PQ-476-ORDER-INVALID", customer.Id, supplier.Id, product.Id);

        product.Status = 0;                                            // 审批后商品被停用
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteConversionMutationRules.ProductMasterText, ex.Message);
        Assert.Empty(db.PurchaseOrders);
        Assert.Equal(7L, Sequence(db, rule.Id));
        Assert.Single(db.PurchaseQuoteDecisions.AsNoTracking());       // 审批决定历史一字不改
    }

    [Fact]
    public async Task 单行转采购订单_归属客户已删除_受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUserWithMenus(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var customer = SeedCustomer(db);
        var rule = SeedNumberRule(db, DocumentType.PurchaseOrder, "PO", 7);
        var ctl = HttpController(db, user);
        var quote = SeedSelectedApprovedQuote(db, "PQ-476-ORDER-CUST", customer.Id, supplier.Id, product.Id);

        customer.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.PurchaseOrders);
        Assert.Equal(7L, Sequence(db, rule.Id));
    }

    [Fact]
    public async Task 单行转采购订单_合法实时主数据_正向生成并留痕来源()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUserWithMenus(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var customer = SeedCustomer(db);
        var rule = SeedNumberRule(db, DocumentType.PurchaseOrder, "PO", 7);
        var ctl = HttpController(db, user);
        var quote = SeedSelectedApprovedQuote(db, "PQ-476-ORDER-OK", customer.Id, supplier.Id, product.Id);

        var ok = Assert.IsType<OkObjectResult>(await ctl.ToPurchaseOrder(quote.Id));

        var data = Assert.IsType<ApiResponse<PurchaseOrderConversionResult>>(ok.Value).Data!;
        Assert.True(data.Id > 0);
        var order = db.PurchaseOrders.Single();
        Assert.Equal(supplier.Id, order.SupplierId);
        Assert.Equal(customer.Id, order.OwningCustomerId);
        Assert.Equal(product.Id, db.PurchaseOrderDetails.Single().ProductId);
        Assert.Equal(8L, Sequence(db, rule.Id));                        // 恰好消耗一个采购单号

        var source = db.PurchaseQuotes.AsNoTracking().Single();
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, source.Status);
        Assert.Equal(order.OrderNo, source.RefOrderNo);
    }

    [Fact]
    public async Task 批次转采购订单_一行商品引用缺失_整批拒绝零发号零写入()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUserWithMenus(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var customer = SeedCustomer(db);
        var rule = SeedNumberRule(db, DocumentType.PurchaseOrder, "PO", 7);
        var ctl = HttpController(db, user);
        var batchNo = "PQ-476-BATCH-INVALID";
        var good = SeedSelectedApprovedQuote(db, batchNo, customer.Id, supplier.Id, product.Id);
        var bad = SeedSelectedApprovedQuote(db, batchNo, customer.Id, supplier.Id, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.BatchToOrder(
            new PurchaseQuoteBatchConversionRequest { QuoteNo = batchNo }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentProductText, ex.Message);
        Assert.Empty(db.PurchaseOrders);                               // 绝不生成部分采购订单 / 部分批次组
        Assert.Equal(7L, Sequence(db, rule.Id));                       // 零发号
        foreach (var id in new[] { good.Id, bad.Id })
        {
            var source = db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == id);
            Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
            Assert.Equal(string.Empty, source.RefOrderNo ?? string.Empty);
        }
    }

    [Fact]
    public void 源码契约_唯一复核接入写入与承诺入口()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "PurchaseQuoteController.cs");
        // ERP-491：写入 / 选择 / 带入预填 / 单行转单 / 批次转单不再有依据 Request.Path 的方法级豁免。
        Assert.DoesNotContain("RequiresLiveMasterValidation", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Request.Path.HasValue", controller, StringComparison.Ordinal);
        Assert.Contains("PurchaseQuoteMasterReferenceRules.EnsureComparisonReferencesAsync", controller);
        Assert.Contains("PurchaseQuoteConversion.EnsureSourceMasterReferencesAsync", controller);
        Assert.Contains("PurchaseQuoteMasterReferenceRules.IsSelectionCommitment", controller);
        Assert.DoesNotContain("AllowAnonymous", controller, StringComparison.Ordinal);

        var conversion = ReadSource("src", "ERP.Api", "Controllers", "PurchaseQuoteConversion.cs");
        Assert.Contains("PurchaseQuoteMasterReferenceRules.EnsureCommitmentReferencesAsync", conversion);
        Assert.Contains("EnsureSourceMasterReferencesAsync", conversion);

        var rules = ReadSource("src", "ERP.Application", "Services",
            "PurchaseQuoteMasterReferenceRules.cs");
        Assert.Contains("CommitmentProductText", rules);
        Assert.Contains("ProductId = 0", rules);                        // 绝不静默零占位（口径文案同源）
        Assert.Contains("ToDictionaryAsync", rules);                    // 有界批量查询
        Assert.DoesNotContain("SaveChangesAsync", rules, StringComparison.Ordinal);   // 只读判定，不落库

        var doc = ReadSource("docs", "purchase-quote-master-reference-validation.md");
        Assert.Contains("PurchaseQuoteMasterReferenceRules", doc);
        Assert.Contains("(localdb)\\NEWERP_AutoAcceptance", doc);
        Assert.Contains("NEWERP_AUTOTEST", doc);
        Assert.Contains("ProductId = 0", doc);
    }

    // ==================== 工厂与种子数据 ====================

    /// <summary>
    /// 真实 HTTP 请求形状的控制器（<c>Request.Path</c> 已赋值 → 实时主数据引用复核执行；
    /// 特权账号 → 既有菜单 / 客户范围授权放行）。绝不伪造授权，也不放宽既有授权规则。
    /// </summary>
    private static PurchaseQuoteController HttpController(ErpDbContext db, long userId)
    {
        var controller = new PurchaseQuoteController(new GenericService<PurchaseQuote>(db), db,
            new DocumentNumberService(db));
        var http = new DefaultHttpContext();
        http.Request.Path = QuotePath;
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static PurchaseQuote NewQuote(string quoteNo, long? customerId = null, long? supplierId = null,
        long? productId = null) => new()
    {
        QuoteNo = quoteNo,
        QuoteDate = new DateTime(2026, 10, 1),
        ProductId = productId,
        ProductName = "ERP476 商品",
        Spec = "大号",
        Unit = "PCS",
        Quantity = 100m,
        SupplierId = supplierId,
        SupplierName = supplierId is > 0 ? $"ERP476 供应商 {supplierId}" : string.Empty,
        SupplierType = "档口",
        QuotePrice = 2m,
        TotalAmount = 200m,
        Currency = "USD",
        TaxIncluded = false,
        DeliveryDays = 10,
        MinOrderQty = 1,
        PaymentTerms = "现结",
        IsSelected = false,
        Status = PurchaseQuoteMutationRules.PendingStatus,
        CustomerId = customerId,
        CustomerName = "ERP476 客户名快照",
        Remark = "ERP476_TEST"
    };

    private static PurchaseQuote SeedQuote(ErpDbContext db, string quoteNo, long? customerId, long? supplierId,
        long? productId, string status = PurchaseQuoteMutationRules.PendingStatus, bool selected = false)
    {
        var quote = NewQuote(quoteNo, customerId, supplierId, productId);
        quote.Status = status;
        quote.IsSelected = selected;
        db.PurchaseQuotes.Add(quote);
        db.SaveChanges();
        return quote;
    }

    /// <summary>已选中 + 已批准的比价行（可承诺转采购订单的来源；审批决定 append-only）。</summary>
    private static PurchaseQuote SeedSelectedApprovedQuote(ErpDbContext db, string quoteNo, long customerId,
        long supplierId, long? productId)
    {
        var quote = SeedQuote(db, quoteNo, customerId, supplierId, productId,
            status: PurchaseQuoteConversion.SelectedStatus, selected: true);
        db.PurchaseQuoteDecisions.Add(new PurchaseQuoteDecision
        {
            QuoteId = quote.Id,
            QuoteNo = quote.QuoteNo,
            Decision = PurchaseQuoteApproval.Approved,
            SelectedSupplierId = quote.SupplierId,
            SelectedSupplierName = quote.SupplierName,
            DecisionBasis = "单价最优",
            DecidedBy = 1L,
            DecidedByName = "审批人",
            DecidedAt = DateTime.Now,
            DecisionRef = PurchaseQuoteApproval.DecisionRef(quote)
        });
        db.SaveChanges();
        return quote;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-476-{Guid.NewGuid():N}", CustomerName = "ERP476 客户", Status = status,
            CreditStatus = "正常", DepositRatio = 30m, IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseSupplier SeedSupplier(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = $"S-476-{Guid.NewGuid():N}", SupplierName = "ERP476 供应商", Status = status,
            IsDeleted = deleted
        };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var product = new BaseProduct
        {
            ProductCode = $"P-476-{Guid.NewGuid():N}", ProductName = "ERP476 商品", Unit = "PCS",
            Status = status, IsDeleted = deleted
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    /// <summary>特权账号（系统内置角色）+ 既有「供应商比价」/「采购订单」菜单（不新增任何权限模型）。</summary>
    private static long SeedPrivilegedUserWithMenus(ErpDbContext db)
    {
        var userId = TestAuth.SeedPrivilegedUser(db);
        var roleId = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).First();
        GrantMenu(db, roleId, PurchaseQuoteAuthorizationRules.RequiredMenuCode);
        GrantMenu(db, roleId, PurchaseQuoteAuthorizationRules.DestinationMenuCode);
        return userId;
    }

    private static void GrantMenu(ErpDbContext db, long roleId, string menuCode)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }

    /// <summary>受限（非特权）账号：非系统内置角色且未授予任何菜单 → 既有菜单授权 fail closed。</summary>
    private static long SeedRestrictedUserWithoutMenu(ErpDbContext db)
    {
        var code = $"erp476-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static SysDocumentNumberRule SeedNumberRule(ErpDbContext db, DocumentType type, string prefix,
        long current)
    {
        var rule = new SysDocumentNumberRule
        {
            DocumentType = type, RuleCode = $"{prefix}-{Guid.NewGuid():N}", RuleName = $"{prefix} 单号",
            Prefix = prefix, DateFormat = "yyyyMMdd", SerialLength = 4, Separator = string.Empty,
            CurrentSequence = current, IsDeleted = false
        };
        db.SysDocumentNumberRules.Add(rule);
        db.SaveChanges();
        return rule;
    }

    private static long Sequence(ErpDbContext db, long ruleId)
        => db.SysDocumentNumberRules.AsNoTracking().Single(r => r.Id == ruleId).CurrentSequence;

    /// <summary>读取仓库内源文件（按解决方案根定位，避免依赖输出目录层级）。</summary>
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
