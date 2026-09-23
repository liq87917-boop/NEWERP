namespace ERP.Application.Common;

/// <summary>
/// 当前登录用户上下文
/// </summary>
public class CurrentUser
{
    /// <summary>用户 Id</summary>
    public long Id { get; set; }

    /// <summary>登录账号</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>显示姓名</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>角色编码集合</summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>权限编码集合</summary>
    public List<string> Permissions { get; set; } = new();
}
