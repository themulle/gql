namespace GqlGateway.Application.Mcp.Interfaces;

using GqlGateway.Domain.Model;

/// <summary>
/// Thread-safe singleton store managing active MCP sessions across scoped HTTP requests.
/// </summary>
public interface IMcpSessionStore
{
    /// <summary>
    /// Creates and persists a new session.
    /// </summary>
    McpSessionContext CreateSession(
        string servicePrincipalId,
        string tenantId,
        string? userSid = null,
        System.Collections.Generic.IReadOnlyList<string>? roles = null,
        System.Collections.Generic.IReadOnlyList<string>? groupSids = null);

    /// <summary>
    /// Retrieves an active session by ID.
    /// </summary>
    McpSessionContext? GetSession(string sessionId);

    /// <summary>
    /// Removes and terminates an active session.
    /// </summary>
    bool RemoveSession(string sessionId);

    /// <summary>
    /// Registers an active SSE event sender callback for the specified session.
    /// </summary>
    void RegisterSseSender(string sessionId, System.Func<string, string, System.Threading.Tasks.Task> sendEventAsync);

    /// <summary>
    /// Sends an SSE event to the client stream of an active session.
    /// </summary>
    System.Threading.Tasks.Task<bool> SendEventAsync(string sessionId, string eventType, string eventData);
}
