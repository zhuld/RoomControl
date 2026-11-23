namespace RoomControl.Api.Models;

/// <summary>应用内授权角色；名称转换由扩展方法统一映射到 API 字符串。</summary>
public enum UserRole
{
    Admin = 0,
    Operator = 1,
    Viewer = 2,
}

/// <summary>集中处理角色的 API 标识与界面显示名称转换。</summary>
public static class UserRoleExtensions
{
    public const string AdminName = "admin";
    public const string OperatorName = "operator";
    public const string ViewerName = "viewer";

    public static string DisplayName(this UserRole role) => role switch
    {
        UserRole.Admin => "管理员",
        UserRole.Operator => "操作员",
        UserRole.Viewer => "观看员",
        _ => role.ToString(),
    };

    public static bool TryFromName(string? name, out UserRole role)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case AdminName:
                role = UserRole.Admin;
                return true;
            case OperatorName:
                role = UserRole.Operator;
                return true;
            case ViewerName:
                role = UserRole.Viewer;
                return true;
            default:
                // 返回值通过 false 表示解析失败；Viewer 仅用于满足 out 参数，不代表接受未知角色。
                role = UserRole.Viewer;
                return false;
        }
    }

    public static string ToName(this UserRole role) => role switch
    {
        UserRole.Admin => AdminName,
        UserRole.Operator => OperatorName,
        UserRole.Viewer => ViewerName,
        _ => ViewerName,
    };
}
