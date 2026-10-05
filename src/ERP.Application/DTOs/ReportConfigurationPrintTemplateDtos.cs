using System.Collections.Generic;

namespace ERP.Application.DTOs;

/// <summary>
/// 打印模板绑定目录（ERP-312 Stage 2）的对外兼容性状态取值：兼容（compatible）或显式不支持（unsupported）。
/// 绝不返回「parity-passed」——该状态只能由迁移登记册在存在真实比对证据时派生。
/// </summary>
public static class ReportPrintTemplateCompatibilityText
{
    /// <summary>受控打印模板族：已映射到精确受控数据集键与有限旧字段别名。</summary>
    public const string Compatible = "compatible";

    /// <summary>受控打印模板族：显式阻塞（基础资料 / 阶段 3 主子表单据等，绝不猜测联接或静默回落）。</summary>
    public const string Unsupported = "unsupported";
}

/// <summary>
/// 有限旧打印字段别名（只读、服务端编译期常量）：一个旧打印字段键到受控数据集列的精确映射。
/// <para><see cref="LegacyKey"/> 来自旧打印模板 <c>SysPrintTemplate.FieldKeys</c>；<see cref="ColumnKey"/>
/// 为该列在受控数据集适配器中的精确列键（与旧单据导出白名单 / 单证中心字段完全一致）。</para>
/// </summary>
public sealed record ReportPrintTemplateFieldAlias(
    string LegacyKey,
    string ColumnKey,
    string Title,
    string Type);

/// <summary>
/// 受控打印模板族定义（有限、只读）：来自 <c>PrintTemplateController.PrintableTitles</c> 的封闭清单。
/// <para>支持族映射到精确受控数据集键与有限旧字段别名；不支持族显式阻塞（<see cref="Supported"/> = false + 原因）。</para>
/// </summary>
public sealed record ReportPrintTemplateFamilyDefinition(
    string FamilyKey,
    string Title,
    string DatasetKey,
    bool Supported,
    string UnsupportedReason,
    IReadOnlyList<string> RequiredMenuCodes,
    string RequiredMenuText,
    IReadOnlyList<ReportPrintTemplateFieldAlias> FieldAliases);

/// <summary>
/// 已保存打印模板的只读描述（ERP-312 Stage 2）：保留身份 / 默认 / 顺序 / 纸张 / 字号 / 字体 / 页眉页脚设置，
/// 绝不回写模板、绝不授予权限；FieldKeys 仅暴露受控字段顺序。
/// </summary>
public sealed class ReportPrintTemplateDescriptorDto
{
    /// <summary>模板主键（保存的身份）。</summary>
    public long Id { get; set; }

    /// <summary>单据类型（菜单编码）。</summary>
    public string BillType { get; set; } = string.Empty;

    /// <summary>模板名称。</summary>
    public string TemplateName { get; set; } = string.Empty;

    /// <summary>打印标题（为空时使用单据中文名称）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>是否为该单据类型的默认模板。</summary>
    public bool IsDefault { get; set; }

    /// <summary>稳定顺序（默认模板优先，其次按 Id 升序）。</summary>
    public int SortOrder { get; set; }

    /// <summary>纸张规格。</summary>
    public string PaperSize { get; set; } = string.Empty;

    /// <summary>正文字号（px）。</summary>
    public int FontSize { get; set; }

    /// <summary>正文字体族。</summary>
    public string FontFamily { get; set; } = string.Empty;

    /// <summary>是否显示公司抬头。</summary>
    public bool ShowCompanyHeader { get; set; }

    /// <summary>是否打印明细（副表）。</summary>
    public bool ShowDetailTable { get; set; }

    /// <summary>是否打印备注。</summary>
    public bool ShowRemark { get; set; }

    /// <summary>页脚文本。</summary>
    public string FooterText { get; set; } = string.Empty;

    /// <summary>打印字段顺序（旧字段键，保持保存顺序；畸形时为空，绝不影响保存原文）。</summary>
    public IReadOnlyList<string> FieldKeys { get; set; } = new List<string>();

    /// <summary>是否含网格布局（Excel 式设计器数据）。</summary>
    public bool HasGridLayout { get; set; }
}

/// <summary>打印模板族目录项（兼容 / 显式不支持 + 当前账号可见的已保存模板）。</summary>
public sealed class ReportPrintTemplateFamilyDto
{
    public string FamilyKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool Supported { get; set; }
    public string UnsupportedReason { get; set; } = string.Empty;
    public string DatasetKey { get; set; } = string.Empty;

    /// <summary>对外兼容性状态（compatible / unsupported；绝不 parity-passed）。</summary>
    public string CompatibilityStatus { get; set; } = ReportPrintTemplateCompatibilityText.Unsupported;

    public IReadOnlyList<string> RequiredMenuCodes { get; set; } = new List<string>();
    public string RequiredMenuText { get; set; } = string.Empty;
    public IReadOnlyList<ReportPrintTemplateFieldAlias> FieldAliases { get; set; } = new List<ReportPrintTemplateFieldAlias>();
    public List<ReportPrintTemplateDescriptorDto> Templates { get; set; } = new();
}

/// <summary>打印模板绑定目录（只读、有界）：仅当前账号已授权族的兼容性声明与已保存模板。</summary>
public sealed record ReportPrintTemplateCatalogDto(
    int SchemaVersion,
    List<ReportPrintTemplateFamilyDto> Families);

/// <summary>
/// 绑定请求：指定一个已保存模板 + 有序旧字段键 + 可选布局 JSON，把其绑定到受控数据集。
/// <para>字段键只能命中该族的有限旧字段别名；布局 JSON 只做有界 / 合法 JSON 校验，绝不执行布局表达式。</para>
/// </summary>
public sealed class ReportPrintTemplateBindingRequest
{
    /// <summary>打印模板族标识（必须命中受控族目录）。</summary>
    public string FamilyKey { get; set; } = string.Empty;

    /// <summary>已保存打印模板 Id（必须属于该族）。</summary>
    public long TemplateId { get; set; }

    /// <summary>请求输出的旧字段键（有序、去重、保持顺序；空 = 模板保存顺序或族默认全列）。</summary>
    public IReadOnlyList<string> FieldKeys { get; set; } = new List<string>();

    /// <summary>可选布局 JSON（提供时须有界且为合法 JSON；否则复用模板已保存布局）。</summary>
    public string? LayoutJson { get; set; }
}

/// <summary>绑定结果：模板 → 受控数据集键 + 有序受控列（按请求顺序）。</summary>
public sealed class ReportPrintTemplateBindingDto
{
    public long TemplateId { get; set; }
    public string FamilyKey { get; set; } = string.Empty;
    public string DatasetKey { get; set; } = string.Empty;

    /// <summary>绑定后的受控列（与请求字段顺序一致；绝不含未知 / 未授权列）。</summary>
    public IReadOnlyList<ReportPrintTemplateFieldAlias> BoundColumns { get; set; } = new List<ReportPrintTemplateFieldAlias>();
}
