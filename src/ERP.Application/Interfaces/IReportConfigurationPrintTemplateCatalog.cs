using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 受控打印模板绑定目录接缝（ERP-312 Stage 2）：把既有 <c>SysPrintTemplate</c> 只读地枚举为「兼容 / 显式不支持」的
/// 封闭族目录，并在每次绑定请求时重新校验既有菜单授权与受控数据集列权限，绝不回写模板、绝不授予权限、
/// 绝不执行布局表达式。
/// <para>无身份时 fail closed（抛未认证）；未授权族 / 未知字段别名 / 不支持族一律显式拒绝。</para>
/// </summary>
public interface IReportConfigurationPrintTemplateCatalog
{
    /// <summary>返回当前账号已授权族的打印模板绑定目录（只读、有界；未授权族被省略）。</summary>
    Task<ReportPrintTemplateCatalogDto> GetCatalogAsync(long? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把一个已保存模板绑定到受控数据集：重新校验族支持 / 菜单授权 / 模板归属 / 数据集与列权限 /
    /// 有限字段别名与字段顺序 / 有界合法 LayoutJson，返回有序受控列。
    /// </summary>
    Task<ReportPrintTemplateBindingDto> BindAsync(
        ReportPrintTemplateBindingRequest request, long? userId, CancellationToken cancellationToken = default);
}
