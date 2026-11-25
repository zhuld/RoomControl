namespace RoomControl.Api.Models;

/// <summary>手动任务：把多间教室的同名控制点聚合为一个总览按钮，点击即批量下发。</summary>
public sealed class ManualTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    /// <summary>创建时的控制点名称快照，用于列表与总览按钮显示。</summary>
    public string JoinName { get; set; } = string.Empty;
    /// <summary>执行前是否要求操作员二次确认。</summary>
    public bool ConfirmBeforeRun { get; set; } = true;
    public int SortOrder { get; set; }
    public List<ManualTaskTarget> Targets { get; set; } = [];
}

/// <summary>手动任务覆盖的一间教室及其中一个控制点。</summary>
public sealed class ManualTaskTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ManualTaskId { get; set; }
    public ManualTask ManualTask { get; set; } = null!;
    public Guid? JoinMappingId { get; set; }
    public JoinMapping? JoinMapping { get; set; }
    /// <summary>教室名称快照，控制点删除后仍可识别历史目标。</summary>
    public string RoomName { get; set; } = string.Empty;
    public string JoinName { get; set; } = string.Empty;
}

/// <summary>一次手动任务执行的汇总结果；随所属任务一起删除。</summary>
public sealed class ManualTaskExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ManualTaskId { get; set; }
    public ManualTask ManualTask { get; set; } = null!;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    /// <summary>Succeeded（全部成功）、Partial（部分失败）或 Failed（全部失败）。</summary>
    public string Status { get; set; } = "Pending";
    /// <summary>失败教室与原因摘要，全部成功时为 null。</summary>
    public string? Error { get; set; }
}
