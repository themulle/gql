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

- **Dynamic Schema & Multi-Database Engine Support**:
  - Dynamic type projection and schema generation based on the active governance catalog.
  - Native support for 5 major database backends: **MSSQL (SQL Server)**, **SQLite**, **PostgreSQL**, **Databricks**, and **Oracle**.
  - Efficient DataLoader-based batching and selective child relation loading with chunking to protect underlying database parameter limits (e.g. SQLite 999, Oracle 1000, MSSQL 2100, PostgreSQL/Databricks 10000).
  - Introspection and Banana Cake Pop (Nitro) UI configurable per environment.

- **Column-Level Data Masking & Dynamic RLS**:
  - Transparent column-level policies: `Clear`, `Mask` (redaction / format-preserving masking / zero-allocation HMAC-SHA256 pseudonymization via `HMACSHA256.HashData` and stack memory), or `Deny`.
  - Side-channel inference defense (Rule 5 compliance): GraphQL AST `where` clauses referencing `Mask` or `Deny` columns are strictly rejected with a `SecurityException`, thwarting binary search inference attacks.
  - Dialect-aware SQL Row-Level Security (RLS) generation supporting SQLite, SQL Server (T-SQL), PostgreSQL (PL/pgSQL), Databricks, and Oracle with full row filter propagation across nested child DataLoaders.
  - Support for comparison operators, set inclusion (`IN`, tuple `IN`), temporal validity filters, and parameterized subqueries (`EXISTS`).

- **High-Performance Two-Tier Caching & Invalidation**:
  - **L1 In-Memory Cache** (MemoryCache) for ultra-low latency sub-millisecond lookups with lock-free table indexing.
  - **L2 Distributed Cache** (Redis) with fast pipelined batch operations.
  - **Epoch-based Invalidation**: Every table governance change increments a cryptographic monotonic epoch number, immediately invalidating stale cache entries across all gateway instances without cache stampedes.
  - High-throughput batch hydration for active consents (`WHERE consent_id IN (...)`) eliminating N+1 query overhead.

- **Tamper-Evident SHA-256 Audit Hash Chain**:
  - Every access evaluation, consent creation, approval, and revocation records an immutable audit entry.
  - Transaction-safe atomic audit log persistence with zero-allocation `SHA256.HashData(payloadBytes, hashBytes)` preventing hash chain forking under high concurrency.
  - Continuous cryptographic SHA-256 hash chaining (`PrevHash -> EntryHash`) persisted across gateway restarts and verifiable via automated health routines.

- **Enterprise Network & Edge Protection**:
  - Pre-Authentication IP Rate Limiting and Post-Authentication SID Token-Bucket Concurrency Limiting.
  - Anti-CSRF Preflight enforcement on GraphQL endpoints.
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
                  │                               │  Health Checks, DI Configuration
                  └──────────────┬────────────────┘
                                 │
                  ┌──────────────▼────────────────┐
                  │      GqlGateway.GraphQL       │  Hot Chocolate Schema, Types,
                  │                               │  DataLoaders, Query/Mutation Resolvers
                  └──────────────┬────────────────┘
                                 │
                  ┌──────────────▼────────────────┐
                  │     GqlGateway.Application    │  Use Cases, Consent Resolution,
                  │                               │  Masking, RLS Generation, Interfaces
                  └──────────────┬────────────────┘
                                 │
         ┌───────────────────────┴───────────────────────┐
         │                                               │
┌────────▼───────────────────────┐             ┌─────────▼─────────────────────┐
│    GqlGateway.Infrastructure   │             │       GqlGateway.Domain       │
│                                │             │                               │
│ SQLite/ADO.NET, Redis/Memory,  │             │ Domain Entities, Value Objects│
│ Cryptography, Event Bus        │             │ (Sid, TableIdentifier), Enums │
└────────────────────────────────┘             └───────────────────────────────┘
```

### Projects

| Project | Target | Description |
|---|---|---|
| [`GqlGateway.Domain`](src/GqlGateway.Domain) | `net10.0` | Value Objects (`Sid`, `TableIdentifier`, `CompositeKey`), Models, Options, Enums |
| [`GqlGateway.Application`](src/GqlGateway.Application) | `net10.0` | Core business services (`ConsentResolutionService`, `ColumnMaskingProvider`, `RlsFilterGenerator`, `ChunkedQueryExecutor`), segregated interfaces |
| [`GqlGateway.Infrastructure`](src/GqlGateway.Infrastructure) | `net10.0` | Persistence (`SqliteGovernanceRepository`), Caching (`ConsentCacheService`), Event Bus (`InProcessChannelEventBus`), Secret Providers |
| [`GqlGateway.GraphQL`](src/GqlGateway.GraphQL) | `net10.0` | Hot Chocolate 14 GraphQL engine, dynamic types, queries, mutations, DataLoader execution, Source-Generated Regexes |
| [`GqlGateway.Api`](src/GqlGateway.Api) | `net10.0` | ASP.NET Core Minimal API, hosting, rate limiting, anti-CSRF, forwarded headers |
| [`GqlGateway.Benchmarks`](benchmarks/GqlGateway.Benchmarks) | `net10.0` | BenchmarkDotNet suites for throughput, cache hit/miss, and masking allocations |
| [`GqlGateway.Tests.Unit`](tests/GqlGateway.Tests.Unit) | `net10.0` | 290 Unit & Property-Based tests (xUnit, Shouldly, FsCheck) |
| [`GqlGateway.Tests.Architecture`](tests/GqlGateway.Tests.Architecture) | `net10.0` | NetArchTest rules enforcing Clean Architecture dependency directions |
| [`GqlGateway.Tests.Integration`](tests/GqlGateway.Tests.Integration) | `net10.0` | 23 End-to-end integration tests using `WebApplicationFactory<Program>` |

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
Currently passes **316 / 316 tests (100% green)** across Unit, Architecture, and Integration test suites:
- **290 Unit Tests** (Domain Edge-Cases, Multi-Domain Cross-Dialect RLS, Four-Eyes & Delegation Stress, Concurrency & Audit Replication, DataLoader Odd Batching, AST Filter Inference Defense, Column Masking)
- **3 Architecture Tests** (Clean Architecture layering enforcement via NetArchTest)
- **23 Integration Tests** (End-to-end ASP.NET Core GraphQL pipeline, Auth, Anti-CSRF, Four-Eyes Multi-Step Approval, Vacation Delegation, Odd Batch DataLoaders)

### 3. Run Gateway Locally

```bash
dotnet run --project src/GqlGateway.Api/GqlGateway.Api.csproj
```

The service starts locally on `http://localhost:5000` (or `https://localhost:5001`). In `Development` mode, the Hot Chocolate Banana Cake Pop GraphQL IDE is accessible at:
- `http://localhost:5000/graphql`

---

## 🔒 Authentication Simulation in Development

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
      "EnableTestAuthHandler": false
    },
    "GovernanceDb": {
      "Provider": "Sqlite",
      "ConnectionString": "Data Source=governance.db",
      "SeedDemoData": true
    },
    "Caching": {
      "L1MemoryCache": {
        "SizeLimitMb": 512,
        "DefaultTtlMinutes": 10
      },
      "Redis": {
        "Configuration": "localhost:6379,abortConnect=false"
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
- [**Developer Guide**](docs/developer-guide.md) – Guidelines for extending services, adding resolvers, writing TDD tests, and coding standards.
- [**Threat Model & Security Whitepaper**](docs/threat-model/threat-model.md) – STRIDE analysis, attack surface, mitigation matrices, and cryptographic guarantees.
- [**Operations & HA Runbook**](docs/operations-runbook.md) – Rolling updates, graceful traffic drain protocol, alerts, backup & restore procedures.
- [**Architecture Decision Records (ADRs)**](docs/adr/) – Key architectural decisions (ADR-001 through ADR-009).

---

## 📄 License

Internal Enterprise Application. All rights reserved.
