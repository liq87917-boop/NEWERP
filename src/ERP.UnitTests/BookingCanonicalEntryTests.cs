using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-484 订柜信息规范入口（<c>api/container/bookings</c>）与「省略订柜单号」手工录入契约单元测试
/// （内存库 <see cref="TestDbFactory"/>，不连接 SQL Server、不启动 API）。
/// <para>覆盖：<list type="bullet">
/// <item>实体层：<see cref="ContainerBooking.BookingNo"/> 不再显式 <c>[Required]</c>（省略 / 空串照常绑定），
/// 长度约束保留；</item>
/// <item>新增入口：省略 / 空串 / 伪造订柜单号一律由服务端权威单号覆盖，主键 / 状态（待提交草稿）/ 删除态 /
/// 时间戳全部由服务端覆盖（客户端注入值不参与持久化）；</item>
/// <item>typed 可空引用与日期往返：可选供应商 / 报关行显式关联与清空（<c>null</c>）原样往返，可选日期
/// （订柜 / 开船 / ETD·ETA·ATD·ATA / 查验 / 放行）与 ERP-040 跟踪字段（出运方式 / B/L / S-O / 中转港 /
/// 拖车 / 查验三态）保存 → 重开 → 修改原样往返，且规范 <c>BookingNo</c> 全程不变；</item>
/// <item>拒绝先于编号与持久化：无身份 / 缺菜单 / 无效 · 已删除 · 已停用客户或供应商 / 非法出运方式 /
/// 超长 B/L / 自相矛盾查验 / 不存在 · 停用 · 类型不符报关行一律零写入且零占号；</item>
/// <item>订柜信息只是物流证据：提交 / 审核 / 取消等有效状态变更绝不创建预装柜 / 装柜 / 库存 / 库存流水 /
/// 财务记录；</item>
/// <item>接线契约：既有「订柜信息」菜单不再进入旧存储过程族（旧族读侧保留）、规范模块接线、校验先于单号预约。</item>
/// </list></para>
/// <para>真实隔离 SQL / HTTP 的端到端验收见
/// <c>ERP.IntegrationTests/BookingCanonicalEntrySqlServerTests.cs</c>。</para>
/// </summary>
public class BookingCanonicalEntryTests
{
    private const long CustomerOwn = 984001L;
    private const long SupplierOk = 984101L;
    private const long SupplierOff = 984102L;
    private const long SupplierDeleted = 984103L;
    private const long BrokerOk = 984201L;
    private const long BrokerOff = 984202L;
    private const long BrokerWrongType = 984203L;

    // ==================== 0. 脚手架 ====================

    private sealed class CountingDocumentNumberService : IDocumentNumberService
    {
        private int _seq;

        /// <summary>已被请求生成的订柜单号次数（用于证明被拒请求绝不消耗订柜单号）。</summary>
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
        {
            Calls++;
            Assert.Equal(DocumentType.ContainerBooking, documentType);
            return Task.FromResult($"DG{DateTime.Now:yyyyMMdd}{++_seq:D4}");
        }
    }

    private static ContainerBookingController NewController(
        ErpDbContext db, long? userId, IDocumentNumberService? noService = null)
    {
        var ctl = new ContainerBookingController(db, noService ?? new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static object? Anon(object data, string name) => data.GetType().GetProperty(name)?.GetValue(data);

    // ==================== 1. 实体契约：省略订柜单号 ====================

    [Fact]
    public void Entity_订柜单号不再显式必填_长度约束保留()
    {
        var property = typeof(ContainerBooking).GetProperty(nameof(ContainerBooking.BookingNo))!;

        Assert.Equal(typeof(string), property.PropertyType);
        Assert.False(Attribute.IsDefined(property, typeof(RequiredAttribute), true),
            "ContainerBooking.BookingNo 不得显式必填（否则省略订柜单号会在模型绑定阶段被拒为 HTTP 400）");
        var maxLength = Assert.Single(property
            .GetCustomAttributes(typeof(MaxLengthAttribute), true)
            .Cast<MaxLengthAttribute>());
        Assert.Equal(50, maxLength.Length);
        Assert.Equal(string.Empty, new ContainerBooking().BookingNo);
    }

    // ==================== 2. 省略 / 空串 / 伪造订柜单号：服务端权威覆盖 ====================

    [Fact]
    public async Task Create_省略订柜单号_服务端生成权威单号与规范草稿身份()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerOwn, "本人客户");
        var privileged = TestAuth.SeedPrivilegedUser(db);
        var no = new CountingDocumentNumberService();
        var ctl = NewController(db, privileged, no);

        // 客户端注入伪造值：省略的单号（默认空串）+ 伪造主键 / 状态 / 删除态 / 时间戳
        var request = NewBooking();
        request.Id = 987654;
        request.Status = DocumentStatus.Approved;
        request.IsDeleted = true;
        request.UpdatedAt = new DateTime(2000, 1, 1);
        request.CreatedAt = new DateTime(2000, 1, 1);

        var ok = Assert.IsType<OkObjectResult>(await ctl.Create(request));
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);

        Assert.Equal(1, no.Calls);
        var stored = db.ContainerBookings.Single();
        Assert.True(stored.Id > 0);
        Assert.Equal(stored.Id, Assert.IsType<long>(Anon(resp.Data!, "Id")));
        Assert.Equal(stored.BookingNo, Assert.IsType<string>(Anon(resp.Data!, "BookingNo")));
        Assert.StartsWith("DG", stored.BookingNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(DocumentStatus.Pending, Assert.IsType<DocumentStatus>(Anon(resp.Data!, "Status")));
        Assert.False(stored.IsDeleted);
        Assert.NotNull(stored.UpdatedAt);                             // 时间戳由既有审计统一刷新（服务端持有）
        Assert.NotEqual(new DateTime(2000, 1, 1), stored.UpdatedAt);  // 客户端注入的伪造时间戳不被采信
        Assert.True(stored.CreatedAt >= DateTime.Today);
    }

    [Fact]
    public async Task Create_空串与伪造订柜单号_服务端权威覆盖且不落客户端值()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerOwn, "本人客户");
        var privileged = TestAuth.SeedPrivilegedUser(db);
        var no = new CountingDocumentNumberService();
        var ctl = NewController(db, privileged, no);

        var blank = NewBooking();
        blank.BookingNo = "   ";
        var forged = NewBooking();
        forged.BookingNo = "DG-FORGED-0001";

        Assert.IsType<OkObjectResult>(await ctl.Create(blank));
        Assert.IsType<OkObjectResult>(await ctl.Create(forged));

        Assert.Equal(2, no.Calls);
        var stored = db.ContainerBookings.OrderBy(b => b.Id).ToList();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, b => Assert.StartsWith("DG", b.BookingNo));
        Assert.All(stored, b => Assert.NotEqual("DG-FORGED-0001", b.BookingNo));
        Assert.Equal(2, stored.Select(b => b.BookingNo).Distinct().Count());
    }


    // ==================== 3. typed 可空引用 / 日期 / 跟踪字段往返 ====================

    [Fact]
    public async Task Create_与Update_可空引用日期与跟踪字段保存重开修改原样往返()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerOwn, "本人客户");
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedOtherInfo(db, BrokerOk, "CustomsBroker", "BR-OK", "可用报关行");
        var privileged = TestAuth.SeedPrivilegedUser(db);
        var ctl = NewController(db, privileged);

        var request = NewBooking();
        request.SupplierId = SupplierOk;
        request.ContainerType = ContainerType.GP40;
        request.ShippingCompany = "COSCO";
        request.VoyageNo = "V-2026-01";
        request.SailingDate = new DateTime(2026, 3, 20);
        request.DeparturePort = "SHANGHAI";
        request.DestinationPort = "ROTTERDAM";
        request.ShipmentMode = "LCL";
        request.BillOfLadingNo = "BL-0001";
        request.ShippingOrderNo = "SO-0001";
        request.TransitPort = "SINGAPORE";
        request.Etd = new DateTime(2026, 3, 21);
        request.Eta = new DateTime(2026, 4, 10);
        request.Atd = new DateTime(2026, 3, 22);
        request.Ata = new DateTime(2026, 4, 11);
        request.TruckerName = "顺达拖车";
        request.CustomsBrokerId = BrokerOk;
        request.InspectionRequired = true;
        request.InspectionDate = new DateTime(2026, 3, 19);
        request.CustomsReleaseDate = new DateTime(2026, 3, 23);
        request.Remark = "ERP-484_ROUNDTRIP";

        Assert.IsType<OkObjectResult>(await ctl.Create(request));
        var created = db.ContainerBookings.Single();
        var canonicalNo = created.BookingNo;
        Assert.StartsWith("DG", canonicalNo);
        Assert.Equal(SupplierOk, created.SupplierId);
        Assert.Equal("LCL", created.ShipmentMode);
        Assert.Equal("BL-0001", created.BillOfLadingNo);
        Assert.Equal("SO-0001", created.ShippingOrderNo);
        Assert.Equal("SINGAPORE", created.TransitPort);
        Assert.Equal(new DateTime(2026, 3, 21), created.Etd);
        Assert.Equal(new DateTime(2026, 4, 11), created.Ata);
        Assert.Equal("顺达拖车", created.TruckerName);
        Assert.Equal(BrokerOk, created.CustomsBrokerId);
        Assert.Equal("可用报关行", created.CustomsBrokerName);   // 服务端按字典项权威写入名称快照
        Assert.True(created.InspectionRequired);
        Assert.Equal(new DateTime(2026, 3, 19), created.InspectionDate);
        Assert.Equal(new DateTime(2026, 3, 23), created.CustomsReleaseDate);

        // 重开：详情按规范 Id 回读原样（含报关行可用标注）
        var detail = AssertOk<ContainerBooking>(await ctl.GetById(created.Id));
        Assert.Equal(canonicalNo, detail.BookingNo);
        Assert.Equal("ROTTERDAM", detail.DestinationPort);
        Assert.True(detail.CustomsBrokerAvailable);

        // 修改：清空可空引用与日期（留空 = 未指定 / 未知），订柜单号仍由服务端持有、不参与修改
        var edit = NewBooking();
        edit.SupplierId = null;
        edit.ContainerType = ContainerType.GP40;
        edit.ShippingCompany = "COSCO";
        edit.SailingDate = null;
        edit.DeparturePort = "SHANGHAI";
        edit.ShipmentMode = string.Empty;
        edit.CustomsBrokerId = null;
        edit.InspectionRequired = false;
        edit.InspectionDate = null;
        edit.Etd = null;
        edit.Eta = null;
        edit.Atd = null;
        edit.Ata = null;
        edit.CustomsReleaseDate = null;
        edit.Remark = "ERP-484_ROUNDTRIP_EDIT";

        Assert.IsType<OkObjectResult>(await ctl.Update(created.Id, edit));

        db.ChangeTracker.Clear();
        var updated = db.ContainerBookings.Single();
        Assert.Equal(canonicalNo, updated.BookingNo);           // 规范单号全程不变（服务端持有）
        Assert.Null(updated.SupplierId);
        Assert.Null(updated.CustomsBrokerId);
        Assert.Equal(string.Empty, updated.CustomsBrokerName);
        Assert.Equal(string.Empty, updated.ShipmentMode);
        Assert.Null(updated.Etd);
        Assert.Null(updated.Ata);
        Assert.False(updated.InspectionRequired);               // 显式「不需要查验」，绝不回落为未知
        Assert.Null(updated.InspectionDate);
        Assert.Null(updated.CustomsReleaseDate);
        Assert.Equal("ERP-484_ROUNDTRIP_EDIT", updated.Remark);
        Assert.Equal(DocumentStatus.Pending, updated.Status);
    }


    // ==================== 4. 拒绝先于编号与持久化 ====================

    [Fact]
    public async Task 被拒绝请求_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerOwn, "本人客户");
        SeedCustomer(db, 984004L, "停用客户", status: 0);
        SeedCustomer(db, 984005L, "已删除客户", deleted: true);
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedSupplier(db, SupplierOff, "停用供应商", status: 0);
        SeedSupplier(db, SupplierDeleted, "已删除供应商", deleted: true);
        SeedOtherInfo(db, BrokerOff, "CustomsBroker", "BR-OFF", "停用报关行", status: 0);
        SeedOtherInfo(db, BrokerWrongType, "Port", "P-1", "港口字典项");
        var privileged = TestAuth.SeedPrivilegedUser(db);
        var noMenuUser = SeedPlainUser(db);
        var no = new CountingDocumentNumberService();

        // 无身份 / 缺既有「订柜信息」菜单
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, null, no).Create(NewBooking()));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, noMenuUser, no).Create(NewBooking()));

        var ctl = NewController(db, privileged, no);
        // 客户：必填正整数 / 不存在 / 已删除 / 已停用
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewBooking(customerId: 0)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(NewBooking(customerId: 999999L)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(NewBooking(customerId: 984005L)));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Create(NewBooking(customerId: 984004L)));
        // 供应商：已停用 / 已删除
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Create(NewBooking(supplierId: SupplierOff)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(NewBooking(supplierId: SupplierDeleted)));
        // 非法出运方式 / 超长 B/L / 自相矛盾查验
        var badMode = NewBooking();
        badMode.ShipmentMode = "海运";
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(badMode));
        var longBl = NewBooking();
        longBl.BillOfLadingNo = new string('A', 51);
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(longBl));
        var contradiction = NewBooking();
        contradiction.InspectionRequired = false;
        contradiction.InspectionDate = DateTime.Today;
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(contradiction));
        // 报关行：不存在 / 已停用 / 类型不符
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(NewBooking(brokerId: 999998L)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewBooking(brokerId: BrokerOff)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewBooking(brokerId: BrokerWrongType)));

        // 全部拒绝都发生在单据号预约与任何写入之前
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.ContainerBookings);
    }

    // ==================== 5. 生命周期与下游零副作用 ====================

    [Fact]
    public async Task 生命周期_提交审核取消_合法流转且不产生下游单据或库存财务记录()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerOwn, "本人客户");
        var privileged = TestAuth.SeedPrivilegedUser(db);
        var ctl = NewController(db, privileged);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewBooking()));
        var booking = db.ContainerBookings.Single();
        Assert.Equal(DocumentStatus.Pending, booking.Status);

        // 非法流转：待提交直接审核 → 拒绝且状态不变
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Approve(booking.Id));
        Assert.Equal(DocumentStatus.Pending, db.ContainerBookings.Single().Status);

        Assert.IsType<OkObjectResult>(await ctl.Submit(booking.Id));
        Assert.Equal(DocumentStatus.Submitted, db.ContainerBookings.Single().Status);

        Assert.IsType<OkObjectResult>(await ctl.Approve(booking.Id));
        Assert.Equal(DocumentStatus.Approved, db.ContainerBookings.Single().Status);

        Assert.IsType<OkObjectResult>(await ctl.Cancel(booking.Id));
        Assert.Equal(DocumentStatus.Cancelled, db.ContainerBookings.Single().Status);

        // 订柜信息只是物流证据：不产生预装柜 / 库存 / 库存流水 / 财务记录
        Assert.Empty(db.ContainerPreLoadings);
        Assert.Empty(db.StockIns);
        Assert.Empty(db.StockOuts);
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.FinanceReceipts);
    }


    // ==================== 6. 接线契约：菜单 / 规范模块 / 表单 ====================

    [Fact]
    public void 接线_既有订柜信息菜单走规范模块_表单不提交订柜单号且可选引用可空()
    {
        var modules = ReadSource("src/ERP.Api/wwwroot/js/modules-doc2.js");
        var block = ModuleBlock(modules, "  booking: {");

        Assert.Contains("api: '/api/container/bookings'", block);
        Assert.Contains("canSubmit: true", block);
        var fieldsStart = block.IndexOf("fields: [", StringComparison.Ordinal);
        Assert.True(fieldsStart >= 0);
        var fields = block[fieldsStart..];
        Assert.DoesNotContain("key: 'bookingNo'", fields);              // 表单不提交订柜单号
        Assert.Contains("type: 'ref', ref: 'customer'", block);         // 客户为既有主数据引用（必填）
        Assert.Contains("ref: 'supplier', nullable: true", block);      // 可选供应商显式可空引用
        Assert.Contains("ref: 'customsBroker', nullable: true", block); // 可选报关行显式可空引用
        Assert.Contains("key: 'shipmentMode'", fields);                 // ERP-040 跟踪字段参与往返
        Assert.Contains("key: 'inspectionRequired'", fields);
        Assert.Contains("valueType: 'bool?'", fields);
        Assert.Contains("key: 'remark'", fields);
        Assert.Contains("key: 'etd'", fields);
        Assert.Contains("key: 'customsReleaseDate'", fields);

        var crud = ReadSource("src/ERP.Api/wwwroot/js/crud.js");
        Assert.Contains("f.nullable === true ? null : 0", crud);        // 声明式可空引用留空提交 null
        Assert.Contains("if (f.type === 'ref') v = v === '' ? null : Number(v);", crud);
        Assert.Contains("if (f.type === 'date') v = v ? v + 'T00:00:00' : null;", crud);
        Assert.Contains("await api(`${mod.api}/${id}`, 'PUT', body)", crud);
        Assert.Contains("await api(mod.api, 'POST', body)", crud);
        Assert.Contains("const row = await api(`${mod.api}/${id}`)", crud);

        // 旧存储过程族写路径仍由 ERP-404 门禁 fail closed；booking 已不在生效路由映射中（读侧保留）
        var billV2 = ReadSource("src/ERP.Api/wwwroot/js/bill-v2.js");
        var mapStart = billV2.IndexOf("const BILL_CODE_MAP = {", StringComparison.Ordinal);
        var mapEnd = billV2.IndexOf("};", mapStart, StringComparison.Ordinal);
        Assert.True(mapStart >= 0 && mapEnd > mapStart);
        var mapText = billV2[mapStart..mapEnd];
        Assert.False(Regex.IsMatch(mapText, @"^\s{2}'?booking'?\s*:", RegexOptions.Multiline),
            "BILL_CODE_MAP 不得把 booking 映射到旧存储过程族（菜单必须落到规范模块）");

        // 控制器：授权 / 主数据校验严格先于跟踪字段校验与单据号预约
        var controller = ReadSource("src/ERP.Api/Controllers/ContainerControllers.cs");
        var writeAuth = controller.IndexOf("BookingAuthorizationRules.EnsureWriteAuthorizedAsync", StringComparison.Ordinal);
        var applyTracking = controller.IndexOf("ContainerShipmentTrackingService.ApplyAsync(Db, entity, stored: null)", StringComparison.Ordinal);
        var generate = controller.IndexOf("GenerateAsync(DocumentType.ContainerBooking)", StringComparison.Ordinal);
        Assert.True(writeAuth >= 0 && applyTracking > writeAuth && generate > applyTracking);
        Assert.Contains("entity.IsDeleted = false;", controller);
        Assert.Contains("entity.UpdatedAt = null;", controller);
    }


    // ==================== 脚手架与种子数据 ====================

    private static ContainerBooking NewBooking(long customerId = CustomerOwn, long? supplierId = null, long? brokerId = null)
        => new()
        {
            BookingDate = DateTime.Today,
            CustomerId = customerId,
            SupplierId = supplierId,
            CustomsBrokerId = brokerId,
            Remark = "ERP-484_TEST"
        };

    private static void SeedCustomer(ErpDbContext db, long id, string name,
        long? empId = null, int status = 1, bool deleted = false)
    {
        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = id, CustomerCode = $"BC-484-{id}", CustomerName = name,
            EmpId = empId, Status = status, IsDeleted = deleted
        });
        db.SaveChanges();
    }

    private static void SeedSupplier(ErpDbContext db, long id, string name, int status = 1, bool deleted = false)
    {
        db.BaseSuppliers.Add(new BaseSupplier
        {
            Id = id, SupplierCode = $"BS-484-{id}", SupplierName = name,
            Status = status, IsDeleted = deleted
        });
        db.SaveChanges();
    }

    private static void SeedOtherInfo(ErpDbContext db, long id, string infoType, string code, string name,
        int status = 1, bool deleted = false)
    {
        db.BaseOtherInfos.Add(new BaseOtherInfo
        {
            Id = id, InfoType = infoType, InfoCode = code, InfoName = name,
            Status = status, IsDeleted = deleted
        });
        db.SaveChanges();
    }

    /// <summary>普通账号（未映射业务员 / 未授予既有「订柜信息」菜单）：用于证明缺菜单 fail closed 且零占号。</summary>
    private static long SeedPlainUser(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"bc484-plain-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "普通账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await action());
        Assert.Equal(expected, ex.Code);
    }

    private static ApiResponse<T> AssertOkResponse<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var resp = AssertOkResponse<T>(result);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>截取 <c>modules-doc2.js</c> 中某个模块的文本块（从模块键到下一个两空格缩进的 <c>},</c>）。</summary>
    private static string ModuleBlock(string source, string moduleKey)
    {
        var start = source.IndexOf(moduleKey, StringComparison.Ordinal);
        Assert.True(start >= 0, $"缺少模块 {moduleKey}");
        var end = source.IndexOf("\n  },", start, StringComparison.Ordinal);
        Assert.True(end > start, $"模块 {moduleKey} 未闭合");
        return source[start..end];
    }

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
