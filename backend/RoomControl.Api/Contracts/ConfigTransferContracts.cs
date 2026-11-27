using RoomControl.Api.Models;

namespace RoomControl.Api.Contracts;

/// <summary>配置文件版本；导入时只接受当前版本，避免旧程序把未知字段当作默认值。</summary>
public static class ConfigDocumentVersion
{
    public const int Current = 1;
}

/// <summary>完整的站点配置快照；不含账号、操作日志与任务执行记录。导出与导入共用同一结构。</summary>
public sealed record ConfigDocument(
    int Version,
    DateTimeOffset ExportedAtUtc,
    ConfigBranding? Branding,
    IReadOnlyList<ConfigBuilding>? Buildings,
    IReadOnlyList<ConfigRoom>? Rooms,
    IReadOnlyList<ConfigSchedule>? Schedules,
    IReadOnlyList<ConfigManualTask>? ManualTasks);

/// <summary>站点名称、副标题与 Logo。</summary>
public sealed record ConfigBranding(string? DisplayName, string? Subtitle, string? LogoDataUrl);

/// <summary>楼栋及其楼层名称；教室通过名称引用位置。</summary>
public sealed record ConfigBuilding(string? Name, IReadOnlyList<string?>? Floors);

/// <summary>教室及其可选的中控配置。</summary>
public sealed record ConfigRoom(string? Name, string? Building, string? Floor, ConfigController? Controller);

/// <summary>中控连接参数与全部控制点。</summary>
public sealed record ConfigController(
    string? AdapterType,
    string? Host,
    int Port,
    int Ipid,
    bool Enabled,
    IReadOnlyList<ConfigJoin>? Joins);

/// <summary>数字控制点；列表顺序即控制台按钮顺序。</summary>
public sealed record ConfigJoin(
    string? Name,
    int SendJoinNumber,
    int StateJoinNumber,
    int EnableJoinNumber,
    bool ShowConfirmationPopup);

/// <summary>自动任务组；多个目标共享同一次计划定义。</summary>
public sealed record ConfigSchedule(
    string? Name,
    ScheduleKind Kind,
    DateTimeOffset? RunAtUtc,
    int WeekdayMask,
    TimeSpan LocalTime,
    bool Enabled,
    IReadOnlyList<ConfigTarget>? Targets);

/// <summary>手动任务；目标必须使用同名控制点。</summary>
public sealed record ConfigManualTask(string? Name, bool ConfirmBeforeRun, IReadOnlyList<ConfigTarget>? Targets);

/// <summary>按名称引用一间教室中的一个控制点，便于跨实例迁移。</summary>
public sealed record ConfigTarget(string? Building, string? Floor, string? Room, string? Join);

/// <summary>导入完成后的数量摘要。</summary>
public sealed record ConfigImportSummary(
    int BuildingCount,
    int RoomCount,
    int ControllerCount,
    int JoinCount,
    int ScheduleCount,
    int ManualTaskCount);
