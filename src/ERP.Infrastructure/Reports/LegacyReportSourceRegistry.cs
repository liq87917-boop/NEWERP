using System.Globalization;
using System.Reflection;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 旧报表来源统一接缝（ERP-330）的服务端受控登记册：把每个旧报表条目（固定报表 / 动态报表 /
/// 旧单据导出族 / 客户报告包 / 单证 / 财务报表 / 打印模板族）映射到其既有只读读取服务，
/// 并把异质结果归一化为同一个有界旧结果快照（<see cref="ReportMigrationParitySnapshotDto"/>）。
/// <para>每次读取都按当前账号重新校验菜单授权（fail closed）；需要数据范围的固定报表由既有服务
/// 在数据库端按 <see cref="SalespersonDataScopeService"/> 过滤；无绑定的来源显式返回
/// environment-blocked，绝不回退为全表读取；全程遵守既有分页 / 日期范围上限与取消令牌。</para>
/// </summary>
public sealed class LegacyReportSourceRegistry : ILegacyReportSource
{
    private delegate Task<ReportMigrationParitySnapshotDto> SourceHandler(
        LegacyReportSourceRequest request, CancellationToken cancellationToken);

    private const int MaxKeyLength = 128;
    private const int MaxPageSize = 200;

    private readonly IErpDbContext _db;
    private readonly IReportService _reports;
    private readonly IDynamicSalesOrderReportQuery _salesOrderQuery;
    private readonly IDynamicReceivableReportQuery _receivableQuery;
    private readonly IDynamicPurchaseOrderReportQuery _purchaseOrderQuery;
    private readonly ILegacyBillExportReadService _billExportReader;

    private readonly Dictionary<string, SourceHandler> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, ReportMigrationRegistryEntryDefinition> Definitions =
        BuildDefinitions();

    public LegacyReportSourceRegistry(
        IErpDbContext db,
        IReportService reports,
        IDynamicSalesOrderReportQuery salesOrderQuery,
        IDynamicReceivableReportQuery receivableQuery,
        IDynamicPurchaseOrderReportQuery purchaseOrderQuery,
        ILegacyBillExportReadService billExportReader)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _salesOrderQuery = salesOrderQuery ?? throw new ArgumentNullException(nameof(salesOrderQuery));
        _receivableQuery = receivableQuery ?? throw new ArgumentNullException(nameof(receivableQuery));
        _purchaseOrderQuery = purchaseOrderQuery ?? throw new ArgumentNullException(nameof(purchaseOrderQuery));
        _billExportReader = billExportReader ?? throw new ArgumentNullException(nameof(billExportReader));

        RegisterFixedReports();
        RegisterFinancialStatements();
        RegisterDynamicReports();
        RegisterExports();
        RegisterPacket();
        RegisterDocuments();
        RegisterPrintTemplateFamilies();
    }

    /// <inheritdoc />
    public async Task<LegacyReportSourceResult> ReadAsync(
        LegacyReportSourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // 1) 空白 / 畸形 / 未知键在打开任何查询之前精确拒绝。
        var definition = ResolveDefinition(request.LegacyKey);

        // 2) 身份校验（未认证直接拒绝）。
        if (request.UserId is null or <= 0)
            throw new BusinessException("请先登录后再读取旧报表来源", ErrorCodes.Unauthorized);

        // 3) 当前账号菜单授权重检（fail closed，权限撤销立即收敛）。
        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, request.UserId.Value);
        if (!IsMenuAuthorized(definition, menuCodes))
        {
            return Result(request.LegacyKey, definition, LegacyReportSourceStatus.Forbidden,
                ErrorCodes.Forbidden,
                $"当前账号没有「{definition.RequiredMenuText}」模块授权：拒绝读取旧报表来源（fail closed，不返回任何数据）");
        }

        // 4) 派发到既有读取服务；未绑定来源显式 environment-blocked，绝不回退为全表读取。
        if (!_handlers.TryGetValue(definition.LegacyKey, out var handler))
        {
            return Result(request.LegacyKey, definition, LegacyReportSourceStatus.EnvironmentBlocked,
                ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported,
                $"旧报表来源 {definition.LegacyKey} 尚未绑定受控读取实现（environment-blocked，绝不回退为全表读取）");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await handler(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Rows.Count > MaxPageSize)
                throw new BusinessException(
                    $"旧报表来源结果超过有界比较上限（{MaxPageSize} 行）；请收窄筛选范围，禁止截断后声明匹配",
                    ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);
            return Result(request.LegacyKey, definition, LegacyReportSourceStatus.Success,
                null, null, snapshot);
        }
        catch (BusinessException ex) when (ex.Code == ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported)
        {
            return Result(request.LegacyKey, definition, LegacyReportSourceStatus.EnvironmentBlocked,
                ex.Code, ex.Message);
        }
    }

    // ==================== 1. 登记：固定报表 ====================

    private void RegisterFixedReports()
    {
        Register("report:product-sales-ranking", (r, ct) => ScopedListAsync(r, s =>
            _reports.GetProductSalesRankingAsync(StartOf(r), EndOf(r), TopOf(r), s, null)));
        Register("report:order-profit", (r, ct) => ScopedListAsync(r, s =>
            _reports.GetOrderProfitEstimateAsync(StartOf(r), EndOf(r), s, null)));
        Register("report:customer-shipment", (r, ct) => ScopedListAsync(r, s =>
            _reports.GetCustomerShipmentStatsAsync(StartOf(r), EndOf(r), s, null)));
        Register("report:salesman-output", (r, ct) => ScopedListAsync(r, s =>
            _reports.GetSalesmanOutputAsync(StartOf(r), EndOf(r), s, null)));
        Register("report:sales-commission", (r, ct) => ScopedListAsync(r, s =>
            _reports.GetSalesCommissionAsync(StartOf(r), EndOf(r), s)));
        Register("report:container-stats", (r, ct) => ScopedListAsync(r, s =>
            _reports.GetContainerStatsAsync(StartOf(r), EndOf(r), s)));
        Register("report:follow-up-due", (r, ct) => ScopedListAsync(r, s =>
            _reports.GetFollowUpDueAsync(AsOf(r), AheadDaysOf(r), s)));
        Register("report:quotation-conversion", (r, ct) => ScopedListAsync(r, s =>
            _reports.GetQuotationConversionAsync(StartOf(r), EndOf(r), s, null)));
    }

    // ==================== 2. 登记：财务报表 ====================

    private void RegisterFinancialStatements()
    {
        Register("report:balance-sheet", async (r, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return FromStatement(await _reports.GetBalanceSheetAsync(AsOf(r)));
        });
        Register("report:income-statement", async (r, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return FromStatement(await _reports.GetIncomeStatementAsync(StartOf(r), EndOf(r)));
        });
        Register("report:cash-flow", async (r, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return FromStatement(await _reports.GetCashFlowStatementAsync(StartOf(r), EndOf(r)));
        });
        Register("report:ar-aging", async (r, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return FromObjects(await _reports.GetArAgingAsync(AsOf(r)), null);
        });
        Register("report:purchase-cost", async (r, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return FromObjects(await _reports.GetPurchaseCostAsync(StartOf(r), EndOf(r)), null);
        });
        Register("report:tax-refund-summary", async (r, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return FromObjects(await _reports.GetTaxRefundSummaryAsync(), null);
        });
        Register("report:stock-alert", async (r, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return FromObjects(await _reports.GetStockAlertAsync(), null);
        });
    }

    // ==================== 3. 登记：动态报表（既有查询接缝） ====================

    private void RegisterDynamicReports()
    {
        Register("dynamic:sales-order", async (r, ct) =>
        {
            var page = await _salesOrderQuery.PreviewAsync(new DynamicSalesOrderReportRequest
            {
                StartDate = r.Start,
                EndDate = r.End,
                Page = PageOf(r),
                PageSize = ClampPageSize(r.PageSize, 200),
            }, r.UserId, ct);
            return FromFieldPage(page.Columns, page.Rows, c => c.Key, c => c.DataType, "sales-order");
        });

        Register("dynamic:receivable", async (r, ct) =>
        {
            var page = await _receivableQuery.PreviewAsync(new DynamicReceivableReportRequest
            {
                StartDate = r.Start,
                EndDate = r.End,
                CustomerId = r.CustomerId,
                Page = PageOf(r),
                PageSize = ClampPageSize(r.PageSize, 100),
            }, r.UserId, ct);
            return FromFieldPage(page.Columns, page.Rows, c => c.Key, c => c.DataType, "receivable");
        });

        Register("dynamic:purchase-order", async (r, ct) =>
        {
            var page = await _purchaseOrderQuery.PreviewAsync(new DynamicPurchaseOrderReportRequest
            {
                StartDate = r.Start,
                EndDate = r.End,
                Page = PageOf(r),
                PageSize = ClampPageSize(r.PageSize, 100),
            }, r.UserId, ct);
            return FromFieldPage(page.Columns, page.Rows, c => c.Key, c => c.DataType, "purchase-order");
        });
    }

    // ==================== 4. 登记：旧单据导出族 ====================

    private void RegisterExports()
    {
        foreach (var family in LegacyBillExportCatalog.Families)
        {
            var familyKey = family.FamilyKey;
            Register("export:bill-proc:" + familyKey, (r, ct) => ReadBillExportAsync(familyKey, r, ct));
        }
    }

    private async Task<ReportMigrationParitySnapshotDto> ReadBillExportAsync(
        string familyKey, LegacyReportSourceRequest request, CancellationToken cancellationToken)
    {
        var query = new LegacyBillExportQuery
        {
            FamilyKey = familyKey,
            Page = PageOf(request),
            PageSize = ClampPageSize(request.PageSize, MaxPageSize),
            StartDate = request.Start,
            EndDate = request.End,
            Keyword = request.Keyword,
        };

        var page = await _billExportReader.ReadPageAsync(query, cancellationToken);
        return FromBillExportPage(page, familyKey);
    }

    // ==================== 5. 登记：客户报告包 ====================

    private void RegisterPacket()
        => Register("packet:customer-report-packet", (r, ct) => ReadPacketAsync(r, ct));

    private async Task<ReportMigrationParitySnapshotDto> ReadPacketAsync(
        LegacyReportSourceRequest request, CancellationToken cancellationToken)
    {
        var customerId = request.CustomerId ?? 0;
        CustomerReportPacketRules.ValidateCustomerId(customerId);
        CustomerReportPacketRules.ValidateDateRange(request.Start, request.End);

        var page = PageOf(request);
        var pageSize = ClampPageSize(request.PageSize, CustomerReportPacketRules.MaxPageSize);

        var salesOrders = await _salesOrderQuery.PreviewAsync(new DynamicSalesOrderReportRequest
        {
            CustomerId = customerId,
            StartDate = request.Start,
            EndDate = request.End,
            Page = page,
            PageSize = pageSize,
        }, request.UserId, cancellationToken);

        var receivableEvidence = await _receivableQuery.PreviewAsync(new DynamicReceivableReportRequest
        {
            CustomerId = customerId,
            StartDate = request.Start,
            EndDate = request.End,
            Page = page,
            PageSize = pageSize,
        }, request.UserId, cancellationToken);

        var packet = CustomerReportPacketRules.BuildPacket(customerId, salesOrders, receivableEvidence);
        return FromPacket(packet);
    }

    // ==================== 6. 登记：单证打印 / 导出 ====================

    private void RegisterDocuments()
    {
        Register("document:trade-document-print", (r, ct) => ReadTradeDocumentAsync(r, ct));
        Register("document:trade-document-export-excel", (r, ct) => ReadTradeDocumentAsync(r, ct));
    }

    private async Task<ReportMigrationParitySnapshotDto> ReadTradeDocumentAsync(
        LegacyReportSourceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var documentId = request.DocumentId ?? 0;
        if (documentId <= 0)
            throw BusinessException.InvalidParameter("旧单证读取必须指定正整数的单证 Id");

        var take = ClampPageSize(request.PageSize, TradeDocumentItemRules.MaxLinesPerDocument);
        var list = await TradeDocumentItemService.ListAsync(_db, documentId, take);
        return FromObjects(list.Items, null);
    }

    // ==================== 7. 登记：打印模板族 ====================

    private void RegisterPrintTemplateFamilies()
    {
        foreach (var family in ReportPrintTemplateFamilies.Families)
        {
            var legacyKey = "print-template:" + family.FamilyKey;
            if (LegacyBillExportCatalog.TryResolve(family.FamilyKey, out var billFamily))
            {
                var familyKey = billFamily.FamilyKey;
                Register(legacyKey, (r, ct) => ReadBillExportAsync(familyKey, r, ct));
            }
            else if (string.Equals(family.FamilyKey, "doc-center", StringComparison.OrdinalIgnoreCase))
            {
                Register(legacyKey, (r, ct) => ReadTradeDocumentAsync(r, ct));
            }
            // 其余打印模板族（基础资料 / 销售单据）当前阶段无独立旧读取接缝 → 保留为 environment-blocked。
        }
    }

    // ==================== 归一化：有界旧结果快照 ====================

    private async Task<ReportMigrationParitySnapshotDto> ScopedListAsync<T>(
        LegacyReportSourceRequest request, Func<SalespersonDataScope, Task<List<T>>> read)
    {
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, request.UserId);
        var items = await read(scope);
        return FromObjects(items, scope);
    }

    private static ReportMigrationParitySnapshotDto FromStatement(ReportDtos.FinancialStatement statement)
    {
        var columns = new ReportMigrationParityColumnDto[]
        {
            new("lineName", ReportConfigurationConstants.TypeText, null, null),
            new("amount", ReportConfigurationConstants.TypeNumber, null, null),
        };

        var rows = statement.Lines
            .Select(l => new ReportMigrationParityRowDto(
                new[] { l.Name },
                null,
                null,
                new object?[] { l.Name, l.Amount }))
            .ToList();

        return new ReportMigrationParitySnapshotDto(columns, rows, GlobalPermissions());
    }

    private static ReportMigrationParitySnapshotDto FromObjects<T>(
        IReadOnlyList<T> items, SalespersonDataScope? scope)
    {
        var props = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
            .OrderBy(p => p.MetadataToken)
            .ToList();

        var columns = props
            .Select(p => new ReportMigrationParityColumnDto(p.Name, ParityType(p.PropertyType), null, null))
            .ToList();

        var rows = new List<ReportMigrationParityRowDto>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i]!;
            var cells = new object?[props.Count];
            for (var c = 0; c < props.Count; c++)
                cells[c] = props[c].GetValue(item);

            rows.Add(new ReportMigrationParityRowDto(
                new[] { RowKeyOf(item, props, i) },
                StringProp(item, props, "Currency"),
                StringProp(item, props, "Unit"),
                cells));
        }

        return new ReportMigrationParitySnapshotDto(columns, rows, PermissionsOf(items, scope));
    }

    private static ReportMigrationParitySnapshotDto FromFieldPage<TField>(
        IReadOnlyList<TField> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        Func<TField, string> keyOf,
        Func<TField, string> typeOf,
        string scopeFingerprint)
    {
        var parityColumns = columns
            .Select(c => new ReportMigrationParityColumnDto(keyOf(c), typeOf(c), null, null))
            .ToList();

        var parityRows = new List<ReportMigrationParityRowDto>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var cells = parityColumns
                .Select(c => TryDictValue(row, c.Key, out var v) ? v : null)
                .ToList();

            parityRows.Add(new ReportMigrationParityRowDto(
                new[] { DictKey(row, i) },
                DictValue(row, "currency"),
                DictValue(row, "unit"),
                cells));
        }

        return new ReportMigrationParitySnapshotDto(
            parityColumns,
            parityRows,
            new ReportMigrationParityPermissionsDto(
                OwnedIdsFromDicts(rows),
                scopeFingerprint));
    }

    private static ReportMigrationParitySnapshotDto FromBillExportPage(
        LegacyBillExportPage page, string familyKey)
    {
        var columns = page.Columns
            .Select(c => new ReportMigrationParityColumnDto(c.Key, c.Type, null, null))
            .ToList();

        var rows = new List<ReportMigrationParityRowDto>();
        for (var i = 0; i < page.Rows.Count; i++)
        {
            var row = page.Rows[i];
            var cells = columns
                .Select(c => TryDictValue(row, c.Key, out var v) ? v : null)
                .ToList();

            rows.Add(new ReportMigrationParityRowDto(
                new[] { DictKey(row, i) },
                DictValue(row, "Currency") ?? DictValue(row, "currency"),
                DictValue(row, "Unit") ?? DictValue(row, "unit"),
                cells));
        }

        return new ReportMigrationParitySnapshotDto(
            columns,
            rows,
            new ReportMigrationParityPermissionsDto(
                OwnedIdsFromDicts(page.Rows),
                "bill-export:" + familyKey));
    }

    private static ReportMigrationParitySnapshotDto FromPacket(CustomerReportPacketDto packet)
    {
        var columns = new ReportMigrationParityColumnDto[]
        {
            new("section", ReportConfigurationConstants.TypeText, null, null),
            new("total", ReportConfigurationConstants.TypeNumber, null, null),
            new("readOnly", ReportConfigurationConstants.TypeText, null, null),
        };

        var rows = new List<ReportMigrationParityRowDto>
        {
            new(new[] { "sales-order" }, null, null,
                new object?[] { "sales-order", packet.SalesOrders.Total, packet.ReadOnlyText }),
            new(new[] { "receivable" }, null, null,
                new object?[] { "receivable", packet.ReceivableEvidence.Total, packet.ReadOnlyText }),
        };

        return new ReportMigrationParitySnapshotDto(columns, rows, GlobalPermissions());
    }

    // ==================== 反射辅助 ====================

    private static readonly string[] KeyPriority =
    {
        "Id", "OrderId", "CustomerId", "ProductId", "TradeDocumentId", "SupplierId",
        "OrderNo", "DocNo", "RefundPeriod", "BucketKey", "SalesmanName", "Name", "FollowNo",
    };

    private static readonly string[] IdPriority =
    {
        "Id", "CustomerId", "OrderId", "ProductId", "TradeDocumentId", "SupplierId",
    };

    private static readonly string[] DictKeyPriority =
    {
        "BillNo", "billNo", "Oid", "id", "orderId", "customerId", "invoiceId", "purchaseOrderId", "tradeDocumentId",
    };

    private static string RowKeyOf(object item, List<PropertyInfo> props, int index)
    {
        foreach (var key in KeyPriority)
        {
            var prop = props.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
            if (prop is null)
                continue;

            var value = prop.GetValue(item);
            if (IsBlankKey(value))
                continue;

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? $"row:{index}";
        }

        return $"row:{index}";
    }

    private static bool IsBlankKey(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        byte b => b == 0,
        short s => s == 0,
        int i => i == 0,
        long l => l == 0,
        decimal d => d == 0,
        double d => d == 0,
        float f => f == 0,
        _ => false,
    };

    private static string? StringProp(object item, List<PropertyInfo> props, string name)
    {
        var prop = props.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (prop is null)
            return null;

        var value = prop.GetValue(item);
        return value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string ParityType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(decimal) || underlying == typeof(double) || underlying == typeof(float)
            || underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short)
            || underlying == typeof(byte) || underlying == typeof(uint) || underlying == typeof(ulong)
            || underlying == typeof(ushort) || underlying == typeof(sbyte))
        {
            return ReportConfigurationConstants.TypeNumber;
        }

        if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset))
            return ReportConfigurationConstants.TypeDate;

        if (underlying == typeof(bool))
            return ReportConfigurationConstants.TypeBoolean;

        if (underlying.IsEnum)
            return ReportConfigurationConstants.TypeEnum;

        return ReportConfigurationConstants.TypeText;
    }

    private static ReportMigrationParityPermissionsDto PermissionsOf<T>(
        IReadOnlyList<T> items, SalespersonDataScope? scope)
    {
        var props = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
            .ToList();

        var ids = new List<long>();
        foreach (var item in items)
        {
            foreach (var key in IdPriority)
            {
                var prop = props.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                if (prop is null)
                    continue;

                if (ToLong(prop.GetValue(item)) is { } id && id != 0)
                {
                    ids.Add(id);
                    break;
                }
            }
        }

        var fingerprint = scope is null
            ? "global"
            : scope.IsPrivileged
                ? "privileged"
                : "customers:" + string.Join(",", (scope.AllowedCustomerIds ?? new HashSet<long>()).OrderBy(x => x));

        return new ReportMigrationParityPermissionsDto(
            ids.Distinct().OrderBy(x => x).ToList(),
            fingerprint);
    }

    private static string DictKey(Dictionary<string, object?> row, int index)
    {
        foreach (var key in DictKeyPriority)
        {
            if (TryDictValue(row, key, out var value) && value is not null)
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? $"row:{index}";
        }

        return $"row:{index}";
    }

    private static string? DictValue(Dictionary<string, object?> row, string name)
    {
        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Value is null ? null : Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static bool TryDictValue(Dictionary<string, object?> row, string name, out object? value)
    {
        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static IReadOnlyList<long> OwnedIdsFromDicts(IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var ids = new List<long>();
        foreach (var row in rows)
        {
            foreach (var key in IdPriority)
            {
                if (TryDictValue(row, key, out var value) && ToLong(value) is { } id && id != 0)
                {
                    ids.Add(id);
                    break;
                }
            }
        }

        return ids.Distinct().OrderBy(x => x).ToList();
    }

    private static long? ToLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        _ => null,
    };

    private static ReportMigrationParityPermissionsDto GlobalPermissions()
        => new(Array.Empty<long>(), "global");

    // ==================== 键 / 授权 / 有界参数辅助 ====================

    private static Dictionary<string, ReportMigrationRegistryEntryDefinition> BuildDefinitions()
    {
        var map = new Dictionary<string, ReportMigrationRegistryEntryDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in ReportMigrationRegistryManifest.Entries
            .Concat(LegacyBillExportCatalog.RegistryEntries)
            .Concat(ReportPrintTemplateFamilies.RegistryEntries))
        {
            map[definition.LegacyKey] = definition;
        }

        return map;
    }

    private static ReportMigrationRegistryEntryDefinition ResolveDefinition(string? legacyKey)
    {
        if (string.IsNullOrWhiteSpace(legacyKey))
            throw BusinessException.InvalidParameter("旧报表来源键不能为空");

        var key = legacyKey.Trim();
        if (key.Length > MaxKeyLength || ContainsUnsafeKey(key))
            throw BusinessException.InvalidParameter($"旧报表来源键非法：{key}");

        if (!Definitions.TryGetValue(key, out var definition))
            throw BusinessException.InvalidParameter($"未知的旧报表来源键：{key}");

        return definition;
    }

    private static bool ContainsUnsafeKey(string value)
        => value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or ':'));

    private static bool IsMenuAuthorized(
        ReportMigrationRegistryEntryDefinition definition, HashSet<string> authorizedMenuCodes)
    {
        if (definition.RequiredMenuCodes.Count == 0)
            return false;

        foreach (var code in definition.RequiredMenuCodes)
        {
            if (!authorizedMenuCodes.Contains(code))
                return false;
        }

        return true;
    }

    private void Register(string legacyKey, SourceHandler handler)
        => _handlers[legacyKey] = handler;

    private static DateTime StartOf(LegacyReportSourceRequest request)
        => (request.Start ?? DateTime.Today).Date;

    private static DateTime EndOf(LegacyReportSourceRequest request)
        => (request.End ?? DateTime.Today).Date;

    private static DateTime AsOf(LegacyReportSourceRequest request)
        => (request.AsOfDate ?? DateTime.Today).Date;

    private static int TopOf(LegacyReportSourceRequest request)
        => request.Top is < 1 or > 200 ? 200 : request.Top;

    private static int AheadDaysOf(LegacyReportSourceRequest request)
        => request.AheadDays is < 1 or > 365 ? 7 : request.AheadDays;

    private static int PageOf(LegacyReportSourceRequest request)
        => request.Page < 1 ? 1 : request.Page;

    private static int ClampPageSize(int pageSize, int max)
        => pageSize < 1 ? max : Math.Min(pageSize, max);

    private static LegacyReportSourceResult Result(
        string? legacyKey,
        ReportMigrationRegistryEntryDefinition definition,
        LegacyReportSourceStatus status,
        int? errorCode,
        string? errorMessage,
        ReportMigrationParitySnapshotDto? snapshot = null)
        => new(legacyKey?.Trim(), definition.Category, status, errorCode, errorMessage, snapshot);
}






