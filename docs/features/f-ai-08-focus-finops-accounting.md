# F-AI-08: FOCUS-konformes FinOps Accounting für Token & Compute

**Status:** Geplant (Welle 2 / Q4 2026)  
**Komponenten:** `FocusCostAccountingService.cs`, `FinOpsBudgetEnforcer.cs`, `FocusMetricsExporter.cs`  
**Referenzen:** [`implementation-plan-welle-2-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-welle-2-2026-10-02.md), [FinOps Foundation FOCUS Specification](https://focus.finops.org/)

---

## 1. Übersicht & Problemstellung
Autonome KI-Agenten (über Model Context Protocol / MCP) lösen durch mehrstufiges Reasoning unkontrollierte Kaskaden von Backend-Abfragen und LLM-Inferenz aus ("Denial of Wallet"). Plattform-Teams in Konzernen können diese Kosten weder transparent messen noch verursachergerecht auf Abteilungen oder Mandanten umlegen (Chargeback / Showback).

## 2. Architektur & Umsetzung
- Standardisiertes Kosten- und Nutzungsmodell nach **FOCUS v1.2 / v1.4** (FinOps Open Cost and Usage Specification).
- Granulare Messung pro Turn/Request:
  - Prompt- und Completion-Tokens (über MCP Inferenz-Proxy)
  - Backend-I/O und Query-Ausführungszeiten (DB-CPU, Pushdown-Laufzeiten)
  - Zuordnung zu `TenantId`, `PrincipalId`, `ServicePrincipal` und `AgentSessionId`
- Konfigurierbare Soft- und Hard-Budget-Caps:
  - **Soft Cap:** Löst Alerting via OpenTelemetry / Webhook aus (`finops.budget.warning`).
  - **Hard Cap:** Blockiert weitere Agenten-Aufrufe mit `429 Too Many Requests` bzw. `FINOPS_BUDGET_EXCEEDED`.
- Export über REST-Endpunkt `/api/v1/finops/focus` (Parquet, CSV, JSON) und OTel-Metriken (`gateway_finops_billed_cost_total`).

## 3. Business Value
- Vollständige Kostenwahrheit und Kostentransparenz für Enterprise-KI-Investitionen.
- Schutz vor unkontrollierten Inferenz-Kostenexplosionen in Multi-Tenant-Umgebungen.
