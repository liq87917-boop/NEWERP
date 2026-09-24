using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 装柜清单多客户参与方服务（ERP-041）。职责：
/// <list type="number">
/// <item><b>维护</b>（<see cref="CreateAsync"/> / <see cref="UpdateAsync"/> / <see cref="SetPrimaryAsync"/> /
/// <see cref="SetStatusAsync"/> / <see cref="DeleteAsync"/>）：只写参与方子表
/// <c>ContainerLoadingListParticipants</c>，服务端统一规范化文本与数值，校验客户引用的存在性、启用状态与重复性；</item>
/// <item><b>兼容客户字段同步</b>：只有「显式指定主参与方」时才把参与方客户写入装柜清单原有的
/// <c>CustomerId</c>（兼容主客户字段），未维护参与方的历史清单原样沿用，读取路径不写库；</item>
/// <item><b>读取</b>（<see cref="ListAsync"/>）：按装柜清单返回**有界**列表，默认含停用参与方（历史可读）
/// 并显式标注可用性；客户信息按批次一次解析，不做逐行查询；</item>
/// <item><b>列表标注</b>（<see cref="AnnotateAsync"/>）：为装柜清单列表 / 详情补写参与方条数、主参与方与
/// 「历史单客户视图」标记。</item>
/// </list>
/// <para>边界（重要）：除参与方子表自身与装柜清单的兼容客户字段外不写任何数据 ——
/// <strong>不</strong>按数量 / 体积 / 金额分摊费用、<strong>不</strong>生成费用单或结算记录，
/// <strong>不</strong>改写装柜明细的数量 / 箱数 / 重量 / 体积、柜号、订柜跟踪值、
/// 单证 <c>TradeDocuments</c>、库存 <c>Stocks</c> / 库存流水 <c>StockMovements</c>，
/// 也不改写客户主数据与客户名称历史快照。</para>
/// </summary>
public static class ContainerLoadingParticipantService
{
    /// <summary>并发 / 唯一索引兜底时的对外文案（清单 + 客户重复或主参与方冲突）</summary>
    public const string DuplicateConflictMessage = "该装柜清单的参与方正在被其他请求写入（客户重复或主参与方冲突），请重试";

    /// <summary>
    /// 校验装柜清单存在（未删除）并返回实体；参与方必须挂在既有装柜清单下。
    /// <para>读取路径也用本方法，因此已审核 / 已作废等状态的历史清单参与方仍照常可读（不做可维护性拦截）。</para>
    /// </summary>
    public static async Task<ContainerLoadingList> EnsureLoadingListAsync(IErpDbContext db, long loadingListId)
    {
        if (loadingListId <= 0)
            throw BusinessException.InvalidParameter("装柜清单 Id 不合法");

        return await db.ContainerLoadingLists
            .FirstOrDefaultAsync(o => o.Id == loadingListId && !o.IsDeleted)
            ?? throw BusinessException.NotFound($"装柜清单（Id={loadingListId}）不存在或已删除，不能维护参与方");
    }

    /// <summary>
    /// 可维护性：只有**已作废（Cancelled）**的装柜清单禁止维护参与方（作废柜的客户归属必须冻结）。
    /// <para>其余状态（待提交 / 已提交 / 已审核 / 已完成）仍可维护：参与方是**操作性的客户归属清单**，
    /// 拼柜客户在出运前后都可能需要补充或纠正；它唯一会写到的装柜清单字段是任务明确要求的
    /// 兼容主客户字段 <c>CustomerId</c>，且只在「显式指定主参与方」时同步，不改写柜号、明细、
    /// 跟踪值、单证、费用、库存与客户主数据。</para>
    /// </summary>
    private static void EnsureMaintainable(ContainerLoadingList loadingList)
    {
        if (loadingList.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict(
                $"装柜清单「{loadingList.LoadingListNo}」已作废，不能维护参与方（历史参与方仍可只读查看）");
    }

    /// <summary>
    /// 读取某装柜清单的参与方（<b>只读，不写库</b>）：默认包含停用参与方（历史可读），
    /// <paramref name="activeOnly"/> 为真时只返回启用中的参与方；结果按上限
    /// <see cref="ContainerLoadingParticipantRules.MaxParticipantsPerLoadingList"/> 收敛，保证视图有界；
    /// 排序按排序号 → Id，稳定且与主参与方判定无关（主参与方由 IsPrimary 显式标记）。
    /// </summary>
    public static async Task<List<ContainerLoadingParticipantDto>> ListAsync(
        IErpDbContext db, long loadingListId, bool activeOnly = false,
        int take = ContainerLoadingParticipantRules.MaxParticipantsPerLoadingList)
    {
        var loadingList = await EnsureLoadingListAsync(db, loadingListId);

        var bound = take <= 0 ? ContainerLoadingParticipantRules.MaxParticipantsPerLoadingList
            : Math.Min(take, ContainerLoadingParticipantRules.MaxParticipantsPerLoadingList);

        var query = db.ContainerLoadingListParticipants.AsNoTracking()
            .Where(x => x.LoadingListId == loadingListId && !x.IsDeleted);
        if (activeOnly)
            query = query.Where(x => x.Status == ContainerLoadingParticipantRules.ActiveStatus);

        var rows = await query
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Take(bound)
            .ToListAsync();

        return await MapAsync(db, loadingList, rows);
    }

    /// <summary>
    /// 新增参与方（只写参与方子表，必要时同步装柜清单的兼容客户字段）：
    /// 客户必须存在、未删除且启用；同一清单内同一客户不重复（含停用行）；
    /// <c>IsPrimary = true</c> 时该清单不得已有启用主参与方（更换主参与方请走「设为主参与方」），
    /// 且兼容客户字段会在同一批保存中同步为该客户。
    /// </summary>
    public static async Task<ContainerLoadingParticipantDto> CreateAsync(
        IErpDbContext db, long loadingListId, ContainerLoadingParticipantSaveDto dto)
    {
        var loadingList = await EnsureLoadingListAsync(db, loadingListId);
        if (dto is null)
            throw BusinessException.InvalidParameter("参与方数据不能为空");
        EnsureMaintainable(loadingList);

        var customer = await LoadCustomerAsync(db, dto.CustomerId);
        ContainerLoadingParticipantRules.EnsureCustomerSelectable(customer, dto.CustomerId);

        var status = ContainerLoadingParticipantRules.NormalizeStatus(dto.Status);
        var primary = dto.IsPrimary ?? false;
        ContainerLoadingParticipantRules.EnsurePrimaryCompatible(primary, status);

        var siblings = await LoadSiblingsAsync(db, loadingListId);
        ContainerLoadingParticipantRules.EnsureParticipantBound(siblings.Count);
        ContainerLoadingParticipantRules.EnsureListCustomerUnique(siblings, customer.Id);

        // 主参与方唯一性只针对「启用中的主参与方」判定：停用行即使带标记也不占用主参与方位
        if (status == ContainerLoadingParticipantRules.ActiveStatus)
            ContainerLoadingParticipantRules.EnsurePrimaryUnique(siblings, primary);

        var entity = new ContainerLoadingListParticipant
        {
            LoadingListId = loadingListId,
            CustomerId = customer.Id,
            CustomerCode = ContainerLoadingParticipantRules.SnapshotCustomerCode(customer),
            CustomerName = ContainerLoadingParticipantRules.SnapshotCustomerName(customer),
            IsPrimary = primary,
            Status = status,
            SortOrder = ContainerLoadingParticipantRules.NormalizeSortOrder(dto.SortOrder),
            Remark = ContainerLoadingParticipantRules.NormalizeRemark(dto.Remark)
        };

        db.ContainerLoadingListParticipants.Add(entity);

        // 兼容客户字段同步：只在「显式指定主参与方」时发生，并与参与方同行落库（同一次 SaveChanges）
        if (primary)
            loadingList.CustomerId = entity.CustomerId;

        await SaveAsync(db);
        return (await MapAsync(db, loadingList, new List<ContainerLoadingListParticipant> { entity })).Single();
    }

    /// <summary>
    /// 修改参与方（只写参与方子表，必要时同步装柜清单的兼容客户字段）：字段重新规范化并重新做重复判定。
    /// <para>引用口径：<b>新选或更换</b>的客户必须是可用客户（存在、未删除、启用）；
    /// 引用<b>未变更</b>时保留历史编码 / 名称快照，即使客户后来被停用或改名也不会被静默改写。</para>
    /// <para>主参与方口径：<c>IsPrimary = true</c> 视为显式的「设为主参与方」（先释放旧主参与方再置新主并同步兼容字段）；
    /// 直接取消主参与方（<c>IsPrimary = false</c>）会被拒绝 —— 主参与方只能改指他人，
    /// 或随「停用 / 删除」在清单不再有启用参与方时回退为历史单客户视图。</para>
    /// </summary>
    public static async Task<ContainerLoadingParticipantDto> UpdateAsync(
        IErpDbContext db, long loadingListId, long id, ContainerLoadingParticipantSaveDto dto)
    {
        var loadingList = await EnsureLoadingListAsync(db, loadingListId);
        if (dto is null)
            throw BusinessException.InvalidParameter("参与方数据不能为空");
        EnsureMaintainable(loadingList);

        var entity = await FindAsync(db, loadingListId, id);
        var siblings = await LoadSiblingsAsync(db, loadingListId);

        if (dto.CustomerId <= 0)
            throw BusinessException.InvalidParameter("参与客户不能为空");

        var status = ContainerLoadingParticipantRules.NormalizeStatus(dto.Status);
        var requestedPrimary = dto.IsPrimary;
        var customerChanged = dto.CustomerId != entity.CustomerId;

        // 主参与方不能被静默取消；停用主参与方必须先改指其他启用参与方
        if (requestedPrimary == false && entity.IsPrimary)
            throw BusinessException.RuleConflict(
                "主参与方不能被直接取消：请先把其他启用参与方设为主参与方"
                + "（或停用 / 删除本清单的全部参与方，回退为历史单客户视图）");
        if (requestedPrimary == true && status != ContainerLoadingParticipantRules.ActiveStatus)
            throw BusinessException.InvalidParameter("只有启用状态的参与方可以设为主参与方，请先启用该参与方");
        if (status != ContainerLoadingParticipantRules.ActiveStatus)
            ContainerLoadingParticipantRules.EnsurePrimaryRemovable(siblings, entity, "停用");

        // 全部校验通过后才开始写入，避免校验失败留下半更新
        BaseCustomer? customer = null;
        if (customerChanged)
        {
            customer = await LoadCustomerAsync(db, dto.CustomerId);
            ContainerLoadingParticipantRules.EnsureCustomerSelectable(customer, dto.CustomerId);
            ContainerLoadingParticipantRules.EnsureListCustomerUnique(siblings, customer.Id, entity.Id);
        }

        if (requestedPrimary == true && !entity.IsPrimary)
        {
            var promotableId = customerChanged ? dto.CustomerId : entity.CustomerId;
            var promotable = customerChanged ? customer : await LoadCustomerAsync(db, promotableId);
            ContainerLoadingParticipantRules.EnsureCustomerPromotable(promotable, promotableId);
        }

        if (customerChanged)
        {
            entity.CustomerId = customer!.Id;
            entity.CustomerCode = ContainerLoadingParticipantRules.SnapshotCustomerCode(customer);
            entity.CustomerName = ContainerLoadingParticipantRules.SnapshotCustomerName(customer);
        }

        entity.Status = status;
        entity.SortOrder = ContainerLoadingParticipantRules.NormalizeSortOrder(dto.SortOrder);
        entity.Remark = ContainerLoadingParticipantRules.NormalizeRemark(dto.Remark);
        if (status != ContainerLoadingParticipantRules.ActiveStatus)
            entity.IsPrimary = false;                        // 停用释放主标记（与过滤唯一索引口径一致）

        await SaveAsync(db);

        if (requestedPrimary == true && !entity.IsPrimary)
            await ApplyPrimaryAsync(db, loadingList, entity, await LoadSiblingsAsync(db, loadingListId));

        return (await MapAsync(db, loadingList, new List<ContainerLoadingListParticipant> { entity })).Single();
    }

    /// <summary>
    /// 显式设置「主参与方」（只写参与方子表 + 兼容客户字段）：
    /// 只有启用中的参与方可以被设为主参与方，且其客户必须仍然可用；
    /// 切换时先释放同清单内旧的启用主参与方（一次批量写回），再置新主参与方并把清单兼容客户字段同步为该客户
    /// —— 与列表顺序无关，也不依赖「最后提交者胜出」；同清单主参与方唯一性由服务端判定 + 过滤唯一索引双重兜底。
    /// </summary>
    public static async Task<ContainerLoadingParticipantDto> SetPrimaryAsync(
        IErpDbContext db, long loadingListId, long id)
    {
        var loadingList = await EnsureLoadingListAsync(db, loadingListId);
        EnsureMaintainable(loadingList);

        var entity = await FindAsync(db, loadingListId, id);
        if (!ContainerLoadingParticipantRules.IsSelectable(entity))
            throw BusinessException.InvalidParameter("只有启用状态的参与方可以被设为主参与方，请先启用该参与方");

        var customer = await LoadCustomerAsync(db, entity.CustomerId);
        ContainerLoadingParticipantRules.EnsureCustomerPromotable(customer, entity.CustomerId);

        await ApplyPrimaryAsync(db, loadingList, entity, await LoadSiblingsAsync(db, loadingListId));
        return (await MapAsync(db, loadingList, new List<ContainerLoadingListParticipant> { entity })).Single();
    }

    /// <summary>
    /// 启用 / 停用参与方（只写参与方子表）：停用会释放主参与方标记，因此只有当本清单不再有其他启用参与方时
    /// 才允许停用当前主参与方（此时清单回退为按兼容字段读取的历史单客户视图，兼容字段保留原值不清空）；
    /// 重新启用时要求客户仍可用（未删除且启用），且不会自动恢复主参与方标记。
    /// </summary>
    public static async Task<ContainerLoadingParticipantDto> SetStatusAsync(
        IErpDbContext db, long loadingListId, long id, int? status)
    {
        var loadingList = await EnsureLoadingListAsync(db, loadingListId);
        EnsureMaintainable(loadingList);

        var entity = await FindAsync(db, loadingListId, id);
        var normalized = ContainerLoadingParticipantRules.NormalizeStatus(status);
        if (normalized == entity.Status)
            return (await MapAsync(db, loadingList, new List<ContainerLoadingListParticipant> { entity })).Single();

        if (normalized != ContainerLoadingParticipantRules.ActiveStatus)
        {
            var siblings = await LoadSiblingsAsync(db, loadingListId);
            ContainerLoadingParticipantRules.EnsurePrimaryRemovable(siblings, entity, "停用");
            entity.Status = ContainerLoadingParticipantRules.DisabledStatus;
            entity.IsPrimary = false;
        }
        else
        {
            // 重新启用等同于「重新选用该客户」：客户必须仍然可用
            var customer = await LoadCustomerAsync(db, entity.CustomerId);
            ContainerLoadingParticipantRules.EnsureCustomerSelectable(customer, entity.CustomerId);
            entity.Status = ContainerLoadingParticipantRules.ActiveStatus;
        }

        await SaveAsync(db);
        return (await MapAsync(db, loadingList, new List<ContainerLoadingListParticipant> { entity })).Single();
    }

    /// <summary>
    /// 删除参与方（<b>只做本表软删除</b>，保留行以便历史可读）：不物理删除、不改写装柜清单明细 / 柜号 /
    /// 订柜跟踪值 / 单证 / 费用 / 库存与客户主数据；删除同时释放主参与方标记，
    /// 且当前主参与方只有在清单不再有其他启用参与方时才允许删除。
    /// </summary>
    public static async Task DeleteAsync(IErpDbContext db, long loadingListId, long id)
    {
        var loadingList = await EnsureLoadingListAsync(db, loadingListId);
        EnsureMaintainable(loadingList);

        var entity = await FindAsync(db, loadingListId, id);
        var siblings = await LoadSiblingsAsync(db, loadingListId);
        ContainerLoadingParticipantRules.EnsurePrimaryRemovable(siblings, entity, "删除");

        entity.IsDeleted = true;
        entity.IsPrimary = false;
        await SaveAsync(db);
    }

    /// <summary>
    /// 读取标注（<b>不写库</b>）：为装柜清单列表 / 详情补写
    /// 「启用参与方数」「参与方总数（含停用）」「主参与方客户与可用性」「历史单客户视图」标记。
    /// <para>参与方与主参与方客户按**一次批量查询**解析（无逐行查询）；完全没有参与方行的历史清单
    /// 计数为 0 且标记为历史单客户视图，无需任何回填，也不会在读取时写库。</para>
    /// </summary>
    public static async Task AnnotateAsync(IErpDbContext db, IEnumerable<ContainerLoadingList> loadingLists)
    {
        var lists = loadingLists as IList<ContainerLoadingList> ?? loadingLists.ToList();
        if (lists.Count == 0) return;

        var ids = lists.Select(x => x.Id).Distinct().ToList();
        var rows = await db.ContainerLoadingListParticipants.AsNoTracking()
            .Where(x => !x.IsDeleted && ids.Contains(x.LoadingListId))
            .Select(x => new { x.LoadingListId, x.CustomerId, x.CustomerName, x.Status, x.IsPrimary })
            .ToListAsync();

        var grouped = rows.GroupBy(x => x.LoadingListId).ToDictionary(g => g.Key, g => g.ToList());

        var primaryCustomerIds = rows
            .Where(x => x.Status == ContainerLoadingParticipantRules.ActiveStatus && x.IsPrimary)
            .Select(x => x.CustomerId)
            .Distinct()
            .ToList();
        var availablePrimaryIds = primaryCustomerIds.Count == 0
            ? new HashSet<long>()
            : (await db.BaseCustomers.AsNoTracking()
                .Where(c => primaryCustomerIds.Contains(c.Id)
                            && !c.IsDeleted
                            && c.Status == ContainerLoadingParticipantRules.ActiveStatus)
                .Select(c => c.Id)
                .ToListAsync()).ToHashSet();

        foreach (var list in lists)
        {
            if (!grouped.TryGetValue(list.Id, out var participants) || participants.Count == 0)
            {
                list.ParticipantCount = 0;
                list.ParticipantTotalCount = 0;
                list.PrimaryParticipantCustomerId = null;
                list.PrimaryParticipantCustomerName = string.Empty;
                list.PrimaryParticipantAvailable = true;
                list.LegacySingleCustomer = true;      // 没有任何参与方行 = 历史单客户视图
                continue;
            }

            var primary = participants.FirstOrDefault(
                x => x.Status == ContainerLoadingParticipantRules.ActiveStatus && x.IsPrimary);

            list.ParticipantTotalCount = participants.Count;
            list.ParticipantCount = participants.Count(x => x.Status == ContainerLoadingParticipantRules.ActiveStatus);
            list.PrimaryParticipantCustomerId = primary?.CustomerId;
            list.PrimaryParticipantCustomerName = primary?.CustomerName ?? string.Empty;
            list.PrimaryParticipantAvailable = primary is null || availablePrimaryIds.Contains(primary.CustomerId);
            list.LegacySingleCustomer = false;
        }
    }

    /// <summary>
    /// 参与方实体读取标注（<b>不写库</b>）：为详情返回的参与方集合补写客户可用性、
    /// 客户主数据当前名称与「快照名称已过期」标记（一次批量查询解析全部客户，无逐行查询）。
    /// </summary>
    public static async Task AnnotateParticipantsAsync(
        IErpDbContext db, IEnumerable<ContainerLoadingListParticipant> participants)
    {
        var rows = participants as IList<ContainerLoadingListParticipant> ?? participants.ToList();
        if (rows.Count == 0) return;

        var customerIds = rows.Select(x => x.CustomerId).Distinct().ToList();
        var customers = await db.BaseCustomers.AsNoTracking()
            .Where(c => customerIds.Contains(c.Id))
            .ToListAsync();
        var customersById = customers.ToDictionary(c => c.Id);

        foreach (var row in rows)
        {
            customersById.TryGetValue(row.CustomerId, out var customer);
            var available = customer is not null
                && !customer.IsDeleted
                && customer.Status == ContainerLoadingParticipantRules.ActiveStatus;

            row.CustomerAvailable = available;
            row.CustomerCurrentName = customer?.CustomerName ?? string.Empty;
            row.CustomerRenamed = available
                && !string.IsNullOrWhiteSpace(row.CustomerCurrentName)
                && !string.Equals(
                    ContainerLoadingParticipantRules.DisplayValue(row.CustomerCurrentName),
                    ContainerLoadingParticipantRules.DisplayValue(row.CustomerName),
                    StringComparison.Ordinal);
        }
    }

    /// <summary>按「装柜清单 + 参与方」定位未删除参与方；不属于该清单时按不存在处理</summary>
    private static async Task<ContainerLoadingListParticipant> FindAsync(IErpDbContext db, long loadingListId, long id)
    {
        if (id <= 0)
            throw BusinessException.InvalidParameter("参与方 Id 不合法");

        return await db.ContainerLoadingListParticipants
            .FirstOrDefaultAsync(x => x.Id == id && x.LoadingListId == loadingListId && !x.IsDeleted)
            ?? throw BusinessException.NotFound($"参与方（Id={id}）在该装柜清单下不存在或已删除");
    }

    /// <summary>
    /// 该清单下未删除的全部参与方（含停用），用于重复 / 主参与方唯一性判定与主参与方切换。
    /// 使用**跟踪**实体：主参与方切换需要就地释放旧主参与方并在同一批保存中写回。
    /// </summary>
    private static async Task<List<ContainerLoadingListParticipant>> LoadSiblingsAsync(
        IErpDbContext db, long loadingListId) =>
        await db.ContainerLoadingListParticipants
            .Where(x => x.LoadingListId == loadingListId && !x.IsDeleted)
            .ToListAsync();

    /// <summary>
    /// 按 Id 读取客户（未删除判定交给规则方法，因此这里**不过滤** IsDeleted，
    /// 以便区分「客户行已软删除」与「客户不存在」两类可读错误）。
    /// </summary>
    private static async Task<BaseCustomer> LoadCustomerAsync(IErpDbContext db, long customerId)
    {
        if (customerId <= 0)
            throw BusinessException.InvalidParameter("参与客户 Id 不合法");

        return await db.BaseCustomers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId)
            ?? throw BusinessException.NotFound($"客户（Id={customerId}）不存在或已删除，请重新选择");
    }

    /// <summary>
    /// 两段式显式切换主参与方：先在同一清单内释放旧的启用主参与方（一次批量写回），
    /// 再置新主参与方并把装柜清单的**兼容客户字段**同步为该客户（同批落库）。
    /// <para>任何时刻清单内都不会存在两条启用主参与方，切换结果不依赖列表顺序，也不依赖「最后提交者胜出」；
    /// 并发下由过滤唯一索引 <c>UX_ContainerLoadingListParticipants_ListPrimary</c> 兜底并转为可读的重复错误。</para>
    /// </summary>
    private static async Task ApplyPrimaryAsync(
        IErpDbContext db, ContainerLoadingList loadingList, ContainerLoadingListParticipant target,
        IReadOnlyList<ContainerLoadingListParticipant> siblings)
    {
        var conflicts = siblings
            .Where(x => x.Id != target.Id && ContainerLoadingParticipantRules.IsPrimaryActive(x))
            .ToList();

        if (conflicts.Count > 0)
        {
            foreach (var conflict in conflicts) conflict.IsPrimary = false;
            await SaveAsync(db);
        }

        target.IsPrimary = true;
        loadingList.CustomerId = target.CustomerId;   // 兼容主客户字段：与显式置主同一批保存
        await SaveAsync(db);
    }

    /// <summary>
    /// 实体 → 读取 DTO（含服务端解析的客户信息与可用性标注）。
    /// 客户按批次一次查询解析，避免逐行查询；客户行缺失时退化为可辨识的占位文案
    /// （而不是静默变空），保证历史参与方仍然可读。
    /// </summary>
    private static async Task<List<ContainerLoadingParticipantDto>> MapAsync(
        IErpDbContext db, ContainerLoadingList loadingList, IReadOnlyList<ContainerLoadingListParticipant> rows)
    {
        var result = new List<ContainerLoadingParticipantDto>(rows.Count);
        if (rows.Count == 0) return result;

        var customerIds = rows.Select(x => x.CustomerId).Distinct().ToList();
        var customers = await db.BaseCustomers.AsNoTracking()
            .Where(c => customerIds.Contains(c.Id))
            .ToListAsync();
        var customersById = customers.ToDictionary(c => c.Id);

        foreach (var row in rows)
        {
            customersById.TryGetValue(row.CustomerId, out var customer);
            var customerAvailable = customer is not null
                && !customer.IsDeleted
                && customer.Status == ContainerLoadingParticipantRules.ActiveStatus;
            var currentName = customer?.CustomerName ?? string.Empty;
            var renamed = customerAvailable
                && !string.IsNullOrWhiteSpace(currentName)
                && !string.Equals(
                    ContainerLoadingParticipantRules.DisplayValue(currentName),
                    ContainerLoadingParticipantRules.DisplayValue(row.CustomerName),
                    StringComparison.Ordinal);
            var selectable = ContainerLoadingParticipantRules.IsSelectable(row);

            result.Add(new ContainerLoadingParticipantDto(
                row.Id,
                row.LoadingListId,
                loadingList.LoadingListNo ?? string.Empty,
                row.CustomerId,
                row.CustomerCode ?? string.Empty,
                row.CustomerName ?? string.Empty,
                currentName,
                renamed,
                customerAvailable,
                row.IsPrimary,
                row.Status,
                selectable,
                ContainerLoadingParticipantRules.StatusText(row.Status),
                ContainerLoadingParticipantRules.AvailabilityText(selectable, customerAvailable),
                row.SortOrder,
                row.Remark ?? string.Empty,
                row.CreatedAt,
                row.UpdatedAt));
        }

        return result;
    }

    /// <summary>保存并把数据库层唯一索引冲突转换为可读的业务错误（并发写入兜底）</summary>
    private static async Task SaveAsync(IErpDbContext db)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (LooksLikeUniqueViolation(ex))
        {
            throw BusinessException.Duplicate(DuplicateConflictMessage);
        }
    }

    /// <summary>
    /// 是否为唯一索引 / 唯一约束冲突（SQL Server 2601 重复键 / 2627 违反唯一约束）。
    /// 非唯一性冲突（如结构缺失）原样上抛，不被误报成重复。
    /// </summary>
    private static bool LooksLikeUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("2601", StringComparison.Ordinal)
                || message.Contains("2627", StringComparison.Ordinal)
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("UNIQUE", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
