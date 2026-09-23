using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 出口单证台账控制器（单证中心，阶段 2 新增）
/// 维护：报关单 / 装箱单 / 商业发票 CI / 形式发票 PI / 产地证 CO·Form A·Form E / 提单 B/L 等
///       单证的登记、状态跟踪（待制作 → 已制作 → 已提交客户 → 已使用）与份数管理
/// </summary>
[ApiController]
[Route("api/trade/documents")]
[Authorize]
public class TradeDocumentController : BaseCrudController<TradeDocument>
{
    public TradeDocumentController(IGenericService<TradeDocument> service) : base(service) { }
}
