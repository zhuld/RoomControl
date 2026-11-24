using RoomControl.Api.Models;

namespace RoomControl.Api.Data;

/// <summary>计划任务的共用查询条件。</summary>
public static class ScheduleQueryExtensions
{
    /// <summary>
    /// 按组筛选任务目标；旧版单目标任务没有 GroupId，用目标自身 ID 作为组标识。
    /// </summary>
    public static IQueryable<ControlSchedule> WhereGroup(this IQueryable<ControlSchedule> schedules, Guid groupId) =>
        schedules.Where(item => item.GroupId == groupId || (item.GroupId == null && item.Id == groupId));
}
