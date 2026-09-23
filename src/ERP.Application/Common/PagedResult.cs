namespace ERP.Application.Common;

/// <summary>
/// 分页查询结果
/// </summary>
public class PagedResult<T>
{
    /// <summary>数据集合</summary>
    public List<T> Items { get; set; } = new();

    /// <summary>总记录数</summary>
    public int Total { get; set; }

    /// <summary>当前页码（从 1 开始）</summary>
    public int Page { get; set; }

    /// <summary>每页条数</summary>
    public int PageSize { get; set; }

    /// <summary>总页数</summary>
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

/// <summary>
/// 分页查询参数
/// </summary>
public class PageQuery
{
    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>关键字（模糊搜索）</summary>
    public string? Keyword { get; set; }

    /// <summary>校验并修正分页参数</summary>
    public void Normalize()
    {
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = 20;
        // 上限放宽以支持「不限」（前端不限传 100000）
        if (PageSize > 100000) PageSize = 100000;
    }
}
