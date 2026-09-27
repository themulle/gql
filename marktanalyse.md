# 📊 Enterprise Product Management: Marktrecherche, Feature-Gap-Analyse & Reifegrad-Prüfung (GqlGateway)

**Rolle:** Principal Enterprise Product Manager & Platform Strategist  
**Marktumfeld:** 2025/2026 Enterprise API & GraphQL Federation (Apollo GraphOS / Router v2.17+, Hasura DDN v3, WunderGraph Cosmo, StepZen, Immuta)  
**Status:** Aktualisiert nach Abschluss von P1, P2, P3 und P7 (General Availability)  
**Ziel:** Nachvollziehbarer Produktstatus, Dokumentation gelieferter Differenzierungs-Moats und Priorisierung der nächsten Roadmap-Phasen nach dem RICE-C-Modell.

---

## 1. Executive Summary & Marktkontext 2025 / 2026

Der Markt für Enterprise GraphQL und API Gateways wird 2025/2026 durch drei fundamentale Marktbewegungen definiert:

1. **Von Query-Aggregation zu "Agentic AI Orchestration":**
   * Apollo hat mit dem *Apollo MCP Server* und *GraphOS Agent Tools* den Weg geebnet, um GraphQL Supergraphs als Tool-Provider für autonome KI-Agenten bereitzustellen.
   * **Unsere Marktposition:** Mit [ADR-014](file:///root/gql/docs/adr/ADR-014-enterprise-model-context-protocol-and-ai-data-guardrails.md) und der Implementierung in [`GqlGateway.GraphQL.Mcp`](file:///root/gql/src/GqlGateway.GraphQL/Mcp/GatewayMcpQueryExecutor.cs) besitzt GqlGateway ein klares Alleinstellungsmerkmal: Wir erzwingen Zero-Trust Guardrails (PII-Scrubbing, Fail-Closed Audit-Logging, echte Anrufer-Identitätsübertragung und Session-Ownership-Validierung) direkt vor dem LLM-Token-Stream.
2. **Enterprise Data Governance & Zero-Touch Data Catalogs:**
   * Reine RBAC/ABAC-Gateways (Apollo, Cosmo) greifen zu kurz. Fortune-500-Unternehmen verlangen automatisierte Klassifizierungs-Synchronisation aus führenden Metadaten-Katalogen (Microsoft Purview, Collibra, OpenMetadata) ohne manuelle Doppelpflege.
   * **Unsere Marktposition:** Durch den schlüsselfertigen Rollout von **P1** liest GqlGateway Tabellen- und Spaltenmetadaten, PII-Kennzeichnungen und DSGVO-Art.-9-Klassifizierungen nativ per REST und Event-Webhooks ein und übersetzt sie in automatische Maskierungsregeln.
3. **Federation & Edge Performance bei striktem Zero-Trust:**
   * Bestehende Router delegieren Autorisierung entweder an Subgraphs (Apollo) oder erfordern teure Zusatz-Lizenzen (Hasura DDN).
   * **Unsere Marktposition:** Mit **P7** (Hot Chocolate Fusion Subgraph Router mit Zero-Trust Context Forwarding) und **P3** (CDN Cache-Tags mit automatischem Fallback auf `Cache-Control: private, no-store` bei aktiven RLS/Maskierungsregeln) liefert GqlGateway maximale Edge-Skalierbarkeit ohne Compliance-Risiko.
4. **Enterprise Customizing, C#-Ökosystem & Sonderfreigabe-Workflows:**
   * In Enterprise-Landschaften dominiert C#/.NET im Backend. Etablierte Gateways (Apollo in Rust/Rhai, Kong in Lua, Tyk/Envoy in Go/C++) erzwingen Fremdsprachen oder bestrafen Anpassungen mit hohen gRPC-Sidecar-Latenzen. Zudem agieren sie rein binär (Allow/Deny), während Enterprises dynamische Sonderfreigaben (JIT, 4-Augen, Break-Glass) fordern.
   * **Unsere Marktposition:** GqlGateway schließt diese Lücke durch ein **Dual-Mode Extensibility Framework** (native C# In-Process DLLs/NuGet im Hot Path für Zero-IPC-Latenz sowie out-of-process gRPC) und transformiert das Gateway zur aktiven **Governance-Workflow-Engine**, die Sonderfreigaben direkt im Ingress/Egress-Lifecycle mit ServiceNow/Jira verzahnt.


---

## 2. Reifegrad- & Vollständigkeitsprüfung vorhandener Features

Bestandsaufnahme aller Gateway-Module zur Dokumentation der Marktreife (General Availability / GA):

| Modul / Feature | Zustand im Repository | Reifegrad | Status & verbleibende Roadmap-Gaps |
| :--- | :--- | :---: | :--- |
| **Data Catalog Connectors (P1)** | Vollständig implementiert ([`PurviewDataCatalogClient`](file:///root/gql/src/GqlGateway.Infrastructure/DataCatalog/PurviewDataCatalogClient.cs), [`CollibraDataCatalogClient`](file:///root/gql/src/GqlGateway.Infrastructure/DataCatalog/CollibraDataCatalogClient.cs), [`OpenMetadataDataCatalogClient`](file:///root/gql/src/GqlGateway.Infrastructure/DataCatalog/OpenMetadataDataCatalogClient.cs), [`DataCatalogClientFactory`](file:///root/gql/src/GqlGateway.Infrastructure/DataCatalog/DataCatalogClientFactory.cs), [`DataCatalogSyncService`](file:///root/gql/src/GqlGateway.Application/DataCatalog/Services/DataCatalogSyncService.cs), Webhook HMAC-Validierung). | **100% (GA)** | ✅ **Vollständig abgeschlossen.** Native REST-Clients mit Polly 8 Resilienz, Entra ID OAuth, PII- & DSGVO-Art.-9-Mapping und Epoch-Invalidierung aktiv. |
| **Client Quotas & Cost Telemetrie (P2)** | Vollständig implementiert ([`ClientTierResolver`](file:///root/gql/src/GqlGateway.Application/Caching/Services/ClientTierResolver.cs), [`CostAndQuotaMiddleware`](file:///root/gql/src/GqlGateway.GraphQL/Interceptors/CostAndQuotaMiddleware.cs), [`RedisRateLimiterService`](file:///root/gql/src/GqlGateway.Infrastructure/RateLimiting/RedisRateLimiterService.cs)). | **100% (GA)** | ✅ **Vollständig abgeschlossen.** Client-Tiering (`Free`, `Standard`, `Enterprise`, `Internal`), atomares Lua Token Bucket in Redis, Response-Header (`X-Query-Cost`, `X-RateLimit-*`) und `extensions.cost`. |
| **CDN Cache-Tag Headers & Edge Invalidation (P3)** | Vollständig implementiert ([`CdnCacheTagVisitor`](file:///root/gql/src/GqlGateway.GraphQL/Interceptors/CdnCacheTagVisitor.cs), [`CdnCacheTagMiddleware`](file:///root/gql/src/GqlGateway.GraphQL/Interceptors/CdnCacheTagMiddleware.cs), [`CloudflareCdnPurgeService`](file:///root/gql/src/GqlGateway.Infrastructure/Cdn/CloudflareCdnPurgeService.cs), [`FastlyCdnPurgeService`](file:///root/gql/src/GqlGateway.Infrastructure/Cdn/FastlyCdnPurgeService.cs)). | **100% (GA)** | ✅ **Vollständig abgeschlossen.** AST-Tag-Extraktion, Zero-Trust Cache Isolation (`private, no-store` bei RLS/Maskierung) und asynchrone Mutation-Invalidierung via Outbox. |
| **Subgraph Federation Router (P7)** | Vollständig implementiert ([`SubgraphSecurityDelegatingHandler`](file:///root/gql/src/GqlGateway.GraphQL/Federation/SubgraphSecurityDelegatingHandler.cs), [`SubgraphResultMaskingMiddleware`](file:///root/gql/src/GqlGateway.GraphQL/Federation/SubgraphResultMaskingMiddleware.cs), [`FusionGatewayExtensions`](file:///root/gql/src/GqlGateway.GraphQL/Federation/FusionGatewayExtensions.cs)). | **100% (GA)** | ✅ **Vollständig abgeschlossen.** Hot Chocolate Fusion Subgraph Router mit Zero-Trust Client Token Forwarding und In-Memory Result Masking auf aggregierten Daten. |
| **Casbin ABAC & RLS Pushdown** | Vollständig im AST-zu-SQL integriert ([`RowFilterSqlBuilder`](file:///root/gql/src/GqlGateway.Application/Services/RowFilterSqlBuilder.cs), [`AdvancedRlsFilterGenerator`](file:///root/gql/src/GqlGateway.Application/Services/AdvancedRlsFilterGenerator.cs)). Dialekte: Postgres, MSSQL, SQLite. | **90%** | ❌ Hot-Reload von Casbin-Policies ohne Pod-Restart.<br/>❌ Visueller Policy-Tester / Simulator für Data Stewards (siehe P6). |
| **Model Context Protocol (MCP) & AI Guardrails** | SSE-Handshake (`/mcp/sse`), JSON-RPC Handler ([`McpProtocolHandler`](file:///root/gql/src/GqlGateway.Application/Mcp/Services/McpProtocolHandler.cs)), AI Data Guardrail ([`AiDataGuardrailService`](file:///root/gql/src/GqlGateway.Application/Mcp/Services/AiDataGuardrailService.cs)), Session-Ownership Schutz, SigV4 Audit-Export. | **85%** | ❌ Stdio- und Streamable HTTP-Transport für Entwickler-CLIs (Claude Code / Cursor).<br/>❌ Semantische Prompt-Injection- & Jailbreak-Erkennung (NeMo Guardrails / Llama Guard). |
| **ITSM Closed Loop (ServiceNow / Jira)** | Outbox Pattern ([`ItsmOutboxDispatcherHostedService`](file:///root/gql/src/GqlGateway.Infrastructure/Itsm/ItsmOutboxDispatcherHostedService.cs)), Webhook Ingestion ([`ItsmWebhookHandler`](file:///root/gql/src/GqlGateway.Infrastructure/Itsm/ItsmWebhookHandler.cs)), Triage-Engine. | **80%** | ❌ Direkte Outbound-REST-Clients für ServiceNow Table API & Jira Cloud REST v3.<br/>❌ Automatischer Rezertifizierungs- & Verlängerungs-Workflow für ablaufende temporäre Consents. |
| **Lineage & DSGVO Art. 15 Auskunft** | Lineage Graph Store ([`LineageImpactAnalyzerService`](file:///root/gql/src/GqlGateway.Application/Lineage/LineageImpactAnalyzerService.cs)), GDPR Art. 15 Subject Access Report Generator, zyklensichere DFS/Kahn-Validierung. | **85%** | ❌ Standardisierter PDF/Audit-Export für externe Datenschutzbeauftragte.<br/>❌ Lineage-Push zu OpenLineage / Apache Atlas. |
| **Modern Lakehouse Connector (P4)** | Vollständig implementiert ([`IcebergMetadataReader`](file:///root/gql_extensions/src/GqlGateway.Extensions/Lakehouse/Services/IcebergMetadataReader.cs), [`IcebergPartitionPruner`](file:///root/gql_extensions/src/GqlGateway.Extensions/Lakehouse/Services/IcebergPartitionPruner.cs), [`LakehouseDataSourceExecutor`](file:///root/gql_extensions/src/GqlGateway.Extensions/Lakehouse/Services/LakehouseDataSourceExecutor.cs), Storage-Provider für Local, S3 SigV4 & Azure Blob, Integrationstests). | **100% (GA)** | ✅ **Vollständig abgeschlossen.** Nativer Apache Iceberg v2 Lakehouse-Connector mit L1-Metadaten-/Manifest-Cache (`MetadataCacheTtlMinutes`), vektorisiertem Partition- & Min/Max-Stats-Pruning, Fail-Closed Zero-Trust Governance und automatischer PII/GDPR-Spaltenmaskierung. |
| **Subscriptions & Realtime Events (P5)** | Vollständig implementiert ([`Subscription.cs`](file:///root/gql/src/GqlGateway.GraphQL/Subscriptions/Subscription.cs), [`WebSocketAuthInterceptor.cs`](file:///root/gql/src/GqlGateway.GraphQL/Subscriptions/WebSocketAuthInterceptor.cs), [`StreamRlsPolicyEnforcer.cs`](file:///root/gql/src/GqlGateway.Application/Streaming/Services/StreamRlsPolicyEnforcer.cs), [`InMemoryCdcEventChannel.cs`](file:///root/gql/src/GqlGateway.Infrastructure/Streaming/InMemoryCdcEventChannel.cs), [`DebeziumCdcParser.cs`](file:///root/gql/src/GqlGateway.Infrastructure/Streaming/DebeziumCdcParser.cs)). | **100% (GA)** | ✅ **Vollständig abgeschlossen.** WebSocket (`graphql-transport-ws`) und SSE Subscriptions mit dynamischer In-Stream Row Level Security (Casbin ABAC), In-Stream Column Masking, strikter Mandanten-Isolation und Debezium/Kafka CDC Ingestion. |
| **Management Studio & UI (P6)** | Reines Headless-Gateway. | **0%** | 🔴 Visuelles Web-Dashboard für Data Stewards (Policy Simulator, Audit-Viewer, Schema Explorer). |

---

## 3. Aktualisierte Wettbewerber-Matrix & Differenzierungs-Moats

| Konkurrent | Stärken | Kritische Schwachstellen & Lücken | GqlGateway Moat (Unser Alleinstellungsmerkmal) |
| :--- | :--- | :--- | :--- |
| **Apollo GraphQL**<br/>*(Router / Federation v2 / GraphOS)* | • Marktführer Schema Federation<br/>• Großes Entwickler-Ökosystem<br/>• Hohe JS/Rust Router Performance | • Router unter restriktiver ELv2-Lizenz<br/>• **Schlechte Data-Governance**: RLS nur delegiert an Subgraphs<br/>• Keine native Unternehmenskatalog-Synchronisation<br/>• Fehlende DSGVO Art. 9 Automatisierung | **Integrierte Zero-Trust Governance & Fusion**: Hot Chocolate Fusion mit striktem Zero-Trust Context Forwarding, In-Memory-Masking auf aggregierten Daten und nativer Sync mit Purview/Collibra/OpenMetadata. |
| **Hasura Enterprise**<br/>*(DDN / Data Delivery Network)* | • Instant GraphQL über SQL-DBs<br/>• Declarative Permissions<br/>• Schnelles Prototyping | • Starker Vendor-Lockin in proprietäre Hasura-Metadaten<br/>• Sehr teure Enterprise-Lizenzmodelle<br/>• Föderierte Governance über mehrere Data Domains schwerfällig<br/>• Kein integrierter 4-Augen Justification-Workflow | **Open Governance & Lower TCO**: Keine proprietäre Plattformbindung, automatisierte ITSM-Freigaben (ServiceNow/Jira), dbt-Manifest Ingestion und vollständige On-Prem/Sovereign Cloud Eignung. |
| **WunderGraph / Cosmo** | • Open-Source Apollo-Alternative<br/>• Rust-basierter Router<br/>• Entwicklerzentrierter BFF-Fokus | • Primär auf Web-Frontend-Entwickler ausgerichtet<br/>• **Fehlende Enterprise-Compliance**: Kein BSI/DSGVO Audit-Trail (SHA-256 Hash-Chains)<br/>• Keine Kerberos/Active Directory Legacy-Absicherung<br/>• Keine Data Catalog Federation | **Enterprise Grade & Compliance**: SHA-256 manipulationssichere Audit-Logs mit WORM-S3-Export, hybride IdP-Föderation (Entra ID, OIDC, Kerberos) und DSGVO Art. 15 Auskunfts-APIs. |
| **Data Security Suites**<br/>*(Immuta, Privacera)* | • Sehr starke Policy Engines für Snowflake/Databricks | • **Kein GraphQL- oder API-Gateway**: Setzen tief in Datenbanken/Lakehouses an<br/>• Hohe Komplexität und Latenz für Anwendungsentwickler | **Unified Access Layer**: Bringt datenbanknahe Zero-Trust Governance direkt an die GraphQL- und MCP-Schnittstelle von Applikationen und KI-Agenten. |
| **Tyk.io / Kong / Envoy**<br/>*(Klassische API Gateways)* | • Ausgereiftes API-Management & Dev-Portale<br/>• Ingress/Egress-Hooks über Plugins & Coprozesse (Tyk gRPC, Envoy `ext_proc`, Kong Lua/Go) | • **Latenz- & Memory-Penalty**: Out-of-Process gRPC im Ingress & Egress erfordert 2 Netzwerk/IPC-Hops und 4x Protobuf-Serialisierung pro Call (+1 bis 5 ms)<br/>• **Kein GraphQL AST Deep Context**: Egress-Filterung (z. B. Masking) muss teure, flache JSON-Bäume im Nachgang parsen statt RLS-Pushdown im Query-AST<br/>• **Statisch binäres Modell**: Nur Allow/Deny, keine dynamischen Sonderfreigabe-Workflows im Request-Flow<br/>• **Sprachbarriere für Enterprise-Teams**: Native In-Process-Erweiterungen verlangen Go, C++ oder Lua; C# nur über externe Sidecars möglich | **First-Class Enterprise Customizing & Active Governance**:<br/>1. *In-Process Hot Path*: Native C# Middlewares (`.dll` / NuGet / DI) mit Zero-IPC-Latenz und direktem AST-/Span-Zugriff.<br/>2. *Out-of-Process gRPC*: Entkoppelte gRPC-Interceptors für polyglotte Teams.<br/>3. *Sonderfreigaben*: JIT-Access, interaktive 4-Augen-Challenges und revisionssicheres Break-Glass Audit-Hashing. |

---

### 3.1 Deep Dive: Ingress/Egress Customizing, C#-Ökosystem & Sonderfreigabe-Workflows

In der Enterprise-Praxis scheitern API- und Daten-Gateways selten am Standard-Routing, sondern an der **"Last-Mile-Speziallogik"** (proprietäre Tokens, Token-Exchange mit Altsystemen, interne Compliance-Hashing-Auditoren, branchenspezifische PII-Maskierung und Sonderfreigaben).

#### A. Architekturvergleich: Native C# In-Process vs. Out-of-Process gRPC Coprozess

```mermaid
flowchart LR
    subgraph Client ["Client HTTP/GraphQL"]
        REQ["Request"]
    end

    subgraph Gateway ["Gateway Pipeline"]
        ING["Ingress Hook"]
        CORE["Core Engine / AST Pushdown"]
        EGR["Egress Hook"]
        ING --> CORE --> EGR
    end

    subgraph Pattern1 ["Tyk/Envoy Modell (Out-of-Process gRPC)"]
        GRPC_ING["gRPC Service (Ingress)"]
        GRPC_EGR["gRPC Service (Egress)"]
    end

    subgraph Pattern2 ["GqlGateway Modell (Native C# In-Process)"]
        DLL_ING["C# Middleware (Zero-Copy Span)"]
        DLL_EGR["C# Middleware (Deep AST Context)"]
    end

    REQ --> ING
    ING -.->|Hop 1: IPC/Protobuf| GRPC_ING
    EGR -.->|Hop 2: IPC/Protobuf| GRPC_EGR

    ING ===|Zero-Latency In-Memory| DLL_ING
    EGR ===|Zero-Latency In-Memory| DLL_EGR
```

1. **Der Double-Hop-Flaschenhals externer Coprozesse**: Das Zwischenschalten externer gRPC-Dienste im Ingress und Egress bietet Prozessisolation, kostet aber messbar Performance: +1 bis 5 ms P99-Latenz und massiver Memory-Overhead bei großen Egress-Payloads (JSON Re-Parsing).
2. **Der C#-Vorteil im Enterprise**: Da C# in Enterprise-Landschaften (Finanzen, Industrie, Behörden) stark verbreitet ist, senkt eine native C#-Erweiterbarkeit die TCO drastisch. Entwicklerteams nutzen bestehende Enterprise-NuGet-Pakete, Dependency Injection und Logging-Infrastrukturen ohne Sprachbruch.

#### B. Paradigmenwechsel: Vom binären Allow/Deny zur dynamischen Workflow-Orchestrierung

Klassische Gateways kennen nur Allow oder Deny. In regulierten Branchen scheitert dies: Mitarbeiter benötigen für Vorfälle, Audits oder Sonderfälle **temporäre Ausnahme- und Sonderfreigaben (JIT, Break-Glass, 4-Augen-Prinzip)**.

```mermaid
sequenceDiagram
    autonumber
    actor User as Data Consumer / Analyst
    participant GW as GqlGateway (Ingress Hook)
    participant WF as Workflow Service (C# / ITSM)
    participant Approver as Data Owner / ServiceNow
    participant DB as Backend Target DB
    participant EGR as GqlGateway (Egress Hook)

    User->>GW: 1. Query mit sensiblen Daten (z. B. VIP/Patientendaten)
    GW->>WF: 2. Ingress Check: Liegt Sonderfreigabe vor?
    
    alt Keine Freigabe vorhanden (Interaktive Challenge)
        WF->>Approver: 3a. Erzeuge Approval-Ticket (ServiceNow / Jira / 4-Augen)
        WF-->>GW: 3b. Status: ApprovalPending (Workflow-ID #WF-8812)
        GW-->>User: 3c. 412 Precondition Failed / Challenge mit Freigabe-URL
    else Sonderfreigabe aktiv (z. B. JIT-Token oder Break-Glass)
        WF-->>GW: 4a. Status: Approved (Temporärer Consent gültig)
        GW->>DB: 4b. Pushdown Query mit gelockertem RLS-Filter
        DB-->>EGR: 4c. Rohdaten
        EGR->>EGR: 4d. Revisionssicherer SHA-256 Audit-Hash (#WF-8812)
        EGR-->>User: 4e. Daten mit Audit-Lineage-Header
    end
```

* **Justification-Driven Access**: Ingress-Validierung von `X-Access-Justification: INC-49102` gegen ServiceNow/Jira.
* **Interaktive 4-Augen-Freigabe (DSGVO Art. 9)**: Strukturierte `ConsentRequired`-Challenge statt 403 Forbidden; automatische Epochen-Invalidierung via Redis bei Genehmigung.
* **Break-Glass**: Notfall-Zugriff für SREs mit SOC-Alarmierung und lückenlosem SHA-256 Audit-Hash-Chaining.


---

## 4. Priorisierungs-Framework: Aktualisierte RICE-C Matrix

Mit dem erfolgreichen Abschluss von **P1, P2, P3 und P7** aktualisiert sich das Priorisierungs-Ranking wie folgt:

$$\text{RICE-C Score} = \frac{\text{Reach} \times \text{Impact} \times \text{Confidence} \times \text{ComplianceWeight}}{\text{Effort}}$$

| Initiative / Feature | Reach | Impact | Conf. | Comp. | Effort | **Score** | Status & Priorität |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **P1: Konkrete Data Catalog Connectors** | 8 | 2.5 | 90% | 1.8 | 3 W | **10.8** | ✅ **100% Abgeschlossen (GA)** |
| **P2: Dynamic Client Quotas & Cost Telemetrie** | 9 | 1.8 | 95% | 1.2 | 1.5 W | **12.3** | ✅ **100% Abgeschlossen (GA)** |
| **P7: Subgraph Federation (Hot Chocolate Fusion)** | 6 | 2.5 | 90% | 1.2 | 1.8 W | **9.0** | ✅ **100% Abgeschlossen (GA)** |
| **P3: CDN Cache-Tag Headers & Edge Invalidation** | 8 | 2.2 | 90% | 1.1 | 2 W | **8.7** | ✅ **100% Abgeschlossen (GA)** |
| **P5: Realtime Event Subscriptions (Kafka/CDC)** | 7 | 2.5 | 85% | 1.3 | 4 W | **4.8** | ✅ **100% Abgeschlossen (GA)** |
| **P9: Ingress/Egress Extensibility SDK & Workflow Interceptors**<br/>*(Dual-Mode C# In-Process DLLs & gRPC Coprocess, JIT Sonderfreigaben)* | 8 | 2.5 | 90% | 1.6 | 3 W | **9.6** | 🟢 **Hohe Priorität (Q2/Q3)** |
| **P6: Data Steward Studio & Policy Simulator**<br/>*(Lightweight Blazor / SPA Admin Dashboard)* | 7 | 2.2 | 90% | 1.6 | 4 W | **5.5** | 🟢 **Nächste Priorität (Q2 - P1)** |
| **P4: Modern Lakehouse Connector (Iceberg / Parquet)**<br/>*(Umsetzung von [ADR-015](file:///root/gql/docs/adr/ADR-015-apache-iceberg-lakehouse-connector-and-zero-trust-pushdown.md))* | 6 | 3.0 | 90% | 1.3 | 4 W | **4.3** | ✅ **100% Abgeschlossen (GA)** |
| **P8: Schema Registry & CI/CD Checks (`rover`-Pendant)**<br/>*(Breaking Change Detection via CLI & GitHub Action)* | 6 | 1.8 | 85% | 1.2 | 3.5 W | **3.1** | 🔵 **Q3 (P3)** |

---

## 5. Strategische Roadmap & Nächste Entwicklungsphasen

```mermaid
flowchart TD
    subgraph Delivered["Bereits Geliefert (General Availability)"]
        direction TB
        D1["P1 Data Catalogs (Purview, Collibra, OpenMetadata)"]
        D2["P2 Client Quotas & Cost Telemetry (Redis Lua)"]
        D3["P3 CDN Cache-Tags & Edge Invalidation (Cloudflare/Fastly)"]
        D4["P7 Hot Chocolate Fusion Subgraph Router"]
        D5["P5 Realtime CDC & Event Subscriptions mit In-Stream RLS"]
        D6["P4 Apache Iceberg v2 Lakehouse Connector mit Zero-Trust Pushdown"]
    end

    subgraph PhaseNext["Nächste Phase: Governance Studio & Extensibility (Q2 2026)"]
        direction TB
        E1["P6 GqlGateway Studio: Visual Policy Simulator & Audit UI"]
        E2["P9 Ingress/Egress Extensibility SDK & Workflow Interceptors"]
    end

    subgraph PhaseRegistry["Phase Registry & Quality (Q3 2026)"]
        direction TB
        F1["P8 Schema Registry, Schema Contracts & CI/CD Breaking Change Linter"]
    end

    Delivered --> PhaseNext
    PhaseNext --> PhaseRegistry
```

### Konkrete Handlungsempfehlungen für die nächsten Sprints:

1. **P6 Data Steward Studio & Policy Simulator (Score: 5.5):**
   * Bereitstellung eines intuitiven Management-Frontends (z.B. Blazor WebAssembly oder React SPA embedded).
   * **Core Feature:** Ein "What-If" Policy Simulator, mit dem Sicherheitsbeauftragte und Data Stewards interaktiv prüfen können, wie Rollen, Abteilungen und Justifications auf konkrete Tabellen und Spaltenmaskierungen wirken.
2. **P9 Ingress/Egress Extensibility SDK & Workflow Interceptors (Hoher Strategischer Fit, Score: 9.6):**
   * Bereitstellung eines modularen Plugin-SDKs für native C# In-Process Middlewares (`.dll` / NuGet) im Hot Path sowie out-of-process gRPC Coprozessen.
   * Integration von Just-in-Time (JIT) Sonderfreigaben (`X-Access-Justification`), 4-Augen Challenge-Responses und revisionssicherem Break-Glass Audit-Hashing.
3. **P8 Schema Registry, Contracts & CI/CD Checks (`rover`-Pendant):**
   * CLI-Tool zur Validierung von Schemata gegen aktive Clients und Breaking Change Detection.

