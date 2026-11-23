using System.Net;

namespace RoomControl.Api.Services;

/// <summary>认证类日志的账号与来源：操作者记录成功登录的账号，对象记录来源 IP。</summary>
internal static class AuditLogAuthTarget
{
    /// <summary>登录接口写入成功登录的用户名，供审计中间件记录操作者。</summary>
    public const string UsernameItemKey = "AuditLog.Username";

    // 常见的反向代理客户端地址头；仅在连接来自本机代理时采信，避免被外部伪造。
    private static readonly string[] ProxyIpHeaders = ["X-Real-IP", "CF-Connecting-IP"];

    public static string ResolveActor(HttpContext context)
    {
        // 登录请求完成时当前请求仍无身份，优先使用登录接口写入的账号。
        if (context.Items[UsernameItemKey] is string username && !string.IsNullOrWhiteSpace(username))
        {
            return username;
        }
        return context.User.Identity?.Name is { Length: > 0 } name ? name : "anonymous";
    }

    public static string Build(HttpContext context)
    {
        var address = ResolveClientAddress(context);
        return address is null ? string.Empty : $"IP {FormatAddress(address)}";
    }

    private static IPAddress? ResolveClientAddress(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null || !IPAddress.IsLoopback(address))
        {
            return address;
        }

        // 同机反向代理可能只写 X-Real-IP / CF-Connecting-IP，而未转发 X-Forwarded-For。
        foreach (var header in ProxyIpHeaders)
        {
            if (TryParseIp(context.Request.Headers[header].ToString(), out var forwarded))
            {
                return forwarded;
            }
        }
        return address;
    }

    private static bool TryParseIp(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var first = value.Split(',')[0].Trim();
        if (first.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            !IPAddress.TryParse(first, out var parsed))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    // 统一显示为可读地址：IPv4 映射地址去前缀，本机 IPv6 回环显示为 127.0.0.1。
    private static string FormatAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }
        return IPAddress.IsLoopback(address) ? IPAddress.Loopback.ToString() : address.ToString();
    }
}
