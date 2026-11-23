namespace RoomControl.Api.Services;

/// <summary>控制器离线或网络操作中断时抛出，API 将其映射为 503 响应。</summary>
public sealed class ControllerUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
