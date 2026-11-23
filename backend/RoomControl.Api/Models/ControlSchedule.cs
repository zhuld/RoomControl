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
    public DateTimeOffset? RunAtUtc { get; set; }
    public int WeekdayMask { get; set; }
    public TimeSpan LocalTime { get; set; }
    public string TimeZoneId { get; set; } = "Asia/Shanghai";
    public DateTimeOffset? NextRunAtUtc { get; set; }
}

/// <summary>一次性执行或按周重复执行。</summary>
public enum ScheduleKind
{
    Once,
    Weekly
}
