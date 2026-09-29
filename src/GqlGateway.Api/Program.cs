using GqlGateway.Api.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel limits
builder.WebHost.ConfigureKestrel(options =>
{
    // Allow up to 100 MB for streaming dbt manifests and large audit/governance payloads
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024;
});

// 1. Serilog Setup
builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// 2. DI Container Validation
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

// 3. Modular Service Registrations
var gatewayOptions = builder.Services.AddGatewayOptions(builder.Configuration, builder.Environment);
builder.Services.AddGatewayInfrastructure(gatewayOptions);
builder.Services.AddGatewayAuth(gatewayOptions, builder.Environment);
builder.Services.AddGatewayGraphQL(gatewayOptions);

// 4. Build and Pipeline Configuration
var app = builder.Build();

app.UseGatewayPipeline(gatewayOptions);
app.MapGatewayEndpoints(gatewayOptions);

app.Run();

// Make Program class accessible for WebApplicationFactory in integration tests
public partial class Program { }
