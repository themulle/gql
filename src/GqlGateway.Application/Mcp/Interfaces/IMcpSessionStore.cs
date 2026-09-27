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
    McpSessionContext CreateSession(string servicePrincipalId, string tenantId);

    /// <summary>
    /// Retrieves an active session by ID.
    /// </summary>
    McpSessionContext? GetSession(string sessionId);

    /// <summary>
    /// Removes and terminates an active session.
    /// </summary>
    bool RemoveSession(string sessionId);
}
