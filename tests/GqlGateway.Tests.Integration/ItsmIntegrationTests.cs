namespace GqlGateway.Tests.Integration;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public class ItsmIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private const string WebhookSecret = "super-secret-itsm-token-12345";

    public ItsmIntegrationTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("ITSM__WEBHOOK_SECRET", WebhookSecret);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "500");
            builder.UseSetting("Gateway:Itsm:Enabled", "true");
            builder.UseSetting("Gateway:Itsm:InstanceToTenantMap:inst-tenant-a", "tenant-a");
            builder.UseSetting("Gateway:Itsm:InstanceToTenantMap:inst-tenant-b", "tenant-b");
        });
    }

    private string ComputeSignature(string payload, DateTimeOffset? timestamp = null)
    {
        var keyBytes = Encoding.UTF8.GetBytes(WebhookSecret);
        var message = timestamp.HasValue
            ? $"t={timestamp.Value:O}.v1={payload}"
            : payload;
        var hash = HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash);
    }

    [Fact]
    public async Task Webhook_WithInvalidSignature_Returns401Unauthorized()
    {
        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = "INC1001",
            InstanceId = "inst-tenant-a",
            Action = "APPROVE"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", "DEADBEEF01020304");
        request.Headers.Add("X-ITSM-Timestamp", DateTimeOffset.UtcNow.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_WithExpiredTimestamp_Returns401Unauthorized()
    {
        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = "INC1002",
            InstanceId = "inst-tenant-a",
            Action = "APPROVE"
        });

        var signature = ComputeSignature(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", signature);
        request.Headers.Add("X-ITSM-Timestamp", DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_WithCrossTenantMismatch_RejectsAndDoesNotActivateConsent()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        // Create consent request for tenant-a
        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();

        var ticketId = $"TICKET-{Guid.NewGuid():N}";
        var consentReq = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-USER-1"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-USER-1",
            BusinessJustification = "Cross-tenant mismatch test",
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        await repo.CreateConsentRequestAsync(consentReq);

        // Webhook callback comes from instance bound to tenant-b!
        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = ticketId,
            InstanceId = "inst-tenant-b", // Maps to tenant-b -> mismatch!
            Action = "APPROVE"
        });

        var signature = ComputeSignature(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", signature);
        request.Headers.Add("X-ITSM-Timestamp", DateTimeOffset.UtcNow.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Verify request is STILL in PENDING_EXTERNAL_APPROVAL (not activated)
        var updated = await repo.GetConsentRequestAsync(consentReq.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
    }

    [Fact]
    public async Task Webhook_WithValidSignatureAndTenant_ActivatesConsentIdempotently()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();

        var ticketId = $"TICKET-{Guid.NewGuid():N}";
        var consentReq = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-USER-2"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-USER-2",
            BusinessJustification = "Valid webhook test",
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        await repo.CreateConsentRequestAsync(consentReq);

        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = ticketId,
            InstanceId = "inst-tenant-a", // Maps to tenant-a -> matches!
            Action = "APPROVE"
        });

        var signature = ComputeSignature(payload);
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change"))
        {
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            request.Headers.Add("X-ITSM-Signature", signature);
            request.Headers.Add("X-ITSM-Timestamp", DateTimeOffset.UtcNow.ToString("O"));

            var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Verify request is now APPROVED
        var updated = await repo.GetConsentRequestAsync(consentReq.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("APPROVED");

        // Verify active consent was created
        var activeConsents = await repo.GetActiveConsentsForSubjectsAsync(
            [new Sid("S-1-5-21-USER-2")],
            tableId,
            DateTimeOffset.UtcNow);
        activeConsents.Count.ShouldBeGreaterThan(0);

        // Repeat webhook call (idempotency check)
        using (var repeatRequest = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change"))
        {
            repeatRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            repeatRequest.Headers.Add("X-ITSM-Signature", signature);
            repeatRequest.Headers.Add("X-ITSM-Timestamp", DateTimeOffset.UtcNow.ToString("O"));

            var repeatResponse = await client.SendAsync(repeatRequest);
            repeatResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Webhook_WithoutTimestampHeader_Returns400BadRequest()
    {
        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = "INC1001",
            InstanceId = "inst-tenant-a",
            Action = "APPROVE"
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", "DEADBEEF01020304");
        // No X-ITSM-Timestamp header!

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Webhook_WhenActionIsReject_RejectsConsentRequest()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();

        var ticketId = $"REJECT-TICKET-{Guid.NewGuid():N}";
        var consentReq = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-REJECT-USER"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-REJECT-USER",
            BusinessJustification = "Reject webhook test",
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        await repo.CreateConsentRequestAsync(consentReq);

        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = ticketId,
            InstanceId = "inst-tenant-a",
            Action = "REJECT",
            Reason = "Denied by governance review in ServiceNow"
        });

        var timestamp = DateTimeOffset.UtcNow;
        var signature = ComputeSignature(payload, timestamp);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", signature);
        request.Headers.Add("X-ITSM-Timestamp", timestamp.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Verify request is REJECTED
        var updated = await repo.GetConsentRequestAsync(consentReq.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("REJECTED");

        // Verify NO active consent was created
        var activeConsents = await repo.GetActiveConsentsForSubjectsAsync(
            [new Sid("S-1-5-21-REJECT-USER")],
            tableId,
            DateTimeOffset.UtcNow);
        activeConsents.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Webhook_WithTimestampBoundSignature_ActivatesConsentSuccessfully()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(tableId);
        meta.ShouldNotBeNull();

        var ticketId = $"TS-BOUND-TICKET-{Guid.NewGuid():N}";
        var consentReq = new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-TS-USER"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-TS-USER",
            BusinessJustification = "Timestamp-bound signature test",
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId,
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        };
        await repo.CreateConsentRequestAsync(consentReq);

        var client = _factory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            TicketId = ticketId,
            InstanceId = "inst-tenant-a",
            Action = "APPROVE"
        });

        var timestamp = DateTimeOffset.UtcNow;
        var signature = ComputeSignature(payload, timestamp); // Signs $"t={timestamp:O}.v1={payload}"
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/itsm/status-change");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-ITSM-Signature", signature);
        request.Headers.Add("X-ITSM-Timestamp", timestamp.ToString("O"));

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Verify request is APPROVED
        var updated = await repo.GetConsentRequestAsync(consentReq.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe("APPROVED");
    }

    [Fact]
    public async Task Mutation_RequestTableAccess_WhenItsmFails_RollsBackRequestLeavingZeroOrphanedRecords()
    {
        var offlineFactory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Itsm:ServiceNowBaseUrl", "http://127.0.0.1:54321/offline");
        });

        var client = offlineFactory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-a");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-USER-1");

        var uniqueJustification = $"Rollback-Test-{Guid.NewGuid():N}";
        var mutation = $$"""
            mutation {
                requestTableAccess(
                    domain: "finance",
                    schema: "dbo",
                    tableName: "finance_table_1",
                    justification: "{{uniqueJustification}}",
                    durationDays: 30
                ) {
                    requestId
                    status
                    message
                }
            }
            """;

        var response = await client.PostAsJsonAsync("/graphql", new { query = mutation });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("ITSM_UNAVAILABLE");

        // Verify that 0 requests exist in DB with this justification (rolled back)
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        var pendingRequests = await repo.GetPendingRequestsForApproverAsync(new Sid("S-1-5-21-DATAOWNER-1"));
        pendingRequests.Any(r => r.BusinessJustification == uniqueJustification).ShouldBeFalse();
    }
}
