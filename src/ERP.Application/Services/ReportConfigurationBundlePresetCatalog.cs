using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>捆绑预设共享参数的编译期绑定（字段键 + 有限操作符）。</summary>
internal sealed record BundlePresetParameterBindingSeed(string FieldKey, string Operator);

/// <summary>捆绑预设单节模板（编译期）：原始节标题 / 数据集 / 原始列选择 / 参数绑定。</summary>
internal sealed record BundlePresetSectionSeed(
    string SectionKey,
    string Title,
    string DatasetKey,
    IReadOnlyList<string> FieldKeys,
    IReadOnlyDictionary<string, BundlePresetParameterBindingSeed> ParameterBindings);

/// <summary>捆绑预设共享参数元数据（编译期）：键 / 类型 / 中文标签 / 是否必填。</summary>
internal sealed record BundlePresetParameterSeed(string Key, string Type, string Label, bool Required);

/// <summary>捆绑预设模板种子（编译期）：有限、不可变；每条预设键控到一条迁移登记册条目，并声明独立前置菜单。</summary>
internal sealed record ReportConfigurationBundlePresetSeed(
    string PresetKey,
    string LegacyKey,
    string Name,
    IReadOnlyList<string> RequiredMenuCodes,
    IReadOnlyList<BundlePresetParameterSeed> Parameters,
    IReadOnlyList<BundlePresetSectionSeed> Sections);

/// <summary>
/// 捆绑预设编译期清单（ERP-310 Stage 2）：有限、不可变、顺序稳定。每条预设把已迁移旧报表的
/// 多节输出重新表达为受控数据集上的可校验多节定义。绝不新增权限 / 存储 / 每报表控制器 / 设计器 / 导出器。
/// </summary>
internal static class ReportConfigurationBundlePresetManifest
{
    private const string SalesOrderSheetName = CustomerReportPacketRules.SalesOrderSheetName;
    private const string ReceivableEvidenceSheetName = CustomerReportPacketRules.ReceivableEvidenceSheetName;

    private static readonly string[] OrderEvidenceFields =
    {
        "orderId", "orderNo", "orderDate", "status", "customerId", "customerName", "currency",
        "orderAmount", "recordedDepositAmount", "orderedQuantity", "shippedQuantity",
        "pendingShipmentQuantity", "outstandingQuantity", "shipmentStatus", "hasApprovedShipment",
        "receiptCoverageStatus", "linkedReceiptAmount", "pendingReceiptAmount", "uncoveredAmount",
        "receiptAllocationStatus", "recordedReceiptAllocationAmount", "invoiceEvidenceStatus",
        "recordedInvoicedAmount", "overReceived", "note",
    };

    private static readonly string[] UnlinkedReceiptFields =
    {
        "receiptId", "receiptNo", "receiptDate", "customerId", "customerName", "currency",
        "amount", "paymentMethod", "status", "evidenceStatus", "evidenceText",
        "receiptLinkageStatus", "receiptLinkageText", "referenceField", "note",
    };

    public static readonly IReadOnlyList<ReportConfigurationBundlePresetSeed> Presets = new[]
    {
        // ==================== 模板族 1：客户报告包（销售订单 + 应收证据） ====================
        new ReportConfigurationBundlePresetSeed(
            "customer-report-packet",
            "packet:customer-report-packet",
            "客户报告包（销售订单 + 应收证据）",
            new[] { "sales-order", "customer" },
            new BundlePresetParameterSeed[]
            {
                new(ReportConfigurationBundlePresetConstants.ParameterCustomer,
                    ReportConfigurationBundlePresetConstants.ParameterTypeNumber, "客户", Required: true),
                new(ReportConfigurationBundlePresetConstants.ParameterDate,
                    ReportConfigurationBundlePresetConstants.ParameterTypeDate, "日期区间", Required: false),
            },
            new[]
            {
                new BundlePresetSectionSeed(
                    "sales-order",
                    SalesOrderSheetName,
                    ReportConfigurationConstants.DatasetSalesOrder,
                    new[] { "orderNo", "orderDate", "customerId", "currency", "totalAmount", "status" },
                    new Dictionary<string, BundlePresetParameterBindingSeed>(StringComparer.OrdinalIgnoreCase)
                    {
                        [ReportConfigurationBundlePresetConstants.ParameterCustomer] =
                            new("customerId", ReportConfigurationConstants.OperatorEq),
                        [ReportConfigurationBundlePresetConstants.ParameterDate] =
                            new("orderDate", ReportConfigurationConstants.OperatorBetween),
                    }),
                new BundlePresetSectionSeed(
                    "receivable-evidence",
                    ReceivableEvidenceSheetName,
                    ReportConfigurationConstants.DatasetReceivable,
                    new[] { "invoiceNumber", "invoiceDate", "customerName", "currency", "grossAmount", "remainingAmount", "statusText" },
                    new Dictionary<string, BundlePresetParameterBindingSeed>(StringComparer.OrdinalIgnoreCase)
                    {
                        [ReportConfigurationBundlePresetConstants.ParameterCustomer] =
                            new("customerId", ReportConfigurationConstants.OperatorEq),
                        [ReportConfigurationBundlePresetConstants.ParameterDate] =
                            new("invoiceDate", ReportConfigurationConstants.OperatorBetween),
                    }),
            }),

        // ==================== 模板族 2：客户订单与收款核对（订单证据 + 未关联收款证据） ====================
        new ReportConfigurationBundlePresetSeed(
            "receipt-reconciliation",
            "dynamic:receipt-reconciliation",
            "客户订单与收款核对（订单证据 + 未关联收款证据）",
            new[] { "sales-order" },
            new BundlePresetParameterSeed[]
            {
                new(ReportConfigurationBundlePresetConstants.ParameterCustomer,
                    ReportConfigurationBundlePresetConstants.ParameterTypeNumber, "客户", Required: false),
                new(ReportConfigurationBundlePresetConstants.ParameterStatus,
                    ReportConfigurationBundlePresetConstants.ParameterTypeText, "收款证据状态", Required: false),
            },
            new[]
            {
                new BundlePresetSectionSeed(
                    "order-evidence",
                    "订单证据",
                    ReportConfigurationConstants.DatasetReceiptReconciliation,
                    OrderEvidenceFields,
                    new Dictionary<string, BundlePresetParameterBindingSeed>(StringComparer.OrdinalIgnoreCase)
                    {
                        [ReportConfigurationBundlePresetConstants.ParameterCustomer] =
                            new("customerId", ReportConfigurationConstants.OperatorEq),
                        [ReportConfigurationBundlePresetConstants.ParameterStatus] =
                            new("receiptCoverageStatus", ReportConfigurationConstants.OperatorEq),
                    }),
                new BundlePresetSectionSeed(
                    "unlinked-receipts",
                    "未关联收款证据",
                    ReportConfigurationConstants.DatasetUnlinkedReceipt,
                    UnlinkedReceiptFields,
                    new Dictionary<string, BundlePresetParameterBindingSeed>(StringComparer.OrdinalIgnoreCase)
                    {
                        [ReportConfigurationBundlePresetConstants.ParameterCustomer] =
                            new("customerId", ReportConfigurationConstants.OperatorEq),
                        [ReportConfigurationBundlePresetConstants.ParameterStatus] =
                            new("evidenceStatus", ReportConfigurationConstants.OperatorEq),
                    }),
            }),
    };

    private static readonly Dictionary<string, string> MenuLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sales-order"] = "销售订单",
        ["customer"] = "客户资料",
    };

    public static string MenuLabel(string menuCode)
        => MenuLabels.TryGetValue(menuCode, out var label) ? label : menuCode;
}

/// <summary>
/// 通用报表配置捆绑预设编排（ERP-310 Stage 2）实现：只读列出 + 参数化私有物化。
/// 物化先确认预设 preset-ready，再对每一节重新校验数据集授权、参数绑定与有界定义（绝不信任预设载荷），
/// 全部通过后才落为当前用户私有草稿；任何一节失败即整体回滚本次新建的私有草稿。
/// </summary>
public class ReportConfigurationBundlePresetCatalog : IReportConfigurationBundlePresetCatalog
{
    private readonly IReportConfigurationCatalog _catalog;
    private readonly IReportConfigurationService _service;
    private readonly IErpDbContext _db;

    public ReportConfigurationBundlePresetCatalog(
        IReportConfigurationCatalog catalog,
        IReportConfigurationService service,
        IErpDbContext db)
    {
        _catalog = catalog;
        _service = service;
        _db = db;
    }

    /// <inheritdoc />
    public async Task<List<ReportConfigurationBundlePresetDto>> ListPresetsAsync(
        long? userId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(userId);

        var result = new List<ReportConfigurationBundlePresetDto>();
        foreach (var preset in ReportConfigurationBundlePresetManifest.Presets)
        {
            var dto = await TryBuildAsync(preset, userId!.Value, cancellationToken);
            if (dto is not null)
                result.Add(dto);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationBundlePresetDto?> GetPresetAsync(
        string presetKey, long? userId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(userId);
        if (string.IsNullOrWhiteSpace(presetKey))
            return null;

        var preset = ReportConfigurationBundlePresetManifest.Presets.FirstOrDefault(p =>
            string.Equals(p.PresetKey, presetKey.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preset is null)
            return null;

        return await TryBuildAsync(preset, userId!.Value, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationBundlePresetMaterializationDto> MaterializeAsync(
        string presetKey,
        ReportConfigurationBundlePresetMaterializeRequest request,
        long? userId,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(userId);
        ArgumentNullException.ThrowIfNull(request);

        var preset = await RequireReadyPresetAsync(presetKey, userId!.Value, cancellationToken);
        var sections = await ValidateAndBuildSectionsAsync(preset, request, userId.Value, cancellationToken);

        var bundle = new ReportConfigurationBundleRequest
        {
            Sections = new List<ReportConfigurationBundleSectionRequest>(sections.Count),
        };

        // 全有或全无（ERP-315）：共享 EF 数据库事务。逐节 CreateAsync 的 SaveChanges 只入队到当前事务，
        // 统一 Commit 后才落库；取消 / 任一节失败 / 提交失败都整体回滚，绝不留下本次新建的部分可用草稿。
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var section in sections)
            {
                var dto = await _service.CreateAsync(userId.Value, section.Save, cancellationToken);
                bundle.Sections.Add(new ReportConfigurationBundleSectionRequest
                {
                    ConfigurationId = dto.Id,
                    Title = section.Title,
                });
            }

            await CommitAsync(transaction, cancellationToken);
        }
        catch (Exception original)
        {
            await RollbackPreservingAsync(transaction, original);
            throw;
        }

        return new ReportConfigurationBundlePresetMaterializationDto
        {
            PresetKey = preset.PresetKey,
            LegacyKey = preset.LegacyKey,
            Name = preset.Name,
            Bundle = bundle,
            Sections = preset.Sections.Select(s => new ReportConfigurationBundlePresetSectionDto
            {
                SectionKey = s.SectionKey,
                Title = s.Title,
                DatasetKey = s.DatasetKey,
                DatasetLabel = ResolveDatasetLabel(s.DatasetKey),
                FieldKeys = s.FieldKeys.ToList(),
            }).ToList(),
        };
    }

    private sealed record PreparedSection(string Title, string DatasetKey, ReportConfigurationSaveDto Save);

    private async Task<List<PreparedSection>> ValidateAndBuildSectionsAsync(
        ReportConfigurationBundlePresetSeed preset,
        ReportConfigurationBundlePresetMaterializeRequest request,
        long userId,
        CancellationToken cancellationToken)
    {
        ValidateDeclaredParameters(preset, request);

        var prepared = new List<PreparedSection>(preset.Sections.Count);
        foreach (var section in preset.Sections)
        {
            var dataset = await _catalog.GetDatasetAsync(section.DatasetKey, userId, cancellationToken)
                ?? throw BusinessException.RuleConflict($"数据集 {section.DatasetKey} 不存在或未授权，无法物化捆绑预设");

            foreach (var (parameterKey, binding) in section.ParameterBindings)
                ValidateBinding(parameterKey, binding, dataset);

            var definition = BuildDefinition(section);
            definition.Filters = BuildFilters(section, request);
            ReportConfigurationRules.Validate(definition, dataset);

            prepared.Add(new PreparedSection(
                section.Title,
                section.DatasetKey,
                new ReportConfigurationSaveDto
                {
                    Name = $"{preset.Name} - {section.Title}",
                    Definition = definition,
                }));
        }

        return prepared;
    }

    private static void ValidateDeclaredParameters(
        ReportConfigurationBundlePresetSeed preset,
        ReportConfigurationBundlePresetMaterializeRequest request)
    {
        var declared = preset.Parameters
            .Select(p => p.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (request.CustomerId.HasValue
            && !declared.Contains(ReportConfigurationBundlePresetConstants.ParameterCustomer))
            throw BusinessException.InvalidParameter("该捆绑预设不支持客户参数");

        if ((request.StartDate.HasValue || request.EndDate.HasValue)
            && !declared.Contains(ReportConfigurationBundlePresetConstants.ParameterDate))
            throw BusinessException.InvalidParameter("该捆绑预设不支持日期参数");

        if (!string.IsNullOrWhiteSpace(request.Status)
            && !declared.Contains(ReportConfigurationBundlePresetConstants.ParameterStatus))
            throw BusinessException.InvalidParameter("该捆绑预设不支持状态参数");

        if (request.CustomerId is <= 0)
            throw BusinessException.InvalidParameter("客户 Id 必须为正整数");

        if (request.StartDate.HasValue && request.EndDate.HasValue
            && request.StartDate.Value.Date > request.EndDate.Value.Date)
            throw BusinessException.InvalidParameter("开始日期不能晚于结束日期");

        if (request.Status is { Length: > ReportConfigurationBundlePresetConstants.MaxStatusLength })
            throw BusinessException.InvalidParameter(
                $"状态参数最长 {ReportConfigurationBundlePresetConstants.MaxStatusLength} 个字符");

        foreach (var parameter in preset.Parameters.Where(p => p.Required))
        {
            if (string.Equals(parameter.Key, ReportConfigurationBundlePresetConstants.ParameterCustomer, StringComparison.OrdinalIgnoreCase)
                && request.CustomerId is null)
                throw BusinessException.InvalidParameter($"参数 {parameter.Label} 为必填");
        }
    }

    private static void ValidateBinding(
        string parameterKey,
        BundlePresetParameterBindingSeed binding,
        ReportConfigurationDatasetDto dataset)
    {
        var field = dataset.Fields.FirstOrDefault(f =>
            string.Equals(f.Key, binding.FieldKey, StringComparison.OrdinalIgnoreCase));
        if (field is null)
            throw BusinessException.InvalidParameter(
                $"预设参数 {parameterKey} 绑定的字段 {binding.FieldKey} 不在数据集 {dataset.DatasetKey} 的字段目录中");
        if (field.Hidden)
            throw BusinessException.InvalidParameter(
                $"预设参数 {parameterKey} 绑定的字段 {binding.FieldKey} 已隐藏");
        if (!field.Filterable)
            throw BusinessException.InvalidParameter(
                $"预设参数 {parameterKey} 绑定的字段 {binding.FieldKey} 不可筛选");
        if (field.FilterOperators is null
            || !field.FilterOperators.Contains(binding.Operator, StringComparer.OrdinalIgnoreCase))
            throw BusinessException.InvalidParameter(
                $"预设参数 {parameterKey} 绑定的字段 {binding.FieldKey} 不支持操作符 {binding.Operator}");
        if (!TypeMatches(parameterKey, field.Type))
            throw BusinessException.InvalidParameter(
                $"预设参数 {parameterKey} 的类型与字段 {binding.FieldKey} 类型（{field.Type}）不匹配");
    }

    private static bool TypeMatches(string parameterKey, string fieldType)
    {
        return parameterKey switch
        {
            ReportConfigurationBundlePresetConstants.ParameterCustomer =>
                string.Equals(fieldType, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase),
            ReportConfigurationBundlePresetConstants.ParameterDate =>
                string.Equals(fieldType, ReportConfigurationConstants.TypeDate, StringComparison.OrdinalIgnoreCase),
            ReportConfigurationBundlePresetConstants.ParameterStatus =>
                string.Equals(fieldType, ReportConfigurationConstants.TypeText, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fieldType, ReportConfigurationConstants.TypeEnum, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static ReportConfigurationDefinition BuildDefinition(BundlePresetSectionSeed section)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = section.DatasetKey,
            Fields = section.FieldKeys.ToList(),
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
            Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
        };

    private static List<ReportConfigurationFilter> BuildFilters(
        BundlePresetSectionSeed section,
        ReportConfigurationBundlePresetMaterializeRequest request)
    {
        var filters = new List<ReportConfigurationFilter>();

        if (section.ParameterBindings.TryGetValue(
                ReportConfigurationBundlePresetConstants.ParameterCustomer, out var customerBinding)
            && request.CustomerId.HasValue)
        {
            filters.Add(new ReportConfigurationFilter
            {
                FieldKey = customerBinding.FieldKey,
                Operator = customerBinding.Operator,
                Value = request.CustomerId.Value,
            });
        }

        if (section.ParameterBindings.TryGetValue(
                ReportConfigurationBundlePresetConstants.ParameterDate, out var dateBinding))
        {
            AddDateFilters(filters, dateBinding, request.StartDate, request.EndDate);
        }

        if (section.ParameterBindings.TryGetValue(
                ReportConfigurationBundlePresetConstants.ParameterStatus, out var statusBinding)
            && !string.IsNullOrWhiteSpace(request.Status))
        {
            filters.Add(new ReportConfigurationFilter
            {
                FieldKey = statusBinding.FieldKey,
                Operator = statusBinding.Operator,
                Value = request.Status.Trim(),
            });
        }

        return filters;
    }

    private static void AddDateFilters(
        List<ReportConfigurationFilter> filters,
        BundlePresetParameterBindingSeed binding,
        DateTime? start,
        DateTime? end)
    {
        if (start.HasValue && end.HasValue)
        {
            filters.Add(new ReportConfigurationFilter
            {
                FieldKey = binding.FieldKey,
                Operator = ReportConfigurationConstants.OperatorBetween,
                Value = start.Value,
                Value2 = end.Value,
            });
        }
        else if (start.HasValue)
        {
            filters.Add(new ReportConfigurationFilter
            {
                FieldKey = binding.FieldKey,
                Operator = ReportConfigurationConstants.OperatorGte,
                Value = start.Value,
            });
        }
        else if (end.HasValue)
        {
            filters.Add(new ReportConfigurationFilter
            {
                FieldKey = binding.FieldKey,
                Operator = ReportConfigurationConstants.OperatorLte,
                Value = end.Value,
            });
        }
    }

    private async Task<ReportConfigurationBundlePresetDto?> TryBuildAsync(
        ReportConfigurationBundlePresetSeed preset,
        long userId,
        CancellationToken cancellationToken)
    {
        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(_db, userId);

        var prerequisites = new List<ReportConfigurationBundlePresetPrerequisiteDto>();
        var menusSatisfied = true;
        foreach (var code in preset.RequiredMenuCodes)
        {
            var satisfied = menuCodes.Contains(code);
            if (!satisfied)
                menusSatisfied = false;
            prerequisites.Add(new ReportConfigurationBundlePresetPrerequisiteDto
            {
                Kind = ReportConfigurationBundlePresetConstants.PrerequisiteMenu,
                Code = code,
                Label = ReportConfigurationBundlePresetManifest.MenuLabel(code),
                Satisfied = satisfied,
            });
        }

        if (!menusSatisfied)
            return null;

        var datasetReady = 0;
        var sections = new List<ReportConfigurationBundlePresetSectionDto>(preset.Sections.Count);
        foreach (var section in preset.Sections)
        {
            var dataset = await _catalog.GetDatasetAsync(section.DatasetKey, userId, cancellationToken);
            var satisfied = dataset is not null;
            if (satisfied)
                datasetReady++;

            prerequisites.Add(new ReportConfigurationBundlePresetPrerequisiteDto
            {
                Kind = ReportConfigurationBundlePresetConstants.PrerequisiteDataset,
                Code = section.DatasetKey,
                Label = dataset?.Label ?? section.DatasetKey,
                Satisfied = satisfied,
            });

            sections.Add(new ReportConfigurationBundlePresetSectionDto
            {
                SectionKey = section.SectionKey,
                Title = section.Title,
                DatasetKey = section.DatasetKey,
                DatasetLabel = dataset?.Label ?? section.DatasetKey,
                FieldKeys = section.FieldKeys.ToList(),
            });
        }

        var readiness = datasetReady == preset.Sections.Count
            ? ReportConfigurationBundlePresetConstants.ReadinessPresetReady
            : ReportConfigurationBundlePresetConstants.ReadinessDatasetReady;

        return new ReportConfigurationBundlePresetDto
        {
            PresetKey = preset.PresetKey,
            LegacyKey = preset.LegacyKey,
            Name = preset.Name,
            Readiness = readiness,
            Parameters = preset.Parameters.Select(p => new ReportConfigurationBundlePresetParameterDto
            {
                Key = p.Key,
                Type = p.Type,
                Label = p.Label,
                Required = p.Required,
            }).ToList(),
            Sections = sections,
            Prerequisites = prerequisites,
        };
    }

    private async Task<ReportConfigurationBundlePresetSeed> RequireReadyPresetAsync(
        string presetKey, long userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(presetKey))
            throw BusinessException.NotFound("捆绑预设不存在");

        var preset = ReportConfigurationBundlePresetManifest.Presets.FirstOrDefault(p =>
            string.Equals(p.PresetKey, presetKey.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw BusinessException.NotFound("捆绑预设不存在");

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(_db, userId);
        if (preset.RequiredMenuCodes.Any(code => !menuCodes.Contains(code)))
            throw BusinessException.NotFound("捆绑预设不存在或无权访问");

        foreach (var section in preset.Sections)
        {
            if (await _catalog.GetDatasetAsync(section.DatasetKey, userId, cancellationToken) is null)
                throw BusinessException.RuleConflict("捆绑预设尚未就绪，暂不能物化");
        }

        return preset;
    }

    private const string CleanupFailureDataKey = "BundlePreset.CleanupFailure";

    /// <summary>事务提交接缝（可被测试重写注入提交失败）；生产路径直接提交当前共享事务。</summary>
    protected virtual Task CommitAsync(IDbContextTransaction transaction, CancellationToken cancellationToken)
        => transaction.CommitAsync(cancellationToken);

    /// <summary>事务回滚接缝（可被测试重写注入清理失败）；生产路径直接回滚当前共享事务。</summary>
    protected virtual Task RollbackAsync(IDbContextTransaction transaction, CancellationToken cleanupToken)
        => transaction.RollbackAsync(cleanupToken);

    private async Task RollbackPreservingAsync(IDbContextTransaction transaction, Exception original)
    {
        // 有界清理令牌独立于已取消的请求：取消后仍能完成回滚，且绝不无限等待。
        using var cleanupCts = new CancellationTokenSource(ReportConfigurationBundlePresetConstants.CleanupTimeout);
        try
        {
            await RollbackAsync(transaction, cleanupCts.Token);
        }
        catch (Exception rollbackFailure)
        {
            // 绝不静默吞掉回滚失败：保留原始失败 / 取消与审计结果，把清理失败作为诊断证据挂到原异常上。
            original.Data[CleanupFailureDataKey] = rollbackFailure;
        }
    }

    private static string ResolveDatasetLabel(string datasetKey)
        => datasetKey switch
        {
            ReportConfigurationConstants.DatasetSalesOrder => "销售订单",
            ReportConfigurationConstants.DatasetReceivable => "客户应收账款证据",
            ReportConfigurationConstants.DatasetReceiptReconciliation => "客户订单与收款核对",
            ReportConfigurationConstants.DatasetUnlinkedReceipt => "客户级未关联收款证据",
            _ => datasetKey,
        };

    private static void EnsureAuthenticated(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问报表捆绑预设", ErrorCodes.Unauthorized);
    }
}
