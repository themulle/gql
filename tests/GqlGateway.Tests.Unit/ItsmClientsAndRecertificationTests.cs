namespace GqlGateway.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.Workflows;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using GqlGateway.Infrastructure.Itsm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public class ItsmClientsAndRecertificationTests
{
    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    [Fact]
    public async Task ServiceNowTableApiClient_ShouldCreateTicketAndReturnTicketId()
    {
        // Arrange
        var mockHandler = new DelegatingHandlerStub(req =>
        {
            req.Method.ShouldBe(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.ShouldContain("/api/now/table/change_request");
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""
                    {
                        "result": {
                            "sys_id": "sys_abc123456",
                            "number": "CHG003001"
                        }
                    }
                    """)
            };
        });

        var httpClient = new HttpClient(mockHandler);
        var options = Options.Create(new GatewayOptions
        {
            Itsm = new ItsmOptions
            {
                ServiceNowBaseUrl = "https://instance.service-now.com",
                ServiceNowUsername = "admin",
                ServiceNowPassword = "password",
                ServiceNowTable = "change_request"
            }
        });

        var client = new ServiceNowTableApiClient(httpClient, options, NullLogger<ServiceNowTableApiClient>.Instance);
        var request = new ItsmTicketRequest(
            new TenantId("tenant-1"),
            new Sid("S-1-5-21-requester"),
            new TableIdentifier("finance", "dbo", "invoices"),
            "Need access for annual audit",
            14,
            JustificationCategory.LegitimateAudit,
            0.95);

        // Act
        var result = await client.CreateAccessTicketAsync(request);

        // Assert
        result.Success.ShouldBeTrue();
        result.TicketReference.ShouldNotBeNull();
        result.TicketReference.TicketId.ShouldBe("CHG003001");
        result.TicketReference.TicketUrl.ShouldContain("sys_abc123456");
    }

    [Fact]
    public async Task JiraCloudRestClient_ShouldCreateIssueAndReturnIssueKey()
    {
        // Arrange
        var mockHandler = new DelegatingHandlerStub(req =>
        {
            req.Method.ShouldBe(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.ShouldContain("/rest/api/3/issue");
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""
                    {
                        "id": "10024",
                        "key": "SEC-882"
                    }
                    """)
            };
        });

        var httpClient = new HttpClient(mockHandler);
        var options = Options.Create(new GatewayOptions
        {
            Itsm = new ItsmOptions
            {
                JiraBaseUrl = "https://org.atlassian.net",
                JiraEmail = "service@org.com",
                JiraApiToken = "secret-token",
                JiraProjectKey = "SEC"
            }
        });

        var client = new JiraCloudRestClient(httpClient, options, NullLogger<JiraCloudRestClient>.Instance);
        var request = new ItsmTicketRequest(
            new TenantId("tenant-1"),
            new Sid("S-1-5-21-requester"),
            new TableIdentifier("sales", "dbo", "deals"),
            "Quarterly sales analysis",
            7,
            JustificationCategory.LegitimateAudit,
            0.9);

        // Act
        var result = await client.CreateAccessTicketAsync(request);

        // Assert
        result.Success.ShouldBeTrue();
        result.TicketReference.ShouldNotBeNull();
        result.TicketReference.TicketId.ShouldBe("SEC-882");
        result.TicketReference.TicketUrl.ShouldContain("SEC-882");
    }

    [Fact]
    public async Task ConsentRecertificationWorkflowService_ShouldScanAndEnqueueExpiringConsents()
    {
        // Arrange
        var consentRepo = Substitute.For<IConsentRepository>();
        var outboxRepo = Substitute.For<IItsmOutboxRepository>();
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var options = Options.Create(new GatewayOptions());

        var consentId = Guid.NewGuid();
        var expiringConsent = new Consent
        {
            Id = consentId,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("crm", "dbo", "leads"),
            GranteeSid = new Sid("S-1-5-21-lead-user"),
            TenantId = new TenantId("tenant-alpha"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-10),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1) // Expires in 24 hours
        };

        consentRepo.GetExpiringConsentsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([expiringConsent]));

        var workflow = new ConsentRecertificationWorkflowService(
            consentRepo,
            outboxRepo,
            options,
            NullLogger<ConsentRecertificationWorkflowService>.Instance,
            auditRepo);

        // Act
        var enqueuedCount = await workflow.ScanAndTriggerExpiringConsentRecertificationsAsync();

        // Assert
        enqueuedCount.ShouldBe(1);
        await outboxRepo.Received(1).EnqueueAsync(
            Arg.Is<ItsmOutboxMessage>(m => m.EventType == "ConsentRecertificationRequired" && m.RequestId == consentId.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsentRecertificationWorkflowService_ExtendConsentExpiry_ShouldExtendAndAudit()
    {
        // Arrange
        var consentRepo = Substitute.For<IConsentRepository>();
        var outboxRepo = Substitute.For<IItsmOutboxRepository>();
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var options = Options.Create(new GatewayOptions());

        var consentId = Guid.NewGuid();
        var existingConsent = new Consent
        {
            Id = consentId,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("crm", "dbo", "leads"),
            GranteeSid = new Sid("S-1-5-21-lead-user"),
            TenantId = new TenantId("tenant-alpha"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-10),
            ValidTo = DateTimeOffset.UtcNow.AddHours(2)
        };

        consentRepo.GetConsentByIdAsync(consentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Consent?>(existingConsent));

        var workflow = new ConsentRecertificationWorkflowService(
            consentRepo,
            outboxRepo,
            options,
            NullLogger<ConsentRecertificationWorkflowService>.Instance,
            auditRepo);

        // Act
        var success = await workflow.ExtendConsentExpiryAsync(
            consentId,
            TimeSpan.FromDays(30),
            new Sid("S-1-5-21-data-steward"),
            "Recertification approved after quarterly access audit.");

        // Assert
        success.ShouldBeTrue();
        await consentRepo.Received(1).ExtendConsentExpiryAsync(
            consentId,
            Arg.Is<DateTimeOffset>(d => d > DateTimeOffset.UtcNow.AddDays(29)),
            Arg.Any<CancellationToken>());
        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(a => a.EventType == "CONSENT_RECERTIFIED_AND_EXTENDED"),
            Arg.Any<CancellationToken>());
    }
}
