using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 迁移 parity 比对证据服务（ERP-332 Stage 2）接缝：对一条旧报表，在同一有界隔离夹具上
/// （当前账号菜单 + 数据范围、统一日期 / 分页参数、既有执行预算 + 取消令牌）分别运行旧来源接缝与
/// 通用数据集预览，喂给四维比较器（数据粒度 / 币种单位 / 权限 / 输出语义），产出
/// <see cref="ReportMigrationParityEvidenceDto"/>。无真实比对证据返回 null（fail closed）。
/// <para>只读、无写库、绝不执行任意 SQL、绝不新增实体 / 权限 / 环境变量。</para>
/// </summary>
public interface IReportMigrationParityEvidenceService
{
    /// <summary>返回指定旧报表的比对证据；无证据（未知键 / 未绑定来源 / 未授权 / 映射或比较分歧）返回 null（fail closed）。</summary>
    Task<ReportMigrationParityEvidenceDto?> GetEvidenceAsync(
        string legacyKey, long userId, CancellationToken cancellationToken = default);
}
