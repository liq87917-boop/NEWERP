using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 样品管理控制器（阶段 2 新增）
/// 维护：打样 / 寄样 / 借样 / 客户来样的登记、样品费与结算、寄出信息与客户反馈结果
/// </summary>
[ApiController]
[Route("api/crm/samples")]
[Authorize]
public class SampleController : BaseCrudController<Sample>
{
    public SampleController(IGenericService<Sample> service) : base(service) { }
}
