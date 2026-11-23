namespace RoomControl.Api.Models;

/// <summary>定时控制任务；多间教室的同一次计划各自保留独立目标与执行历史。</summary>
public sealed class ControlSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>同一计划任务在多间教室产生的目标共享该 ID；为空表示旧版单目标任务。</summary>
    public Guid? GroupId { get; set; }
    public Guid? JoinMappingId { get; set; }
    public JoinMapping? JoinMapping { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public string JoinName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public ScheduleKind Kind { get; set; }
    // 一次性任务的执行时刻；每周任务为 null。
    public DateTimeOffset? RunAtUtc { get; set; }
    // 星期位掩码，位号与 DayOfWeek 一致（周日为最低位）。
    public int WeekdayMask { get; set; }
    // 每周任务在服务器本地时区的执行时刻。
    public TimeSpan LocalTime { get; set; }
    // 下一次执行时间；暂停或执行完毕的一次性任务为 null。
    public DateTimeOffset? NextRunAtUtc { get; set; }
}

/// <summary>一次性执行或按周重复执行。</summary>
public enum ScheduleKind
{
    Once,
    Weekly
}
