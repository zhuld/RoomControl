namespace RoomControl.Api.Models;

/// <summary>单次自动任务执行结果；随所属任务目标一起删除。</summary>
public sealed class ScheduleExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScheduleId { get; set; }
    public ControlSchedule Schedule { get; set; } = null!;
    public DateTimeOffset ScheduledAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    // Pending（执行中）、Succeeded 或 Failed。
    public string Status { get; set; } = "Pending";
    public string? Error { get; set; }
}
