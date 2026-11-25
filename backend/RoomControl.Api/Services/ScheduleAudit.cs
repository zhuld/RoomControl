using Microsoft.EntityFrameworkCore;
using RoomControl.Api.Data;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>定时任务后台留痕：整个任务组每次触发只由组内一个目标写一条记录，避免多教室刷屏。</summary>
internal static class ScheduleAudit
{
    public const string ExecutedAction = "自动执行定时任务";
    public const string MissedAction = "定时任务未自动执行（平台停机期间到期）";

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
        return (leaderId == schedule.Id, $"{scope} · 定时任务「{schedule.Name}」");
    }
}
