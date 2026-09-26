# Developer Guide - GraphQL Enterprise Gateway

This guide assists engineers in contributing to the GraphQL Enterprise Gateway, writing tests, and running the project locally with zero external dependencies.

---

## 1. Prerequisites & Environment

- **.NET SDK**: 10.0 or higher (`net10.0`)
- **IDE**: Visual Studio 2026, VS Code with C# Dev Kit, or JetBrains Rider
- **No External Services Required**: The gateway includes an in-memory SQLite governance catalog and test authentication simulation, allowing full local execution without Docker, SQL Server, or Redis.

---

## 2. Quick Start (Local Run)

### 2.1 Build the Solution
```bash
dotnet build GqlGateway.sln
```
The build enforces strict typing and treat-warnings-as-errors (`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`).

### 2.2 Run the WebHost
```bash
dotnet run --project src/GqlGateway.Api/GqlGateway.Api.csproj
```
The server will start on `http://localhost:5000`. Hot Chocolate Nitro Banana Cake Pop IDE is accessible at:
- `http://localhost:5000/graphql`

---

## 3. Simulating Authentication in Development

The gateway supports multiple authentication schemes in development:

### 3.1 TestAuthHandler (Header-based Simulation)
When running in `Development` mode, the `TestAuthHandler` is enabled. You can impersonate any Windows User or Group SID by passing custom HTTP headers:
- `X-Test-User-Sid`: The calling user's SID (e.g. `S-1-5-21-1001`)
- `X-Test-Group-Sids`: Comma-separated group SIDs (e.g. `S-1-5-21-FINANCE-ANALYSTS,S-1-5-21-ALL-STAFF`)
- `X-Test-Roles`: Comma-separated application roles (e.g. `DataConsumer,DataOwner,GovernanceAdmin`)

```bash
curl -X POST http://localhost:5000/graphql \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -H "X-Test-User-Sid: S-1-5-21-1001" \
  -H "X-Test-Group-Sids: S-1-5-21-FINANCE-ANALYSTS" \
  -d '{"query": "{ catalog { domain schemaName tableName displayName } }"}'
```

### 3.2 Traefik ForwardAuth (Kubernetes Ingress Simulation)
Simulate requests originating from a Traefik Ingress controller:
```bash
curl -X POST http://localhost:5000/graphql \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -H "X-Forwarded-User: S-1-5-21-1001" \
  -H "X-Forwarded-Groups: S-1-5-21-FINANCE-ANALYSTS" \
  -d '{"query": "{ catalog { domain schemaName tableName } }"}'
```

### 3.3 HTTP Basic Authentication & `/api/auth/login`
Test credential validation and direct Basic Auth GraphQL queries:
```bash
# Verify credentials
curl -u "analyst:Secret123!" http://localhost:5000/api/auth/login

# Direct GraphQL query
curl -X POST http://localhost:5000/graphql \
  -u "analyst:Secret123!" \
  -H "Content-Type: application/json" \
  -H "GraphQL-Preflight: 1" \
  -d '{"query": "{ catalog { domain schemaName tableName } }"}'
```

---

## 4. Architecture Guidelines & Quality Gates

The codebase follows **Clean Architecture**:
- `GqlGateway.Domain`: Pure business rules, entities, and domain calculation services. Zero dependencies on external libraries or frameworks.
- `GqlGateway.Application`: Core execution engine (`GatewayExecutionService`, `IGatewayExecutionService`), business services (`ConsentResolutionService`, `ColumnMaskingProvider`, `RlsFilterGenerator`, `ChunkedQueryExecutor`), data source executors (`SqlDataSourceExecutor`), and repository contracts.
- `GqlGateway.Infrastructure`: ADO.NET SQL persistence (`SqlConnectionFactory`, `SqliteGovernanceRepository`), Redis multi-instance messaging (`RedisEventBus`), rate limiters, idempotency stores, and authentication handlers (`ForwardAuthAuthenticationHandler`, `BasicAuthenticationHandler`, `EnterpriseClaimsTransformation`).
- `GqlGateway.GraphQL`: Hot Chocolate schema configuration, dynamic types, queries, and mutations.
- `GqlGateway.Api`: ASP.NET Core host, Basic Auth login endpoint, rate limiting middlewares, health check probes, and graceful drain hosted service.

### 4.1 Running Automated Tests
```bash
# Run all tests (Unit, Integration, Architecture)
dotnet test GqlGateway.sln

# Run only Architecture boundary tests
dotnet test tests/GqlGateway.Tests.Architecture/GqlGateway.Tests.Architecture.csproj

# Run Unit & Property-Based tests
dotnet test tests/GqlGateway.Tests.Unit/GqlGateway.Tests.Unit.csproj

# Run Walking Skeleton Integration tests
dotnet test tests/GqlGateway.Tests.Integration/GqlGateway.Tests.Integration.csproj
```

---

## 5. Adding New Queries and Dynamic Tables

1. **Register Table in Governance Catalog**: Add entry in `TABLES` table with `CATALOG_NAME`, `SCHEMA_NAME`, and `TABLE_NAME`.
2. **Define Columns and Types**: Add column specifications in `TABLE_COLUMNS`.
3. **Configure Hot Chocolate Dynamic Type**: Handled automatically by `DynamicTableType` which inspects catalog metadata, applies scalar conversions, and hooks field masking.
4. **Grant Consent**: Ensure the calling SID has an active `ALLOW` consent for the table before querying, otherwise Zero Trust will return `FORBIDDEN`.
