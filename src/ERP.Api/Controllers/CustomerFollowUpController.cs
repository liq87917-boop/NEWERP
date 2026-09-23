using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 客户跟进记录控制器（CRM，阶段 2 新增）
/// 维护：与客户的每一次接触（电话 / 微信 / 邮件 / 拜访 / 展会 / 寄样）、跟进结果与下次跟进日期
/// </summary>
[ApiController]
[Route("api/crm/follow-ups")]
[Authorize]
public class CustomerFollowUpController : BaseCrudController<CustomerFollowUp>
{
    public CustomerFollowUpController(IGenericService<CustomerFollowUp> service) : base(service) { }
}
