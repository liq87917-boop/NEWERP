using ERP.Infrastructure;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// 支持 ERP_ 前缀的环境变量注入（如 ERP_ConnectionStrings__Default -> ConnectionStrings:Default）
builder.Configuration.AddEnvironmentVariables(prefix: "ERP_");

// ============ Serilog 日志 ============
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File("logs/erp-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .CreateLogger();
builder.Host.UseSerilog();

// ============ 服务注册 ============
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // 枚举输出为数字，前端按值判断状态
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "外贸 ERP 系统 API",
        Version = "v1",
        Description = "外贸管理 ERP WebAPI 接口文档"
    });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "请输入 JWT 令牌（不含 Bearer 前缀）"
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// ============ 基础设施（数据库 + 服务） ============
builder.Services.AddInfrastructure(builder.Configuration);

// 钉钉通知：HttpClient 工厂 + 推送服务（业务单据状态变化时推送到钉钉群机器人）
builder.Services.AddHttpClient();
builder.Services.AddScoped<ERP.Api.Services.DingTalkService>();
// 客户跟进提醒后台服务（按系统参数配置的时间，每日把到期/逾期的客户跟进推送到钉钉；默认关闭）
// 注册为单例并复用同一实例：既作为后台任务运行，也可被控制器注入以支持「手动推送一次」
builder.Services.AddSingleton<ERP.Api.Services.FollowUpReminderService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ERP.Api.Services.FollowUpReminderService>());

// ============ JWT 认证 ============
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("未配置 JWT 密钥");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "ERP.Api";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "ERP.Client";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            // 认证失败时返回统一错误格式
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("{\"code\":2000,\"message\":\"未认证或登录已过期\",\"data\":null}");
            },
            OnForbidden = async context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("{\"code\":2002,\"message\":\"权限不足\",\"data\":null}");
            }
        };
    });

builder.Services.AddAuthorization();

// ============ CORS ============
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

// ============ 数据库初始化（自动建表 + 结构升级 + 种子数据） ============
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ErpDbContext>();
    await db.Database.EnsureCreatedAsync();
    // 补齐已存在数据库的新增表/列（幂等，保证版本升级无需人工执行 DDL）
    await SchemaUpgrader.EnsureUpgradedAsync(db);
    await SeedData.InitializeAsync(db);
    // 种子数据之后再做一次（幂等）：全新库首次初始化时基础菜单由 SeedData 创建，
    // 菜单归属/排序等依赖基础菜单的升级项需要在此之后才能正确生效
    await SchemaUpgrader.EnsureUpgradedAsync(db);
}

// ============ 中间件管道 ============
app.UseSerilogRequestLogging();
app.UseMiddleware<ERP.Api.Middleware.ExceptionHandlingMiddleware>();
app.UseMiddleware<ERP.Api.Middleware.OperationLogMiddleware>();

// 静态文件（前端可视化页面，位于 wwwroot）
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        /* 静态资源强制禁用缓存，避免用户浏览器拿到旧版 CSS/JS 导致样式异常 */
        ctx.Context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        ctx.Context.Response.Headers["Pragma"] = "no-cache";
        ctx.Context.Response.Headers["Expires"] = "0";
    }
});

// Swagger 接口文档（开发与生产环境均可用，便于交付与调试）
app.UseSwagger();
app.UseSwaggerUI();

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

// 根路径默认返回前端页面（index.html）
app.MapFallbackToFile("index.html");

app.MapControllers();

app.Run();

// 供集成测试引用的入口点
public partial class Program { }
