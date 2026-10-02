namespace GqlGateway.Infrastructure.Diagnostics;

// Forward to Domain.Diagnostics for backwards compatibility
public static class GatewayDiagnostics
{
    public const string ActivitySourceName = GqlGateway.Domain.Diagnostics.GatewayDiagnostics.ActivitySourceName;
    public const string MeterName = GqlGateway.Domain.Diagnostics.GatewayDiagnostics.MeterName;

    public static System.Diagnostics.ActivitySource Source => GqlGateway.Domain.Diagnostics.GatewayDiagnostics.Source;
    public static System.Diagnostics.Metrics.Meter Meter => GqlGateway.Domain.Diagnostics.GatewayDiagnostics.Meter;

    public static System.Diagnostics.Metrics.Counter<long> ForbiddenRequestsCounter => GqlGateway.Domain.Diagnostics.GatewayDiagnostics.ForbiddenRequestsCounter;
    public static System.Diagnostics.Metrics.Counter<long> QueryTooComplexCounter => GqlGateway.Domain.Diagnostics.GatewayDiagnostics.QueryTooComplexCounter;
    public static System.Diagnostics.Metrics.Counter<long> CrossTenantMismatchCounter => GqlGateway.Domain.Diagnostics.GatewayDiagnostics.CrossTenantMismatchCounter;
    public static System.Diagnostics.Metrics.Histogram<double> PolicyEvaluationDuration => GqlGateway.Domain.Diagnostics.GatewayDiagnostics.PolicyEvaluationDuration;
    public static System.Diagnostics.Metrics.Histogram<double> LineageTraversalDuration => GqlGateway.Domain.Diagnostics.GatewayDiagnostics.LineageTraversalDuration;

    public static void SetSafeTag(System.Diagnostics.Activity? activity, string spanName, string key, object? value)
        => GqlGateway.Domain.Diagnostics.GatewayDiagnostics.SetSafeTag(activity, spanName, key, value);
}
