namespace ERP.Application.DTOs;

/// <summary>
/// 通用报表配置捆绑预设（ERP-310 Stage 2）的平台常量：把通用多节模板的共享参数词汇与
/// 物化 readiness 状态限定为有限取值。参数 / 状态只允许本类中的有限枚举，绝不接受任意 SQL、
/// 字段名、标识符或联接语义。
/// </summary>
public static class ReportConfigurationBundlePresetConstants
{
    // ==================== 共享参数键（有限、类型化） ====================

    /// <summary>客户参数：正整数客户 Id，按 eq 绑定到各节已授权的客户字段。</summary>
    public const string ParameterCustomer = "customer";

    /// <summary>日期参数：可选日期区间（起 / 止），按 between / gte / lte 绑定到各节已授权的日期字段。</summary>
    public const string ParameterDate = "date";

    /// <summary>状态参数：有限状态文本，按 eq 绑定到各节已授权的状态字段。</summary>
    public const string ParameterStatus = "status";

    // ==================== 参数类型（有限、与目录 DataType 对齐） ====================

    public const string ParameterTypeNumber = ReportConfigurationConstants.TypeNumber;
    public const string ParameterTypeDate = ReportConfigurationConstants.TypeDate;
    public const string ParameterTypeText = ReportConfigurationConstants.TypeText;

    // ==================== 物化 readiness（有限、单调、绝不 presence-only parity-passed） ====================

    /// <summary>菜单未授权：不列出（fail closed）。</summary>
    public const string ReadinessPending = "pending";

    /// <summary>菜单已授权，但部分节的受控数据集尚未就绪。</summary>
    public const string ReadinessDatasetReady = "dataset-ready";

    /// <summary>菜单与全部节的受控数据集均就绪，可物化。</summary>
    public const string ReadinessPresetReady = "preset-ready";

    // ==================== 前置条件类别（有限） ====================

    public const string PrerequisiteMenu = "menu";
    public const string PrerequisiteDataset = "dataset";

    /// <summary>单个预设最多声明的共享参数数（3：customer / date / status）。</summary>
    public const int MaxParameters = 3;

    /// <summary>状态参数最大长度。</summary>
    public const int MaxStatusLength = 100;
}

/// <summary>捆绑预设的共享参数元数据（只读、有限）：键 / 类型 / 中文标签 / 是否必填。</summary>
public sealed class ReportConfigurationBundlePresetParameterDto
{
    /// <summary>参数键（customer / date / status）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>参数类型（number / date / text，与目录 DataType 对齐）。</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>参数中文标签。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>是否必填（物化时必须提供且合法）。</summary>
    public bool Required { get; set; }
}

/// <summary>捆绑预设的单节元数据（只读、预览可见）：节键 / 原始节标题 / 数据集与原始列选择。</summary>
public sealed class ReportConfigurationBundlePresetSectionDto
{
    /// <summary>节键（稳定，物化时定位模板）。</summary>
    public string SectionKey { get; set; } = string.Empty;

    /// <summary>原始节标题（与旧路由分区标题同源）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>受控数据集键。</summary>
    public string DatasetKey { get; set; } = string.Empty;

    /// <summary>受控数据集中文标签。</summary>
    public string DatasetLabel { get; set; } = string.Empty;

    /// <summary>原始列选择（有序字段键，物化后作为该节定义字段）。</summary>
    public List<string> FieldKeys { get; set; } = new();
}

/// <summary>捆绑预设的独立前置条件（menu / dataset）：每项单独声明是否满足，绝不整体 presence-only。</summary>
public sealed class ReportConfigurationBundlePresetPrerequisiteDto
{
    /// <summary>前置条件类别（menu / dataset）。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>前置条件键（菜单编码或数据集键）。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>前置条件中文标签。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>当前账号是否满足该前置条件（运行时重检，fail closed）。</summary>
    public bool Satisfied { get; set; }
}

/// <summary>捆绑预设模板（ERP-310 Stage 2，只读）：通用多节模板，物化后成为当前用户私有定义并复用既有捆绑引擎。</summary>
public sealed class ReportConfigurationBundlePresetDto
{
    /// <summary>稳定预设键（物化端点路径参数）。</summary>
    public string PresetKey { get; set; } = string.Empty;

    /// <summary>迁移登记册条目键（legacy key）。</summary>
    public string LegacyKey { get; set; } = string.Empty;

    /// <summary>预设模板名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>物化 readiness（dataset-ready / preset-ready；pending 不列出）。绝不出现 presence-only parity-passed。</summary>
    public string Readiness { get; set; } = ReportConfigurationBundlePresetConstants.ReadinessPending;

    /// <summary>有限共享参数元数据（有序）。</summary>
    public List<ReportConfigurationBundlePresetParameterDto> Parameters { get; set; } = new();

    /// <summary>有序节模板元数据（顺序即输出顺序）。</summary>
    public List<ReportConfigurationBundlePresetSectionDto> Sections { get; set; } = new();

    /// <summary>独立前置条件清单（菜单 + 数据集，逐项声明满足状态）。</summary>
    public List<ReportConfigurationBundlePresetPrerequisiteDto> Prerequisites { get; set; } = new();
}

/// <summary>捆绑预设物化请求（客户端提交有限参数值；所有者 Id 由服务端认证注入，客户端不得提交）。</summary>
public sealed class ReportConfigurationBundlePresetMaterializeRequest
{
    /// <summary>客户 Id（仅 customer 参数；正整数）。</summary>
    public long? CustomerId { get; set; }

    /// <summary>日期起（仅 date 参数；含当日）。</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>日期止（仅 date 参数；含当日）。</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>状态（仅 status 参数；有限文本）。</summary>
    public string? Status { get; set; }
}

/// <summary>捆绑预设物化结果（ERP-310 Stage 2）：返回物化后的有界捆绑请求（引用新私有草稿）与节元数据。</summary>
public sealed class ReportConfigurationBundlePresetMaterializationDto
{
    /// <summary>预设键。</summary>
    public string PresetKey { get; set; } = string.Empty;

    /// <summary>迁移登记册条目键（legacy key）。</summary>
    public string LegacyKey { get; set; } = string.Empty;

    /// <summary>预设模板名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>物化后的有界捆绑请求（可直接提交给既有捆绑预览 / 导出端点）。</summary>
    public ReportConfigurationBundleRequest Bundle { get; set; } = new();

    /// <summary>物化后的节元数据（与 <see cref="Bundle"/> 顺序一致）。</summary>
    public List<ReportConfigurationBundlePresetSectionDto> Sections { get; set; } = new();
}
