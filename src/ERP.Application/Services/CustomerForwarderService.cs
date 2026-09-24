using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客户「指定货代」引用服务（ERP-036）。职责：
/// <list type="number">
/// <item><b>写入校验与快照</b>（<see cref="ApplyAsync"/>）：新增 / 更换引用必须是「其他资料」中
/// <c>InfoType = Forwarder</c> 且启用、未删除的字典项；名称快照一律按字典项由服务端写回，
/// 客户端提交的自由文本不被采信（不允许「随便填一个货代名」）；</item>
/// <item><b>历史引用保护</b>：引用与库中一致且字典项后来被删除 / 停用 / 改类型时，
/// 不清空、不报错、不迁移，保留库中名称快照（历史客户资料照常可读）；</item>
/// <item><b>读取标注</b>（<see cref="AnnotateAsync"/>）：列表 / 详情读取时标注
/// <see cref="BaseCustomer.ForwarderAvailable"/>，不可用引用在界面上显式提示；</item>
/// <item><b>下拉选项</b>（<see cref="LoadOptionsAsync"/>）：只返回当前可选用的货代字典项
/// （未删除、已启用、类型 Forwarder），保证「已停用 / 已删除」的字典项无法被新选中。</item>
/// </list>
/// <para>边界：除客户资料自身的两列外不写任何数据 —— 不改动订舱 ContainerBooking、装柜、报关与费用单据，
/// 也不调用任何外部货代系统；清空货代同样不触达其他单据。</para>
/// </summary>
public static class CustomerForwarderService
{
    /// <summary>
    /// 校验并落地客户资料的指定货代引用。
    /// </summary>
    /// <param name="db">数据访问上下文（只读字典项）</param>
    /// <param name="customer">即将新增 / 更新的客户实体（含客户端提交的 ForwarderId）</param>
    /// <param name="stored">库中已存在的客户资料（新增时为 <c>null</c>）；用于识别「引用未变更」的历史引用</param>
    /// <remarks>
    /// <see cref="BaseCustomer.ForwarderAvailable"/> 是 <c>[NotMapped]</c> 的读取标注，这里一并按服务端口径重算，
    /// 保证写入响应与后续读取的标注一致，且客户端提交的该标记一律不被采信。
    /// </remarks>
    public static async Task ApplyAsync(IErpDbContext db, BaseCustomer customer, BaseCustomer? stored)
    {
        var requested = customer.ForwarderId;

        // 1. 未指定 / 显式清空（0 与 null 同义）：两个字段一并清空，不影响任何其他单据
        if (!requested.HasValue || requested.Value <= 0)
        {
            customer.ForwarderId = null;
            customer.ForwarderName = string.Empty;
            customer.ForwarderAvailable = true;
            return;
        }

        var entry = await db.BaseOtherInfos.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == requested.Value);
        var unchanged = stored is not null && stored.ForwarderId == requested;

        // 2. 引用未变更（老客户直接保存其他字段）：不迁移、不清空历史引用
        if (unchanged)
        {
            if (entry is not null && CustomerForwarderRules.IsSelectableForwarder(entry))
            {
                // 字典项仍可选用 → 按字典项最新名称刷新快照（字典改名后客户资料同步）
                customer.ForwarderName = CustomerForwarderRules.SnapshotName(entry);
                customer.ForwarderAvailable = true;
            }
            else
            {
                // 字典项已删除 / 停用 / 改类型 → 保留库中快照原样，绝不允许客户端借此改写历史名称
                customer.ForwarderName = stored!.ForwarderName;
                customer.ForwarderAvailable = false;   // 与读取标注一致：历史名称照常显示，但不可再新选中
            }
            return;
        }

        // 3. 新增 / 更换引用：必须是可选用字典项，否则拒绝
        CustomerForwarderRules.EnsureSelectableForwarder(entry, requested.Value);
        customer.ForwarderName = CustomerForwarderRules.SnapshotName(entry!);
        customer.ForwarderAvailable = true;
    }

    /// <summary>
    /// 读取「指定货代」下拉可选项（<b>只读，不写库</b>）：只返回未删除、已启用、类型为 Forwarder 的字典项，
    /// 与写入校验口径（<see cref="CustomerForwarderRules.EnsureSelectableForwarder"/>）完全一致，
    /// 因此已停用 / 已删除 / 类型不符的字典项不会出现在下拉中，无法被新指定。
    /// <para>历史引用（客户资料上已有的名称快照）不依赖本方法：即使字典项已不可用，
    /// 客户列表 / 详情仍按 <see cref="BaseCustomer.ForwarderName"/> 照常显示并标注不可用。</para>
    /// </summary>
    public static async Task<List<OtherInfoOptionDto>> LoadOptionsAsync(IErpDbContext db)
    {
        // 先由数据库收敛到「未删除且启用」的候选集，再在内存中按类型口径判定
        // （类型比较忽略大小写与首尾空白，与写入校验使用同一规则，避免大小写差异导致下拉缺项）
        var candidates = await db.BaseOtherInfos.AsNoTracking()
            .Where(o => !o.IsDeleted && o.Status == 1)
            .ToListAsync();

        return candidates
            .Where(o => CustomerForwarderRules.IsSelectableForwarder(o))
            .OrderBy(o => o.SortOrder)
            .ThenBy(o => o.InfoName, StringComparer.Ordinal)
            .Select(o => new OtherInfoOptionDto(
                o.Id, o.InfoType, o.InfoCode, CustomerForwarderRules.SnapshotName(o), Selectable: true))
            .ToList();
    }

    /// <summary>
    /// 读取标注（<b>不写库</b>）：为列表 / 详情返回的客户资料标注指定货代引用是否仍可选用。
    /// 一次查询解析全部引用的字典项，避免逐行查询。
    /// </summary>
    public static async Task AnnotateAsync(IErpDbContext db, IEnumerable<BaseCustomer> customers)
    {
        var list = customers as IList<BaseCustomer> ?? customers.ToList();
        if (list.Count == 0) return;

        var ids = list.Where(c => c.ForwarderId.HasValue && c.ForwarderId.Value > 0)
            .Select(c => c.ForwarderId!.Value)
            .Distinct()
            .ToList();

        var entries = ids.Count == 0
            ? new List<BaseOtherInfo>()
            : await db.BaseOtherInfos.AsNoTracking().Where(o => ids.Contains(o.Id)).ToListAsync();
        var byId = entries.ToDictionary(o => o.Id);

        foreach (var customer in list)
        {
            if (!customer.ForwarderId.HasValue || customer.ForwarderId.Value <= 0)
            {
                // 未指定货代：字段保持空（不写「无」这类占位值），标注为可用（无可提示的失效引用）
                customer.ForwarderId = null;
                customer.ForwarderAvailable = true;
                continue;
            }

            customer.ForwarderAvailable =
                byId.TryGetValue(customer.ForwarderId.Value, out var entry)
                && CustomerForwarderRules.IsSelectableForwarder(entry);
        }
    }
}
