namespace RoomControl.Api.Models;

/// <summary>站点单例Logo设置；未保存时由默认名称和副标题提供初始展示值。</summary>
public sealed class SiteSettings
{
    public static readonly Guid SingletonId = new("2f6d6a2e-1c2b-4c3f-9d38-6b1f0a5e7c41");
    public const string DefaultDisplayName = "RoomControl";
    public const string DefaultSubtitle = "教室中控平台";

    public Guid Id { get; set; } = SingletonId;
    public string DisplayName { get; set; } = DefaultDisplayName;
    public string Subtitle { get; set; } = DefaultSubtitle;
    public string? LogoDataUrl { get; set; }
}
