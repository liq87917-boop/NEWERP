using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Reports;
using ERP.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ERP.Infrastructure;

/// <summary>
/// 基础设施层依赖注入注册
/// </summary>
public static class DependencyInjection
{
    /// <summary>注册数据访问与服务</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // 数据库上下文（SQL Server）
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("未配置数据库连接字符串");

        services.AddDbContext<ErpDbContext>(options =>
            options.UseSqlServer(connectionString));

        // 数据访问抽象（供应用层服务使用）
        services.AddScoped<IErpDbContext>(sp => sp.GetRequiredService<ErpDbContext>());

        // 存储过程数据访问服务（第二阶段：业务单据通过存储过程交互）
        services.AddScoped<StoredProcedureService>();

        // 阿里云 OSS 对象存储服务（图片上传）
        services.AddSingleton<ERP.Infrastructure.Storage.OssStorageService>();

        // 附件内容存储（ERP-061）：唯一内容接缝 IAttachmentContentStore —— 开发 / 测试只启用隔离的
        // 非生产本地存储；生产对象存储（OSS）未实现、未注册、未激活（其凭据 / 桶配置 / 迁移 / 启用
        // 属于生产 OSS Human Gate），因此工厂在遇到 Provider=oss 时显式拒绝而不是静默降级。
        services.AddSingleton<ERP.Application.Interfaces.IAttachmentContentStore>(_ =>
            ERP.Infrastructure.Storage.AttachmentContentStoreFactory.Create(configuration));

        // 商品资料 Excel 导出服务
        services.AddScoped<ERP.Infrastructure.Export.ProductExcelExporter>();

        // JWT 配置（手动绑定，避免 Options 配置扩展的包依赖）
        var jwtOptions = new JwtOptions
        {
            Key = configuration["Jwt:Key"] ?? throw new InvalidOperationException("未配置 JWT 密钥"),
            Issuer = configuration["Jwt:Issuer"] ?? "ERP.Api",
            Audience = configuration["Jwt:Audience"] ?? "ERP.Client",
            ExpireMinutes = int.TryParse(configuration["Jwt:ExpireMinutes"], out var expireMinutes) ? expireMinutes : 120
        };
        services.AddSingleton(jwtOptions);
        services.AddScoped<IJwtTokenService, JwtTokenService>();

        // 应用服务
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IDocumentNumberService, DocumentNumberService>();
        services.AddScoped<IReportService, ReportService>();
        // 动态销售订单报表预览（ERP-112）：只读、有界，复用销售订单菜单授权与业务员数据范围
        services.AddScoped<IDynamicSalesOrderReportQuery, DynamicSalesOrderReportQuery>();
        // 动态客户应收账款证据报表预览（ERP-117）：只读、有界，复用客户资料菜单授权、业务员数据范围与 ERP-074 对账引擎
        services.AddScoped<IDynamicReceivableReportQuery, DynamicReceivableReportQuery>();
        // 动态采购订单报表预览（ERP-125）：只读、有界，复用采购订单菜单授权与未删除可见性（不引入更宽的角色或数据范围策略）
        services.AddScoped<IDynamicPurchaseOrderReportQuery, DynamicPurchaseOrderReportQuery>();

        // 通用报表配置平台（ERP-259 Stage 1）：目录聚合 + 既有数据集适配器（新增数据集只加适配器，不改控制器/目录实现）
        services.AddScoped<IReportConfigurationCatalog, ReportConfigurationCatalog>();
        services.AddScoped<IReportConfigurationDatasetProvider, SalesOrderReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, ReceivableReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, PurchaseOrderReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, SupplierAgingReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, SupplierExposureReportConfigurationDatasetProvider>();
        // 简化财务报表 + 应收账龄（ERP-298）：预览委托既有固定报表服务语义，复用既有报表菜单授权（fail closed）
        services.AddScoped<IReportConfigurationDatasetProvider, BalanceSheetReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, IncomeStatementReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, CashFlowReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, ArAgingReportConfigurationDatasetProvider>();
        // 库存移动 / 库存库龄 / 库存预警（ERP-299）：预览委托既有固定报表服务语义，复用既有报表菜单授权（fail closed）
        services.AddScoped<IReportConfigurationDatasetProvider, InventoryMovementReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, InventoryAgingReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, StockAlertReportConfigurationDatasetProvider>();
        // 商品销量排名 / 订单利润暂估 / 业务员提成（ERP-300）：预览委托既有报表服务语义，复用既有报表菜单授权（fail closed）
        services.AddScoped<IReportConfigurationDatasetProvider, ProductSalesRankingReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, OrderProfitEstimateReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, SalesCommissionReportConfigurationDatasetProvider>();
        // 柜量统计 / 客户出货量（ERP-301）：复用既有报表服务语义，复用既有报表菜单授权（fail closed）
        services.AddScoped<IReportConfigurationDatasetProvider, ContainerStatsReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, CustomerShipmentReportConfigurationDatasetProvider>();
        // 出货财务进度（ERP-301）运行时路径已修正为 Api 层适配器（复用 SalesOrderShipmentFinanceReport/SalesOrderProgress），
        // 在 Program.cs 中注册；此处基础设施层同名类型仅作为既有单测兼容类型保留，不再注册为运行时路径。
        // 跟进提醒 / 报价成交率 / 业务员产值（ERP-302）：复用既有报表服务语义，复用既有报表菜单授权（fail closed）
        services.AddScoped<IReportConfigurationDatasetProvider, FollowUpDueReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, QuotationConversionReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, SalesmanOutputReportConfigurationDatasetProvider>();
        // 代理服务费月度汇总（ERP-303 Stage 2）：复用既有 Application 静态查询语义，复用既有报表菜单授权（fail closed）
        services.AddScoped<IReportConfigurationDatasetProvider, AgencyServiceFeeMonthlyReportConfigurationDatasetProvider>();
        // 采购成本 / 退税汇总（ERP-305 Stage 2）：复用既有固定报表服务语义，复用既有报表菜单授权（fail closed；混合币种分区拒绝）
        services.AddScoped<IReportConfigurationDatasetProvider, PurchaseCostReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, TaxRefundSummaryReportConfigurationDatasetProvider>();
        // 出口字段完整度 / 单证中心打印与台账导出（ERP-306 Stage 2）：复用既有完整度规则与 TradeDocumentPrintModel.From 持久快照语义，复用既有菜单授权（fail closed）
        services.AddScoped<IReportConfigurationDatasetProvider, ProductExportCompletenessReportConfigurationDatasetProvider>();
        services.AddScoped<IReportConfigurationDatasetProvider, TradeDocumentReportConfigurationDatasetProvider>();
        // 旧单据导出族（ERP-308 Stage 2）：受控只读族目录 + 每族一个数据集适配器（复用既有菜单授权与特权全量数据范围，fail closed）
        services.AddScoped<ILegacyBillExportReadService, LegacyBillExportReadService>();
        foreach (var family in LegacyBillExportCatalog.Families)
        {
            var billExportDatasetKey = family.DatasetKey;
            services.AddScoped<IReportConfigurationDatasetProvider>(sp =>
                new LegacyBillExportReportConfigurationDatasetProvider(
                    sp.GetRequiredService<IErpDbContext>(),
                    sp.GetRequiredService<ILegacyBillExportReadService>(),
                    billExportDatasetKey));
        }

        // 通用报表配置平台（ERP-260 Stage 1）：私有报表配置服务（保存/列表/加载/复制/发布/恢复/软删除）
        services.AddScoped<IReportConfigurationService, ReportConfigurationService>();

        // 通用报表配置平台（ERP-261 Stage 1）：预览执行服务（草稿 / 固定发布修订 → 数据集适配器分发）
        services.AddScoped<IReportConfigurationExecutionService, ReportConfigurationExecutionService>();

        // 通用报表配置平台（ERP-307 Stage 2）：有界多节捆绑（复用既有执行服务编排既有定义 / 版本，无新实体 / 无新 SQL）
        services.AddScoped<IReportConfigurationBundleService, ReportConfigurationBundleService>();

        // 通用报表配置平台（ERP-335 Stage 2）：表头 / 明细组合一致读取作用域工厂
        // （在既有作用域 DbContext 上获取 Snapshot 一致只读事务；后端 / 隔离级别不支持时 fail closed）
        services.AddScoped<IReportConfigurationCompositionReadScopeFactory, ReportConfigurationCompositionReadScopeFactory>();

        // 通用报表配置平台（ERP-310 Stage 2）：有界多节捆绑预设（有限模板 + 共享参数绑定 + 私有物化；复用既有捆绑引擎，无新实体 / 无新 SQL）
        services.AddScoped<IReportConfigurationBundlePresetCatalog, ReportConfigurationBundlePresetCatalog>();

        // 通用报表配置平台（ERP-269 Stage 1）：服务端执行预算（每用户并发租约 + 截止时间 + 大小上限；单例共享，平台常量）
        services.AddSingleton<IReportConfigurationExecutionBudget>(_ => new ReportConfigurationExecutionBudget());

        // 通用报表配置平台（ERP-268）：受控关系解析器（客户维度批量只读补全，复用客户资料菜单 + 业务员数据范围）
        services.AddScoped<IReportConfigurationRelationResolver, ReportConfigurationRelationResolver>();

        // 通用报表配置平台（ERP-265 Stage 1）：只读共享授权服务（owner grant/revoke + recipient 只读 / 复制）
        services.AddScoped<IReportConfigurationSharingService, ReportConfigurationSharingService>();

        // 通用报表配置平台（ERP-276 Stage 1）：可移植报表定义导入 / 导出（自有草稿 / 自有发布修订 / 共享固定快照 → 有界 JSON 信封）
        services.AddScoped<IReportConfigurationTransferService, ReportConfigurationTransferService>();

        // 通用报表配置平台（ERP-295 Stage 2）：报表迁移登记册（编译期清单 + 运行时派生；parity-passed 需要真实比对证据，绝不只凭声明）
        services.AddScoped<IReportMigrationPresetCatalog, ReportMigrationPresetCatalog>();
        // 迁移 parity 四维比对接缝（ERP-329 / ERP-331）：数据粒度 / 币种单位 / 权限 + Excel/PDF 输出语义
        services.AddSingleton<IReportMigrationParityComparator, ReportMigrationParityComparator>();
        services.AddSingleton<IReportMigrationOutputComparator, ReportMigrationOutputSemanticsComparator>();
        // 迁移 parity 证据源（ERP-332 Stage 2）：真实「旧路由 vs 通用平台」四维比对证据（数据粒度 / 币种单位 / 权限 / 输出语义）；
        // 证据服务按旧报表键在同一有界隔离夹具上运行两侧并比对；证据源只读、有界，无证据恒 null（fail closed）。
        services.AddSingleton<ReportMigrationParityEvidenceProvider>();
        services.AddSingleton<IReportMigrationParityEvidenceProvider>(sp =>
            sp.GetRequiredService<ReportMigrationParityEvidenceProvider>());
        services.AddSingleton<IReportMigrationParityEvidenceStore>(sp =>
            sp.GetRequiredService<ReportMigrationParityEvidenceProvider>());
        // 迁移 parity 三方一致读取作用域工厂（ERP-338 Stage 2）：旧来源 / 通用预览 / 实际旧产物
        // 在既有作用域 DbContext 上共享同一个 Snapshot 一致只读事务；后端 / 隔离级别 / 嵌套事务不支持时 fail closed。
        services.AddScoped<IReportMigrationParityReadScopeFactory, ReportMigrationParityReadScopeFactory>();
        services.AddScoped<IReportMigrationParityEvidenceService, ReportMigrationParityEvidenceService>();
        services.AddScoped<IReportMigrationRegistry, ReportMigrationRegistry>();

        // 旧报表来源统一接缝（ERP-330 Stage 2）：服务端受控登记册把每个旧报表条目映射到既有读取服务，
        // 并归一化为有界旧结果快照（复用既有菜单授权 + 行/数据范围，fail closed）。
        services.AddScoped<ILegacyReportSource, LegacyReportSourceRegistry>();

        // 旧打印快照统一读取（ERP-334 Stage 2）：复用既有基础资料控制器与报价单/PI GetPrint 语义，
        // 把注册的基础资料与销售单据打印族归一化为同一个有界旧打印快照（fresh 菜单 + 列 + 数据范围校验，fail closed）。
        services.AddScoped<ILegacyPrintSnapshotReadService, LegacyPrintSnapshotReadService>();

        // 旧单据读侧受控只读服务（ERP-405 Stage 3）：复用 ERP-308 有限族目录 + 既有功能菜单授权 + 业务员客户数据范围，
        // 计数 / 分页 / 导航 / 详情全部先受范围约束（受限账号在无权威客户归属族上 fail closed）。
        services.AddScoped<ILegacyBillReadService, LegacyBillReadService>();

        // 旧报表实际产物来源（ERP-333 / ERP-337 Stage 2）：有界有限登记册，经既有规范旧导出器产出真实旧 Excel/PDF 字节；
        // 缺失旧导出 / 字体 / 超限 fail closed，绝不使用通用导出器充当旧导出器。
        services.AddScoped<ILegacyReportArtifactSource>(sp => new LegacyReportArtifactSource(
            sp.GetRequiredService<IDynamicSalesOrderReportQuery>(),
            sp.GetRequiredService<IDynamicReceivableReportQuery>(),
            sp.GetRequiredService<IDynamicPurchaseOrderReportQuery>(),
            sp.GetRequiredService<ILegacyBillExportReadService>(),
            sp.GetRequiredService<IErpDbContext>()));

        // 通用报表配置平台（ERP-296 Stage 2）：报表预设模板编排（只读列出 + 私有物化；数据驱动，不新增每报表控制器/设计器/导出器）
        services.AddScoped<IReportConfigurationPresetCatalog, ReportConfigurationPresetCatalog>();

        // 通用报表配置平台（ERP-312 Stage 2）：受控打印模板绑定目录（只读枚举 + 只读绑定校验；复用既有菜单授权与受控数据集列权限）
        services.AddScoped<IReportConfigurationPrintTemplateCatalog, ReportConfigurationPrintTemplateCatalog>();

        // 通用报表配置平台（ERP-313 Stage 2）：受控打印渲染服务（复用既有执行服务与受控打印模板绑定目录；只读，不写库）
        services.AddScoped<IReportConfigurationPrintRenderService, ReportConfigurationPrintRenderService>();

        // 通用报表配置平台（ERP-317 Stage 2）：基础资料打印族受控数据集适配器（7 族；复用既有基础资料菜单授权与客户业务员数据范围，fail closed）
        foreach (var family in ReportConfigurationMasterDataCatalog.Families)
        {
            var masterDatasetKey = family.DatasetKey;
            services.AddScoped<IReportConfigurationDatasetProvider>(sp =>
                new MasterDataReportConfigurationDatasetProvider(
                    sp.GetRequiredService<IErpDbContext>(),
                    masterDatasetKey));
        }

        // 通用报表配置平台（ERP-318 Stage 2）：销售单据打印族受控数据集适配器（报价单 / 形式发票 PI；复用既有销售单据菜单授权与客户业务员数据范围，fail closed）
        foreach (var family in ReportConfigurationSalesDocumentCatalog.Families)
        {
            var salesDocumentDatasetKey = family.DatasetKey;
            services.AddScoped<IReportConfigurationDatasetProvider>(sp =>
                new SalesDocumentReportConfigurationDatasetProvider(
                    sp.GetRequiredService<IErpDbContext>(),
                    salesDocumentDatasetKey));
        }

        // 库存移动与成本服务（ERP-009：库存单据审核/销审统一经此维护库存与流水）
        services.AddScoped<IInventoryService, InventoryService>();

        // 通用 CRUD 服务（开放泛型注册）
        services.AddScoped(typeof(IGenericService<>), typeof(GenericService<>));

        return services;
    }
}
