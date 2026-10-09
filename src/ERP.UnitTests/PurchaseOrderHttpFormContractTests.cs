using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-429 手工 / 关联采购订单录入的「HTTP 表单 / 正文绑定契约」单元测试（纯内存，不连接 SQL Server、不启动浏览器）。
/// <list type="number">
/// <item><b>真实 MVC 绑定 + 校验管线</b>：直接使用 MVC 自己的 <see cref="IModelBinderFactory"/>（正文绑定源）与
/// <see cref="IObjectModelValidator"/>（与 <c>[ApiController]</c> 自动 400 完全同一条校验路径）读取真实 JSON 正文 ——
/// 因此「省略 / 伪造采购单号」「归属来源与起运港留空为 null」是在<b>真实模型绑定 / 模型校验</b>层被证明的，
/// 而不是只断言控制器内部行为（后者无法证明绑定层不再拒绝请求）。</item>
/// <item><b>服务端权威单号</b>：绑定后的实体交给真实 <see cref="PurchaseOrderController"/>，证明调用方提交值
/// 不决定持久化单号，省略单号时由 <c>IDocumentNumberService</c> 在既有授权与实时主数据校验之后权威生成并落库。</item>
/// <item><b>既有护栏不被放宽</b>：显式来源 0 / 负数仍按既有 ERP-425 来源规则拒绝；无效供应商 / 商品 / 数量 / 条款
/// 仍按 ERP-426 / ERP-427 拒绝且零写入；无身份 / 缺菜单仍 fail closed；非法正文仍按绑定错误拒绝；
/// 数据库列仍为 NOT NULL 且长度 50。</item>
/// </list>
/// <para>安全口径：全部使用内存库 <see cref="TestDbFactory"/>，不读取 appsettings / .env / 生产凭据，
/// 不执行 drop / reset，也不使用生产数据。</para>
/// </summary>
public class PurchaseOrderHttpFormContractTests
{
    // ==================== 0. 真实 MVC 绑定 + 校验管线 ====================

    private static readonly IServiceProvider Mvc = BuildMvcServices();

    private static IServiceProvider BuildMvcServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // 与 ERP.Api/Program.cs 完全相同的 JSON 选项（枚举按字符串提交 / 绑定）。
        services.AddControllers().AddJsonOptions(options =>
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 用 MVC 真实正文绑定器（<c>BindingSource.Body</c>，与 <c>[FromBody]</c> 参数解析出的绑定器完全一致）
    /// 从 JSON 正文绑定 <see cref="PurchaseOrder"/>，随后执行 MVC 真实模型校验。
    /// </summary>
    private static async Task<(PurchaseOrder? Model, ModelStateDictionary ModelState)> BindAsync(string json)
    {
        var http = new DefaultHttpContext();
        var payload = Encoding.UTF8.GetBytes(json);
        http.Request.Method = HttpMethods.Post;
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.ContentLength = payload.Length;
        http.Request.Body = new MemoryStream(payload);

        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor(), new ModelStateDictionary());
        var metadata = Mvc.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(PurchaseOrder));
        var bindingInfo = new BindingInfo { BindingSource = BindingSource.Body, BinderModelName = "entity" };
        var binder = Mvc.GetRequiredService<IModelBinderFactory>().CreateBinder(
            new ModelBinderFactoryContext { Metadata = metadata, BindingInfo = bindingInfo, CacheToken = new object() });
        var context = DefaultModelBindingContext.CreateBindingContext(
            actionContext, new CompositeValueProvider(), metadata, bindingInfo, "entity");
        context.IsTopLevelObject = true;
        await binder.BindModelAsync(context);

        var model = context.Result.Model as PurchaseOrder;
        if (context.Result.IsModelSet && model is not null)
            Mvc.GetRequiredService<IObjectModelValidator>()
                .Validate(actionContext, validationState: null, prefix: string.Empty, model: model);

        return (model, actionContext.ModelState);
    }

    private static string Describe(ModelStateDictionary state)
        => state.IsValid ? "(valid)" : string.Join(" | ",
            state.Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value!.Errors.Select(e => e.ErrorMessage))}]"));

    /// <summary>与浏览器 / 前端 crud.js 提交的手工采购正文同形（可省略单号、可选归属来源 / 起运港）。</summary>
    private static string OrderJson(long supplierId, long productId,
        string? orderNoJson = null, string? owningSalesOrderIdJson = null, string? portIdJson = null,
        string unit = "PCS", string quantityJson = "10", string unitPriceJson = "5", string taxRateJson = "0")
    {
        var orderNo = orderNoJson is null ? string.Empty : ",\"orderNo\":" + orderNoJson;
        var owning = owningSalesOrderIdJson is null ? string.Empty : ",\"owningSalesOrderId\":" + owningSalesOrderIdJson;
        var port = portIdJson is null ? string.Empty : ",\"portId\":" + portIdJson;
        return "{\"orderDate\":\"" + DateTime.Today.ToString("yyyy-MM-dd") + "\""
            + orderNo + owning + port
            + ",\"supplierId\":" + supplierId
            + ",\"currency\":\"CNY\",\"exchangeRate\":1,\"taxRate\":" + taxRateJson
            + ",\"contractNo\":\"PC-429\",\"paymentTerms\":\"月结 30 天\""
            + ",\"details\":[{\"productId\":" + productId
            + ",\"productName\":\"契约采购商品\",\"spec\":\"中\",\"unit\":\"" + unit + "\""
            + ",\"quantity\":" + quantityJson + ",\"unitPrice\":" + unitPriceJson + "}]}";
    }

    // ==================== 1. 实体 / 数据库契约（单号仍非空、仍限长） ====================

    private static ErpDbContext ModelOnlyContext()
        => new(new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_MODEL_ONLY;Integrated Security=true")
            .Options);

    [Fact]
    public void 采购单号_无显式必填特性_但数据库列仍非空且长度仍为50()
    {
        var property = typeof(PurchaseOrder).GetProperty(nameof(PurchaseOrder.OrderNo),
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);

        // 显式 [Required]（AllowEmptyStrings=false）已移除：省略 / 空串单号不再在绑定 / 校验层被 400 拒绝。
        Assert.Null(property!.GetCustomAttribute<RequiredAttribute>());
        // 长度约束保留（HTTP 模型校验层仍然限制 50）。
        Assert.Equal(50, property.GetCustomAttribute<MaxLengthAttribute>()?.Length);

        // 数据库列契约保留：仍是 NOT NULL、长度 50（非空 CLR 引用类型 + [MaxLength] 经 EF 模型体现）。
        using var db = ModelOnlyContext();
        var efProperty = db.Model.FindEntityType(typeof(PurchaseOrder))!.FindProperty(nameof(PurchaseOrder.OrderNo))!;
        Assert.False(efProperty.IsNullable);
        Assert.Equal(50, efProperty.GetMaxLength());
    }

    // ==================== 2. 省略 / 伪造单号与可选引用的真实绑定契约 ====================

    [Fact]
    public async Task 省略采购单号_真实模型绑定与校验通过且不产生单号错误()
    {
        var (model, state) = await BindAsync(OrderJson(1L, 1L));

        Assert.NotNull(model);
        Assert.True(state.IsValid, Describe(state));
        Assert.DoesNotContain(state.Keys, k => k.Contains(nameof(PurchaseOrder.OrderNo), StringComparison.Ordinal));
        Assert.Equal(string.Empty, model!.OrderNo);
    }

    [Fact]
    public async Task 伪造采购单号_绑定层接受但绝不决定服务端权威单号()
    {
        var (model, state) = await BindAsync(OrderJson(1L, 1L, orderNoJson: "\"HACK-429-FORGED\""));
        Assert.NotNull(model);
        Assert.True(state.IsValid, Describe(state));
        Assert.Equal("HACK-429-FORGED", model!.OrderNo);

        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);

        var controller = HttpController(db, SeedPrivilegedUser(db));
        Assert.IsType<OkObjectResult>(await controller.Create(model));

        var persisted = db.PurchaseOrders.Include(o => o.Details).AsNoTracking().Single();
        Assert.StartsWith("PO", persisted.OrderNo, StringComparison.Ordinal);
        Assert.NotEqual("HACK-429-FORGED", persisted.OrderNo);
        Assert.Equal(supplier.Id, persisted.SupplierId);
        Assert.Equal(product.Id, persisted.Details.Single().ProductId);
    }

    [Fact]
    public async Task 可选归属来源与起运港_省略或显式null_均绑定为null且不受零值回落影响()
    {
        // 省略：前端手工采购表单未提交这两个字段（旧前端曾回落到 0，被服务端按显式非法 0 拒绝）。
        var (omitted, omittedState) = await BindAsync(OrderJson(1L, 1L));
        Assert.NotNull(omitted);
        Assert.True(omittedState.IsValid, Describe(omittedState));
        Assert.Null(omitted!.OwningSalesOrderId);
        Assert.Null(omitted.PortId);

        // 显式 null：与 crud.js 对声明可选数值字段留空时提交 null 的正文完全一致。
        var (explicitNull, nullState) = await BindAsync(
            OrderJson(1L, 1L, owningSalesOrderIdJson: "null", portIdJson: "null"));
        Assert.NotNull(explicitNull);
        Assert.True(nullState.IsValid, Describe(nullState));
        Assert.Null(explicitNull!.OwningSalesOrderId);
        Assert.Null(explicitNull.PortId);

        // 未关联手工采购：绑定结果直接交给控制器落库，可选引用保持 NULL。
        using var db = TestDbFactory.Create();
        SeedSupplier(db);
        SeedProduct(db);
        var controller = HttpController(db, SeedPrivilegedUser(db));
        var body = (await BindAsync(OrderJson(1L, 1L, owningSalesOrderIdJson: "null", portIdJson: "null"))).Model!;
        body.SupplierId = db.BaseSuppliers.Single().Id;
        body.Details[0].ProductId = db.BaseProducts.Single().Id;

        Assert.IsType<OkObjectResult>(await controller.Create(body));
        var persisted = db.PurchaseOrders.AsNoTracking().Single();
        Assert.Null(persisted.OwningSalesOrderId);
        Assert.Null(persisted.PortId);
        Assert.StartsWith("PO", persisted.OrderNo, StringComparison.Ordinal);
        Assert.Equal(50m, persisted.TotalAmount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public async Task 显式非法来源0或负数_绑定放行但服务端仍按既有来源规则拒绝且零写入(string value)
    {
        var (model, state) = await BindAsync(OrderJson(1L, 1L, owningSalesOrderIdJson: value));
        Assert.NotNull(model);
        Assert.True(state.IsValid, Describe(state));
        Assert.Equal(long.Parse(value), model!.OwningSalesOrderId);

        using var db = TestDbFactory.Create();
        SeedSupplier(db);
        SeedProduct(db);
        var controller = HttpController(db, SeedPrivilegedUser(db));
        model.SupplierId = db.BaseSuppliers.Single().Id;
        model.Details[0].ProductId = db.BaseProducts.Single().Id;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(model));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.False(await db.PurchaseOrders.AnyAsync());
        Assert.False(await db.PurchaseOrderDetails.AnyAsync());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public async Task 显式非法起运港0或负数_绑定放行但服务端仍按既有主数据护栏拒绝且零写入(string value)
    {
        var (model, state) = await BindAsync(OrderJson(1L, 1L, portIdJson: value));
        Assert.NotNull(model);
        Assert.True(state.IsValid, Describe(state));
        Assert.Equal(long.Parse(value), model!.PortId);

        using var db = TestDbFactory.Create();
        SeedSupplier(db);
        SeedProduct(db);
        var controller = HttpController(db, SeedPrivilegedUser(db));
        model.SupplierId = db.BaseSuppliers.Single().Id;
        model.Details[0].ProductId = db.BaseProducts.Single().Id;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(model));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.False(await db.PurchaseOrders.AnyAsync());
    }

    [Fact]
    public async Task 非法正文_仍按模型绑定错误拒绝()
    {
        var (model, state) = await BindAsync("{\"supplierId\":");

        Assert.Null(model);
        Assert.False(state.IsValid);
        Assert.NotEmpty(state);
    }

    // ==================== 3. 控制器生命周期（真实主数据 + 真实绑定正文） ====================

    [Fact]
    public async Task 手工未关联采购_省略单号与可选引用_服务端权威单号并原样落库()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var userId = SeedPrivilegedUser(db);

        var (model, _) = await BindAsync(OrderJson(supplier.Id, product.Id));
        Assert.NotNull(model);

        var controller = HttpController(db, userId);
        Assert.IsType<OkObjectResult>(await controller.Create(model!));

        var persisted = db.PurchaseOrders.AsNoTracking().Single();
        Assert.StartsWith("PO", persisted.OrderNo, StringComparison.Ordinal);
        Assert.Equal(DocumentStatus.Pending, persisted.Status);
        Assert.Null(persisted.OwningSalesOrderId);
        Assert.Null(persisted.PortId);
        Assert.Equal(50m, persisted.TotalAmount);
        Assert.Equal("月结 30 天", persisted.PaymentTerms);
    }

    [Fact]
    public async Task 显式合法来源与起运港_权威来源快照与执行结算字段原样保留()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var port = SeedPort(db);
        var salesOrder = SeedSalesOrder(db, customer.Id, product.Id, DocumentStatus.Approved);
        var userId = SeedPrivilegedUser(db);

        var (model, state) = await BindAsync(OrderJson(supplier.Id, product.Id,
            owningSalesOrderIdJson: salesOrder.Id.ToString(), portIdJson: port.Id.ToString()));
        Assert.NotNull(model);
        Assert.True(state.IsValid, Describe(state));

        var controller = HttpController(db, userId);
        model!.TaxRate = 13m;
        model.ArrivalProgress = "部分到货";
        model.SettlementProgress = "未结算";
        Assert.IsType<OkObjectResult>(await controller.Create(model));

        var persisted = db.PurchaseOrders.AsNoTracking().Single();
        // 权威来源：来源 Id / 单号与归属客户快照由服务端从已审核来源派生，绝不采信客户端冲突快照。
        Assert.Equal(salesOrder.Id, persisted.OwningSalesOrderId);
        Assert.Equal(salesOrder.OrderNo, persisted.OwningSalesOrderNo);
        Assert.Equal(customer.Id, persisted.OwningCustomerId);
        Assert.Equal(customer.CustomerName, persisted.OwningCustomerName);
        Assert.Equal(port.Id, persisted.PortId);
        Assert.StartsWith("PO", persisted.OrderNo, StringComparison.Ordinal);
        // 原有采购执行 / 结算字段原样保留。
        Assert.Equal(13m, persisted.TaxRate);
        Assert.Equal("部分到货", persisted.ArrivalProgress);
        Assert.Equal("未结算", persisted.SettlementProgress);
    }

    [Fact]
    public async Task 未审核来源_受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var pending = SeedSalesOrder(db, customer.Id, product.Id, DocumentStatus.Pending);

        var (model, _) = await BindAsync(OrderJson(supplier.Id, product.Id,
            owningSalesOrderIdJson: pending.Id.ToString()));
        var controller = HttpController(db, SeedPrivilegedUser(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(model!));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseSalesOrderLinkRules.RequiredMenuText, PurchaseSalesOrderLinkRules.RuleText);
        Assert.False(await db.PurchaseOrders.AnyAsync());
    }

    [Fact]
    public async Task 明细商品不在来源销售订单中_受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var supplier = SeedSupplier(db);
        var sourceProduct = SeedProduct(db);
        var otherProduct = SeedProduct(db);
        var salesOrder = SeedSalesOrder(db, customer.Id, sourceProduct.Id, DocumentStatus.Approved);

        var (model, _) = await BindAsync(OrderJson(supplier.Id, otherProduct.Id,
            owningSalesOrderIdJson: salesOrder.Id.ToString()));
        var controller = HttpController(db, SeedPrivilegedUser(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(model!));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.False(await db.PurchaseOrders.AnyAsync());
    }

    [Fact]
    public async Task 修改_省略来源_保留既有血缘且绝不静默清除()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var port = SeedPort(db);
        var salesOrder = SeedSalesOrder(db, customer.Id, product.Id, DocumentStatus.Approved);
        var controller = HttpController(db, SeedPrivilegedUser(db));

        var (create, _) = await BindAsync(OrderJson(supplier.Id, product.Id,
            owningSalesOrderIdJson: salesOrder.Id.ToString(), portIdJson: port.Id.ToString()));
        Assert.IsType<OkObjectResult>(await controller.Create(create!));
        var orderId = db.PurchaseOrders.AsNoTracking().Single().Id;

        // 前端重新打开后未触碰来源字段：正文省略 owningSalesOrderId（旧前端会回落为 0 而被拒绝）。
        var (update, state) = await BindAsync(OrderJson(supplier.Id, product.Id, portIdJson: port.Id.ToString()));
        Assert.NotNull(update);
        Assert.True(state.IsValid, Describe(state));
        Assert.Null(update!.OwningSalesOrderId);

        Assert.IsType<OkObjectResult>(await controller.Update(orderId, update));

        var persisted = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == orderId);
        Assert.Equal(salesOrder.Id, persisted.OwningSalesOrderId);
        Assert.Equal(salesOrder.OrderNo, persisted.OwningSalesOrderNo);
        Assert.Equal(customer.Id, persisted.OwningCustomerId);
        Assert.Equal(port.Id, persisted.PortId);
    }

    [Fact]
    public async Task 修改_显式非法来源0_受控拒绝且血缘与状态不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var salesOrder = SeedSalesOrder(db, customer.Id, product.Id, DocumentStatus.Approved);
        var controller = HttpController(db, SeedPrivilegedUser(db));

        var (create, _) = await BindAsync(OrderJson(supplier.Id, product.Id,
            owningSalesOrderIdJson: salesOrder.Id.ToString()));
        Assert.IsType<OkObjectResult>(await controller.Create(create!));
        var orderId = db.PurchaseOrders.AsNoTracking().Single().Id;

        var (update, _) = await BindAsync(OrderJson(supplier.Id, product.Id, owningSalesOrderIdJson: "0"));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(orderId, update!));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var persisted = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == orderId);
        Assert.Equal(salesOrder.Id, persisted.OwningSalesOrderId);
        Assert.Equal(DocumentStatus.Pending, persisted.Status);
    }

    [Fact]
    public async Task 无效供应商_受控拒绝且零写入零单号()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db);
        var productId = db.BaseProducts.Single().Id;

        var (model, _) = await BindAsync(OrderJson(987_654_321L, productId));
        var controller = HttpController(db, SeedPrivilegedUser(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(model!));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.False(await db.PurchaseOrders.AnyAsync());
    }

    [Theory]
    [InlineData("0", "5")]
    [InlineData("10", "-1")]
    [InlineData("10", "5", "200")]
    public async Task 非法数量单价或税率_受控拒绝且不消耗单号不落库(string quantity, string unitPrice, string taxRate = "0")
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var rule = SeedPurchaseOrderNumberRule(db);

        var (model, _) = await BindAsync(OrderJson(supplier.Id, product.Id,
            quantityJson: quantity, unitPriceJson: unitPrice, taxRateJson: taxRate));
        var controller = HttpController(db, SeedPrivilegedUser(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(model!));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.False(await db.PurchaseOrders.AnyAsync());
        Assert.False(await db.PurchaseOrderDetails.AnyAsync());
        // 单号预约在条款校验之后：非法请求既不落库也不消耗单号（规则流水保持 0）。
        Assert.Equal(0L, await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.Id == rule.Id).Select(r => r.CurrentSequence).SingleAsync());
    }

    // ==================== 4. 权限仍 fail closed（真实请求管线） ====================

    /// <summary>
    /// 受限业务员只能操作本人客户范围内单据：把采购订单链接到<b>外来客户</b>的已审核来源，
    /// 必须按权威客户数据范围拒绝（先于主数据披露）、零写入；自有客户范围内的来源仍可正常链接。
    /// </summary>
    [Fact]
    public async Task 受限账号_越界归属客户来源_先于主数据披露拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var role = SeedPurchaseOrderRole(db);
        const long OwnCustomerId = 4_290_001L;
        var userId = SeedSalesman(db, role, OwnCustomerId);
        var ownCustomer = db.BaseCustomers.Single(c => c.Id == OwnCustomerId);
        var foreignCustomer = SeedCustomer(db, OwnCustomerId + 7919);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var controller = HttpController(db, userId);

        var foreignSource = SeedSalesOrder(db, foreignCustomer.Id, product.Id, DocumentStatus.Approved);
        var (foreign, _) = await BindAsync(OrderJson(supplier.Id, product.Id,
            owningSalesOrderIdJson: foreignSource.Id.ToString()));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(foreign!));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("数据范围", ex.Message);
        Assert.False(await db.PurchaseOrders.AnyAsync());

        // 自有客户范围内的已审核来源仍可用（授权后行为不变）。
        var ownSource = SeedSalesOrder(db, ownCustomer.Id, product.Id, DocumentStatus.Approved);
        var (own, state) = await BindAsync(OrderJson(supplier.Id, product.Id,
            owningSalesOrderIdJson: ownSource.Id.ToString()));
        Assert.True(state.IsValid, Describe(state));
        Assert.IsType<OkObjectResult>(await controller.Create(own!));

        var persisted = db.PurchaseOrders.AsNoTracking().Single();
        Assert.Equal(ownSource.Id, persisted.OwningSalesOrderId);
        Assert.Equal(OwnCustomerId, persisted.OwningCustomerId);
    }

    [Fact]
    public async Task 无身份_即便省略单号与可选引用_仍按未认证拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);

        var (model, _) = await BindAsync(OrderJson(supplier.Id, product.Id));
        Assert.NotNull(model);

        var controller = HttpController(db, userId: null);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(model!));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.False(await db.PurchaseOrders.AnyAsync());
    }

    [Fact]
    public async Task 受限账号_缺既有菜单_仍按权限不足拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);

        var (model, _) = await BindAsync(OrderJson(supplier.Id, product.Id));
        Assert.NotNull(model);

        var controller = HttpController(db, SeedUserWithoutPurchaseOrderMenu(db));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(model!));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        Assert.False(await db.PurchaseOrders.AnyAsync());
    }

    // ==================== 5. 前端序列化与控制器接线契约（源码） ====================

    [Fact]
    public void 前端契约_逐字段选择加入仅销售目的港与采购可选来源起运港()
    {
        var crud = ReadRepoFile("src", "ERP.Api", "wwwroot", "js", "crud.js");
        // 只有显式可选（f.nullable === true）才提交 null；未声明的数值字段仍回落 0（必填数值零值默认不变）。
        Assert.Contains("f.nullable === true ? null : 0", crud);
        Assert.Contains("if (f.type === 'number') v = v === '' ? (f.nullable === true ? null : 0) : Number(v);", crud);

        var modules = ReadRepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc.js");
        // 精确有限白名单：销售目的港 + 采购可选归属来源 / 起运港。
        Assert.Contains(
            "{ key: 'portId', label: '目的港 Id（港口字典，可留空）', type: 'number', nullable: true }", modules);
        Assert.Contains(
            "{ key: 'owningSalesOrderId', label: '归属销售订单 ID', type: 'number', nullable: true, selector: 'purchase-order-sales-order-source' }",
            modules);
        Assert.Contains("{ key: 'portId', label: '起运港 Id（港口字典，可留空）', type: 'number', nullable: true }", modules);
        // 全站仅此 3 处；其它可用数值字段（汇率 / 税率 / 明细数量单价等）保持默认零值口径，绝不扩散为全局可空转换。
        Assert.Equal(3, CountOccurrences(modules, "nullable: true"));
    }

    [Fact]
    public void 控制器契约_单号在授权与主数据校验之后权威生成且无全局校验抑制()
    {
        var controller = ReadRepoFile("src", "ERP.Api", "Controllers", "PurchaseOrderController.cs");

        // 绝不通过全局校验抑制来放行：只移除实体上针对服务端自有单号的显式必填。
        Assert.DoesNotContain("SuppressImplicitRequiredAttributeForNonNullableReferenceTypes", controller);
        Assert.Contains("entity.OrderNo = await _noService.GenerateAsync(DocumentType.PurchaseOrder);", controller);

        // 绑定契约放宽之后，授权 → 条款 / 主数据校验 → 权威单号 的顺序必须保持不变。
        var create = controller.IndexOf(
            "public async Task<IActionResult> Create([FromBody] PurchaseOrder entity)", StringComparison.Ordinal);
        var authorize = controller.IndexOf("EnsureProposedAuthorizedAsync(entity)", create, StringComparison.Ordinal);
        var terms = controller.IndexOf("PurchaseOrderMutationRules.EnsureValidatedTerms(entity)", create, StringComparison.Ordinal);
        var masters = controller.IndexOf("await EnsureMasterReferencesAsync(entity)", create, StringComparison.Ordinal);
        var generate = controller.IndexOf(
            "_noService.GenerateAsync(DocumentType.PurchaseOrder)", create, StringComparison.Ordinal);

        Assert.True(create >= 0 && authorize > create, "写入授权必须先于单号生成");
        Assert.True(terms > authorize, "条款校验必须在授权之后");
        Assert.True(masters > terms, "实时主数据校验必须在条款校验之后");
        Assert.True(generate > masters, "单号必须在授权与主数据校验之后生成");
    }

    // ==================== 6. 脚手架与种子数据 ====================

    private static string ReadRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var parts = new List<string> { directory!.FullName };
        parts.AddRange(segments);
        return File.ReadAllText(Path.Combine(parts.ToArray()));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    /// <summary>绑定到真实 HTTP 请求管线（<c>Request.Path</c> 已赋值）的控制器，使实时授权与主数据复核按真实请求口径生效。</summary>
    private static PurchaseOrderController HttpController(ErpDbContext db, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        http.Request.Path = "/api/purchase-orders";
        return new PurchaseOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static BaseSupplier SeedSupplier(ErpDbContext db)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = $"S-429-{Guid.NewGuid():N}", SupplierName = "ERP429 供应商", Status = 1
        };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, string unit = "PCS")
    {
        var product = new BaseProduct
        {
            ProductCode = $"P-429-{Guid.NewGuid():N}", ProductName = "ERP429 商品", Spec = "中",
            Unit = unit, Status = 1
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static BaseOtherInfo SeedPort(ErpDbContext db)
    {
        var port = new BaseOtherInfo
        {
            InfoType = PurchaseOrderMasterReferenceRules.PortInfoType,
            InfoCode = $"PORT-429-{Guid.NewGuid():N}", InfoName = "ERP429 起运港", Status = 1
        };
        db.BaseOtherInfos.Add(port);
        db.SaveChanges();
        return port;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-429-{Guid.NewGuid():N}", CustomerName = "ERP429 客户", Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    /// <summary>按指定 Id 播种客户（不存在才创建），并可把客户分配为指定员工的本人客户。</summary>
    private static BaseCustomer SeedCustomer(ErpDbContext db, long id, long? empId = null)
    {
        var existing = db.BaseCustomers.FirstOrDefault(c => c.Id == id);
        if (existing is not null) return existing;

        var customer = new BaseCustomer
        {
            Id = id, CustomerCode = $"C-429-{id}", CustomerName = $"ERP429 客户 {id}",
            EmpId = empId, Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    /// <summary>播种受限采购业务员（登录账号 == 员工编码，ERP-097 权威映射），并把指定客户分配为其本人客户。</summary>
    private static long SeedSalesman(ErpDbContext db, SysRole role, params long[] ownedCustomerIds)
    {
        var code = $"form429-s-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = "ERP429 采购业务员", IsSalesman = true, Status = 1
        };
        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP429 采购业务员", Status = UserStatus.Enabled
        };
        db.BaseEmployees.Add(employee);
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var customerId in ownedCustomerIds)
        {
            var customer = SeedCustomer(db, customerId);
            customer.EmpId = employee.Id;
            db.SaveChanges();
        }

        return user.Id;
    }

    /// <summary>播种一张来源销售订单（真实商品 / 单位，与采购明细同源），按状态用于已审核 / 未审核来源场景。</summary>
    private static SalesOrder SeedSalesOrder(ErpDbContext db, long customerId, long productId,
        DocumentStatus status, string unit = "PCS")
    {
        var order = new SalesOrder
        {
            OrderNo = $"SO-429-{Guid.NewGuid():N}",
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            Status = status,
            Details = new List<SalesOrderDetail>
            {
                new()
                {
                    ProductId = productId, ProductName = "ERP429 来源商品", Spec = "中", Unit = unit,
                    Quantity = 10m, UnitPrice = 5m, Amount = 50m
                }
            }
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>播种采购单号规则（CurrentSequence = 0），用于证明非法请求不消耗单号。</summary>
    private static SysDocumentNumberRule SeedPurchaseOrderNumberRule(ErpDbContext db)
    {
        var rule = new SysDocumentNumberRule
        {
            DocumentType = DocumentType.PurchaseOrder,
            RuleCode = $"PO-429-{Guid.NewGuid():N}",
            RuleName = "ERP429 采购单号规则",
            Prefix = "PO",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = 0
        };
        db.SysDocumentNumberRules.Add(rule);
        db.SaveChanges();
        return rule;
    }

    /// <summary>播种特权采购账号（系统内置角色 + 既有 purchase-order 菜单），不新增任何用户授权。</summary>
    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var role = SeedPurchaseOrderRole(db);
        role.IsSystem = true;
        db.SaveChanges();
        return SeedUser(db, role, UserStatus.Enabled);
    }

    /// <summary>播种带既有 purchase-order 菜单的普通角色。</summary>
    private static SysRole SeedPurchaseOrderRole(ErpDbContext db)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = PurchaseOrderAuthorizationRules.RequiredMenuCode,
            MenuName = PurchaseOrderAuthorizationRules.RequiredMenuText,
            Path = "/purchase/purchase-order",
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        var role = new SysRole { RoleCode = $"FORM429-{Guid.NewGuid():N}", RoleName = "ERP429 采购角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return role;
    }

    /// <summary>播种启用账号但只授予无关菜单（缺少既有 purchase-order 菜单）。</summary>
    private static long SeedUserWithoutPurchaseOrderMenu(ErpDbContext db)
    {
        var menu = new SysMenu
        {
            ParentId = 0, MenuCode = "stock-query", MenuName = "库存查询", Path = "/stock/query",
            MenuType = MenuType.Menu, CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        var role = new SysRole { RoleCode = $"FORM429-O-{Guid.NewGuid():N}", RoleName = "ERP429 其他角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return SeedUser(db, role, UserStatus.Enabled);
    }

    private static long SeedUser(ErpDbContext db, SysRole role, UserStatus status)
    {
        var user = new SysUser
        {
            UserName = $"form429-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP429 账号", Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }
}
