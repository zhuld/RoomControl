namespace RoomControl.Api.Models;

/// <summary>不可变的操作审计记录。</summary>
public sealed class AuditLog
{
    // 列长度统一定义在这里，数据库映射与写入截断共用同一组上限。
    public const int CategoryMaxLength = 32;
    public const int ActionMaxLength = 300;
    public const int TargetMaxLength = 512;
    public const int ActorMaxLength = 60;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Category { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    // 被操作对象的可读名称，例如「控制点「上课」」；无法解析时为空。
    public string Target { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public static class AuditLogCategories
{
    public const string Authentication = "authentication";
    public const string Configuration = "configuration";
    public const string Control = "control";
    public const string Schedule = "schedule";
    // 设备上报引起的控制点电平变化单独归类，便于与控制指令区分。
    public const string State = "state";
    // 中控连接与断开由后台自动产生，单独归类便于排查网络问题。
    public const string Connection = "connection";

    public static bool Contains(string category) =>
        category is Authentication or Configuration or Control or Schedule or State or Connection;
}
