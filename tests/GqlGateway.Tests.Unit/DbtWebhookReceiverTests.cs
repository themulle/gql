namespace GqlGateway.Tests.Unit;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using GqlGateway.Application.Dbt.Interfaces;
using GqlGateway.Domain.Options;
using GqlGateway.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DbtWebhookReceiverTests
{
    private static string ComputeHmacSha256Hex(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [Fact]
    public void ValidateSignature_WithValidHmac_ReturnsTrue()
    {
        var secret = "super-secret-dbt-token-12345";
        var payload = """{"eventId":"evt_1","eventType":"job_run.completed"}""";
        var signature = ComputeHmacSha256Hex(payload, secret);

        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var receiver = new DbtWebhookReceiver(options, circuitBreaker, NullLogger<DbtWebhookReceiver>.Instance);

        var isValid = receiver.ValidateSignature(payload, signature, secret);
        isValid.ShouldBeTrue();

        // Also test with sha256= prefix
        var isValidWithPrefix = receiver.ValidateSignature(payload, "sha256=" + signature, secret);
        isValidWithPrefix.ShouldBeTrue();
    }

    [Fact]
    public void ValidateSignature_WithInvalidHmac_ReturnsFalse()
    {
        var secret = "super-secret-dbt-token-12345";
        var payload = """{"eventId":"evt_1","eventType":"job_run.completed"}""";
        var invalidSignature = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var receiver = new DbtWebhookReceiver(options, circuitBreaker, NullLogger<DbtWebhookReceiver>.Instance);

        var isValid = receiver.ValidateSignature(payload, invalidSignature, secret);
        isValid.ShouldBeFalse();
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithValidPayloadAndSignature_ReturnsSuccess()
    {
        var secret = "test-secret-key";
        var payload = """
        {
            "eventId": "evt_999",
            "eventType": "job_run.completed",
            "timestamp": "2026-09-29T12:00:00Z",
            "accountId": 42,
            "data": {
                "jobId": 101,
                "jobName": "nightly_marts",
                "runId": 5555,
                "runStatus": "Success",
                "environmentId": 7,
                "dbtVersion": "1.8.0"
            }
        }
        """;
        var signature = "sha256=" + ComputeHmacSha256Hex(payload, secret);

        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var receiver = new DbtWebhookReceiver(options, circuitBreaker, NullLogger<DbtWebhookReceiver>.Instance);

        var result = await receiver.ProcessWebhookAsync(payload, signature);

        result.Success.ShouldBeTrue();
        result.RunId.ShouldBe(5555);
        result.EventType.ShouldBe("job_run.completed");
        result.Message.ShouldContain("Success");
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithInvalidSignature_RejectsRequest()
    {
        var secret = "test-secret-key";
        var payload = """{"eventId":"evt_1"}""";

        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var receiver = new DbtWebhookReceiver(options, circuitBreaker, NullLogger<DbtWebhookReceiver>.Instance);

        var result = await receiver.ProcessWebhookAsync(payload, "invalid-sig");

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid or missing HMAC signature");
    }
}
