using System.Text;
using System.Text.Json;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 受控打印模板族目录（ERP-312 / ERP-320 / ERP-321 Stage 2）：<c>PrintTemplateController.PrintableTitles</c> 封闭清单的服务端唯一受控映射。
/// 支持族 = 16 个旧单据导出族（复用 <see cref="LegacyBillExportCatalog"/> 的精确受控数据集键与有限字段白名单）
/// + 7 个基础资料（复用 <see cref="ReportConfigurationMasterDataCatalog"/> 的受控数据集键与有序列白名单）
/// + 2 个销售单据打印族（复用 <see cref="ReportConfigurationSalesDocumentCatalog"/> 的受控数据集键与有序列白名单）
/// + 单证中心（<c>trade-document</c>）；全部受控支持，无显式阻塞族。
/// <para>全部为服务端编译期常量，绝不来自客户端、绝不落到数据库全量元数据发现。</para>
/// </summary>
public static class ReportPrintTemplateFamilies
{
    /// <summary>封闭打印模板族（顺序稳定：16 个旧单据族 → 7 个基础资料 → 报价单 → 形式发票 PI → 单证中心）。</summary>
    public static readonly IReadOnlyList<ReportPrintTemplateFamilyDefinition> Families = BuildFamilies();

    private static readonly Dictionary<string, ReportPrintTemplateFamilyDefinition> ByFamilyKey =
        new(StringComparer.OrdinalIgnoreCase);

    static ReportPrintTemplateFamilies()
    {
        foreach (var family in Families)
            ByFamilyKey[family.FamilyKey] = family;
    }

    /// <summary>打印模板族的迁移登记册条目（支持族映射到受控数据集；不支持族数据集键为空 → parity 恒 pending）。</summary>
    public static readonly IReadOnlyList<ReportMigrationRegistryEntryDefinition> RegistryEntries =
        BuildRegistryEntries();

    private static IReadOnlyList<ReportMigrationRegistryEntryDefinition> BuildRegistryEntries()
        => Families
            .Select(f => new ReportMigrationRegistryEntryDefinition(
                "print-template:" + f.FamilyKey,
                f.Title + " 打印模板",
                ReportMigrationRegistryCategories.PrintTemplate,
                f.DatasetKey,
                f.RequiredMenuCodes,
                f.RequiredMenuText,
                CurrencyUnitSemantics(f),
                false,
                f.Supported))
            .ToList();

    private static string CurrencyUnitSemantics(ReportPrintTemplateFamilyDefinition family)
    {
        if (!family.Supported)
            return "无受控数据集（显式阻塞）";

        if (ReportConfigurationMasterDataCatalog.TryResolve(family.FamilyKey, out _))
            return ReportConfigurationMasterDataCatalog.CurrencyUnitSemantics;

        if (ReportConfigurationSalesDocumentCatalog.TryResolve(family.FamilyKey, out _))
            return ReportConfigurationSalesDocumentCatalog.CurrencyUnitSemantics;

        return "金额按原币呈现；数量按基础单位；不跨币种换算或合并";
    }

    /// <summary>按族键解析（未知 / 空白 / 畸形一律拒绝；在打开任何查询之前调用）。</summary>
    public static ReportPrintTemplateFamilyDefinition Resolve(string? familyKey)
    {
        if (string.IsNullOrWhiteSpace(familyKey))
            throw BusinessException.InvalidParameter("打印模板族标识不能为空");

        var key = familyKey.Trim();
        if (key.Length > 64 || ContainsUnsafeIdentifier(key))
            throw BusinessException.InvalidParameter($"打印模板族标识非法：{key}");

        if (!ByFamilyKey.TryGetValue(key, out var family))
            throw BusinessException.InvalidParameter($"未知的打印模板族标识：{key}");

        return family;
    }

    internal static bool ContainsUnsafeIdentifier(string value)
        => value.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == ':'));

    private static List<ReportPrintTemplateFamilyDefinition> BuildFamilies()
    {
        var families = new List<ReportPrintTemplateFamilyDefinition>();

        foreach (var bill in LegacyBillExportCatalog.Families)
        {
            families.Add(new ReportPrintTemplateFamilyDefinition(
                bill.FamilyKey,
                bill.Title,
                bill.DatasetKey,
                true,
                string.Empty,
                bill.RequiredMenuCodes,
                bill.RequiredMenuText,
                bill.Columns.Select(c => Alias(c.Key, c.Key, c.Title, c.Type)).ToList()));
        }

        families.Add(Master("customer"));
        families.Add(Master("supplier"));
        families.Add(Master("employee"));
        families.Add(Master("expense-account"));
        families.Add(Master("warehouse"));
        families.Add(Master("product"));
        families.Add(Master("other-info"));

        families.Add(SalesDocument("quotation"));
        families.Add(SalesDocument("proforma-invoice"));

        families.Add(new ReportPrintTemplateFamilyDefinition(
            "doc-center",
            "单证中心",
            ReportConfigurationConstants.DatasetTradeDocument,
            true,
            string.Empty,
            new[] { "doc-center" },
            "单证中心",
            TradeDocumentAliases));

        return families;
    }

    private static ReportPrintTemplateFamilyDefinition Master(string familyKey)
    {
        var master = ReportConfigurationMasterDataCatalog.Resolve(familyKey);
        return new ReportPrintTemplateFamilyDefinition(
            master.FamilyKey,
            master.Title,
            master.DatasetKey,
            true,
            string.Empty,
            master.RequiredMenuCodes,
            master.RequiredMenuText,
            master.Columns.Select(c => Alias(c.Key, c.Key, c.Title, c.Type)).ToList());
    }

    private static ReportPrintTemplateFamilyDefinition SalesDocument(string familyKey)
    {
        var sales = ReportConfigurationSalesDocumentCatalog.Resolve(familyKey);
        return new ReportPrintTemplateFamilyDefinition(
            sales.FamilyKey,
            sales.Title,
            sales.DatasetKey,
            true,
            string.Empty,
            sales.RequiredMenuCodes,
            sales.RequiredMenuText,
            sales.Columns.Select(c => Alias(c.Key, c.Key, c.Title, c.Type)).ToList());
    }

    private static ReportPrintTemplateFieldAlias Alias(string legacyKey, string columnKey, string title, string type)
        => new(legacyKey, columnKey, title, type);

    private static ReportPrintTemplateFieldAlias A(string key, string title, string type)
        => Alias(key, key, title, type);

    /// <summary>单证中心（trade-document）的有限旧字段别名：与 TradeDocumentReportConfigurationDatasetProvider 字段完全一致。</summary>
    private static readonly IReadOnlyList<ReportPrintTemplateFieldAlias> TradeDocumentAliases =
        new ReportPrintTemplateFieldAlias[]
        {
            A("id", "单证Id", ReportConfigurationConstants.TypeNumber),
            A("docNo", "单证编号", ReportConfigurationConstants.TypeText),
            A("docType", "单证类型", ReportConfigurationConstants.TypeText),
            A("status", "状态", ReportConfigurationConstants.TypeText),
            A("issueDate", "出具/签发日期", ReportConfigurationConstants.TypeDate),
            A("customerName", "客户名称", ReportConfigurationConstants.TypeText),
            A("salesOrderNo", "关联销售订单号", ReportConfigurationConstants.TypeText),
            A("refNo", "关联柜号/订舱号", ReportConfigurationConstants.TypeText),
            A("declareNo", "关联报关单号", ReportConfigurationConstants.TypeText),
            A("amount", "单证金额", ReportConfigurationConstants.TypeNumber),
            A("currency", "币种", ReportConfigurationConstants.TypeText),
            A("departurePort", "起运港", ReportConfigurationConstants.TypeText),
            A("destinationPort", "目的港", ReportConfigurationConstants.TypeText),
            A("issuedBy", "制作人/出证机构", ReportConfigurationConstants.TypeText),
            A("copies", "份数", ReportConfigurationConstants.TypeNumber),
            A("fileNote", "附件说明", ReportConfigurationConstants.TypeText),
            A("remark", "备注", ReportConfigurationConstants.TypeText),
            A("lineNo", "行序", ReportConfigurationConstants.TypeNumber),
            A("productCode", "商品编码", ReportConfigurationConstants.TypeText),
            A("productNameCn", "商品中文名称", ReportConfigurationConstants.TypeText),
            A("productNameEn", "商品英文名称", ReportConfigurationConstants.TypeText),
            A("spec", "规格型号", ReportConfigurationConstants.TypeText),
            A("quantity", "数量", ReportConfigurationConstants.TypeNumber),
            A("unit", "单位", ReportConfigurationConstants.TypeText),
            A("unitPrice", "单价", ReportConfigurationConstants.TypeNumber),
            A("lineCurrency", "行币种", ReportConfigurationConstants.TypeText),
            A("lineAmount", "行金额", ReportConfigurationConstants.TypeNumber),
            A("packageCount", "箱数", ReportConfigurationConstants.TypeNumber),
            A("netWeight", "净重kg", ReportConfigurationConstants.TypeNumber),
            A("grossWeight", "毛重kg", ReportConfigurationConstants.TypeNumber),
            A("lineRemark", "行备注", ReportConfigurationConstants.TypeText),
        };
}


/// <summary>
/// 受控打印模板绑定目录实现（ERP-312 Stage 2）：只读枚举 + 只读绑定校验。
/// <para>每次调用都按当前账号重新校验既有菜单授权与受控数据集列权限（fail closed）；不支持族 / 未知字段别名 /
/// 不兼容数据集 / 畸形超限 FieldKeys / LayoutJson / 模板归属不匹配一律显式拒绝，绝不执行布局表达式、绝不回写模板。</para>
/// </summary>
public sealed class ReportConfigurationPrintTemplateCatalog : IReportConfigurationPrintTemplateCatalog
{
    private const int MaxFieldKeysChars = 2000;
    private const int MaxLayoutJsonBytes = ReportConfigurationRules.MaxSerializedBytes;
    private const int MaxAliasKeyLength = 64;

    private readonly IErpDbContext _db;
    private readonly IReportConfigurationCatalog _catalog;

    public ReportConfigurationPrintTemplateCatalog(IErpDbContext db, IReportConfigurationCatalog catalog)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public async Task<ReportPrintTemplateCatalogDto> GetCatalogAsync(
        long? userId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(userId);
        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId!.Value);

        var templates = await _db.SysPrintTemplates.AsNoTracking()
            .Where(t => !t.IsDeleted)
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);

        var templatesByBillType = templates
            .GroupBy(t => t.BillType ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var families = new List<ReportPrintTemplateFamilyDto>();
        foreach (var family in ReportPrintTemplateFamilies.Families)
        {
            if (!IsMenuAuthorized(family, menuCodes))
                continue;

            var dto = new ReportPrintTemplateFamilyDto
            {
                FamilyKey = family.FamilyKey,
                Title = family.Title,
                Supported = family.Supported,
                UnsupportedReason = family.UnsupportedReason,
                DatasetKey = family.DatasetKey,
                CompatibilityStatus = family.Supported
                    ? ReportPrintTemplateCompatibilityText.Compatible
                    : ReportPrintTemplateCompatibilityText.Unsupported,
                RequiredMenuCodes = family.RequiredMenuCodes,
                RequiredMenuText = family.RequiredMenuText,
                FieldAliases = family.FieldAliases,
            };

            if (templatesByBillType.TryGetValue(family.FamilyKey, out var familyTemplates))
            {
                var order = 0;
                foreach (var template in familyTemplates)
                {
                    dto.Templates.Add(new ReportPrintTemplateDescriptorDto
                    {
                        Id = template.Id,
                        BillType = template.BillType,
                        TemplateName = template.TemplateName,
                        Title = template.Title,
                        IsDefault = template.IsDefault,
                        SortOrder = order++,
                        PaperSize = template.PaperSize,
                        FontSize = template.FontSize,
                        FontFamily = template.FontFamily,
                        ShowCompanyHeader = template.ShowCompanyHeader,
                        ShowDetailTable = template.ShowDetailTable,
                        ShowRemark = template.ShowRemark,
                        FooterText = template.FooterText,
                        FieldKeys = TryParseFieldKeys(template.FieldKeys),
                        HasGridLayout = !string.IsNullOrWhiteSpace(template.LayoutJson),
                    });
                }
            }

            families.Add(dto);
        }

        return new ReportPrintTemplateCatalogDto(ReportConfigurationRules.CurrentSchemaVersion, families);
    }


    /// <inheritdoc />
    public async Task<ReportPrintTemplateBindingDto> BindAsync(
        ReportPrintTemplateBindingRequest request, long? userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAuthenticated(userId);

        var family = ReportPrintTemplateFamilies.Resolve(request.FamilyKey);
        if (!family.Supported)
            throw BusinessException.RuleConflict(
                $"打印模板族 {family.Title} 尚不支持受控绑定：{family.UnsupportedReason}");

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId!.Value);
        if (!IsMenuAuthorized(family, menuCodes))
            throw new BusinessException($"当前账号无打印模板族 {family.Title} 的访问权限", ErrorCodes.Forbidden);

        if (request.TemplateId <= 0)
            throw BusinessException.InvalidParameter("打印模板 Id 不能为空");
        var template = await _db.SysPrintTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId && !t.IsDeleted, cancellationToken)
            ?? throw BusinessException.NotFound("打印模板不存在");
        if (!string.Equals(template.BillType ?? string.Empty, family.FamilyKey, StringComparison.OrdinalIgnoreCase))
            throw BusinessException.InvalidParameter("打印模板与指定族不匹配");

        var dataset = await _catalog.GetDatasetAsync(family.DatasetKey, userId, cancellationToken)
            ?? throw new BusinessException($"受控数据集 {family.DatasetKey} 当前不可用或未授权", ErrorCodes.Forbidden);

        var boundColumns = BindFieldKeys(family, dataset, request.FieldKeys, template.FieldKeys);

        ValidateLayoutJson(template.LayoutJson);
        ValidateLayoutJson(request.LayoutJson);

        return new ReportPrintTemplateBindingDto
        {
            TemplateId = template.Id,
            FamilyKey = family.FamilyKey,
            DatasetKey = family.DatasetKey,
            BoundColumns = boundColumns,
        };
    }


    private static IReadOnlyList<ReportPrintTemplateFieldAlias> BindFieldKeys(
        ReportPrintTemplateFamilyDefinition family,
        ReportConfigurationDatasetDto dataset,
        IReadOnlyList<string>? requested,
        string? savedFieldKeys)
    {
        var aliasByKey = family.FieldAliases.ToDictionary(a => a.LegacyKey, StringComparer.OrdinalIgnoreCase);
        var columnByKey = dataset.Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

        var keys = (requested ?? Array.Empty<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keys.Count == 0)
            keys = ParseFieldKeysStrict(savedFieldKeys);
        if (keys.Count == 0)
            keys = family.FieldAliases.Select(a => a.LegacyKey).ToList();

        if (keys.Count > ReportConfigurationRules.MaxFields)
            throw BusinessException.InvalidParameter($"打印模板字段数量超过上限 {ReportConfigurationRules.MaxFields}");

        var result = new List<ReportPrintTemplateFieldAlias>(keys.Count);
        foreach (var key in keys)
        {
            if (key.Length > MaxAliasKeyLength || ReportPrintTemplateFamilies.ContainsUnsafeIdentifier(key)
                || !aliasByKey.TryGetValue(key, out var alias))
                throw BusinessException.InvalidParameter($"打印模板族 {family.FamilyKey} 不支持的字段：{key}");

            if (!columnByKey.ContainsKey(alias.ColumnKey))
                throw BusinessException.InvalidParameter(
                    $"打印模板族 {family.FamilyKey} 的字段 {key} 不在当前授权数据集列权限内");

            result.Add(alias);
        }

        return result;
    }

    /// <summary>严格解析已保存 FieldKeys（JSON 字符串数组）；空返回空列表，畸形 / 超限抛参错误。</summary>
    private static List<string> ParseFieldKeysStrict(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new List<string>();

        if (raw.Length > MaxFieldKeysChars)
            throw BusinessException.InvalidParameter($"打印模板 FieldKeys 超过上限 {MaxFieldKeysChars} 字符");

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw BusinessException.InvalidParameter("打印模板 FieldKeys 必须是 JSON 字符串数组");

            var keys = new List<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    throw BusinessException.InvalidParameter("打印模板 FieldKeys 必须是 JSON 字符串数组");
                keys.Add(element.GetString()!);
            }

            return keys;
        }
        catch (JsonException)
        {
            throw BusinessException.InvalidParameter("打印模板 FieldKeys 不是合法 JSON");
        }
    }


    /// <summary>宽容解析已保存 FieldKeys：只读枚举不因历史畸形数据抛异常，畸形 / 超限返回空。</summary>
    private static IReadOnlyList<string> TryParseFieldKeys(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        if (raw.Length > MaxFieldKeysChars)
            return Array.Empty<string>();

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static void ValidateLayoutJson(string? layout)
    {
        if (string.IsNullOrWhiteSpace(layout))
            return;

        if (Encoding.UTF8.GetByteCount(layout) > MaxLayoutJsonBytes)
            throw BusinessException.InvalidParameter($"打印模板 LayoutJson 超过上限 {MaxLayoutJsonBytes} 字节");

        try
        {
            using var _ = JsonDocument.Parse(layout);
        }
        catch (JsonException)
        {
            throw BusinessException.InvalidParameter("打印模板 LayoutJson 不是合法 JSON");
        }
    }

    private static bool IsMenuAuthorized(
        ReportPrintTemplateFamilyDefinition family, HashSet<string> authorizedMenuCodes)
    {
        if (family.RequiredMenuCodes.Count == 0)
            return false;

        foreach (var code in family.RequiredMenuCodes)
        {
            if (!authorizedMenuCodes.Contains(code))
                return false;
        }

        return true;
    }

    private static void EnsureAuthenticated(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问打印模板绑定目录", ErrorCodes.Unauthorized);
    }
}

