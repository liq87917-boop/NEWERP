using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Infrastructure.Data;
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

        // 通用报表配置平台（ERP-260 Stage 1）：私有报表配置服务（保存/列表/加载/复制/发布/恢复/软删除）
        services.AddScoped<IReportConfigurationService, ReportConfigurationService>();

        // 通用报表配置平台（ERP-261 Stage 1）：预览执行服务（草稿 / 固定发布修订 → 数据集适配器分发）
        services.AddScoped<IReportConfigurationExecutionService, ReportConfigurationExecutionService>();

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
        services.AddSingleton<IReportMigrationParityEvidenceProvider, EmptyReportMigrationParityEvidenceProvider>();
        services.AddScoped<IReportMigrationRegistry, ReportMigrationRegistry>();

        // 通用报表配置平台（ERP-296 Stage 2）：报表预设模板编排（只读列出 + 私有物化；数据驱动，不新增每报表控制器/设计器/导出器）
        services.AddScoped<IReportConfigurationPresetCatalog, ReportConfigurationPresetCatalog>();

        // 库存移动与成本服务（ERP-009：库存单据审核/销审统一经此维护库存与流水）
        services.AddScoped<IInventoryService, InventoryService>();

        // 通用 CRUD 服务（开放泛型注册）
        services.AddScoped(typeof(IGenericService<>), typeof(GenericService<>));

        return services;
    }
}
