using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 旧报表实际产物来源接缝（ERP-333 Stage 2）：把有界旧报表键映射到其既有规范导出器（旧 Excel / PDF 导出），
/// 产出真实旧产物字节，供 parity 输出语义比对消费。
/// <para>只读、无写库；每次调用都按当前账号重新校验菜单与行 / 数据范围授权（fail closed），并遵守既有分页 /
/// 日期范围上限与取消令牌。实现方绝不使用通用平台导出器充当旧导出器，绝不执行任意 SQL（除既有受控读取）。</para>
/// </summary>
public interface ILegacyReportArtifactSource
{
    /// <summary>
    /// 读取指定旧报表的真实产物字节（Excel / PDF）。仅支持有界有限集合内的旧报表键；空白 / 畸形 / 未知键在
    /// 打开任何查询之前拒绝（或返回 null，fail closed）。旧导出不可用、缺字体、渲染失败或超限时返回 null
    /// （绝不猜测、绝不回退为通用导出器、绝不返回 caller proof boolean）。
    /// </summary>
    Task<LegacyReportArtifactBytesDto?> ReadArtifactsAsync(
        LegacyReportSourceRequest request, CancellationToken cancellationToken = default);
}
