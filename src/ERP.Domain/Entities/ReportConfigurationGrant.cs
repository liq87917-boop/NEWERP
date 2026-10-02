using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 通用报表配置平台（ERP-265 Stage 1）只读共享授权：所有者以「被授权人用户 Id + 固定发布修订版本号」
/// 授予单个不可变已发布快照的只读访问。粒度精准（recipient + configuration + revision），
/// 不授予草稿、其它修订 / 历史，也不授予所有者编辑权；不支持角色级 / 组织级授权，不提供用户目录检索。
/// <para>并发口径：<see cref="Version"/> 为乐观「预期版本」令牌，每次授权 / 变更 pin / 撤销都 +1；
/// 客户端必须提交上次读到的 expectedVersion，不一致即拒绝陈旧写入
/// （<see cref="BaseEntity.RowVersion"/> 仍作为数据库级行版本兜底）。</para>
/// <para>审计：继承 <see cref="BaseEntity"/>（CreatedAt/CreatedBy/UpdatedAt/UpdatedBy/IsDeleted/RowVersion）；
/// <see cref="GrantedByUserId"/> 额外留痕实际授权人（所有者）。</para>
/// </summary>
public class ReportConfigurationGrant : BaseEntity
{
    /// <summary>被授权人用户 Id（现有激活 ERP 用户；客户端按 Id 精确指定，不暴露用户目录）</summary>
    public long RecipientUserId { get; set; }

    /// <summary>被授权的私有报表配置 Id（引用 <see cref="ReportConfiguration"/>）</summary>
    public long ReportConfigurationId { get; set; }

    /// <summary>被固定的不可变发布修订版本号（所有者再次发布不会改变此 pin；改变 pin 是显式所有者动作）</summary>
    public int RevisionVersion { get; set; }

    /// <summary>授权人（所有者）用户 Id（服务端认证身份快照）</summary>
    public long GrantedByUserId { get; set; }

    /// <summary>乐观「预期版本」并发令牌：每次变更 +1，客户端回传 expectedVersion 比对</summary>
    [ConcurrencyCheck]
    public int Version { get; set; }

    /// <summary>所属配置导航（软删除由服务端统一处理，关系级联删除仅作硬删除兜底）</summary>
    public ReportConfiguration ReportConfiguration { get; set; } = null!;
}
