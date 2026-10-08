using ERP.Application.Common;

namespace ERP.Application.Services;

/// <summary>
/// 旧单据操作历史（<c>GET api/v2/bills/{billType}/{oid}/logs</c>）的精确文档身份、有限路径边界与有界分页口径（ERP-406）。
/// <list type="number">
/// <item><b>精确文档身份</b>：历史行必须命中<strong>精确</strong>的单据路径
/// <c>/api/v2/bills/{billType}/{oid}</c>（或其后<strong>有限动作段</strong>），并按族既有模块标题与权威单号交叉核对；
/// 单号相同但在<strong>其它族 / 其它客户</strong>的单据绝不匹配，前缀碰撞（<c>…/12</c> 命中 <c>…/123</c>）绝不匹配。</item>
/// <item><b>有限路径边界</b>：后代动作段只来自服务端常量集合（<see cref="ActionSegments"/>），
/// 绝不做任意查询 / 前缀匹配 / 用户提供路径。</item>
/// <item><b>有界分页</b>：页码 / 每页条数收敛到受控范围（默认 <see cref="DefaultPageSize"/>，上限 <see cref="MaxPageSize"/>），
/// 偏移用 64 位检查运算，越界立即以 <c>1001</c> 拒绝。</item>
/// <item><b>缺权威记录标识的历史省略</b>：只返回可核验精确路径的历史行；旧日志若缺少可核验的单据路径 / 单号，
/// 一律省略（记录来源限制），绝不按单号单独放行。</item>
/// </list>
/// <para>本类只做纯判定（不触碰数据库、不写日志、不发通知），供控制器在计数 / 分页之前调用。</para>
/// </summary>
public static class LegacyBillHistoryRules
{
    /// <summary>默认每页条数（与既有日志路由一致）。</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页最大条数（服务端常量，客户端不可递增；与旧单据读侧 <c>MaxPageSize</c> 一致）。</summary>
    public const int MaxPageSize = 200;

    /// <summary>零 / 负数 Oid 的拒绝文案（拒绝全局历史；既有全局日志入口为 <c>api/sys/logs</c>）。</summary>
    public const string OidRequiredText =
        "旧单据操作历史必须指定正数单据 Oid（拒绝零 / 负数全局历史）："
        + "全局操作日志请使用既有系统日志入口 api/sys/logs，本路由绝不为普通业务账号放宽";

    /// <summary>旧单据操作历史口径文案（接口 / 文档同源）。</summary>
    public const string RuleText =
        "旧单据操作历史在返回任何单号 / 客户提示 / 计数之前，先复用旧单据读侧门禁"
        + "（实时身份 + 该族既有功能菜单 + 业务员客户数据范围），并要求正数 Oid 命中调用方数据范围内的权威旧库行；"
        + "随后只返回<精确单据路径（含有限动作段）+ 族既有模块标题 + 权威单号>交叉匹配的历史行，"
        + "绝不按单号单独匹配、绝不做前缀碰撞、绝不返回请求体等原始载荷。";

    /// <summary>
    /// 后代动作段有限集合（服务端常量）：只承载既有旧写路由的有限动作，绝不做任意 / 前缀匹配。
    /// </summary>
    public static readonly IReadOnlyList<string> ActionSegments = new[]
    {
        "save", "delete", "audit", "unaudit", "void", "restore",
    };

    /// <summary>
    /// 精确单据基础路径（唯一权威单据标识路径）：族标识非法或 Oid 非正数时在访问任何数据之前拒绝。
    /// </summary>
    public static string DocumentBasePath(string billType, long oid)
    {
        ValidateFamily(billType);
        if (oid <= 0)
            throw BusinessException.InvalidParameter(OidRequiredText);
        return $"/api/v2/bills/{billType}/{oid}";
    }

    /// <summary>
    /// 精确单据的有限允许路径集合：基础路径 + 基础路径 + 有限动作段（全部为服务端常量）。
    /// 因为只做<strong>精确</strong>匹配，<c>…/12</c> 不会命中 <c>…/123</c>（无前缀碰撞）。
    /// </summary>
    public static IReadOnlyList<string> BuildDocumentPaths(string billType, long oid)
    {
        var basePath = DocumentBasePath(billType, oid);
        var paths = new List<string>(ActionSegments.Count + 1) { basePath };
        foreach (var action in ActionSegments)
            paths.Add(basePath + "/" + action);
        return paths;
    }

    /// <summary>
    /// 有界分页：页码 &lt; 1 收敛为 1，每页条数 &lt; 1 收敛为默认值、&gt; 上限收敛为上限；
    /// 偏移用 64 位检查运算，超过 <see cref="int.MaxValue"/> 以 <c>1001</c> 拒绝。
    /// </summary>
    public static (int Page, int PageSize, int Offset) ResolvePaging(int page, int pageSize)
    {
        var normalizedPage = page < 1 ? 1 : page;
        var normalizedSize = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        var offset = (long)(normalizedPage - 1) * normalizedSize;
        if (offset > int.MaxValue)
            throw BusinessException.InvalidParameter("操作历史分页偏移超出安全范围");

        return (normalizedPage, normalizedSize, (int)offset);
    }

    private static void ValidateFamily(string billType)
    {
        if (string.IsNullOrWhiteSpace(billType))
            throw BusinessException.InvalidParameter("旧单据族标识不能为空");

        var key = billType.Trim();
        if (key.Length > 64 || key.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')))
            throw BusinessException.InvalidParameter($"旧单据族标识非法：{key}");
    }
}
