# GqlGateway - Enterprise GraphQL Gateway with Data-Owner-Consent

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![HotChocolate](https://img.shields.io/badge/GraphQL-HotChocolate%2014-F00E2B?logo=graphql)](https://chillicream.com/)
[![Architecture](https://img.shields.io/badge/Architecture-Clean%20%2F%20Onion-blue)](docs/architecture/arc42.md)
[![Security](https://img.shields.io/badge/Security-Zero%20Trust-green)](docs/threat-model/threat-model.md)
[![License](https://img.shields.io/badge/License-Proprietary%20%2F%20Internal-lightgrey)](#)

GqlGateway is a high-performance, secure, centralized enterprise GraphQL gateway built with **.NET 10** and **Hot Chocolate 14**. It provides unified GraphQL access to heterogeneous enterprise databases (**Microsoft SQL Server / MSSQL, SQLite, PostgreSQL, Databricks, Oracle**) while enforcing a strict **Zero-Trust Data-Owner-Consent** governance model.

Instead of traditional coarse-grained role-based access control (RBAC), access to tables, rows, and columns requires explicitly granted, time-bounded, and auditable consents governed directly by data owners.

---

## 🌟 Key Features

- **Zero-Trust Governance Model**:
  - Default fail-closed: Non-consented tables, columns, or rows are strictly denied (`FORBIDDEN`).
  - Active Directory Windows Security Identifiers (`Sid`) integration for both users and security groups.
  - Multi-step **Four-Eyes Approval Workflow** with segregation of duties (requester cannot approve own requests; duplicate approvals rejected).
  - Time-bounded delegations (`DATA_OWNER_DELEGATIONS`) allowing seamless holiday/vacation handovers.

- **Multi-Protocol Enterprise Identity & ForwardAuth (Kubernetes / Traefik)**:
  - **Traefik Ingress ForwardAuth**: Native support for Kubernetes ingress authentication offloading (Authelia, Keycloak, Authentik, OAuth2-Proxy). Validates proxy network CIDRs (`TrustedNetworks`, `TrustedProxies`) and timing-safe shared secrets (`X-Forwarded-Secret`), extracting `X-Forwarded-User`, `X-Forwarded-Groups`, `X-Forwarded-Roles`, and `X-Forwarded-Email`.
  - **Microsoft Entra ID (Azure AD) & AD FS**: Native JWT Bearer token authentication with normalized enterprise claims transformation (`EnterpriseClaimsTransformation`) mapping `oid`, `onprem_sid`, `primarygroupsid`, and claim roles into canonical `Sid` value objects.
  - **HTTP Basic Authentication**: Support for direct Basic Auth headers on GraphQL queries and a dedicated credential verification endpoint (`GET` / `POST /api/auth/login`). Production enforces salted PBKDF2 (`$pbkdf2$...`) with dynamic dummy-iteration parity for non-existent users; plaintext and unsalted SHA-256 are strictly restricted to `Development`. All comparisons use `CryptographicOperations.FixedTimeEquals`.
  - **Kerberos / SPNEGO Negotiate**: Windows Integrated Authentication with strict Kerberos-only enforcement and group SID resolution.
  - **Smart Dynamic Scheme Selector**: Automatic header-based protocol arbitration dispatching requests to ForwardAuth, Bearer, Basic, or Negotiate schemes.

- **Heterogeneous Multi-Source Data Architecture & Real SQL Execution**:
  - Dynamic type projection and schema generation based on the active governance catalog.
  - **Native SQL Execution with RLS Pushdown**: Direct ADO.NET execution via `ISqlConnectionFactory` supporting **MSSQL (SQL Server)**, **SQLite**, **PostgreSQL**, **Databricks**, and **Oracle**. Row-Level Security (RLS) filters are pushed down directly into generated SQL queries (`CombinedRowFilterSql`), preventing unauthorized rows from ever leaving the database engine.
  - **Declarative REST Data Source Engine (Pattern 3)**: Expose external REST APIs with URL-template parameter substitution (`/api/v1/customers/{id}`), header/query pushdown (`X-Tenant-Id`, `X-User-Sid`), bearer token forwarding / API keys, JSONPath extraction, and adaptive batching (`QueryParameterList`, `JsonBodyArray`, `ParallelSingleRequests` throttled via `SemaphoreSlim`). Integrated **SSRF Defense** with DNS pre-resolution (blocking RFC 1918, link-local, loopback, and cloud metadata) and hop-by-hop HTTP redirect protection (`AllowAutoRedirect = false`).
  - **Isolated C# Plugin System (Pattern 4)**: Host specialized HTTP/data connectors in isolated, collectible `AssemblyLoadContext` instances (`IHttpDataSourcePlugin`) preventing dependency collisions with host packages.
  - **Central Zero-Trust Pipeline**: Regardless of source (SQL, REST, or Plugin), all data passes through central consent evaluation (`GatewayExecutionService`), in-memory RLS post-filtering, central column masking, response budgeting, and audit logging.
  - Efficient DataLoader-based batching and selective child relation loading with chunking to protect underlying database parameter limits (e.g. SQLite 999, Oracle 1000, MSSQL 2100, PostgreSQL/Databricks 10000).
  - Introspection and Banana Cake Pop (Nitro) UI configurable per environment.

- **Distributed Multi-Instance Clustering (Redis)**:
  - **Redis Pub/Sub Event Bus (`RedisEventBus`)**: Real-time cross-pod propagation of catalog and policy epoch increments, invalidating distributed caches across all cluster nodes simultaneously.
  - **Resilient Distributed Token-Bucket Rate Limiting (`RedisRateLimiterService`)**: Sliding-window IP rate limiting and atomic token-bucket consumption per user SID across multi-node Kubernetes deployments with **transparent automatic fallback** to local `InMemoryRateLimiterService` (featuring lock-free atomic `Interlocked` counters) upon Redis cluster degradation.
  - **Distributed Mutation Idempotency (`RedisIdempotencyStore`)**: High-availability deduplication of sensitive governance mutations across gateway instances.
  - **Deep Cluster Health Checks (`IGatewayHealthCheckService`)**: Comprehensive Kubernetes readiness probes checking Governance DB, Redis cluster, and Active Directory connectivity.

- **Column-Level Data Masking & Dynamic RLS**:
  - Transparent column-level policies: `Clear`, `Mask` (redaction / zero-allocation format-preserving masking via `ReadOnlySpan<char>` and `string.Create` / HMAC-SHA256 pseudonymization via `HMACSHA256.HashData` and stack memory), or `Deny`.
  - Side-channel inference defense (Rule 5 compliance): GraphQL AST `where` clauses referencing `Mask` or `Deny` columns are strictly rejected with a `SecurityException`, thwarting binary search inference attacks.
  - Dialect-aware SQL Row-Level Security (RLS) generation supporting SQLite, SQL Server (T-SQL), PostgreSQL (PL/pgSQL), Databricks, and Oracle with full row filter propagation across nested child DataLoaders.
  - Support for comparison operators, set inclusion (`IN`, tuple `IN`), temporal validity filters, and parameterized subqueries (`EXISTS`).

- **High-Performance Two-Tier Caching & Invalidation**:
  - **L1 In-Memory Cache** (MemoryCache) for ultra-low latency sub-millisecond lookups with lock-free table indexing.
  - **L2 Distributed Cache** (Redis) with fast pipelined batch operations.
  - **Epoch-based Invalidation**: Monotonic policy epochs invalidate stale cache entries across all gateway instances without cache stampedes.
  - High-throughput batch hydration for active consents (`WHERE consent_id IN (...)`) eliminating N+1 query overhead.

- **Tamper-Evident HMAC-SHA256 Audit Hash Chain**:
  - Every access evaluation, consent creation, approval, and revocation records an immutable audit entry.
  - Transaction-safe atomic audit log persistence with keyed `HMACSHA256.HashData(secretKey, payload)` preventing hash chain tampering even with direct database write access.
  - Continuous cryptographic HMAC-SHA256 hash chaining (`PrevHash -> EntryHash`) persisted across gateway restarts and verifiable via automated health routines using timing-safe `CryptographicOperations.FixedTimeEquals`.

- **OpenMetadata Enterprise Governance Integration**:
  - Direct synchronization of enterprise catalog tables, schemas, columns, and tags from **OpenMetadata**.
  - Automatic column masking generation based on classification tags (e.g. `PII.Sensitive` -> REDACT, `PII.Email` -> MASK_EMAIL, `PII.Pseudonym` -> HMAC_SHA256).
  - OpenMetadata Policies, Rules, Roles, Teams, and Users mapped deterministically to GqlGateway `Consent` entries with Active Directory SID resolution (`TeamToGroupSidMap`, `UserToUserSidMap`).
  - Real-time webhook ingestion (`POST /api/webhooks/openmetadata`) protected by HMAC-SHA256 signature verification (`X-OpenMetadata-Signature`) and fail-closed replay defense requiring mandatory `Id` and `Timestamp` (5-minute sliding window).
  - Background periodic synchronization service (`OpenMetadataSyncBackgroundService`) and administrative GraphQL mutation (`syncOpenMetadata(dryRun: Boolean)`).
  - Immediate multi-instance cache invalidation via monotonic policy epoch incrementation upon catalog/permission sync.

- **Enterprise Network & Edge Protection**:
  - Pre-Authentication IP Rate Limiting and Post-Authentication SID Token-Bucket Concurrency Limiting.
  - Anti-CSRF Preflight enforcement on GraphQL endpoints.
  - Table Oracle Defense: `ErrorSanitizingFilter` masks `TableNotFoundException` as generic `FORBIDDEN` in non-development environments to prevent schema probing.
  - Secure `ReverseProxyOptions` with populated `KnownIPNetworks` (`System.Net.IPNetwork`) and `KnownProxies` to prevent `X-Forwarded-For` spoofing.
  - Dual Kubernetes probes (`/health/live`, `/health/ready`) and 6-phase graceful traffic drain controller for zero-downtime rolling deployments.

- **Zero External Dependencies in Development**:
  - Includes an embedded in-memory SQLite governance catalog and a simulated `TestAuthHandler` enabled exclusively in `Development` mode.

---

## 🏛 Architecture Overview

The solution adheres strictly to **Clean / Onion Architecture** principles with clear layer boundaries:

```
                  ┌───────────────────────────────┐
                  │       GqlGateway.Api          │  ASP.NET Core Host, Middleware,
                  │                               │  Health Checks, DI Configuration, Webhooks
                  └──────────────┬────────────────┘
                                 │
                  ┌──────────────▼────────────────┐
                  │      GqlGateway.GraphQL       │  Hot Chocolate Schema, Types,
                  │                               │  DataLoaders, Query/Mutation Resolvers
                  └──────────────┬────────────────┘
                                 │
                  ┌──────────────▼────────────────┐
                  │     GqlGateway.Application    │  Use Cases, Consent Resolution,
                  │                               │  Masking, RLS Generation, OpenMetadata DTOs
                  └──────────────┬────────────────┘
                                 │
         ┌───────────────────────┴───────────────────────┐
         │                                               │
┌────────▼───────────────────────┐             ┌─────────▼─────────────────────┐
│    GqlGateway.Infrastructure   │             │       GqlGateway.Domain       │
│                                │             │                               │
│ SQLite/ADO.NET, Redis/Memory,  │             │ Domain Entities, Value Objects│
│ Cryptography, OpenMetadataSync │             │ (Sid, TableIdentifier), Enums │
└────────────────────────────────┘             └───────────────────────────────┘
```

### Projects

| Project | Target | Description |
|---|---|---|
| [`GqlGateway.Domain`](src/GqlGateway.Domain) | `net10.0` | Value Objects (`Sid`, `TableIdentifier`, `CompositeKey`), Models, Options, Enums |
| [`GqlGateway.Application`](src/GqlGateway.Application) | `net10.0` | Central execution engine (`GatewayExecutionService`, `IGatewayExecutionService`), business services (`ConsentResolutionService`, `ColumnMaskingProvider`, `RlsFilterGenerator`, `ChunkedQueryExecutor`), data sources (`SqlDataSourceExecutor`, `DeclarativeHttpDataSourceExecutor`), OpenMetadata models & interfaces |
| [`GqlGateway.Infrastructure`](src/GqlGateway.Infrastructure) | `net10.0` | Persistence (`SqliteGovernanceRepository`, `SqlConnectionFactory`), Caching (`ConsentCacheService`), Multi-Instance Messaging (`RedisEventBus`, `InProcessChannelEventBus`), Rate Limiting (`RedisRateLimiterService`), Idempotency (`RedisIdempotencyStore`), Health (`GatewayHealthCheckService`), Security Handlers (`ForwardAuthAuthenticationHandler`, `BasicAuthenticationHandler`, `EnterpriseClaimsTransformation`) |
| [`GqlGateway.GraphQL`](src/GqlGateway.GraphQL) | `net10.0` | Hot Chocolate 14 GraphQL engine, dynamic schemas, types, queries, mutations (`syncOpenMetadata`), DataLoader execution |
| [`GqlGateway.Api`](src/GqlGateway.Api) | `net10.0` | ASP.NET Core Host, Basic Auth Login (`/api/auth/login`), ForwardAuth header security, rate limiting, anti-CSRF, health probes, OpenMetadata webhooks |
| [`GqlGateway.Benchmarks`](benchmarks/GqlGateway.Benchmarks) | `net10.0` | BenchmarkDotNet suites for throughput, cache hit/miss, and masking allocations |
| [`GqlGateway.Tests.Unit`](tests/GqlGateway.Tests.Unit) | `net10.0` | 391 Unit & Property-Based tests (xUnit, Shouldly, FsCheck, NSubstitute) |
| [`GqlGateway.Tests.Architecture`](tests/GqlGateway.Tests.Architecture) | `net10.0` | NetArchTest rules enforcing Clean Architecture dependency directions |
| [`GqlGateway.Tests.Integration`](tests/GqlGateway.Tests.Integration) | `net10.0` | 33 End-to-end integration tests using `WebApplicationFactory<Program>` |

---

## 🚀 Quick Start

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or higher
- C# 14 compatible toolchain (Visual Studio 2026, JetBrains Rider 2025.3+, or VS Code with C# Dev Kit)

### 1. Build Solution

```bash
dotnet build GqlGateway.sln -c Release
```
*Note: The project enforces `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.*

### 2. Run Tests

```bash
dotnet test GqlGateway.sln -c Release
```
Currently passes **429 / 429 tests (100% green)** across Unit, Architecture, and Integration test suites:
- **391 Unit Tests** (Authentication & ForwardAuth Security, Multi-Dialect RLS, Four-Eyes & Delegation Stress, Concurrency & Audit Replication, DataLoader Odd Batching, AST Filter Inference Defense, Zero-Allocation Column Masking)
- **5 Architecture Tests** (Clean Architecture layering enforcement via NetArchTest including zero-dependency checks on AspNetCore in Domain and Application)
- **33 Integration Tests** (End-to-end GraphQL pipeline, Traefik ForwardAuth Ingress, Basic Auth Login & Query Verification, Declarative REST & Plugin Zero-Trust enforcement, Anti-CSRF, Four-Eyes Multi-Step Approval, Vacation Delegation, OpenMetadata webhooks)

### 3. Run Gateway Locally

```bash
dotnet run --project src/GqlGateway.Api/GqlGateway.Api.csproj
```

The service starts locally on `http://localhost:5000` (or `https://localhost:5001`). In `Development` mode, the Hot Chocolate Banana Cake Pop GraphQL IDE is accessible at:
- `http://localhost:5000/graphql`

---

## 🔒 Enterprise Authentication & Ingress Integration

GqlGateway features a Smart Dynamic Authentication Scheme Selector supporting multiple production and development authentication mechanisms:

### 1. Kubernetes Ingress / Traefik ForwardAuth
When running behind an ingress controller (such as Traefik Ingress) that performs authentication (via Authelia, Keycloak, Authentik, or OAuth2-Proxy):
- Ingress passes caller identity and groups via headers:
  - `X-Forwarded-User`: Username or user SID
  - `X-Forwarded-Groups`: Comma-separated group SIDs or names
  - `X-Forwarded-Roles`: Comma-separated roles (e.g. `GovernanceAdmin,DataOwner`)
  - `X-Forwarded-Email`: User email address
  - `X-Forwarded-Secret`: Pre-shared secret header ensuring header authenticity
- Gateway verifies incoming proxy IP against `TrustedNetworks` (CIDR blocks) and `TrustedProxies`.
- Timing-safe comparison of `X-Forwarded-Secret` against Key Vault / AppSettings.

```bash
curl -X POST http://localhost:5000/graphql \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -H "X-Forwarded-User: S-1-5-21-CONSUMER-1" \
  -H "X-Forwarded-Groups: S-1-5-21-FINANCE-ANALYSTS" \
  -H "X-Forwarded-Secret: YOUR-SHARED-SECRET" \
  -d '{"query": "{ catalog { domain schemaName tableName displayName } }"}'
```

### 2. HTTP Basic Authentication & `/api/auth/login`
Supports direct Basic Auth headers on GraphQL requests and a dedicated login endpoint:
- `GET /api/auth/login` or `POST /api/auth/login` verifies credentials and returns user identity metadata.
- GraphQL queries authenticate directly via `Authorization: Basic base64(user:password)`.

```bash
# Verify credentials via login endpoint
curl -u "analyst:Secret123!" http://localhost:5000/api/auth/login

# Direct GraphQL query with Basic Auth
curl -X POST http://localhost:5000/graphql \
  -u "analyst:Secret123!" \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -d '{"query": "{ catalog { domain schemaName tableName } }"}'
```

### 3. Microsoft Entra ID (Azure AD) & AD FS JWT Bearer
- Native JWT Bearer validation against Entra ID / ADFS authority.
- `EnterpriseClaimsTransformation` normalizes Active Directory object IDs (`oid`), on-premises SIDs (`onprem_sid`), and primary group SIDs (`primarygroupsid`) into canonical `ClaimTypes.PrimarySid` and `ClaimTypes.GroupSid`.

```bash
curl -X POST http://localhost:5000/graphql \
  -H "Authorization: Bearer eyJhbGciOi..." \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -d '{"query": "{ catalog { domain schemaName tableName } }"}'
```

### 4. Authentication Simulation in Development
In `Development` mode (with `Gateway:Authentication:EnableTestAuthHandler = true`), you can test any SID, role, and group membership using custom HTTP request headers:

| Header | Description | Example |
|---|---|---|
| `X-Test-User-Sid` | Caller user SID | `S-1-5-21-DATAOWNER-1` or `S-1-5-21-CONSUMER-1` |
| `X-Test-Group-Sids` | Comma-separated group SIDs | `S-1-5-21-FINANCE-ANALYSTS,S-1-5-21-STAFF` |
| `X-Test-Roles` | Comma-separated roles | `GovernanceAdmin` or `ClusterAdmin` |
| `GraphQL-Preflight` | Anti-CSRF header (required on POST) | `1` |

> ⚠️ **Security Warning**: `TestAuthHandler` is strictly blocked in production environments (`IsProduction()`). Any attempt to enable it outside of `Development` triggers a fatal startup validation exception.

### Example: Query Active Catalog via cURL

```bash
curl -X POST http://localhost:5000/graphql \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -H "X-Test-User-Sid: S-1-5-21-CONSUMER-1" \
  -d '{"query": "{ catalog { domain schemaName tableName displayName isConsented columns } }"}'
```

### Example: Request Access to a Table

```bash
curl -X POST http://localhost:5000/graphql \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -H "X-Test-User-Sid: S-1-5-21-CONSUMER-1" \
  -d '{"query": "mutation { requestTableAccess(domain: \"finance\", schema: \"dbo\", table: \"finance_table_1\", justification: \"Q4 Financial Audit Analysis\", daysValid: 30) { requestId status message } }"}'
```

---

## ⚙️ Configuration (`appsettings.json`)

Key configuration settings under the `Gateway` section:

```json
{
  "Gateway": {
    "HighAvailability": {
      "DrainDelaySeconds": 5,
      "ShutdownTimeoutSeconds": 40
    },
    "Authentication": {
      "Domain": "CORP.LOCAL",
      "EnableTestAuthHandler": false,
      "ForwardAuth": {
        "Enabled": true,
        "UserHeader": "X-Forwarded-User",
        "GroupsHeader": "X-Forwarded-Groups",
        "RolesHeader": "X-Forwarded-Roles",
        "SharedSecretHeader": "X-Forwarded-Secret",
        "SharedSecret": "YOUR-SHARED-SECRET",
        "RequireTrustedProxy": true,
        "TrustedNetworks": ["127.0.0.1/32", "::1/128", "10.244.0.0/16"]
      },
      "BasicAuth": {
        "Enabled": true,
        "Users": [
          {
            "Username": "analyst",
            "Password": "SecretPassword123!",
            "Roles": ["DataConsumer"],
            "UserSid": "S-1-5-21-CONSUMER-1",
            "GroupSids": ["S-1-5-21-FINANCE-ANALYSTS"]
          }
        ]
      },
      "EntraId": {
        "Enabled": false,
        "TenantId": "00000000-0000-0000-0000-000000000000",
        "ClientId": "00000000-0000-0000-0000-000000000000",
        "Audience": "api://gql-gateway"
      },
      "Adfs": {
        "Enabled": false,
        "MetadataAddress": "https://adfs.corp.local/federationmetadata/2007-06/federationmetadata.xml",
        "Audience": "microsoft:identityserver:gql-gateway"
      }
    },
    "GovernanceDb": {
      "Provider": "Sqlite",
      "ConnectionString": "Data Source=governance.db",
      "SeedDemoData": true
    },
    "DataSources": {
      "finance": {
        "Provider": "SqlServer",
        "ConnectionString": "Server=sql-finance.corp.local;Database=FinanceDb;Integrated Security=SSPI;"
      },
      "hr": {
        "Provider": "PostgreSql",
        "ConnectionString": "Host=pg-hr.corp.local;Database=HrDb;Username=gql_app;Password=secret"
      }
    },
    "Caching": {
      "L1MemoryCache": {
        "SizeLimitMb": 512,
        "DefaultTtlMinutes": 10
      },
      "Redis": {
        "Configuration": "redis-cluster.corp.local:6379,abortConnect=false"
      }
    },
    "ReverseProxy": {
      "Enabled": true,
      "KnownNetworks": ["127.0.0.1/32", "::1/128", "10.0.0.0/8"],
      "KnownProxies": []
    },
    "GraphQL": {
      "EndpointPath": "/graphql",
      "MaxAllowedExecutionDepth": 10,
      "MaxAllowedComplexity": 1500
    },
    "DataMasking": {
      "HmacKeyId": "key-2026-q1"
    }
  }
}
```

---

## 📝 Code Review & Export Artifacts

For offline security audits, external architecture reviews, or LLM-assisted code reviews, pre-bundled review and diff files can be generated in the repository root:

| Artifact | Size | Description | Target Audience |
| :--- | :--- | :--- | :--- |
| [`review.txt`](file:///root/gql/review.txt) | ~436 KB | Consolidated bundle of all production C# source code (`src/**/*.cs`, 73 files) with a Table of Contents and standard file separators (`FILE: <path>`). | AI/LLM Reviewers, Single-File Ingestion |
| [`src_codebase_review.txt`](file:///root/gql/src_codebase_review.txt) | ~436 KB | Exact mirror of `review.txt` for tooling expecting the `src_codebase_review` naming convention. | Automated CI/CD Review Pipelines |
| [`full_codebase_review.txt`](file:///root/gql/full_codebase_review.txt) | ~812 KB | Extended bundle including all production (`src/`), test (`tests/`), and benchmark (`benchmarks/`) C# code (108 files total). | Comprehensive Test & Benchmark Audits |
| [`review_diff.patch`](file:///root/gql/review_diff.patch) / [`codebase.diff`](file:///root/gql/codebase.diff) | ~928 KB | Complete unified Git diff across all commits relative to upstream `origin/main`. | Git / Patch Tools, PR Reviewers |
| [`src_codebase.diff`](file:///root/gql/src_codebase.diff) | ~454 KB | Unified Git diff restricted strictly to production code under `src/`. | Production Code Reviewers |

### Re-generating Review Artifacts

To regenerate these review bundles and diffs after modifying code:

```bash
# 1. Regenerate production source bundle (review.txt)
(
  echo "================================================================================"
  echo "GQLGATEWAY PRODUCTION SOURCE CODE EXPORT (src/**/*.cs)"
  echo "Generated: $(date -u '+%Y-%m-%d %H:%M:%SZ')"
  echo "================================================================================"
  echo ""
  echo "TABLE OF CONTENTS:"
  find src -name "*.cs" | sort | while read -r f; do echo "  - $f"; done
  echo ""
  find src -name "*.cs" | sort | while read -r f; do
    echo "================================================================================"
    echo "FILE: $f"
    echo "================================================================================"
    cat "$f"
    echo ""
  done
) > review.txt

# 2. Regenerate git diffs against origin/main
git diff origin/main...HEAD > review_diff.patch
git diff origin/main...HEAD -- src > src_codebase.diff
```

---

## 📊 Benchmarks

Run the benchmark suite using BenchmarkDotNet:

```bash
dotnet run --project benchmarks/GqlGateway.Benchmarks/GqlGateway.Benchmarks.csproj -c Release
```

Included benchmark suites:
- **`ConsentResolutionBenchmark`**: Resolution latency across 100+ subject SIDs and permissive consent rules.
- **`ConsentCacheBenchmark`**: L1/L2 cache hit vs. miss latency with epoch invalidation checks.
- **`ColumnMaskingBenchmark`**: Throughput and memory allocation across Redaction, Masking, and HMAC Pseudonymization.
- **`GatewayLoadBenchmark`**: High-concurrency end-to-end GraphQL execution.

---

## 📚 Detailed Documentation

For comprehensive engineering and operational guides, consult the `docs/` directory:

- [**arc42 Architecture Documentation**](docs/architecture/arc42.md) – System context, building blocks, runtime view, deployment, and quality goals.
- [**Configuration Guide**](docs/configuration-guide.md) – Comprehensive reference of all `appsettings.json` sections, environment variables, startup validations, and production hardening.
- [**Developer Guide**](docs/developer-guide.md) – Guidelines for extending services, adding resolvers, writing TDD tests, and coding standards.
- [**Threat Model & Security Whitepaper**](docs/threat-model/threat-model.md) – STRIDE analysis, attack surface, mitigation matrices, and cryptographic guarantees.
- [**Operations & HA Runbook**](docs/operations-runbook.md) – Rolling updates, graceful traffic drain protocol, alerts, backup & restore procedures.
- [**Architecture Decision Records (ADRs)**](docs/adr/) – Key architectural decisions (ADR-001 through ADR-009).

---

## 📄 License

Internal Enterprise Application. All rights reserved.
