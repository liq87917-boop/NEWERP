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
using System.Text;
using System.Text.Json.Serialization;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-428 手工销售订单录入的「HTTP 表单 / 正文绑定契约」单元测试（纯内存，不连接 SQL Server、不启动浏览器）。
/// <list type="number">
/// <item><b>真实 MVC 绑定 + 校验管线</b>：直接使用 MVC 自己的 <see cref="IModelBinderFactory"/>（正文绑定源）与
/// <see cref="IObjectModelValidator"/>（与 <c>[ApiController]</c> 自动 400 完全同一条校验路径）读取真实 JSON 正文 ——
/// 因此「省略 / 伪造订单号」「可选目的港留空为 null」是在<b>真实模型绑定 / 模型校验</b>层被证明的，
/// 而不是只断言控制器内部行为（后者无法证明绑定层不再拒绝请求）。</item>
/// <item><b>服务端权威单号</b>：绑定后的实体交给真实 <see cref="SalesOrderController"/>，证明调用方提交值
/// 不决定持久化单号，省略单号时由 <c>IDocumentNumberService</c> 权威生成并落库。</item>
/// <item><b>既有护栏不被放宽</b>：目的港 0 / 负数 / 不存在仍在既有 ERP-423 主数据护栏被拒绝；无身份 / 缺菜单
/// 仍 fail closed 且零写入；非法正文仍按绑定错误拒绝；数据库列仍为 NOT NULL 且长度 50。</item>
/// </list>
/// <para>安全口径：全部使用内存库 <see cref="TestDbFactory"/>，不读取 appsettings / .env / 生产凭据，
/// 不执行 drop / reset，也不使用生产数据。</para>
/// </summary>
public class SalesOrderHttpFormContractTests
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
    /// 从 JSON 正文绑定 <see cref="SalesOrder"/>，随后执行 MVC 真实模型校验。
    /// </summary>
    private static async Task<(SalesOrder? Model, ModelStateDictionary ModelState)> BindAsync(string json)
    {
        var http = new DefaultHttpContext();
        var payload = Encoding.UTF8.GetBytes(json);
        http.Request.Method = HttpMethods.Post;
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.ContentLength = payload.Length;
        http.Request.Body = new MemoryStream(payload);

        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor(), new ModelStateDictionary());
        var metadata = Mvc.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(SalesOrder));
        // 绑定源必须是正文：与 MVC 为 [FromBody] 参数解析出的绑定器选择完全一致（由 BindingInfo 决定绑定器提供程序）。
        var bindingInfo = new BindingInfo { BindingSource = BindingSource.Body, BinderModelName = "entity" };
        var binder = Mvc.GetRequiredService<IModelBinderFactory>().CreateBinder(
            new ModelBinderFactoryContext { Metadata = metadata, BindingInfo = bindingInfo, CacheToken = new object() });
        var context = DefaultModelBindingContext.CreateBindingContext(
            actionContext, new CompositeValueProvider(), metadata, bindingInfo, "entity");
        context.IsTopLevelObject = true;
        await binder.BindModelAsync(context);

        var model = context.Result.Model as SalesOrder;
        if (context.Result.IsModelSet && model is not null)
            Mvc.GetRequiredService<IObjectModelValidator>()
                .Validate(actionContext, validationState: null, prefix: string.Empty, model: model);

        return (model, actionContext.ModelState);
    }

    private static string Describe(ModelStateDictionary state)
        => state.IsValid ? "(valid)" : string.Join(" | ",
            state.Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value!.Errors.Select(e => e.ErrorMessage))}]"));

    /// <summary>与浏览器 / 前端 crud.js 提交的手工销售订单正文同形（可省略订单号、可选目的港）。</summary>
    private static string OrderJson(long customerId, long productId,
        string? orderNoJson = null, string? portIdJson = null)
    {
        var orderNo = orderNoJson is null ? string.Empty : $",\"orderNo\":{orderNoJson}";
        var portId = portIdJson is null ? string.Empty : $",\"portId\":{portIdJson}";
        return "{\"orderDate\":\"" + DateTime.Today.ToString("yyyy-MM-dd") + "\""
            + orderNo + portId
            + ",\"customerId\":" + customerId
            + ",\"currency\":\"USD\",\"exchangeRate\":7.2,\"depositRatio\":30,\"commissionRatio\":0"
            + ",\"customerPoNo\":\"BUYERPO-428\",\"contractNo\":\"SC-428\",\"tradeTerms\":\"FOB\""
            + ",\"details\":[{\"productId\":" + productId
            + ",\"productName\":\"契约商品\",\"spec\":\"大\",\"unit\":\"PCS\",\"quantity\":10,\"unitPrice\":100}]}";
    }

    // ==================== 1. 实体 / 数据库契约（单号仍非空、仍限长） ====================

    private static ErpDbContext ModelOnlyContext()
        => new(new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_MODEL_ONLY;Integrated Security=true")
            .Options);

    [Fact]
    public void 订单号_无显式必填特性_但数据库列仍非空且长度仍为50()
    {
        var property = typeof(SalesOrder).GetProperty(nameof(SalesOrder.OrderNo), BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);

        // 显式 [Required]（AllowEmptyStrings=false）已移除：省略 / 空串单号不再在绑定 / 校验层被 400 拒绝。
        Assert.Null(property!.GetCustomAttribute<RequiredAttribute>());
        // 长度约束保留（HTTP 模型校验层仍然限制 50）。
        Assert.Equal(50, property.GetCustomAttribute<MaxLengthAttribute>()?.Length);

        // 数据库列契约保留：仍是 NOT NULL、长度 50（非空 CLR 引用类型 + [MaxLength] 经 EF 模型体现）。
        using var db = ModelOnlyContext();
        var efProperty = db.Model.FindEntityType(typeof(SalesOrder))!.FindProperty(nameof(SalesOrder.OrderNo))!;
        Assert.False(efProperty.IsNullable);
        Assert.Equal(50, efProperty.GetMaxLength());
    }

    // ==================== 2. 省略 / 伪造单号的真实绑定契约 ====================

    [Fact]
    public async Task 省略订单号_真实模型绑定与校验通过且不产生订单号错误()
    {
        using var db = TestDbFactory.Create();
        var (customer, product) = SeedMasters(db);

        var (model, state) = await BindAsync(OrderJson(customer.Id, product.Id));

        Assert.NotNull(model);
        Assert.True(state.IsValid, Describe(state));
        Assert.DoesNotContain(state.Keys, k => k.Contains(nameof(SalesOrder.OrderNo), StringComparison.Ordinal));
        Assert.Equal(string.Empty, model!.OrderNo);
    }

    [Fact]
    public async Task 空串订单号_真实模型绑定与校验仍通过()
    {
        using var db = TestDbFactory.Create();
        var (customer, product) = SeedMasters(db);

        var (model, state) = await BindAsync(OrderJson(customer.Id, product.Id, orderNoJson: "\"\""));

        Assert.NotNull(model);
        Assert.True(state.IsValid, Describe(state));
        Assert.Equal(string.Empty, model!.OrderNo);
    }

    [Fact]
    public async Task 伪造订单号_绑定保留调用方值_但持久化号码由服务端权威生成()
    {
        using var db = TestDbFactory.Create();
        var (customer, product) = SeedMasters(db);
        var forged = "HACK-ORDER-428";

        var (model, state) = await BindAsync(OrderJson(customer.Id, product.Id, orderNoJson: '"' + forged + '"'));
        Assert.NotNull(model);
        Assert.True(state.IsValid, Describe(state));
        Assert.Equal(forged, model!.OrderNo);          // 绑定层不吞掉调用方值……

        var controller = NewController(db, TestAuth.SeedPrivilegedUser(db));
        Assert.IsType<OkObjectResult>(await controller.Create(model));

        // ……但持久化号码由服务端权威生成：调用方提交值不能决定落库单号。
        var stored = await db.SalesOrders.AsNoTracking().SingleAsync();
        Assert.StartsWith("SO", stored.OrderNo, StringComparison.Ordinal);
        Assert.NotEqual(forged, stored.OrderNo);
    }

    [Fact]
    public async Task 省略订单号_控制器新增_服务端权威号码落库并可回读()
    {
        using var db = TestDbFactory.Create();
        var (customer, product) = SeedMasters(db);

        var (model, state) = await BindAsync(OrderJson(customer.Id, product.Id));
        Assert.NotNull(model);
        Assert.True(state.IsValid, Describe(state));

        var controller = NewController(db, TestAuth.SeedPrivilegedUser(db));
        Assert.IsType<OkObjectResult>(await controller.Create(model!));

        var stored = await db.SalesOrders.AsNoTracking().SingleAsync();
        Assert.StartsWith("SO", stored.OrderNo, StringComparison.Ordinal);
        Assert.True(stored.OrderNo.Length is > 0 and <= 50);

        var detail = Assert.IsType<ApiResponse<SalesOrder>>(
            Assert.IsType<OkObjectResult>(await controller.GetById(stored.Id)).Value).Data!;
        Assert.Equal(stored.OrderNo, detail.OrderNo);
    }

    // ==================== 3. 可选目的港：留空 = null，非法引用仍被拒绝 ====================

    [Fact]
    public async Task 可选目的港_留空或显式null_绑定为null且校验通过()
    {
        using var db = TestDbFactory.Create();
        var (customer, product) = SeedMasters(db);

        var (omitted, omittedState) = await BindAsync(OrderJson(customer.Id, product.Id));
        Assert.NotNull(omitted);
        Assert.True(omittedState.IsValid, Describe(omittedState));
        Assert.Null(omitted!.PortId);

        var (explicitNull, nullState) = await BindAsync(OrderJson(customer.Id, product.Id, portIdJson: "null"));
        Assert.NotNull(explicitNull);
        Assert.True(nullState.IsValid, Describe(nullState));
        Assert.Null(explicitNull!.PortId);
    }

    [Fact]
    public async Task 可选目的港_正整数_原样绑定并通过校验()
    {
        using var db = TestDbFactory.Create();
        var (customer, product) = SeedMasters(db);
        var port = SeedPort(db);

        var (model, state) = await BindAsync(OrderJson(customer.Id, product.Id, portIdJson: port.Id.ToString()));

        Assert.NotNull(model);
        Assert.True(state.IsValid, Describe(state));
        Assert.Equal(port.Id, model!.PortId);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public async Task 可选目的港_零或负数_服务端仍按参数错误拒绝且零写入(string portIdJson)
    {
        using var db = TestDbFactory.Create();
        var (customer, product) = SeedMasters(db);

        var (model, state) = await BindAsync(OrderJson(customer.Id, product.Id, portIdJson: portIdJson));
        Assert.NotNull(model);
        Assert.True(state.IsValid, Describe(state));

        var controller = NewController(db, TestAuth.SeedPrivilegedUser(db));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(model!));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderMasterReferenceRules.PortRequiredText, ex.Message);
        Assert.False(await db.SalesOrders.AnyAsync());
        Assert.False(await db.SalesOrderDetails.AnyAsync());
    }

    [Fact]
    public async Task 可选目的港_不存在_服务端仍按不存在拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var (customer, product) = SeedMasters(db);

        var (model, _) = await BindAsync(OrderJson(customer.Id, product.Id, portIdJson: "999999"));
        Assert.NotNull(model);

        var controller = NewController(db, TestAuth.SeedPrivilegedUser(db));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(model!));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.False(await db.SalesOrders.AnyAsync());
    }

    // ==================== 4. 非法正文 / 权限仍 fail closed ====================

    [Fact]
    public async Task 非法正文_仍按模型绑定错误拒绝()
    {
        var (model, state) = await BindAsync("{\"customerId\":");

        Assert.Null(model);
        Assert.False(state.IsValid);
        Assert.NotEmpty(state);
    }

    [Fact]
    public async Task 无身份_即便省略单号与目的港留空_仍按未认证拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var (customer, product) = SeedMasters(db);

        var (model, _) = await BindAsync(OrderJson(customer.Id, product.Id));
        Assert.NotNull(model);

        var controller = NewController(db, userId: null);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(model!));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.False(await db.SalesOrders.AnyAsync());
    }

    [Fact]
    public async Task 受限账号_缺既有菜单_仍按权限不足拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var (customer, product) = SeedMasters(db);

        var (model, _) = await BindAsync(OrderJson(customer.Id, product.Id));
        Assert.NotNull(model);

        var controller = NewController(db, SeedRestrictedUser(db));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(model!));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(SalesOrderMutationAuthorizationRules.MenuDeniedText, ex.Message);
        Assert.False(await db.SalesOrders.AnyAsync());
    }

    // ==================== 5. 前端序列化与控制器接线契约 ====================

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

    [Fact]
    public void 前端契约_可选数值留空提交null_必填数值零值默认不变_且全站仅目的港加入()
    {
        var crud = ReadRepoFile("src", "ERP.Api", "wwwroot", "js", "crud.js");
        // 只有显式可选（f.nullable === true）才提交 null；未声明的数值字段仍回落 0（既有必填数值零值默认不变）。
        Assert.Contains("f.nullable === true ? null : 0", crud);

        var modules = ReadRepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc.js");
        Assert.Contains(
            "{ key: 'portId', label: '目的港 Id（港口字典，可留空）', type: 'number', nullable: true }", modules);
        // 选择加入是逐字段的：全站模块目录里只有销售订单目的港这一处，其它模块语义不变。
        Assert.Equal(1, CountOccurrences(modules, "nullable: true"));
    }

    [Fact]
    public void 控制器契约_单号在授权与主数据校验之后权威生成且无全局校验抑制()
    {
        var controller = ReadRepoFile("src", "ERP.Api", "Controllers", "SalesOrderController.cs");

        // 绝不通过全局校验抑制来放行：只移除实体上针对服务端自有单号的显式必填。
        Assert.DoesNotContain("SuppressImplicitRequiredAttributeForNonNullableReferenceTypes", controller);
        Assert.Contains("entity.OrderNo = await _noService.GenerateAsync(DocumentType.SalesOrder);", controller);

        // 绑定契约放宽之后，授权 → 主数据校验 → 权威单号 的顺序必须保持不变。
        var create = controller.IndexOf(
            "public async Task<IActionResult> Create([FromBody] SalesOrder entity)", StringComparison.Ordinal);
        var authorize = controller.IndexOf("EnsureCanonicalWriteAuthorizedAsync", create, StringComparison.Ordinal);
        var masters = controller.IndexOf(
            "SalesOrderMasterReferenceRules.EnsureMasterReferencesAsync", create, StringComparison.Ordinal);
        var generate = controller.IndexOf(
            "_noService.GenerateAsync(DocumentType.SalesOrder)", create, StringComparison.Ordinal);

        Assert.True(create >= 0 && authorize > create, "写入授权必须先于单号生成");
        Assert.True(masters > authorize, "实时主数据校验必须先于单号生成");
        Assert.True(generate > masters, "单号必须在授权与主数据校验之后生成");
    }

    // ==================== 6. 脚手架与种子数据 ====================

    private static SalesOrderController NewController(ErpDbContext db, long? userId)
    {
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static (BaseCustomer Customer, BaseProduct Product) SeedMasters(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-428-{Guid.NewGuid():N}", CustomerName = "ERP428 客户",
            Status = 1, DepositRatio = 30m
        };
        var product = new BaseProduct
        {
            ProductCode = $"P-428-{Guid.NewGuid():N}", ProductName = "ERP428 商品", Unit = "PCS", Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return (customer, product);
    }

    private static BaseOtherInfo SeedPort(ErpDbContext db)
    {
        var port = new BaseOtherInfo
        {
            InfoType = SalesOrderMasterReferenceRules.PortInfoType,
            InfoCode = $"PORT-428-{Guid.NewGuid():N}", InfoName = "ERP428 港口", Status = 1
        };
        db.BaseOtherInfos.Add(port);
        db.SaveChanges();
        return port;
    }

    private static long SeedRestrictedUser(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"form428-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP428 受限账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleCode = $"FORM428-{Guid.NewGuid():N}", RoleName = "ERP428 受限角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }
}
