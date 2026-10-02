namespace GqlGateway.Api.Security;

using System.Net;
using GqlGateway.Application.Interfaces;
using Microsoft.AspNetCore.Http;

public sealed class HttpContextClientIpResolver : IClientIpResolver
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextClientIpResolver(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public IPAddress ResolveClientIp()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null)
        {
            return IPAddress.Loopback;
        }

        if (context.Items.TryGetValue("OriginalTcpRemoteIp", out var origIpObj))
        {
            if (origIpObj is IPAddress origIp)
            {
                return origIp;
            }
            if (origIpObj is string origIpStr && IPAddress.TryParse(origIpStr, out var parsedOrig))
            {
                return parsedOrig;
            }
        }

        if (context.Connection.RemoteIpAddress != null)
        {
            return context.Connection.RemoteIpAddress;
        }

        if (context.User?.FindFirst("ip")?.Value is { Length: > 0 } ipClaim &&
            IPAddress.TryParse(ipClaim, out var parsedClaim))
        {
            return parsedClaim;
        }

        return IPAddress.Loopback;
    }
}
