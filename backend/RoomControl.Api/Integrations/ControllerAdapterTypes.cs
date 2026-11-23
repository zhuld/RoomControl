namespace RoomControl.Api.Integrations;

/// <summary>供 API 和配置界面展示的控制器适配器类型描述。</summary>
public sealed record ControllerAdapterOption(string Key, string DisplayName, string Description);

/// <summary>受支持的中控适配器清单及其稳定配置键。</summary>
public static class ControllerAdapterTypes
{
    public static IReadOnlyList<ControllerAdapterOption> All { get; } =
    [
        new("crestron-cip", "Crestron 中控", "CIP 协议 · Crestron 中控")
    ];

    public static ControllerAdapterOption? Find(string? key) =>
        All.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
}
