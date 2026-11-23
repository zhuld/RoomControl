using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证登录日志：操作者为成功登录的账号，对象为来源 IP。</summary>
public sealed class AuditLogAuthTargetTests
{
    [Fact]
    public void ResolveActor_UsesSuccessfulLoginUsername()
    {
        var context = new DefaultHttpContext();
        context.Items[AuditLogAuthTarget.UsernameItemKey] = "bob";

        Assert.Equal("bob", AuditLogAuthTarget.ResolveActor(context));
    }

    [Fact]
    public void ResolveActor_FallsBackToPrincipalName()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin")],
            "test"));

        Assert.Equal("admin", AuditLogAuthTarget.ResolveActor(context));
    }

    [Fact]
    public void ResolveActor_ReturnsAnonymousForFailedLogin()
    {
        Assert.Equal("anonymous", AuditLogAuthTarget.ResolveActor(new DefaultHttpContext()));
    }

    [Theory]
    [InlineData("10.1.2.3", "IP 10.1.2.3")]
    [InlineData("::ffff:192.168.1.20", "IP 192.168.1.20")]
    [InlineData("::1", "IP 127.0.0.1")]
    public void Build_RecordsClientIp(string address, string expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);

        Assert.Equal(expected, AuditLogAuthTarget.Build(context));
    }

    [Fact]
    public void Build_UsesRealIpHeaderFromLoopbackProxy()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.IPv6Loopback;
        context.Request.Headers["X-Real-IP"] = "203.0.113.9";

        Assert.Equal("IP 203.0.113.9", AuditLogAuthTarget.Build(context));
    }

    [Fact]
    public void Build_UsesCloudflareHeaderFromLoopbackProxy()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["CF-Connecting-IP"] = "198.51.100.7";

        Assert.Equal("IP 198.51.100.7", AuditLogAuthTarget.Build(context));
    }

    [Fact]
    public void Build_IgnoresProxyHeadersFromRemoteClients()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.50");
        context.Request.Headers["X-Real-IP"] = "198.51.100.7";

        Assert.Equal("IP 203.0.113.50", AuditLogAuthTarget.Build(context));
    }
}
