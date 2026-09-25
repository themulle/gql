using HotChocolate;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GqlGateway.Api.Middleware;

public sealed class ErrorSanitizingFilter : IErrorFilter
{
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ErrorSanitizingFilter> _logger;

    public ErrorSanitizingFilter(IHostEnvironment environment, ILogger<ErrorSanitizingFilter> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    private static readonly HashSet<string> WhitelistedSafeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FORBIDDEN",
        "UNAUTHORIZED",
        "NOT_FOUND",
        "RATE_LIMIT_EXCEEDED",
        "QUERY_TOO_COMPLEX",
        "BAD_REQUEST",
        "ALREADY_PROCESSED",
        "VALIDATION_ERROR"
    };

    public IError OnError(IError error)
    {
        if (_environment.IsDevelopment())
        {
            return error;
        }

        // In non-development (Staging, QA, Production):
        if (error.Exception != null)
        {
            _logger.LogError(error.Exception, "GraphQL Execution Error [{Code}]: {Message}", error.Code, error.Message);
        }

        // Whitelisted client codes: retain safe message/code, but strictly strip internal exception details
        if (error.Code != null && WhitelistedSafeCodes.Contains(error.Code))
        {
            return error.RemoveException();
        }

        // Non-whitelisted or unhandled technical exceptions: mask as generic INTERNAL_SERVER_ERROR
        return error
            .WithMessage("Ein interner Serverfehler ist aufgetreten.")
            .WithCode("INTERNAL_SERVER_ERROR")
            .RemoveException();
    }
}
