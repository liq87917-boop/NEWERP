using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 私有报表配置生命周期状态（ERP-260 Stage 1）：草稿 / 已发布。
/// <para>草稿 = 存在尚未发布的工作定义（或工作定义与最近已发布修订不一致）；
/// 已发布 = 当前工作定义与最近一次已发布修订快照一致。</para>
/// </summary>
public enum ReportConfigurationStatus
{
    /// <summary>草稿（可编辑工作定义）</summary>
    Draft = 0,

    /// <summary>已发布（当前工作定义已由不可变修订快照固定）</summary>
    Published = 1,
}

/// <summary>
/// 用户私有报表配置（ERP-260 Stage 1）：个人工作区保存的报表定义（草稿 / 已发布），
/// 只属于 <see cref="OwnerUserId"/> 一位已认证用户；不提供角色共享 / 公共模板（后续 Stage 1 任务再扩展）。
/// <para>定义（<see cref="DefinitionJson"/>）在保存前已按当前账号的已授权数据集目录做有界校验，
/// 只描述「选择哪些白名单字段 + 有界类型化筛选 + 支持的分组 / 聚合 + 展示」，不含任意 SQL / 脚本 /
/// 用户自选表或联接语义。</para>
/// <para>并发口径：<see cref="Version"/> 为乐观「预期版本」令牌，每次保存 / 发布 / 恢复 / 重命名 /
/// 复制 / 软删除都会 +1；客户端必须提交上次读到的 expectedVersion，不一致即拒绝陈旧写入。
/// （<see cref="BaseEntity.RowVersion"/> 仍作为数据库级行版本兜底。）</para>
/// </summary>
public class ReportConfiguration : BaseEntity
{
    /// <summary>所有者用户 Id（服务端认证身份，客户端不得提交）</summary>
    public long OwnerUserId { get; set; }

    /// <summary>配置名称（1 ~ 200 字符，服务端规范化）</summary>
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>数据集键（必须匹配当前账号已授权数据集，见 <c>ReportConfigurationConstants</c>）</summary>
    [Required, MaxLength(50)]
    public string DatasetKey { get; set; } = string.Empty;

    /// <summary>有界定义 JSON（保存前已按当前目录校验；nvarchar(max) 列）</summary>
    [Required]
    public string DefinitionJson { get; set; } = string.Empty;

    /// <summary>schema 版本（与保存时的 <c>ReportConfigurationRules.CurrentSchemaVersion</c> 一致）</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>生命周期状态（草稿 / 已发布）</summary>
    public ReportConfigurationStatus Status { get; set; } = ReportConfigurationStatus.Draft;

    /// <summary>
    /// 乐观「预期版本」并发令牌：每次变更 +1，客户端提交 expectedVersion 与其比对，
    /// 不一致即拒绝陈旧写入（应用层先行拒绝，数据库层以并发令牌兜底）。
    /// </summary>
    [ConcurrencyCheck]
    public int Version { get; set; }

    /// <summary>最近一次已发布修订版本号（0 = 从未发布）</summary>
    public int CurrentPublishedVersion { get; set; }

    /// <summary>全部已发布修订快照（只追加、不可变；父配置软删除时一并软删除）</summary>
    public ICollection<ReportConfigurationRevision> Revisions { get; set; } = new List<ReportConfigurationRevision>();
}

/// <summary>
/// 私有报表配置的不可变发布修订快照（ERP-260 Stage 1）：每次发布 / 恢复都追加一条
/// 「发布那一刻被固定的、已通过当前目录校验的定义快照」，<see cref="Version"/> 在同一配置内单调递增。
/// <para>口径：草稿编辑<strong>不</strong>静默替换已发布修订；发布固定精确的已校验快照；
/// 恢复历史版本 = 把旧修订的定义再次发布成一条<strong>新</strong>修订（旧修订保持原样）。
/// 本表只追加，不提供修改与删除接口。</para>
/// </summary>
public class ReportConfigurationRevision : BaseEntity
{
    /// <summary>所属报表配置 Id（引用 <see cref="ReportConfiguration"/>）</summary>
    public long ReportConfigurationId { get; set; }

    /// <summary>所有者用户 Id（发布时的服务端认证身份快照；客户端不得提交）</summary>
    public long OwnerUserId { get; set; }

    /// <summary>发布版本号（同一配置内从 1 起单调递增）</summary>
    public int Version { get; set; }

    /// <summary>发布时的配置名称快照</summary>
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>发布时的数据集键快照</summary>
    [Required, MaxLength(50)]
    public string DatasetKey { get; set; } = string.Empty;

    /// <summary>被固定的有界定义 JSON 快照（nvarchar(max) 列）</summary>
    [Required]
    public string DefinitionJson { get; set; } = string.Empty;

    /// <summary>schema 版本快照</summary>
    public int SchemaVersion { get; set; }

    /// <summary>发布时间</summary>
    public DateTime PublishedAt { get; set; } = DateTime.Now;

    /// <summary>发布人 Id（服务端认证身份）</summary>
    public long PublishedBy { get; set; }

    /// <summary>所属配置（导航；关系为级联删除，软删除由服务端统一处理）</summary>
    public ReportConfiguration ReportConfiguration { get; set; } = null!;
}
