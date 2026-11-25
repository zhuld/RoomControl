using Microsoft.EntityFrameworkCore;
using RoomControl.Api.Data;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>自动任务后台留痕：自动执行按每条下发指令记录，停机错过的一次性任务按任务组记录一条。</summary>
internal static class ScheduleAudit
{
    public const string ExecutedAction = "自动执行自动任务";
    public const string MissedAction = "自动任务未自动执行（平台停机期间到期）";

    /// <summary>每条下发指令的动作文本，带上发送 Join 编号，便于与人工发送记录对照。</summary>
    public static string FormatCommandAction(int sendJoinNumber) => $"{ExecutedAction} Join {sendJoinNumber}";

    /// <summary>逐教室的命令记录对象：教室、控制点与任务名称，任务删除后仍可识别来源。</summary>
    public static string FormatCommandTarget(ControlSchedule schedule) =>
        $"教室「{schedule.RoomName}」· 控制点「{schedule.JoinName}」· 自动任务「{schedule.Name}」";

    /// <summary>判断当前目标是否为组内首个目标，并生成整组的可读对象名称。</summary>
    public static async Task<(bool IsLeader, string Target)> ResolveGroupAsync(
        ControlDbContext db,
        ControlSchedule schedule,
        CancellationToken cancellationToken)
    {
        var groupId = schedule.GroupId ?? schedule.Id;
        var group = await db.Schedules.AsNoTracking()
            .WhereGroup(groupId)
            .Select(item => new { item.Id, item.RoomName })
            .ToListAsync(cancellationToken);

        var leaderId = group.Count == 0 ? schedule.Id : group.Min(item => item.Id);
        var roomNames = group
            .Select(item => item.RoomName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var scope = roomNames.Length switch
        {
            0 => "教室（名称未知）",
            1 => $"教室「{roomNames[0]}」",
            _ => $"教室「{roomNames[0]}」等 {roomNames.Length} 间",
        };
        return (leaderId == schedule.Id, $"{scope} · 自动任务「{schedule.Name}」");
    }
}
