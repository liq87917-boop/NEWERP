using ERP.Domain.Enums;

namespace ERP.Application.Interfaces;

/// <summary>
/// 单据号生成服务接口
/// </summary>
public interface IDocumentNumberService
{
    /// <summary>
    /// 根据单据类型生成单据号（格式：前缀 + 日期 + 流水号，受单据号规则表控制）
    /// </summary>
    Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null);
}
