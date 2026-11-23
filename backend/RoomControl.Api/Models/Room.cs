namespace RoomControl.Api.Models;

/// <summary>可独立配置楼栋楼层和一个中控连接的房间。</summary>
public sealed class Room
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    // 楼栋与楼层保存名称快照，重命名时由接口同步更新所属教室。
    public string Building { get; set; } = string.Empty;
    public string Floor { get; set; } = string.Empty;
    public Controller? Controller { get; set; }
}
