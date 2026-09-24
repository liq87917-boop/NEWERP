using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 单证中心（ERP-030）共享打印（打印预览 / 直接打印 / 打印设计）单元测试：
/// 打印模型字段映射与顺序、缺失可选值的空白语义（不回查客户档案、不按文本推测）、
/// 金额与份数为 0 视为未填写、单表台账无商品明细行、打印接口与路由、
/// 打印模板登记与重复登记防护、前端行操作与脚本接线契约。
/// 口径：只使用单证台账既有字段，不新增数据库结构或明细表。
/// </summary>
public class TradeDocumentPrintTests
{
    private const string 单证编号 = "CI-SO-PRINT-1";

    // ==================== 打印模型：字段映射 ====================

    [Fact]
    public void 打印模型_完整单证_字段取值与顺序完整()
    {
        var document = new TradeDocument
        {
            Id = 12,
            DocNo = 单证编号,
            DocType = "商业发票",
            IssueDate = new DateTime(2026, 9, 23),
            DeclareNo = "310120260923001",
            RefNo = "CTN-1001",
            SalesOrderNo = "SO-PRINT-1",
            CustomerId = 7,
            CustomerName = "义乌客户（打印测试）",
            Amount = 1250.5m,
            Currency = "USD",
            DeparturePort = "NINGBO",
            DestinationPort = "HAMBURG",
            IssuedBy = "单证员 A",
            Copies = 3,
            Status = "已提交客户",
            FileNote = "扫描件：\\files\\ci.pdf",
            Remark = "TELEX RELEASE",
        };

        var model = TradeDocumentPrintModel.From(document);

        Assert.Equal(12, model.Id);
        Assert.Equal(单证编号, model.DocNo);
        Assert.Equal("商业发票", model.DocType);
        Assert.Equal("已提交客户", model.Status);
        Assert.Equal("商业发票", model.Title);       // 模板未配置标题时的打印标题默认值
        Assert.False(model.HasDetailLines);          // 单表台账：不提供商品明细行

        // 字段顺序 = 默认打印顺序（未保存打印模板时前端按此顺序排版）
        Assert.Equal(TradeDocumentPrintSemantics.FieldDefinitions.Select(d => d.Key).ToList(),
            model.Fields.Select(f => f.Key).ToList());

        AssertField(model, TradeDocumentPrintSemantics.DocNo, "单证编号", 单证编号);
        AssertField(model, TradeDocumentPrintSemantics.DocType, "单证类型", "商业发票");
        AssertField(model, TradeDocumentPrintSemantics.Status, "状态", "已提交客户");
        AssertField(model, TradeDocumentPrintSemantics.IssueDate, "出具/签发日期", "2026-09-23");
        AssertField(model, TradeDocumentPrintSemantics.CustomerName, "客户名称", "义乌客户（打印测试）");
        AssertField(model, TradeDocumentPrintSemantics.SalesOrderNo, "关联销售订单号", "SO-PRINT-1");
        AssertField(model, TradeDocumentPrintSemantics.RefNo, "关联柜号/订舱号", "CTN-1001");
        AssertField(model, TradeDocumentPrintSemantics.DeclareNo, "关联报关单号", "310120260923001");
        AssertField(model, TradeDocumentPrintSemantics.Amount, "单证金额", "1250.50");
        AssertField(model, TradeDocumentPrintSemantics.Currency, "币种", "USD");
        AssertField(model, TradeDocumentPrintSemantics.DeparturePort, "起运港", "NINGBO");
        AssertField(model, TradeDocumentPrintSemantics.DestinationPort, "目的港", "HAMBURG");
        AssertField(model, TradeDocumentPrintSemantics.IssuedBy, "制作人/出证机构", "单证员 A");
        AssertField(model, TradeDocumentPrintSemantics.Copies, "份数", "3");
        AssertField(model, TradeDocumentPrintSemantics.FileNote, "附件说明/存放位置", "扫描件：\\files\\ci.pdf");
        AssertField(model, TradeDocumentPrintSemantics.Remark, "备注", "TELEX RELEASE");

        // 金额 / 日期标记供前端格式化，且只有金额字段被标记为金额
        Assert.Equal(new[] { TradeDocumentPrintSemantics.Amount },
            model.Fields.Where(f => f.IsMonetary).Select(f => f.Key).ToArray());
        Assert.Equal(new[] { TradeDocumentPrintSemantics.IssueDate },
            model.Fields.Where(f => f.IsDate).Select(f => f.Key).ToArray());
        Assert.Equal(TradeDocumentPrintSemantics.FieldDefinitions.Count, model.Fields.Count);
    }

    [Fact]
    public void 打印模型_仅登记编号_其余字段空白且标记为不可用()
    {
        var document = new TradeDocument { Id = 1, DocNo = 单证编号, Currency = string.Empty, Status = string.Empty };

        var model = TradeDocumentPrintModel.From(document);

        AssertField(model, TradeDocumentPrintSemantics.DocNo, "单证编号", 单证编号);
        // 其余字段一律「无值」：空白渲染（不是 0、不是由编号 / 类型推测出来的值）
        foreach (var key in new[]
        {
            TradeDocumentPrintSemantics.DocType, TradeDocumentPrintSemantics.Status,
            TradeDocumentPrintSemantics.IssueDate, TradeDocumentPrintSemantics.CustomerName,
            TradeDocumentPrintSemantics.SalesOrderNo, TradeDocumentPrintSemantics.RefNo,
            TradeDocumentPrintSemantics.DeclareNo, TradeDocumentPrintSemantics.Amount,
            TradeDocumentPrintSemantics.Currency, TradeDocumentPrintSemantics.DeparturePort,
            TradeDocumentPrintSemantics.DestinationPort, TradeDocumentPrintSemantics.IssuedBy,
            TradeDocumentPrintSemantics.Copies, TradeDocumentPrintSemantics.FileNote,
            TradeDocumentPrintSemantics.Remark,
        })
        {
            var field = FieldOf(model, key);
            Assert.False(field.Available, $"字段 {key} 台账无值时必须标记为不可用");
            Assert.Equal(string.Empty, field.Value);
        }

        Assert.Equal(1, model.Fields.Count(f => f.Available));      // 只有单证编号有值
        Assert.Equal("单证", model.Title);                          // 未维护单证类型时的标题默认值
    }

    [Fact]
    public void 打印模型_仅有客户Id_不回查客户档案也不臆造客户名称()
    {
        // 台账只落了 CustomerId（历史数据可能没有冗余客户名称）：打印必须留空，而不是去客户档案里补
        var document = new TradeDocument
        {
            Id = 3, DocNo = 单证编号, DocType = "报关单", CustomerId = 7, Currency = "CNY", Status = "待制作",
        };

        var model = TradeDocumentPrintModel.From(document);

        var customerName = FieldOf(model, TradeDocumentPrintSemantics.CustomerName);
        Assert.False(customerName.Available);
        Assert.Equal(string.Empty, customerName.Value);
        // 打印模型不含客户 Id 字段：不把档案主键印在单证上，也不提供任何「按 Id 反查」的出口
        Assert.DoesNotContain(model.Fields, f => f.Key == "customerId");
        Assert.DoesNotContain(model.Fields, f => f.Value == "7");
    }

    [Fact]
    public void 打印模型_金额与份数为零_视为未填写而不是0和0份()
    {
        var document = new TradeDocument
        {
            Id = 4, DocNo = 单证编号, DocType = "装箱单", Currency = "USD", Status = "待制作",
            Amount = 0m, Copies = 0,
        };

        var model = TradeDocumentPrintModel.From(document);

        var amount = FieldOf(model, TradeDocumentPrintSemantics.Amount);
        Assert.False(amount.Available);
        Assert.Equal(string.Empty, amount.Value);       // 不打印 0.00，避免把「未填金额」当成 0 元发票

        var copies = FieldOf(model, TradeDocumentPrintSemantics.Copies);
        Assert.False(copies.Available);
        Assert.Equal(string.Empty, copies.Value);
    }

    [Fact]
    public void 打印模型_状态与币种原样输出_不做状态码映射()
    {
        // 单证状态为中文业务状态（待制作 / 已制作 / 已提交客户 / 已使用），与单据数字状态枚举不是一套口径
        var model = TradeDocumentPrintModel.From(new TradeDocument
        {
            Id = 5, DocNo = 单证编号, DocType = "提单", Status = "已使用", Currency = "EUR",
        });

        AssertField(model, TradeDocumentPrintSemantics.Status, "状态", "已使用");
        AssertField(model, TradeDocumentPrintSemantics.Currency, "币种", "EUR");
    }

    [Fact]
    public void 打印模型_文本字段去除首尾空白_内部空格与换行保留()
    {
        var model = TradeDocumentPrintModel.From(new TradeDocument
        {
            Id = 6, DocNo = "  " + 单证编号 + "  ", DocType = " 产地证 ", Status = "待制作", Currency = "USD",
            Remark = "第一行 TELEX RELEASE\r\n第二行",
        });

        Assert.Equal(单证编号, model.DocNo);
        Assert.Equal("产地证", model.DocType);
        Assert.Equal("第一行 TELEX RELEASE\r\n第二行", FieldOf(model, TradeDocumentPrintSemantics.Remark).Value);
    }

    [Fact]
    public void 打印模型_单表台账_不含任何商品明细字段()
    {
        var model = TradeDocumentPrintModel.From(new TradeDocument
        {
            Id = 7, DocNo = 单证编号, DocType = "商业发票", Amount = 100m, Copies = 1, Currency = "USD",
        });

        Assert.False(model.HasDetailLines);
        foreach (var detailKey in new[] { "productId", "productName", "quantity", "unitPrice", "cartons", "weight" })
            Assert.DoesNotContain(model.Fields, f => f.Key == detailKey);
        Assert.Equal(TradeDocumentPrintSemantics.FieldDefinitions.Count, model.Fields.Count);
    }

    [Fact]
    public void 打印模型_单证为空引用_抛出参数异常()
    {
        Assert.Throws<ArgumentNullException>(() => TradeDocumentPrintModel.From(null!));
    }

    [Fact]
    public void 打印模型_JSON字段名与前端约定一致()
    {
        // 与 Program.cs 的 MVC JSON 配置一致：属性名 camelCase（前端按 data.docNo / data.fields[].available 取值）
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var model = TradeDocumentPrintModel.From(new TradeDocument
        {
            Id = 8, DocNo = 单证编号, DocType = "商业发票", IssueDate = new DateTime(2026, 9, 23),
            Amount = 99.9m, Copies = 2, Currency = "USD", Status = "已制作", RefNo = "CTN-2002",
        });

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(model, options));
        var root = json.RootElement;

        Assert.Equal(8, root.GetProperty("id").GetInt64());
        Assert.Equal(单证编号, root.GetProperty("docNo").GetString());
        Assert.Equal("商业发票", root.GetProperty("docType").GetString());
        Assert.Equal("已制作", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("hasDetailLines").GetBoolean());
        Assert.Equal(TradeDocumentPrintSemantics.FieldDefinitions.Count, root.GetProperty("fields").GetArrayLength());

        var first = root.GetProperty("fields")[0];
        Assert.Equal(TradeDocumentPrintSemantics.DocNo, first.GetProperty("key").GetString());
        Assert.Equal("单证编号", first.GetProperty("label").GetString());
        Assert.True(first.GetProperty("available").GetBoolean());
        Assert.False(first.GetProperty("isMonetary").GetBoolean());
        Assert.False(first.GetProperty("isDate").GetBoolean());

        var amount = root.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("key").GetString() == TradeDocumentPrintSemantics.Amount);
        Assert.Equal("99.90", amount.GetProperty("value").GetString());
        Assert.True(amount.GetProperty("isMonetary").GetBoolean());

        var issueDate = root.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("key").GetString() == TradeDocumentPrintSemantics.IssueDate);
        Assert.Equal("2026-09-23", issueDate.GetProperty("value").GetString());
        Assert.True(issueDate.GetProperty("isDate").GetBoolean());
    }

    // ==================== 打印接口（/api/trade/documents/{id}/print） ====================

    [Fact]
    public void 打印接口_路由模板_为单证Id加print()
    {
        AssertRouteTemplate(typeof(TradeDocumentController), nameof(TradeDocumentController.GetPrint),
            "{id:long}/print");
    }

    [Fact]
    public async Task 打印接口_返回单证台账字段投影_且不修改单据数据()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, 单证编号, "商业发票", copies: 3, amount: 500m);

        var model = GetData<TradeDocumentPrintModel>(await NewTradeDocumentController(db).GetPrint(document.Id));

        Assert.Equal(document.Id, model.Id);
        Assert.Equal(单证编号, model.DocNo);
        Assert.Equal("商业发票", model.Title);
        Assert.Equal("3", FieldOf(model, TradeDocumentPrintSemantics.Copies).Value);
        Assert.Equal("500.00", FieldOf(model, TradeDocumentPrintSemantics.Amount).Value);

        // 只读：打印不改状态、不改更新时间
        var stored = await db.TradeDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        Assert.Equal(document.Status, stored.Status);
        Assert.Equal(document.UpdatedAt, stored.UpdatedAt);
    }

    [Fact]
    public async Task 打印接口_单证不存在_抛业务异常()
    {
        using var db = TestDbFactory.Create();

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => NewTradeDocumentController(db).GetPrint(999));

        Assert.Equal(ErrorCodes.NotFound, error.Code);
    }

    [Fact]
    public async Task 打印接口_已软删除单证_不可打印()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, 单证编号, "提单");
        document.IsDeleted = true;
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => NewTradeDocumentController(db).GetPrint(document.Id));

        Assert.Equal(ErrorCodes.NotFound, error.Code);
    }

    // ==================== 打印模板登记（打印设计） ====================

    [Fact]
    public async Task 打印模板_单证中心内置默认模板_使用中文标题()
    {
        using var db = TestDbFactory.Create();
        db.SysParameters.Add(new SysParameter
        {
            ParamKey = "CompanyName", ParamValue = "某某外贸有限公司", ParamName = "公司名称",
        });
        await db.SaveChangesAsync();

        var template = GetData<SysPrintTemplate>(await new PrintTemplateController(db).GetDefault("doc-center"));

        Assert.Equal("单证中心", template.Title);        // 已登记中文名称，不会显示成 doc-center
        Assert.Equal("A4", template.PaperSize);
        Assert.True(template.IsDefault);
        Assert.Equal(string.Empty, template.FieldKeys);  // 未保存模板时前端按默认打印顺序排版
    }

    [Fact]
    public async Task 打印模板_既有可打印类型标题不受单证中心登记影响()
    {
        using var db = TestDbFactory.Create();
        var controller = new PrintTemplateController(db);

        Assert.Equal("销售订单", GetData<SysPrintTemplate>(await controller.GetDefault("sales-order")).Title);
        Assert.Equal("报价单", GetData<SysPrintTemplate>(await controller.GetDefault("quotation")).Title);
        Assert.Equal("形式发票 PI", GetData<SysPrintTemplate>(await controller.GetDefault("proforma-invoice")).Title);
        Assert.Equal("客户资料", GetData<SysPrintTemplate>(await controller.GetDefault("customer")).Title);
    }

    [Fact]
    public async Task 打印模板_保存单证中心模板_不影响其他单据模板()
    {
        using var db = TestDbFactory.Create();
        var controller = new PrintTemplateController(db);

        await controller.Save(new SysPrintTemplate
        {
            BillType = "doc-center", TemplateName = "单证小票版", PaperSize = "80mm", FontSize = 10, IsDefault = true,
        });
        await controller.Save(new SysPrintTemplate
        {
            BillType = "quotation", TemplateName = "报价单模板", IsDefault = true,
        });

        var docCenter = GetData<SysPrintTemplate>(await controller.GetDefault("doc-center"));
        Assert.Equal("单证小票版", docCenter.TemplateName);
        Assert.Equal("80mm", docCenter.PaperSize);

        var quotation = GetData<SysPrintTemplate>(await controller.GetDefault("quotation"));
        Assert.Equal("报价单模板", quotation.TemplateName);
        Assert.Equal("A4", quotation.PaperSize);

        // 按单据类型筛选互不串台（各类型只登记一份模板）
        Assert.Single(GetData<List<SysPrintTemplate>>(await controller.GetList("doc-center")));
        Assert.Single(GetData<List<SysPrintTemplate>>(await controller.GetList("quotation")));
    }

    // ==================== 前端接线（行操作 / 脚本 / 打印设计登记） ====================

    [Fact]
    public void 前端_单证中心打印动作_与共享打印接口接线一致()
    {
        var directory = JsDirectory();
        Assert.True(Directory.Exists(directory), $"未找到前端脚本目录：{directory}");

        var modulesJs = File.ReadAllText(Path.Combine(directory, "modules.js"));
        var printJs = File.ReadAllText(Path.Combine(directory, "trade-doc-print.js"));
        var indexHtml = File.ReadAllText(Path.Combine(directory, "..", "index.html"));

        // 行操作 → 全局函数名（必须成对存在，拼写漂移会让「更多」里的按钮点了没反应）
        foreach (var function in new[] { "previewTradeDocPrint", "printTradeDocDirect" })
        {
            Assert.Contains($"onclick: '{function}'", modulesJs);
            Assert.Contains($"function {function}(", printJs);
        }

        // 打印数据：单证打印接口（后台台账字段投影） + 共享打印模板接口
        Assert.Contains("${TRADE_DOC_PRINT.api}/${oid}/print`", printJs);
        Assert.Contains("${TRADE_DOC_PRINT.templateApi}/${encodeURIComponent(TRADE_DOC_PRINT.code)}`", printJs);
        Assert.Contains("function fetchTradeDocPrintContext(", printJs);

        // 复用既有共享打印约定：样式注入 / 纸张 / 模板归一化 / 字段标签 / 打印版式类名
        Assert.Contains("ensurePrintStyle();", printJs);
        Assert.Contains("printPageCss(tpl.PaperSize)", printJs);
        Assert.Contains("${PRINT_STYLE}", printJs);
        Assert.Contains("normalizeTemplate(template)", printJs);
        Assert.Contains("moduleFieldLabels(MODULES[TRADE_DOC_PRINT.code])", printJs);
        foreach (var cssClass in new[] { "print-page", "print-title", "print-meta", "print-fields", "print-sign", "print-footer" })
            Assert.Contains($"class=\"{cssClass}\"", printJs);
        // 预览与直接打印共用同一份打印数据与输出实现
        Assert.Contains("function openTradeDocPrintWindow(", printJs);
        Assert.Contains("function printTradeDoc(", printJs);
        Assert.Contains("window.__tradeDocPrintContext", printJs);

        // 缺值语义：只按打印接口的 available 标记渲染，不做客户 / 柜号 / 订单号反查与文本推测
        Assert.Contains("field.available === true", printJs);
        Assert.DoesNotContain("REF_APIS", printJs);
        Assert.DoesNotContain("customerId", printJs);
        Assert.DoesNotContain("/api/base/", printJs);

        // 脚本已注册进页面（否则函数不存在，浏览器验收会「点了没反应」）
        Assert.Contains("/js/trade-doc-print.js", indexHtml);
        Assert.True(indexHtml.IndexOf("/js/trade-doc-print.js", StringComparison.Ordinal)
                    > indexHtml.IndexOf("/js/modules.js", StringComparison.Ordinal));
    }

    [Fact]
    public void 前端打印字段_与后端打印口径一一对应()
    {
        var printJs = File.ReadAllText(Path.Combine(JsDirectory(), "trade-doc-print.js"));

        var start = printJs.IndexOf("fields: [", StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到前端打印字段清单 fields: [...]");
        var end = printJs.IndexOf(']', start);
        Assert.True(end > start, "前端打印字段清单未闭合");
        var keys = Regex.Matches(printJs.Substring(start, end - start), "'([A-Za-z][A-Za-z0-9]*)'")
            .Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(TradeDocumentPrintSemantics.FieldDefinitions.Select(d => d.Key).ToList(), keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());      // 同一字段不重复登记
    }

    [Fact]
    public void 前端_打印设计清单_单证中心只登记一次()
    {
        var directory = JsDirectory();
        var modulesJs = File.ReadAllText(Path.Combine(directory, "modules.js"));
        var printDesignJs = File.ReadAllText(Path.Combine(directory, "print-design.js"));
        var crudJs = File.ReadAllText(Path.Combine(directory, "crud.js"));
        var baseIoJs = File.ReadAllText(Path.Combine(directory, "base-io.js"));

        // 单证中心只在 MODULES 登记一次；不加入 BILL_CONFIG（否则打印设计清单里会与业务单据重复出现）
        Assert.Single(Regex.Matches(modulesJs, "'doc-center':").Cast<Match>());
        foreach (var file in Directory.GetFiles(directory, "bill-config*.js"))
            Assert.DoesNotContain("'doc-center'", File.ReadAllText(file));

        // 「样式设计」单据清单按「业务单据优先、基础资料排除同名」去重，并可预选任意单据类型
        Assert.Contains("const bases = Object.keys(MODULES || {})", printDesignJs);
        Assert.Contains(".filter(k => !(BILL_CONFIG || {})[k])", printDesignJs);
        Assert.Contains("function gotoPrintDesign(code)", printDesignJs);

        // 打印设计入口沿用共享列表页按钮（单证中心不重复放按钮；工具栏按钮由 crud.js 渲染，
        // 处理函数 openModulePrintDesign → gotoPrintDesign 定义在 base-io.js）
        Assert.Contains("onclick=\"openModulePrintDesign()\"", crudJs);
        Assert.Contains("function openModulePrintDesign() { gotoPrintDesign(CURRENT_MODULE_CODE); }", baseIoJs);
    }

    [Fact]
    public void 前端_既有报价与PI与订单打印链路不受影响()
    {
        var salesPiJs = File.ReadAllText(Path.Combine(JsDirectory(), "sales-pi.js"));

        foreach (var configKey in new[]
        {
            "quotation: {", "'proforma-invoice': {", "'sales-order': {", "'purchase-order': {",
        })
        {
            Assert.Contains(configKey, salesPiJs);
        }
        Assert.Contains("function previewSalesDocPrint(", salesPiJs);
        Assert.Contains("function printSalesDocDirect(", salesPiJs);
        Assert.Contains("function buildSalesDocPrintHtml(", salesPiJs);
    }

    // ==================== 工厂与断言辅助 ====================

    private static TradeDocumentController NewTradeDocumentController(ErpDbContext db)
        => new(new GenericService<TradeDocument>(db), db);

    /// <summary>断言控制器方法上存在指定路由模板</summary>
    private static void AssertRouteTemplate(Type controller, string methodName, string expectedTemplate)
    {
        var method = controller.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        var templates = method!.GetCustomAttributes<HttpMethodAttribute>(true)
            .Select(a => a.Template ?? string.Empty)
            .ToList();
        Assert.Contains(expectedTemplate, templates);
    }

    /// <summary>读取统一响应体中的数据</summary>
    private static T GetData<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        return response.Data!;
    }

    private static TradeDocumentPrintField FieldOf(TradeDocumentPrintModel model, string key)
        => Assert.Single(model.Fields.Where(f => f.Key == key));

    private static void AssertField(TradeDocumentPrintModel model, string key, string label, string value)
    {
        var field = FieldOf(model, key);
        Assert.Equal(label, field.Label);
        Assert.Equal(value, field.Value);
        Assert.True(field.Available, $"字段 {key} 应当有落库值");
    }

    /// <summary>前端脚本目录（沿测试程序集输出目录上溯到仓库根，与 UiTestFixture 同一约定）</summary>
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    /// <summary>单证台账种子数据（只使用既有字段，不新增列）</summary>
    private static TradeDocument SeedDocument(ErpDbContext db, string docNo, string docType,
        int copies = 3, decimal amount = 500m)
    {
        var document = new TradeDocument
        {
            DocNo = docNo, DocType = docType, IssueDate = new DateTime(2026, 9, 23),
            DeclareNo = "310120260923001", RefNo = "CTN-1001", SalesOrderNo = "SO-PRINT-1",
            CustomerId = 7, CustomerName = "义乌客户（打印测试）", Amount = amount, Currency = "USD",
            DeparturePort = "NINGBO", DestinationPort = "HAMBURG", IssuedBy = "单证员 A",
            Copies = copies, Status = "待制作", FileNote = string.Empty, Remark = "TELEX RELEASE",
        };
        db.TradeDocuments.Add(document);
        db.SaveChanges();
        return document;
    }
}

