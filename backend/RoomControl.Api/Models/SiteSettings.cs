namespace RoomControl.Api.Models;

/// <summary>站点单例设置；未保存时由默认名称、副标题和日志保留天数提供初始值。</summary>
public sealed class SiteSettings
{
    public static readonly Guid SingletonId = new("2f6d6a2e-1c2b-4c3f-9d38-6b1f0a5e7c41");
    public const string DefaultDisplayName = "RoomControl";
    public const string DefaultSubtitle = "教室中控平台";
    /// <summary>默认保留最近 60 天的操作日志。</summary>
    public const int DefaultLogRetentionDays = 60;

    public Guid Id { get; set; } = SingletonId;
    public string DisplayName { get; set; } = DefaultDisplayName;
    public string Subtitle { get; set; } = DefaultSubtitle;
    public string? LogoDataUrl { get; set; }
    /// <summary>自动清理超过该天数的操作日志；0 表示永久保留。</summary>
    public int LogRetentionDays { get; set; } = DefaultLogRetentionDays;
}
