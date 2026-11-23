using RoomControl.Api.Models;

namespace RoomControl.Api.Contracts;

/// <summary>请求与 API 响应模型；不承载 EF Core 实体。</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>创建教室时提交的名称及所属楼栋和楼层。</summary>
public sealed record CreateRoomRequest(string Name, string? Building, string? Floor);

/// <summary>更新站点名称、副标题和可选的 Data URL Logo。</summary>
public sealed record UpdateBrandingRequest(string DisplayName, string Subtitle, string? LogoDataUrl);

/// <summary>创建楼栋的请求。</summary>
public sealed record CreateBuildingRequest(string Name);

/// <summary>创建楼层的请求。</summary>
public sealed record CreateFloorRequest(string Name);

/// <summary>创建中控连接的网络地址和 CIP 标识。</summary>
public sealed record CreateControllerRequest(
    string AdapterType,
    string Host,
    int Port,
    int Ipid);

/// <summary>更新中控连接配置，并允许启用或停用该连接。</summary>
public sealed record UpdateControllerRequest(
    string AdapterType,
    string Host,
    int Port,
    int Ipid,
    bool Enabled);

/// <summary>创建 Join 映射；使能 Join 可不配置。</summary>
public sealed record CreateJoinMappingRequest(
    string Name,
    int SendJoinNumber,
    int StateJoinNumber,
    int? EnableJoinNumber,
    bool ShowConfirmationPopup = true);

/// <summary>按给定顺序重排某中控下的全部控制点。</summary>
public sealed record ReorderJoinMappingsRequest(IReadOnlyList<Guid> MappingIds);

/// <summary>复制一个数字控制点到指定中控，复制名称、发送/状态/使能 Join 和确认设置。</summary>
public sealed record CopyJoinMappingRequest(IReadOnlyList<Guid> ControllerIds);

/// <summary>复制一个中控的全部数字控制点到指定中控，目标控制点会先全部删除。</summary>
public sealed record CopyAllJoinMappingsRequest(IReadOnlyList<Guid> ControllerIds);

/// <summary>更新 Join 映射的名称、发送 Join 和状态/使能 Join。</summary>
public sealed record UpdateJoinMappingRequest(
    string Name,
    int SendJoinNumber,
    int StateJoinNumber,
    int? EnableJoinNumber,
    bool ShowConfirmationPopup = true);

/// <summary>创建或修改按服务器本地时间执行的一次性或每周重复任务；一个任务可覆盖多间教室的同名控制点。</summary>
public sealed record ScheduleDefinitionRequest(
    IReadOnlyList<Guid> JoinMappingIds,
    string Name,
    ScheduleKind Kind,
    DateTime? RunAtLocal,
    int WeekdayMask,
    TimeSpan LocalTime);

/// <summary>创建用户时提交的身份凭据、角色和启用状态。</summary>
public sealed record CreateUserRequest(string Username, string Password, string Role, bool IsActive);

/// <summary>更新用户时，null 字段表示不修改对应属性；空密码也不会重置密码。</summary>
public sealed record UpdateUserRequest(string? Password, string? Role, bool? IsActive);

/// <summary>修改当前用户密码时提交的旧密码和新密码。</summary>
public sealed record ChangePasswordRequest(string OldPassword, string NewPassword);

/// <summary>修改操作日志的自动清理保留天数；0 表示永久保留。</summary>
public sealed record UpdateLogSettingsRequest(int RetentionDays);

/// <summary>控制点状态反馈；数字状态只有高低两种，未上报时按低电平返回。</summary>
public sealed record JoinStateSnapshotResponse(bool DigitalValue, DateTimeOffset? LastReportedAtUtc);
