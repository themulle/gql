# F-OPS-01: AST-Aware Production Traffic Shadowing & Dark Replay

**Status:** Geplant (Welle 1 / Q4 2026)  
**Komponenten:** `TrafficShadowingMiddleware.cs`, `AstShadowingFilter.cs`, `DarkReplayClient.cs`  
**Referenzen:** [`implementation-plan-welle-1-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-welle-1-2026-10-02.md)

---

## 1. Übersicht & Problemstellung
Statische Schema-Checks erkennen syntaktische Fehler, aber keine Performance-Regressionen, DB-Locking-Probleme oder semantische Datenabweichungen unter realer Produktionslast.

## 2. Architektur & Umsetzung
- Asynchrones Spiegeln eines konfigurierbaren Anteils des produktiven Lese-Traffics auf Canary- oder Staging-Versionen.
- **AST-Mutation Guard:** Schreibende Operationen (GraphQL-Mutationen, SQL `INSERT/UPDATE/DELETE`) werden über den Query-AST zwingend erkannt und im Shadowing-Pfad unterdrückt.
- **PII Redaction:** Sensible Header und PII-Werte werden vor dem Replay bereinigt oder durch synthetische Test-Token ersetzt.
- Automatisiertes Diff-Reporting von Latenzen ($p50, p95, p99$), Statuscodes und Antwortstrukturen.

## 3. Business Value
- Risikofreie Zero-Downtime-Releases für geschäftskritische Core-Banking- und Enterprise-Systeme.
- Frühe Erkennung von Performance- und Index-Bottlenecks vor dem produktiven Rollout.
