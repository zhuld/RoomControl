namespace RoomControl.Api.Models;

/// <summary>可独立配置楼栋楼层和一个中控连接的房间。</summary>
public sealed class Room
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Building { get; set; } = string.Empty;
    public string Floor { get; set; } = string.Empty;
    public Controller? Controller { get; set; }
}
