using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Infrastructure.Data;
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
        // 库存移动与成本服务（ERP-009：库存单据审核/销审统一经此维护库存与流水）
        services.AddScoped<IInventoryService, InventoryService>();

        // 通用 CRUD 服务（开放泛型注册）
        services.AddScoped(typeof(IGenericService<>), typeof(GenericService<>));

        return services;
    }
}
