using System.Security.Claims;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using GqlGateway.GraphQL.Types;
using GqlGateway.Infrastructure.Cache;
using GqlGateway.Infrastructure.Persistence;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace GqlGateway.Tests.Unit;

public class GovernanceSecurityTests : IDisposable
{
    private readonly SqliteGovernanceRepository _repository;
    private readonly Mutation _mutation;

    public GovernanceSecurityTests()
    {
        var epochService = new EpochValidationService();
        _repository = new SqliteGovernanceRepository(epochService);
        _mutation = new Mutation();
    }

    public void Dispose()
    {
        _repository.Dispose();
    }

    private sealed class IsolatedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private static IHttpContextAccessor CreateAccessor(Sid userSid, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, userSid.Value),
            new(ClaimTypes.NameIdentifier, userSid.Value),
            new("objectSid", userSid.Value),
            new(ClaimTypes.Name, $"CORP\\{userSid.Value}")
        };
        foreach (var r in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, r));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth", ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var context = new DefaultHttpContext { User = principal };
        return new IsolatedHttpContextAccessor(context);
    }

    [Fact]
    public async Task RevokeConsent_WhenCalledByUnauthorizedUser_ThrowsForbidden()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var granteeSid = new Sid("S-1-5-21-GRANTEE-1");
        var unauthorizedUserSid = new Sid("S-1-5-21-ATTACKER-99");

        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = granteeSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        var accessor = CreateAccessor(unauthorizedUserSid);

        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await _mutation.RevokeConsentAsync(consent.Id, "Malicious revocation", _repository, accessor);
        });

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN");
    }

    [Fact]
    public async Task RevokeConsent_WhenCalledByTableDataOwner_SucceedsAndRecordsAudit()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var ownerSid = new Sid("S-1-5-21-DATAOWNER-1");
        var granteeSid = new Sid("S-1-5-21-GRANTEE-2");

        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = granteeSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        var accessor = CreateAccessor(ownerSid);

        var result = await _mutation.RevokeConsentAsync(consent.Id, "Legitimate owner revocation", _repository, accessor);
        result.ShouldBeTrue();

        var activeConsents = await _repository.GetActiveConsentsForSubjectsAsync(new[] { granteeSid }, table, DateTimeOffset.UtcNow);
        activeConsents.ShouldBeEmpty();

        var auditEntries = await _repository.GetAuditLogEntriesAsync(10);
        auditEntries.ShouldContain(e => e.EventType == "CONSENT_REVOKED" && e.ActorSid == ownerSid);
    }

    [Fact]
    public async Task RevokeConsent_WhenCalledByGranteeSelf_Succeeds()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var granteeSid = new Sid("S-1-5-21-GRANTEE-SELF");

        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = granteeSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        var accessor = CreateAccessor(granteeSid);

        var result = await _mutation.RevokeConsentAsync(consent.Id, "Self revocation", _repository, accessor);
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task ApproveConsent_WhenUserHasDataOwnerRole_ButNotOwnerOfSpecificTable_ThrowsForbidden()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var requesterSid = new Sid("S-1-5-21-REQUESTER-1");
        var genericDataOwnerSid = new Sid("S-1-5-21-GENERIC-DATAOWNER-NOT-ASSIGNED");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Testing role bypass",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(14)
        });

        // User has AD role 'DataOwner', but is NOT registered as owner for finance_table_1
        var accessor = CreateAccessor(genericDataOwnerSid, "DataOwner");

        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await _mutation.ApproveConsentRequestAsync(req.Id, "key-test-do-role", _repository, accessor);
        });

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN");
    }

    [Fact]
    public async Task ConsentRequest_StateMachine_PreventsReApprovalOrRejection()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var requesterSid = new Sid("S-1-5-21-REQ-SM");
        var approverSid = new Sid("S-1-5-21-DATAOWNER-1");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "State machine test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        var approved = await _repository.ApproveConsentRequestStepAsync(req.Id, approverSid);
        approved.Status.ShouldBe("APPROVED");

        // Attempting to approve an already APPROVED request must throw InvalidOperationException
        var anotherApprover = new Sid("S-1-5-21-APPROVER-2");
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await _repository.ApproveConsentRequestStepAsync(req.Id, anotherApprover);
        });

        // Attempting to reject an already APPROVED request must throw InvalidOperationException
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await _repository.RejectConsentRequestAsync(req.Id, anotherApprover, "Too late");
        });
    }

    [Fact]
    public async Task Idempotency_WhenDifferentUsersUseSameKey_ExecutesSeparately()
    {
        var userA = new Sid("S-1-5-21-USER-AAA");
        var userB = new Sid("S-1-5-21-USER-BBB");
        const string sharedIdempotencyKey = "shared-client-key-42";

        var accessorA = CreateAccessor(userA);
        var accessorB = CreateAccessor(userB);

        var payloadA = await _mutation.RequestTableAccessAsync(
            "finance", "dbo", "finance_table_1", "Reason A", 7, sharedIdempotencyKey, _repository, accessorA);

        var payloadB = await _mutation.RequestTableAccessAsync(
            "finance", "dbo", "finance_table_1", "Reason B", 7, sharedIdempotencyKey, _repository, accessorB);

        // Payloads must not collide!
        payloadA.RequestId.ShouldNotBe(payloadB.RequestId);
    }

    [Fact]
    public async Task GetPendingRequestsForApprover_ReturnsOnlyAssignedTables()
    {
        var tableFinance = new TableIdentifier("finance", "dbo", "finance_table_1");
        var tableSales = new TableIdentifier("sales", "dbo", "sales_table_1");

        var metaFinance = await _repository.GetTableMetadataAsync(tableFinance);
        var metaSales = await _repository.GetTableMetadataAsync(tableSales);
        metaFinance.ShouldNotBeNull();
        metaSales.ShouldNotBeNull();

        // DATAOWNER-1 owns finance_table_1 (seeded). Let's submit requests for finance and sales.
        var ownerFinance = new Sid("S-1-5-21-DATAOWNER-1");
        var requester = new Sid("S-1-5-21-SOME-USER");

        var reqFinance = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = metaFinance.Table.Id,
            TableIdentifier = tableFinance,
            RequesterSid = requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requester.Value,
            BusinessJustification = "Finance access",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(14)
        });

        var reqSales = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = metaSales.Table.Id,
            TableIdentifier = tableSales,
            RequesterSid = requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requester.Value,
            BusinessJustification = "Sales access",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(14)
        });

        var pendingForFinanceOwner = await _repository.GetPendingRequestsForApproverAsync(ownerFinance);

        pendingForFinanceOwner.ShouldContain(r => r.Id == reqFinance.Id);
        pendingForFinanceOwner.ShouldNotContain(r => r.Id == reqSales.Id);
    }

    [Fact]
    public async Task GetPendingRequests_WhenDelegationExpired_ExcludesRequests()
    {
        // Expired delegation must not grant approval view
        var tableFinance = new TableIdentifier("finance", "dbo", "finance_table_1");
        var metaFinance = await _repository.GetTableMetadataAsync(tableFinance);
        metaFinance.ShouldNotBeNull();

        var owners = await _repository.GetDataOwnersForTableAsync(tableFinance);
        var activeOwner = owners.First(o => o.IsActive);

        var delegateSid = new Sid("S-1-5-21-EXPIRED-DELEGATE");

        // Expired delegation: valid_to is in the past
        await _repository.DelegateDataOwnershipAsync(new DataOwnerDelegation
        {
            DataOwnerId = activeOwner.Id,
            DelegateSid = delegateSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-10),
            ValidTo = DateTimeOffset.UtcNow.AddMinutes(-1), // Expired!
            Reason = "Vacation coverage in the past"
        });

        var requester = new Sid("S-1-5-21-REQUESTER-EXPIRED");
        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = metaFinance.Table.Id,
            TableIdentifier = tableFinance,
            RequesterSid = requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requester.Value,
            BusinessJustification = "Access request",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        var pendingForDelegate = await _repository.GetPendingRequestsForApproverAsync(delegateSid);
        pendingForDelegate.ShouldNotContain(r => r.Id == req.Id);
    }

    [Fact]
    public async Task RevokeConsent_GroupGrantee_CannotBeRevokedByMemberViaGranteeSelf()
    {
        // GranteeSelf must strictly apply only to GranteeType.User
        var table = new TableIdentifier("sales", "dbo", "sales_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var groupSid = new Sid("S-1-5-21-SALES-GROUP");

        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group, // GROUP, not User!
            GranteeSid = groupSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        // Caller claiming to have the group's SID directly
        var accessor = CreateAccessor(groupSid);

        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
        {
            await _mutation.RevokeConsentAsync(consent.Id, "Trying self-revoke on group", _repository, accessor);
        });

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN");
    }

    [Fact]
    public async Task RejectConsentRequest_WhenSecondStepRejection_IncrementsStepNumberWithoutUniqueConstraintViolation()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_5");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();
        meta.Table.RequiresFourEyes.ShouldBeTrue();

        var requester = new Sid("S-1-5-21-REQ-1");
        var approver1 = new Sid("S-1-5-21-APP-1");
        var approver2 = new Sid("S-1-5-21-APP-2");

        var owners = await _repository.GetDataOwnersForTableAsync(table);
        var primaryOwner = owners[0];

        await _repository.DelegateDataOwnershipAsync(new DataOwnerDelegation
        {
            Id = Guid.NewGuid(),
            DataOwnerId = primaryOwner.Id,
            DelegateSid = approver1,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Reason = "Four eyes step 1 delegation"
        });

        await _repository.DelegateDataOwnershipAsync(new DataOwnerDelegation
        {
            Id = Guid.NewGuid(),
            DataOwnerId = primaryOwner.Id,
            DelegateSid = approver2,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Reason = "Four eyes step 2 delegation"
        });

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requester,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requester.Value,
            BusinessJustification = "Four eyes rejection test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        // Step 1: Approved by Approver 1 -> Status is PENDING_SECOND_APPROVAL
        var approvedStep1 = await _repository.ApproveConsentRequestStepAsync(req.Id, approver1);
        approvedStep1.Status.ShouldBe("PENDING_SECOND_APPROVAL");

        // Step 2: Rejected by Approver 2 -> Must succeed without UNIQUE constraint violation on step_number
        var rejectedReq = await _repository.RejectConsentRequestAsync(req.Id, approver2, "Second approver rejected");
        rejectedReq.Status.ShouldBe("REJECTED");
    }

    [Fact]
    public async Task GetTransitiveRoles_ReturnsRoleNamesAsStrings()
    {
        var userSid = new Sid("S-1-5-21-TRANSITIVE-USER");
        var roles = await _repository.GetTransitiveRolesAsync(userSid);
        roles.ShouldNotBeNull();
        roles.ShouldBeAssignableTo<IReadOnlySet<string>>();
    }

    [Fact]
    public async Task IsAuthorizedApprover_WhenDirectOwnerOrDelegatedOrUnauthorized()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var directOwnerSid = new Sid("S-1-5-21-DATAOWNER-1");
        var delegateSid = new Sid("S-1-5-21-DELEGATE-TEST");
        var unrelatedSid = new Sid("S-1-5-21-RANDOM-USER");

        // 1. Direct owner is authorized
        var isDirect = await _repository.IsAuthorizedApproverForTableAsync(table, directOwnerSid);
        isDirect.ShouldBeTrue();

        // 2. Unrelated user is not authorized
        var isUnrelated = await _repository.IsAuthorizedApproverForTableAsync(table, unrelatedSid);
        isUnrelated.ShouldBeFalse();

        // 3. Delegate to delegateSid
        var owners = await _repository.GetDataOwnersForTableAsync(table);
        var primaryOwner = owners.First(o => o.AdSid == directOwnerSid);

        await _repository.DelegateDataOwnershipAsync(new DataOwnerDelegation
        {
            Id = Guid.NewGuid(),
            DataOwnerId = primaryOwner.Id,
            DelegateSid = delegateSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Reason = "Vacation delegation"
        });

        // Delegate is now authorized
        var isDelegate = await _repository.IsAuthorizedApproverForTableAsync(table, delegateSid);
        isDelegate.ShouldBeTrue();
    }

    [Fact]
    public async Task ApproveConsent_WhenUserIsDelegatedApprover_Succeeds()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var directOwnerSid = new Sid("S-1-5-21-DATAOWNER-1");
        var delegateSid = new Sid("S-1-5-21-DELEGATE-APPROVE");
        var requesterSid = new Sid("S-1-5-21-REQUESTER-DELEGATE");

        var owners = await _repository.GetDataOwnersForTableAsync(table);
        var primaryOwner = owners.First(o => o.AdSid == directOwnerSid);

        await _repository.DelegateDataOwnershipAsync(new DataOwnerDelegation
        {
            Id = Guid.NewGuid(),
            DataOwnerId = primaryOwner.Id,
            DelegateSid = delegateSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Reason = "Coverage delegation"
        });

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Delegate approval test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        var accessor = CreateAccessor(delegateSid);
        var result = await _mutation.ApproveConsentRequestAsync(req.Id, "key-del-app", _repository, accessor);
        result.Status.ShouldBe("APPROVED");
    }

    [Fact]
    public async Task RevokeConsent_WhenCalledDirectlyOnRepoByUnauthorizedUser_ThrowsUnauthorizedAccessException()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var granteeSid = new Sid("S-1-5-21-GRANTEE-DIRECT-REVOKE");
        var attackerSid = new Sid("S-1-5-21-ATTACKER-REVOKE");

        var consent = await _repository.CreateConsentAsync(new Consent
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = granteeSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        // Calling repo.RevokeConsentAsync directly without authorization must throw UnauthorizedAccessException
        await Should.ThrowAsync<UnauthorizedAccessException>(async () =>
        {
            await _repository.RevokeConsentAsync(consent.Id, attackerSid, "Malicious direct revocation");
        });
    }

    [Fact]
    public async Task ApproveConsentRequestStep_WhenCalledDirectlyOnRepoByUnauthorizedUser_ThrowsUnauthorizedAccessException()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var requesterSid = new Sid("S-1-5-21-REQ-DIRECT-APP");
        var unauthorizedApproverSid = new Sid("S-1-5-21-UNAUTHORIZED-APPROVER");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Direct approval auth test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        // Calling repo.ApproveConsentRequestStepAsync directly without authorization must throw UnauthorizedAccessException
        await Should.ThrowAsync<UnauthorizedAccessException>(async () =>
        {
            await _repository.ApproveConsentRequestStepAsync(req.Id, unauthorizedApproverSid);
        });
    }

    [Fact]
    public async Task RejectConsentRequest_WhenCalledDirectlyOnRepoByUnauthorizedUser_ThrowsUnauthorizedAccessException()
    {
        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await _repository.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var requesterSid = new Sid("S-1-5-21-REQ-DIRECT-REJ");
        var unauthorizedApproverSid = new Sid("S-1-5-21-UNAUTHORIZED-APPROVER");

        var req = await _repository.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = table,
            RequesterSid = requesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = requesterSid.Value,
            BusinessJustification = "Direct rejection auth test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7)
        });

        // Calling repo.RejectConsentRequestAsync directly without authorization must throw UnauthorizedAccessException
        await Should.ThrowAsync<UnauthorizedAccessException>(async () =>
        {
            await _repository.RejectConsentRequestAsync(req.Id, unauthorizedApproverSid, "Malicious rejection");
        });
    }

    private sealed class DummyHostEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void SqliteGovernanceRepository_InProductionWithoutKeyVaultSecret_ThrowsInvalidOperationException()
    {
        var epochService = new EpochValidationService();
        var prodEnv = new DummyHostEnvironment("Production");
        var opts = Microsoft.Extensions.Options.Options.Create(new GqlGateway.Domain.Options.GatewayOptions
        {
            GovernanceDb = new GqlGateway.Domain.Options.GovernanceDbOptions
            {
                ConnectionString = "Data Source=test_prod.db;Mode=ReadWriteCreate;"
            }
        });

        Should.Throw<InvalidOperationException>(() =>
        {
            using var repo = new SqliteGovernanceRepository(epochService, opts, prodEnv, secretProvider: null);
        });
    }

    [Fact]
    public async Task ServiceNowClient_InProductionWithoutBaseAddress_ReturnsError()
    {
        var prodEnv = new DummyHostEnvironment("Production");
        using var httpClient = new System.Net.Http.HttpClient(); // BaseAddress is null
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<GqlGateway.Extensions.Itsm.ServiceNowClient>.Instance;
        var client = new GqlGateway.Extensions.Itsm.ServiceNowClient(httpClient, logger, prodEnv);

        var result = await client.CreateAccessTicketAsync(new ItsmTicketRequest(
            new TenantId("tenant-a"),
            new Sid("S-1-5-21-1234"),
            new TableIdentifier("finance", "dbo", "invoices"),
            "Need access",
            7,
            null,
            null));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ITSM_NOT_CONFIGURED");
    }
}
