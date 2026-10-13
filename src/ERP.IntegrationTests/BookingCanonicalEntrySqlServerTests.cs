using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-484 订柜信息规范入口（<c>api/container/bookings</c>）的<b>真实 HTTP + 真实隔离 SQL</b> 验收
/// （真实 ERP.Api 进程 + 全新 GUID <c>NEWERP_AUTOTEST</c> 库）：
/// <list type="number">
/// <item><b>真实 HTTP</b>：请求经真实 Kestrel 管线（MVC 绑定 + <c>[ApiController]</c> 模型校验 + JWT 认证 + 既有授权），
/// 因此「省略订柜单号」由<b>绑定层</b>证明（修复前正是绑定层因 <c>[Required]</c> 返回 HTTP 400
/// <c>{"errors":{"BookingNo":[...]}}</c>），直接实例化控制器的测试无法证明这一点；夹具为真实 API 进程注入
/// <b>进程内临时 JWT 签名密钥</b>（随机生成、只存活在该进程、绝不落盘 / 绝不继承空白签名配置），认证链路保持真实。</item>
/// <item><b>权威单号与规范 Id</b>：省略 / 伪造订柜单号一律持久化服务端生成单号（<c>DG</c> 前缀），返回规范 <c>Id</c>
/// 与待提交草稿状态；回读（详情 / 列表）与修改都按规范 <c>Id</c> 往返。</item>
/// <item><b>typed 可空引用与日期 / 跟踪字段</b>：可选供应商与报关行可显式关联 / 显式清空（<c>null</c> = 未指定），
/// 可选日期与 ERP-040 跟踪字段（出运方式 / B/L / S-O / 中转港 / ETD·ETA·ATD·ATA / 拖车 / 查验三态 / 放行日期）
/// 保存 → 重开 → 修改原样往返。</item>
/// <item><b>有效生命周期</b>：提交 → 审核 → 取消按既有授权执行；非法流转（待提交直接审核 / 取消后再提交）零状态变化。</item>
/// <item><b>零副作用拒绝</b>：匿名 / 缺菜单 / 停用客户 / 类型不符报关行一律受控拒绝，且订柜行、单据号序列与
/// 预装柜 / 库存 / 库存流水 / 财务表零变化；有效状态变更同样不产生下游记录。</item>
/// <item><b>专用目标护栏</b>：必须在任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + 库名前缀
/// <c>NEWERP_AUTOTEST</c> + <c>Integrated Security=true</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在
/// 立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>客户 / 供应商 / 报关行字典项为<b>夹具数据</b>（EF 直接播种到本次全新隔离库的既有 <c>BaseCustomers</c> /
/// <c>BaseSuppliers</c> / <c>BaseOtherInfos</c> 表），被测写入（订柜创建 / 修改 / 提交 / 审核 / 取消）全部经真实 HTTP；
/// 构建完成不等于阶段验收。</para>
/// </summary>
public sealed class BookingCanonicalEntrySqlServerTests
    : IClassFixture<BookingCanonicalEntrySqlServerFixture>
{
    private readonly BookingCanonicalEntrySqlServerFixture _fx;

    public BookingCanonicalEntrySqlServerTests(BookingCanonicalEntrySqlServerFixture fixture) => _fx = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fx.ConnectionString);
        Assert.Equal($"(localdb)\\{BookingCanonicalEntrySqlServerFixture.InstanceMarker}",
            target.DataSource, ignoreCase: true);
        Assert.StartsWith(BookingCanonicalEntrySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ==================== HTTP 脚手架 ====================

    private static async Task<(HttpStatusCode Status, JsonElement Root, string Raw)> SendAsync(
        HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        return (response.StatusCode, doc.RootElement.Clone(), raw);
    }

    /// <summary>订柜信息正文：与前端 crud.js 提交同形（**省略 bookingNo**；可选引用 / 日期可为 <c>null</c>）。</summary>
    private static Dictionary<string, object?> BookingBody(
        long customerId, long? supplierId = null, long? customsBrokerId = null,
        DateTime? bookingDate = null, DateTime? sailingDate = null,
        string shipmentMode = "", string billOfLadingNo = "", string shippingOrderNo = "",
        string transitPort = "", DateTime? etd = null, DateTime? eta = null,
        DateTime? atd = null, DateTime? ata = null, string truckerName = "",
        bool? inspectionRequired = null, DateTime? inspectionDate = null,
        DateTime? customsReleaseDate = null, string remark = "HTTP484")
        => new()
        {
            ["bookingDate"] = bookingDate ?? DateTime.Today,
            ["customerId"] = customerId,
            ["supplierId"] = supplierId,
            ["containerType"] = "GP40",
            ["shippingCompany"] = "COSCO",
            ["voyageNo"] = "V-484",
            ["sailingDate"] = sailingDate,
            ["departurePort"] = "SHANGHAI",
            ["destinationPort"] = "ROTTERDAM",
            ["shipmentMode"] = shipmentMode,
            ["billOfLadingNo"] = billOfLadingNo,
            ["shippingOrderNo"] = shippingOrderNo,
            ["transitPort"] = transitPort,
            ["etd"] = etd,
            ["eta"] = eta,
            ["atd"] = atd,
            ["ata"] = ata,
            ["truckerName"] = truckerName,
            ["customsBrokerId"] = customsBrokerId,
            ["inspectionRequired"] = inspectionRequired,
            ["inspectionDate"] = inspectionDate,
            ["customsReleaseDate"] = customsReleaseDate,
            ["remark"] = remark
        };


    // ==================== 1. 省略订柜单号：真实 HTTP 新增 / 回读 / 修改 ====================

    [Fact]
    public async Task 省略订柜单号_真实HTTP新增回读修改_服务端权威单号与草稿状态()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await _fx.SeedCustomerAsync("BC484-C-" + tag);
        var supplierId = await _fx.SeedSupplierAsync("BS484-S-" + tag);
        var brokerId = await _fx.SeedCustomsBrokerAsync("BR484-B-" + tag);

        var (createdStatus, createdRoot, raw) = await SendAsync(admin, HttpMethod.Post,
            "/api/container/bookings",
            BookingBody(customerId, supplierId, brokerId,
                bookingDate: new DateTime(2026, 9, 30), sailingDate: new DateTime(2026, 10, 2),
                shipmentMode: "LCL", billOfLadingNo: "BL-HTTP484", shippingOrderNo: "SO-HTTP484",
                transitPort: "SINGAPORE", etd: new DateTime(2026, 10, 3), eta: new DateTime(2026, 10, 20),
                atd: new DateTime(2026, 10, 4), ata: new DateTime(2026, 10, 21), truckerName: "顺达拖车",
                inspectionRequired: true, inspectionDate: new DateTime(2026, 10, 1),
                customsReleaseDate: new DateTime(2026, 10, 5), remark: "HTTP484"));
        Assert.Equal(HttpStatusCode.OK, createdStatus);
        Assert.Equal(0, createdRoot.GetProperty("code").GetInt32());
        Assert.DoesNotContain("is required", raw, StringComparison.OrdinalIgnoreCase);

        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();
        var number = createdRoot.GetProperty("data").GetProperty("bookingNo").GetString()!;
        Assert.True(id > 0);
        Assert.StartsWith("DG", number, StringComparison.Ordinal);
        Assert.Equal("Pending", createdRoot.GetProperty("data").GetProperty("status").GetString());

        var persisted = await _fx.ReadBookingByIdAsync(id);
        Assert.Equal(1, persisted.Count);
        Assert.Equal(number, persisted.BookingNo);
        Assert.Equal(0, persisted.Status);                       // Pending（待提交草稿）
        Assert.False(persisted.IsDeleted);
        Assert.Equal(customerId, persisted.CustomerId);
        Assert.Equal(supplierId, persisted.SupplierId);
        Assert.Equal(brokerId, persisted.CustomsBrokerId);
        Assert.Equal("LCL", persisted.ShipmentMode);
        Assert.Equal("BL-HTTP484", persisted.BillOfLadingNo);
        Assert.Equal(new DateTime(2026, 10, 3), persisted.Etd);
        Assert.Equal(new DateTime(2026, 10, 21), persisted.Ata);
        Assert.True(persisted.InspectionRequired);
        Assert.Equal(new DateTime(2026, 10, 5), persisted.CustomsReleaseDate);
        Assert.Equal("HTTP484", persisted.Remark);
        Assert.Equal("BR484-B-" + tag, persisted.CustomsBrokerName);   // 名称快照由服务端权威写入

        // 详情 / 列表按规范 Id 回读一致（表单重开口径）
        var (detailStatus, detailRoot, _) = await SendAsync(admin, HttpMethod.Get,
            $"/api/container/bookings/{id}");
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Equal(number, detailRoot.GetProperty("data").GetProperty("bookingNo").GetString());
        var (listStatus, listRoot, _) = await SendAsync(admin, HttpMethod.Get,
            "/api/container/bookings?page=1&pageSize=5&keyword=" + Uri.EscapeDataString(number));
        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Equal(id, listRoot.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetInt64());

        // 修改（伪造单号 / 状态 / 删除态 / 时间戳）：单号 / 状态 / 删除态保持服务端值，可空引用可清空
        var edit = BookingBody(customerId, null, null, shipmentMode: "FCL", remark: "UPDATED");
        edit["bookingNo"] = "HACK-DG-484";
        edit["status"] = 5;
        edit["isDeleted"] = true;
        edit["createdAt"] = new DateTime(2000, 1, 1);
        var (editStatus, editRoot, _) = await SendAsync(admin, HttpMethod.Put,
            $"/api/container/bookings/{id}", edit);
        Assert.Equal(HttpStatusCode.OK, editStatus);
        Assert.Equal(0, editRoot.GetProperty("code").GetInt32());

        var reloaded = await _fx.ReadBookingByIdAsync(id);
        Assert.Equal(number, reloaded.BookingNo);
        Assert.Equal(0, reloaded.Status);
        Assert.False(reloaded.IsDeleted);
        Assert.Null(reloaded.SupplierId);                        // 可空引用显式清空 = 未指定
        Assert.Null(reloaded.CustomsBrokerId);
        Assert.Equal(string.Empty, reloaded.CustomsBrokerName);
        Assert.Equal("FCL", reloaded.ShipmentMode);
        Assert.Null(reloaded.Etd);
        Assert.Equal("UPDATED", reloaded.Remark);
    }
    // ==================== 2. 可空引用与跟踪三态：真实 HTTP 显式关联 / 清空 ====================

    [Fact]
    public async Task 可空引用与跟踪字段_真实HTTP显式关联与清空往返()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await _fx.SeedCustomerAsync("BC484-RT-" + tag);
        var supplierId = await _fx.SeedSupplierAsync("BS484-RT-" + tag);
        var brokerId = await _fx.SeedCustomsBrokerAsync("BR484-RT-" + tag);

        var (_, createdRoot, _) = await SendAsync(admin, HttpMethod.Post,
            "/api/container/bookings",
            BookingBody(customerId, supplierId, brokerId, shipmentMode: "LCL",
                billOfLadingNo: "BL-RT", shippingOrderNo: "SO-RT", transitPort: "HONGKONG",
                etd: new DateTime(2026, 11, 1), eta: new DateTime(2026, 11, 20),
                inspectionRequired: false));
        Assert.Equal(0, createdRoot.GetProperty("code").GetInt32());
        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();
        var number = createdRoot.GetProperty("data").GetProperty("bookingNo").GetString()!;

        var linked = await _fx.ReadBookingByIdAsync(id);
        Assert.Equal(supplierId, linked.SupplierId);
        Assert.Equal(brokerId, linked.CustomsBrokerId);
        Assert.Equal("LCL", linked.ShipmentMode);
        Assert.Equal("HONGKONG", linked.TransitPort);
        Assert.Equal(new DateTime(2026, 11, 1), linked.Etd);
        Assert.False(linked.InspectionRequired);                 // 显式「不需要查验」原样落库
        Assert.Null(linked.InspectionDate);

        // 显式清空（提交 null）+ 三态改为未知（null）→ 未指定 / 未知，单号 / 状态不变
        var (clearStatus, clearRoot, _) = await SendAsync(admin, HttpMethod.Put,
            $"/api/container/bookings/{id}",
            BookingBody(customerId, null, null, shipmentMode: "", inspectionRequired: null));
        Assert.Equal(HttpStatusCode.OK, clearStatus);
        Assert.Equal(0, clearRoot.GetProperty("code").GetInt32());

        var cleared = await _fx.ReadBookingByIdAsync(id);
        Assert.Null(cleared.SupplierId);
        Assert.Null(cleared.CustomsBrokerId);
        Assert.Equal(string.Empty, cleared.CustomsBrokerName);
        Assert.Equal(string.Empty, cleared.ShipmentMode);
        Assert.Null(cleared.InspectionRequired);                 // 未知：绝不回落为 false
        Assert.Equal(number, cleared.BookingNo);
        Assert.Equal(0, cleared.Status);
    }

    // ==================== 3. 拒绝先于编号与持久化：真实 HTTP 零写入零占号 ====================

    [Fact]
    public async Task 匿名缺菜单与无效主数据_真实HTTP拒绝且零写入零占号()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await _fx.SeedCustomerAsync("BC484-C-" + tag);
        var disabledCustomerId = await _fx.SeedCustomerAsync("BC484-OFF-" + tag, status: 0);
        var wrongTypeId = await _fx.SeedWrongTypeInfoAsync("CUR484-" + tag);

        var before = await _fx.ReadSnapshotAsync();

        // 停用客户 → 受控拒绝（规则冲突）
        var disabled = await SendAsync(admin, HttpMethod.Post, "/api/container/bookings",
            BookingBody(disabledCustomerId));
        Assert.Equal(HttpStatusCode.OK, disabled.Status);
        Assert.Equal(ErrorCodes.RuleConflict, disabled.Root.GetProperty("code").GetInt32());

        // 非报关行字典项 → 非法引用
        var wrongType = await SendAsync(admin, HttpMethod.Post, "/api/container/bookings",
            BookingBody(customerId, customsBrokerId: wrongTypeId));
        Assert.Equal(HttpStatusCode.OK, wrongType.Status);
        Assert.Equal(ErrorCodes.InvalidParameter, wrongType.Root.GetProperty("code").GetInt32());

        // 匿名 → 未认证（真实 JWT 认证链路，不是缺失密钥回落）
        using var anonymous = _fx.CreateAnonymousClient();
        var anonymousResult = await SendAsync(anonymous, HttpMethod.Post, "/api/container/bookings",
            BookingBody(customerId));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResult.Status);
        Assert.Equal(ErrorCodes.Unauthorized, anonymousResult.Root.GetProperty("code").GetInt32());

        // 已认证但无「订柜信息」菜单（真实既有授权）→ 模块授权拒绝
        var user = "bc484-nomenu-" + tag;
        var password = "Test@484" + tag;
        await _fx.CreateUserWithoutRolesAsync(admin, user, password);
        using var temporary = await _fx.LoginAsync(user, password);
        var noMenu = await SendAsync(temporary, HttpMethod.Post, "/api/container/bookings",
            BookingBody(customerId));
        Assert.Equal(HttpStatusCode.OK, noMenu.Status);
        Assert.Equal(ErrorCodes.Forbidden, noMenu.Root.GetProperty("code").GetInt32());

        var after = await _fx.ReadSnapshotAsync();
        Assert.Equal(before.BookingCount, after.BookingCount);
        Assert.Equal(before.Sequence, after.Sequence);        // 被拒请求绝不消耗订柜单号
    }

    // ==================== 4. 有效生命周期：真实 HTTP 提交 / 审核 / 取消 ====================

    [Fact]
    public async Task 生命周期_真实HTTP提交审核取消_合法流转且零下游副作用()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await _fx.SeedCustomerAsync("BC484-L-" + tag);
        var before = await _fx.ReadSnapshotAsync();

        var (_, createdRoot, _) = await SendAsync(admin, HttpMethod.Post, "/api/container/bookings",
            BookingBody(customerId));
        Assert.Equal(0, createdRoot.GetProperty("code").GetInt32());
        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();
        var number = createdRoot.GetProperty("data").GetProperty("bookingNo").GetString()!;

        // 待提交直接审核 → 受控拒绝（规则冲突），零状态变化
        var earlyApprove = await SendAsync(admin, HttpMethod.Post,
            $"/api/container/bookings/{id}/approve");
        Assert.Equal(ErrorCodes.RuleConflict, earlyApprove.Root.GetProperty("code").GetInt32());
        Assert.Equal(0, (await _fx.ReadBookingByIdAsync(id)).Status);

        var submit = await SendAsync(admin, HttpMethod.Post, $"/api/container/bookings/{id}/submit");
        Assert.Equal(0, submit.Root.GetProperty("code").GetInt32());
        Assert.Equal(1, (await _fx.ReadBookingByIdAsync(id)).Status);

        var approve = await SendAsync(admin, HttpMethod.Post, $"/api/container/bookings/{id}/approve");
        Assert.Equal(0, approve.Root.GetProperty("code").GetInt32());
        Assert.Equal(2, (await _fx.ReadBookingByIdAsync(id)).Status);

        var cancel = await SendAsync(admin, HttpMethod.Post, $"/api/container/bookings/{id}/cancel");
        Assert.Equal(0, cancel.Root.GetProperty("code").GetInt32());
        Assert.Equal(5, (await _fx.ReadBookingByIdAsync(id)).Status);

        // 取消后再推进状态（提交）→ 受控拒绝，零状态变化、零单号变化
        var resubmit = await SendAsync(admin, HttpMethod.Post, $"/api/container/bookings/{id}/submit");
        Assert.Equal(ErrorCodes.RuleConflict, resubmit.Root.GetProperty("code").GetInt32());
        var stored = await _fx.ReadBookingByIdAsync(id);
        Assert.Equal(5, stored.Status);
        Assert.Equal(number, stored.BookingNo);
        Assert.False(stored.IsDeleted);

        var after = await _fx.ReadSnapshotAsync();
        Assert.Equal(before.BookingCount + 1, after.BookingCount);
        // 订柜信息只是物流证据：状态推进绝不创建预装柜 / 库存 / 库存流水 / 财务记录
        Assert.Equal(before.PreLoadingCount, after.PreLoadingCount);
        Assert.Equal(before.StockInCount, after.StockInCount);
        Assert.Equal(before.StockOutCount, after.StockOutCount);
        Assert.Equal(before.StockMovementCount, after.StockMovementCount);
        Assert.Equal(before.StockCount, after.StockCount);
        Assert.Equal(before.ReceiptCount, after.ReceiptCount);
    }

    // ==================== 5. 旧族分离：旧写路径 fail closed，规范入口不受影响 ====================

    [Fact]
    public async Task 旧族写路径fail_closed_规范入口不受影响()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await _fx.SeedCustomerAsync("BC484-LG-" + tag);

        // 旧写路径（ERP-404 门禁）：即使具备既有功能菜单也整体 fail closed（绝不调用旧存储过程）
        var legacy = new Dictionary<string, object?>
        {
            ["Action"] = "add",
            ["BillNo"] = "LEGACY-DG-HTTP484-" + tag,
            ["CustomerId"] = customerId
        };
        var legacyResult = await SendAsync(admin, HttpMethod.Post, "/api/v2/bills/booking/save", legacy);
        Assert.Equal(HttpStatusCode.OK, legacyResult.Status);
        Assert.NotEqual(0, legacyResult.Root.GetProperty("code").GetInt32());

        // 规范入口（api/container/bookings，规范 Id 口径）在同一次运行中仍可正常写入
        var (_, createdRoot, _) = await SendAsync(admin, HttpMethod.Post,
            "/api/container/bookings", BookingBody(customerId));
        Assert.Equal(0, createdRoot.GetProperty("code").GetInt32());
        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();
        Assert.StartsWith("DG", createdRoot.GetProperty("data").GetProperty("bookingNo").GetString()!,
            StringComparison.Ordinal);
        Assert.Equal(1, (await _fx.ReadBookingByIdAsync(id)).Count);
    }
}





/// <summary>
/// ERP-484 真实 HTTP + 真实隔离 SQL 夹具（订柜信息规范入口）：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、
/// 库名前缀 <c>NEWERP_AUTOTEST</c> 与 <c>Integrated Security=true</c>；只使用<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝（绝不 drop / reset / 复用）；连接串只来自进程环境变量或专用 localdb 默认值。
/// <para>启动真实 ERP.Api 进程（真实 Kestrel / JWT 认证 / 既有授权），并为该进程注入<b>进程内临时 JWT 签名密钥</b>
/// （随机生成、只存在于该进程的环境变量、绝不落盘、绝不复用仓库内空白签名配置）；测试账号仍是既有种子账号
/// （不新增 / 不持久化凭据）。</para>
/// </summary>
public sealed class BookingCanonicalEntrySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string AdminUser = "admin";
    public const string AdminPassword = "Admin@123";

    private const string DefaultDatabaseName = DatabasePrefix + "_BOOKINGCANONICAL";
    private const string PortVariable = "ERP_484_HTTP_PORT";
    private const string DefaultPort = "5401";

    private readonly StringBuilder _apiConsole = new();
    private readonly object _apiConsoleLock = new();
    private Process? _apiProcess;

    public string ConnectionString { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>按规范 Id 直接读取的订柜信息行（单号 / 状态 / 删除态 / 可空引用 / 日期 / 跟踪字段 / 备注）。</summary>
    public readonly record struct BookingRow(int Count, long Id, string? BookingNo, int Status, bool IsDeleted,
        long CustomerId, long? SupplierId, long? CustomsBrokerId, string? CustomsBrokerName,
        string? ShipmentMode, string? BillOfLadingNo, string? ShippingOrderNo, string? TransitPort,
        DateTime? Etd, DateTime? Eta, DateTime? Atd, DateTime? Ata, string? TruckerName,
        bool? InspectionRequired, DateTime? InspectionDate, DateTime? CustomsReleaseDate, string? Remark);

    /// <summary>零副作用拒绝 / 有效状态变更所需的整表计数快照（订柜 + 单据号序列 + 预装柜 · 库存 / 库存流水 · 财务）。</summary>
    public readonly record struct WriteSnapshot(long BookingCount, long Sequence, long PreLoadingCount,
        long StockInCount, long StockOutCount, long StockMovementCount, long StockCount, long ReceiptCount);

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        var connectionString = string.IsNullOrWhiteSpace(configured)
            ? $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
              + "Integrated Security=true;TrustServerCertificate=true;"
            : configured;

        // 访问任何数据库之前：实例 / 库名前缀 / 集成安全 + 显式测试上下文（不满足立 fail-closed）。
        AssertDedicatedTarget(connectionString);
        TestDatabaseSafetyGuard.EnsureApprovedTarget(connectionString, "订柜信息规范入口集成验收");
        ConnectionString = connectionString;

        // 访问任何数据库之前：只允许全新 GUID 后缀库；同名库已存在立即拒绝。
        await AssertFreshDatabaseAsync(connectionString);

        BaseUrl = $"http://localhost:{Environment.GetEnvironmentVariable(PortVariable) ?? DefaultPort}";
        EnsurePortIsFree();
        _apiProcess = StartApi(connectionString);
        try
        {
            await WaitForApiReadyAsync();
        }
        catch
        {
            DumpApiConsole();
            throw;
        }
    }

    public Task DisposeAsync()
    {
        DumpApiConsole();
        try
        {
            if (_apiProcess is not null && !_apiProcess.HasExited)
                _apiProcess.Kill(entireProcessTree: true);
        }
        catch { /* 结束进程失败不影响断言结果 */ }
        try { _apiProcess?.Dispose(); } catch { /* 忽略 */ }
        return Task.CompletedTask;
    }

    public ErpDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options);

    // ==================== HTTP 入口（真实登录 + 匿名） ====================

    public HttpClient CreateAnonymousClient()
        => new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(60) };

    public Task<HttpClient> LoginAsAdminAsync() => LoginAsync(AdminUser, AdminPassword);

    /// <summary>用既有登录接口取得真值 JWT（令牌只存活在内存，绝不写入日志 / 证据）。</summary>
    public async Task<HttpClient> LoginAsync(string userName, string password)
    {
        using var anonymous = CreateAnonymousClient();
        var payload = JsonSerializer.Serialize(new { userName, password });
        using var response = await anonymous.PostAsync("/api/auth/login",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        var token = doc.RootElement.GetProperty("data").GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>经既有用户管理接口新建账号且不分配任何角色（不新增任何授权 / 菜单 / 角色）。</summary>
    public async Task CreateUserWithoutRolesAsync(HttpClient admin, string userName, string password)
    {
        var payload = JsonSerializer.Serialize(new
        {
            userName, password, displayName = userName, email = string.Empty, phone = string.Empty,
            roleIds = Array.Empty<long>()
        });
        using var response = await admin.PostAsync("/api/sys/users",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
    }

    // ==================== 夹具数据（既有主数据表，只写夹具行） ====================

    /// <summary>播种一张既有供应商（夹具数据；不新增表 / 列 / 权限，也不触达订柜信息）。</summary>
    public async Task<long> SeedSupplierAsync(string code, int status = 1)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var supplier = new BaseSupplier
        {
            SupplierCode = code[..Math.Min(code.Length, 30)],
            SupplierName = code,
            Status = status
        };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    /// <summary>播种一张既有客户（夹具数据；不新增表 / 列 / 权限，也不触达订柜信息）。</summary>
    public async Task<long> SeedCustomerAsync(string code, int status = 1)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var customer = new BaseCustomer
        {
            CustomerCode = code[..Math.Min(code.Length, 30)],
            CustomerName = code,
            Status = status
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    /// <summary>播种一条既有「报关行」字典项（<c>InfoType = CustomsBroker</c>，夹具数据）。</summary>
    public async Task<long> SeedCustomsBrokerAsync(string code, int status = 1)
        => await SeedOtherInfoAsync("CustomsBroker", code, status);

    /// <summary>播种一条非报关行字典项（<c>InfoType = Currency</c>），用于证明非报关行引用不被采信。</summary>
    public async Task<long> SeedWrongTypeInfoAsync(string code)
        => await SeedOtherInfoAsync("Currency", code, 1);

    private async Task<long> SeedOtherInfoAsync(string infoType, string code, int status)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var info = new BaseOtherInfo
        {
            InfoType = infoType,
            InfoCode = code[..Math.Min(code.Length, 30)],
            InfoName = code,
            Status = status
        };
        db.BaseOtherInfos.Add(info);
        await db.SaveChangesAsync();
        return info.Id;
    }

    // ==================== 真实 SQL 复核（同一全新 GUID 库，只读） ====================

    /// <summary>按规范 Id 读取订柜信息行（真实 SQL Server，独立连接）。</summary>
    public async Task<BookingRow> ReadBookingByIdAsync(long id)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var booking = await db.ContainerBookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
        if (booking is null)
            return new BookingRow(0, 0, null, -1, false, 0L, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null);

        return new BookingRow(1, booking.Id, booking.BookingNo, (int)booking.Status, booking.IsDeleted,
            booking.CustomerId, booking.SupplierId, booking.CustomsBrokerId, booking.CustomsBrokerName,
            booking.ShipmentMode, booking.BillOfLadingNo, booking.ShippingOrderNo, booking.TransitPort,
            booking.Etd, booking.Eta, booking.Atd, booking.Ata, booking.TruckerName,
            booking.InspectionRequired, booking.InspectionDate, booking.CustomsReleaseDate, booking.Remark);
    }

    /// <summary>零副作用拒绝 / 有效状态变更所需的整表计数快照（订柜 + 单号序列 + 预装柜 · 库存 / 库存流水 · 财务）。</summary>
    public async Task<WriteSnapshot> ReadSnapshotAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var sequence = await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.ContainerBooking && !r.IsDeleted)
            .Select(r => r.CurrentSequence)
            .FirstOrDefaultAsync();
        return new WriteSnapshot(
            await db.ContainerBookings.CountAsync(),
            sequence,
            await db.ContainerPreLoadings.CountAsync(),
            await db.StockIns.CountAsync(),
            await db.StockOuts.CountAsync(),
            await db.StockMovements.CountAsync(),
            await db.Stocks.CountAsync(),
            await db.FinanceReceipts.CountAsync());
    }

    // ==================== 专用目标护栏与进程脚手架 ====================

    /// <summary>专用目标护栏：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
    public static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.Equal($"(localdb)\\{InstanceMarker}", builder.DataSource ?? string.Empty, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, builder.InitialCatalog ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    /// <summary>任何数据库访问之前拒绝既有目标：只允许本次全新 GUID 后缀库。</summary>
    private static async Task AssertFreshDatabaseAsync(string connectionString)
    {
        AssertDedicatedTarget(connectionString);
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(master.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DB_ID(@database)";
        cmd.Parameters.AddWithValue("@database", database);
        var existing = await cmd.ExecuteScalarAsync();
        if (existing is not null && existing != DBNull.Value)
            throw new InvalidOperationException(
                "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
    }

    /// <summary>启动前确认端口空闲：避免静默连上残留 / 外部实例产出无效证据。</summary>
    private void EnsurePortIsFree()
    {
        var uri = new Uri(BaseUrl);
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            if (probe.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromMilliseconds(500)))
                throw new InvalidOperationException(
                    $"端口 {uri.Port} 已被其它进程监听（很可能是上一次验收残留的 ERP.Api）："
                    + "继续执行会让本次验收连上外部实例并产出无效证据，请先结束占用该端口的进程后重试。");
        }
        catch (AggregateException)
        {
            // 连接被拒绝 = 端口空闲，正常继续。
        }
    }

    /// <summary>进程内临时 JWT 签名密钥（随机生成：只注入被测 API 进程的环境变量，绝不落盘、绝不写入日志 / 证据）。</summary>
    private static string NewEphemeralJwtKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    private Process StartApi(string connectionString)
    {
        var binDir = ResolveApiBinDirectory();
        var dllPath = Path.Combine(binDir, "ERP.Api.dll");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\" --urls {BaseUrl} --contentRoot \"{binDir}\"",
            WorkingDirectory = binDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.EnvironmentVariables[TestDatabaseSafetyGuard.TestRunVariable] = TestDatabaseSafetyGuard.TestRunValue;
        psi.EnvironmentVariables["ERP_ConnectionStrings__Default"] = connectionString;
        // 真实认证链路（JWT 仍是唯一登录凭据）：只注入进程内临时签名密钥，不继承仓库里的空白 Jwt:Key，
        // 也不复用任何真实 / 生产密钥；密钥只存活于该进程环境变量，绝不落盘、绝不写入证据。
        psi.EnvironmentVariables["Jwt__Key"] = NewEphemeralJwtKey();
        psi.EnvironmentVariables["Jwt__Issuer"] = "ERP.Api";
        psi.EnvironmentVariables["Jwt__Audience"] = "ERP.Client";

        var process = Process.Start(psi)!;
        process.OutputDataReceived += (_, e) => AppendApiConsole(e.Data);
        process.ErrorDataReceived += (_, e) => AppendApiConsole(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static string ResolveApiBinDirectory()
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = baseDir.Parent?.Name ?? "Release";
        var root = baseDir;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NEWERP.sln"))) root = root.Parent;
        Assert.NotNull(root);

        foreach (var candidate in new[] { configuration, "Release", "Debug" })
        {
            var binDir = Path.Combine(root!.FullName, "src", "ERP.Api", "bin", candidate, "net8.0");
            if (File.Exists(Path.Combine(binDir, "ERP.Api.dll"))) return binDir;
        }
        throw new InvalidOperationException("未找到 ERP.Api 构建输出，请先构建解决方案后再运行真实 HTTP 集成验收。");
    }

    private async Task WaitForApiReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(180);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        string lastError = "尚未收到响应";
        while (DateTime.UtcNow < deadline)
        {
            if (_apiProcess?.HasExited == true)
                throw new InvalidOperationException(
                    $"ERP.Api 启动过程中退出（ExitCode={_apiProcess.ExitCode}）。API 控制台尾部输出：\n{ApiConsoleTail()}");
            try
            {
                using var response = await client.GetAsync(BaseUrl + "/swagger/v1/swagger.json");
                if ((int)response.StatusCode == 200) return;
                lastError = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception ex) { lastError = ex.Message; }
            await Task.Delay(500);
        }
        throw new InvalidOperationException(
            $"ERP.Api 在 180 秒内未就绪（最后状态：{lastError}）。API 控制台尾部输出：\n{ApiConsoleTail()}");
    }

    private void AppendApiConsole(string? line)
    {
        if (line is null) return;
        lock (_apiConsoleLock)
        {
            if (_apiConsole.Length > 400_000) _apiConsole.Remove(0, 200_000);
            _apiConsole.Append(line).Append('\n');
        }
    }

    private string ApiConsoleTail(int maxChars = 4000)
    {
        lock (_apiConsoleLock)
        {
            var text = _apiConsole.ToString();
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
    }

    /// <summary>把 API 控制台输出保留到证据目录（若提供），便于门禁失败时定位原始失败原因（绝不写入任何凭据）。</summary>
    private void DumpApiConsole()
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("ERP_AI_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory)) return;
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            File.WriteAllText(Path.Combine(evidenceDirectory, "booking-canonical-entry-api-console.log"),
                ApiConsoleTail(int.MaxValue));
        }
        catch (IOException) { /* 证据写入失败不影响断言结果 */ }
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class BookingCanonicalEntryTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => BookingCanonicalEntrySqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => BookingCanonicalEntrySqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
