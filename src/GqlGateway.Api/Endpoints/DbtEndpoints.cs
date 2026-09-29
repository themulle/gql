namespace GqlGateway.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GqlGateway.Api.Middleware;
using GqlGateway.Application.Dbt.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class DbtEndpoints
{
    public static IEndpointRouteBuilder MapDbtEndpoints(this IEndpointRouteBuilder app)
    {
        // dbt Ingestion & Exposure Endpoints (F-DATA-11)
        app.MapPost("/api/extensions/dbt/sync", async (
            HttpContext context,
            IDbtMetadataIngestionService dbtService) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner") ||
                               context.User.IsInRole("DbtAdmin");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.Request.ContentLength > 100 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Manifest size exceeds maximum allowed size (100 MB)." });
            }

            var dryRun = context.Request.Query.ContainsKey("dryRun") &&
                         bool.TryParse(context.Request.Query["dryRun"], out var dr) && dr;

            var result = await dbtService.IngestManifestStreamAsync(context.Request.Body, dryRun, context.RequestAborted);
            if (!result.Success)
            {
                return Results.BadRequest(result);
            }

            return Results.Ok(result);
        }).RequireAuthorization();

        app.MapGet("/api/extensions/dbt/exposures", async (
            IDbtExposurePublisher exposurePublisher,
            HttpContext context) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var yaml = await exposurePublisher.GenerateExposuresYamlAsync(context.RequestAborted);
            return Results.Content(yaml, "text/yaml; charset=utf-8");
        }).RequireAuthorization();

        app.MapGet("/api/extensions/dbt/proposals", async (
            IDbtMetadataIngestionService dbtService,
            HttpContext context) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            TableIdentifier? table = null;
            if (context.Request.Query.TryGetValue("database", out var db) &&
                context.Request.Query.TryGetValue("schema", out var schema) &&
                context.Request.Query.TryGetValue("table", out var tableName))
            {
                table = new TableIdentifier(db!, schema!, tableName!);
            }

            var proposals = await dbtService.GetPendingProposalsAsync(table, context.RequestAborted);
            return Results.Ok(proposals);
        }).RequireAuthorization();

        app.MapPost("/api/extensions/dbt/proposals/{id:guid}/approve", async (
            Guid id,
            IDbtMetadataIngestionService dbtService,
            HttpContext context) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var user = context.User.Identity?.Name ?? context.User.GetUserSid()?.Value ?? "system_admin";
            try
            {
                var approved = await dbtService.ApproveProposalAsync(id, user, context.RequestAborted);
                return Results.Ok(approved);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Proposal '{id}' not found." });
            }
        }).RequireAuthorization();

        app.MapPost("/api/extensions/dbt/proposals/{id:guid}/reject", async (
            Guid id,
            IDbtMetadataIngestionService dbtService,
            HttpContext context) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var user = context.User.Identity?.Name ?? context.User.GetUserSid()?.Value ?? "system_admin";
            try
            {
                var rejected = await dbtService.RejectProposalAsync(id, user, context.RequestAborted);
                return Results.Ok(rejected);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Proposal '{id}' not found." });
            }
        }).RequireAuthorization();

        app.MapPost("/api/extensions/dbt/validate-contract", async (
            HttpContext context,
            IDbtContractValidator validator) =>
        {
            if (context.Request.ContentLength > 100 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Manifest size exceeds maximum allowed size (100 MB)." });
            }

            var result = await validator.ValidateContractsStreamAsync(context.Request.Body, context.RequestAborted);
            return result.IsCompatible ? Results.Ok(result) : Results.UnprocessableEntity(result);
        }).RequireAuthorization();

        // dbt Health & Circuit Breaker Endpoints (F-DBT-1)
        app.MapPost("/api/extensions/dbt/run-results", async (
            HttpContext context,
            IDbtHealthCircuitBreaker circuitBreaker) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.Request.ContentLength > 50 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Run results payload exceeds maximum allowed size (50 MB)." });
            }

            var report = await circuitBreaker.RecordRunResultsAsync(context.Request.Body, context.RequestAborted);
            return Results.Ok(report);
        }).RequireAuthorization();

        app.MapGet("/api/extensions/dbt/health", async (
            HttpContext context,
            IDbtHealthCircuitBreaker circuitBreaker) =>
        {
            if (context.Request.Query.TryGetValue("table", out var tableName) && !string.IsNullOrWhiteSpace(tableName))
            {
                var db = context.Request.Query.TryGetValue("database", out var dbVal) && !string.IsNullOrWhiteSpace(dbVal) ? dbVal.ToString() : "default";
                var schema = context.Request.Query.TryGetValue("schema", out var schemaVal) && !string.IsNullOrWhiteSpace(schemaVal) ? schemaVal.ToString() : "default";
                var tableId = new TableIdentifier(db, schema, tableName!);
                var health = await circuitBreaker.GetTableHealthAsync(tableId, context.RequestAborted);
                return Results.Ok(health);
            }

            var allStates = await circuitBreaker.GetAllHealthStatesAsync(context.RequestAborted);
            return Results.Ok(allStates);
        }).RequireAuthorization();

        app.MapPost("/api/extensions/dbt/health/reset", async (
            HttpContext context,
            IDbtHealthCircuitBreaker circuitBreaker) =>
        {
            var isPrivileged = context.User.IsInRole("GovernanceAdmin") ||
                               context.User.IsInRole("ClusterAdmin") ||
                               context.User.IsInRole("DataOwner");
            if (!isPrivileged)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.Request.Query.TryGetValue("table", out var tableName) && !string.IsNullOrWhiteSpace(tableName))
            {
                var db = context.Request.Query.TryGetValue("database", out var dbVal) && !string.IsNullOrWhiteSpace(dbVal) ? dbVal.ToString() : "default";
                var schema = context.Request.Query.TryGetValue("schema", out var schemaVal) && !string.IsNullOrWhiteSpace(schemaVal) ? schemaVal.ToString() : "default";
                var tableId = new TableIdentifier(db, schema, tableName!);
                await circuitBreaker.ResetTableHealthAsync(tableId, context.RequestAborted);
                return Results.Ok(new { message = $"Table '{tableId}' health reset to healthy." });
            }

            await circuitBreaker.ResetAllAsync(context.RequestAborted);
            return Results.Ok(new { message = "All table health states reset to healthy." });
        }).RequireAuthorization();

        // F-DBT-4: dbt Cloud & Orchestrator HMAC Webhook Receiver
        app.MapPost("/api/extensions/dbt/webhooks/dbt-cloud", async (
            HttpContext context,
            IDbtWebhookReceiver webhookReceiver) =>
        {
            if (context.Request.ContentLength > 10 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Webhook payload exceeds maximum allowed size (10 MB)." });
            }

            using var reader = new StreamReader(context.Request.Body, System.Text.Encoding.UTF8);
            var payload = await reader.ReadToEndAsync(context.RequestAborted);

            string? signatureHeader = null;
            if (context.Request.Headers.TryGetValue("X-Dbt-Signature", out var dbtSig))
            {
                signatureHeader = dbtSig.ToString();
            }
            else if (context.Request.Headers.TryGetValue("X-Hub-Signature-256", out var hubSig))
            {
                signatureHeader = hubSig.ToString();
            }

            var result = await webhookReceiver.ProcessWebhookAsync(payload, signatureHeader, context.RequestAborted);
            if (!result.Success)
            {
                return Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(result);
        }).AllowAnonymous();

        return app;
    }
}
