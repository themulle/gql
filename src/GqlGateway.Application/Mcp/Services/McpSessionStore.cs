namespace GqlGateway.Application.Mcp.Services;

using System;
using System.Collections.Concurrent;
using GqlGateway.Application.Mcp.Interfaces;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Thread-safe in-memory session store for active MCP connections.
/// </summary>
public sealed class McpSessionStore : IMcpSessionStore
{
    private readonly ConcurrentDictionary<string, McpSessionContext> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<string, string, Task>> _sseSenders = new(StringComparer.Ordinal);
    private readonly ILogger<McpSessionStore> _logger;

    public McpSessionStore(ILogger<McpSessionStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public McpSessionContext CreateSession(string servicePrincipalId, string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(servicePrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Strict TenantId domain validation prevents injection into downstream logs/JSON (N-9)
        var validatedTenant = new GqlGateway.Domain.Common.TenantId(tenantId);

        var sessionId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var session = new McpSessionContext(sessionId, servicePrincipalId, validatedTenant.Value, now, now);
        _sessions[sessionId] = session;

        _logger.LogInformation("Created new MCP session {SessionId} for principal {PrincipalId} in tenant {TenantId}.",
            sessionId, servicePrincipalId, validatedTenant.Value);

        return session;
    }

    public McpSessionContext? GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        return _sessions.TryGetValue(sessionId, out var session) ? session : null;
    }

    public bool RemoveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        _sseSenders.TryRemove(sessionId, out _);
        var removed = _sessions.TryRemove(sessionId, out _);
        if (removed)
        {
            _logger.LogInformation("Terminated MCP session {SessionId}.", sessionId);
        }
        return removed;
    }

    public void RegisterSseSender(string sessionId, Func<string, string, Task> sendEventAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(sendEventAsync);
        _sseSenders[sessionId] = sendEventAsync;
    }

    public async Task<bool> SendEventAsync(string sessionId, string eventType, string eventData)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        if (_sseSenders.TryGetValue(sessionId, out var sender))
        {
            try
            {
                await sender(eventType, eventData).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to dispatch SSE event to MCP session {SessionId}.", sessionId);
                _sseSenders.TryRemove(sessionId, out _);
                return false;
            }
        }
        return false;
    }
}
