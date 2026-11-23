namespace RoomControl.Api.Models;

/// <summary>站点内的楼栋；楼层通过 BuildingId 归属该楼栋。</summary>
public sealed class Building
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public ICollection<Floor> Floors { get; set; } = new List<Floor>();
}

/// <summary>楼栋下可供教室选择的楼层。</summary>
public sealed class Floor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BuildingId { get; set; }
    public Building Building { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
}
