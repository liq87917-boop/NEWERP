using ERP.Domain.Entities;

namespace ERP.Application.Services;

/// <summary>
/// 报价单「行号 / 金额 / 合计 / 有效期」的唯一权威整理逻辑（ERP-035 从
/// <c>QuotationController</c> 抽到应用层，新增 / 修改 / **创建新版本** 三条路径共用同一份实现）：
/// 行号按数组顺序重排为 1..n、金额按「数量 × 单价」并按单据币种精度重算、合计与折人民币由服务端复核，
/// 客户端提交的行号 / 金额 / 合计一律不被信任。
/// <para>ERP-470：实现整体下沉到 <see cref="QuotationCommercialValidationRules"/>（币种 / 汇率 / 数量 / 单价 /
/// 金额精度的唯一权威契约），本类保留为报价单写入路径的稳定入口 —— 校验与整理**同一份**实现，
/// 不存在第二套算法；校验失败时**不改写任何字段**（被拒绝的请求零变更）。</para>
/// </summary>
public static class QuotationLineRules
{
    /// <summary>
    /// 行号 / 金额 / 合计 / 有效期统一整理（后端复核，防止前端篡改合计）：
    /// 先按 <see cref="QuotationCommercialValidationRules"/> 校验币种 / 汇率 / 逐行数量 · 单价，
    /// 再按币种精度（JPY 等 0 位、其余 2 位）服务端复算行金额 / 合计 / 折人民币。
    /// 允许显式支持的空草稿；软删除明细行注入与非法商业条款一律按受控业务错误拒绝。
    /// </summary>
    public static void Normalize(Quotation e)
        => QuotationCommercialValidationRules.NormalizeIncomingDraft(e);
}

