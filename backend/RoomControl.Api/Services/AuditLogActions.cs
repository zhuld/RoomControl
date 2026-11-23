using System.Text.RegularExpressions;

namespace RoomControl.Api.Services;

/// <summary>把接口映射为可读操作名，并把 Join 编号等细节拼接到操作描述中。</summary>
public static partial class AuditLogActions
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["POST /api/auth/login"] = "登录",
        ["POST /api/auth/logout"] = "退出登录",
        ["POST /api/auth/change-password"] = "修改密码",
        ["POST /api/users"] = "创建账号",
        ["PUT /api/users/{id}"] = "更新账号",
        ["DELETE /api/users/{id}"] = "删除账号",
        ["PUT /api/branding"] = "更新站点信息",
        ["PUT /api/logs/settings"] = "更新日志保留设置",
        ["POST /api/buildings"] = "创建楼栋",
        ["PUT /api/buildings/{buildingId}"] = "重命名楼栋",
        ["DELETE /api/buildings/{buildingId}"] = "删除楼栋",
        ["POST /api/buildings/{buildingId}/floors"] = "创建楼层",
        ["PUT /api/floors/{floorId}"] = "重命名楼层",
        ["DELETE /api/floors/{floorId}"] = "删除楼层",
        ["POST /api/rooms"] = "创建教室",
        ["PUT /api/rooms/{roomId}"] = "更新教室",
        ["DELETE /api/rooms/{roomId}"] = "删除教室",
        ["POST /api/rooms/{roomId}/controller"] = "配置中控",
        ["PUT /api/controllers/{controllerId}"] = "更新中控",
        ["DELETE /api/controllers/{controllerId}"] = "移除中控",
        ["POST /api/controllers/{controllerId}/joins"] = "创建控制点",
        ["PUT /api/controllers/{controllerId}/joins/order"] = "调整控制点顺序",
        ["POST /api/controllers/{controllerId}/joins/copy-all"] = "复制全部控制点到教室",
        ["PUT /api/joins/{mappingId}"] = "更新控制点",
        ["DELETE /api/joins/{mappingId}"] = "删除控制点",
        ["POST /api/joins/{mappingId}/copy"] = "复制控制点到教室",
        ["POST /api/joins/{mappingId}/pulse"] = "发送",
        ["POST /api/schedules"] = "创建定时任务",
        ["PUT /api/schedules/groups/{groupId}"] = "更新定时任务",
        ["DELETE /api/schedules/groups/{groupId}"] = "删除定时任务",
        ["DELETE /api/schedules/{scheduleId}"] = "删除定时任务",
        ["POST /api/schedules/groups/{groupId}/pause"] = "暂停定时任务",
        ["POST /api/schedules/groups/{groupId}/resume"] = "恢复定时任务",
    };

    public static string NormalizeRoute(string route) => RouteConstraint().Replace(route, "{$1}");

    public static string Resolve(string method, string route, string? detail)
    {
        var key = $"{method} {NormalizeRoute(route)}";
        var label = Labels.TryGetValue(key, out var found) ? found : key;
        return string.IsNullOrWhiteSpace(detail) ? label : $"{label} {detail}";
    }

    // 去掉路由约束后缀，动作列显示 {mappingId} 而不是 {mappingId:guid}。
    [GeneratedRegex(@"\{(\w+):[^}]+\}")]
    private static partial Regex RouteConstraint();
}
