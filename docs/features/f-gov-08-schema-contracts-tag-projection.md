# F-GOV-08: Dynamic Schema Contracts & Tag-basierte Projektion (`@tag` / `@inaccessible`)

**Status:** Geplant (Welle 2 / Q4 2026)  
**Komponenten:** `SchemaContractFilter.cs`, `SchemaContractMiddleware.cs`, `SchemaContractsOptions.cs`  
**Referenzen:** [`implementation-plan-welle-2-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-welle-2-2026-10-02.md)

---

## 1. Übersicht & Problemstellung
Unternehmen müssen für unterschiedliche Konsumenten (Partner-APIs, B2B-Kunden, mobile Applikationen, interne Entwickler) separate GraphQL-Gateways oder Sub-Schemas pflegen. Dies verursacht Schema-Drift, Redundanz und Sicherheitsrisiken durch versehentliche Offenlegung interner Felder.

## 2. Architektur & Umsetzung
- Zentrale Schemadefinition mit standardisierten Direktiven:
  - `@tag(name: String!)`: Markiert Felder, Typen und Enums für bestimmte Zielgruppen (z. B. `partner`, `mobile`, `public`).
  - `@inaccessible`: Schließt interne Schnittstellendetails global aus allen externen Projektionen aus.
- **Contract Projection Engine (`SchemaContractFilter`):**
  - Generiert zur Start- bzw. Reload-Zeit maßgeschneiderte, vorkompilierte Sub-Schemas für definierte Verträge.
  - Bereinigt Waisentypen (Orphan Types), die durch das Entfernen von Feldern entstehen.
- **Isolierte Introspektion:**
  - Introspektionsabfragen (`__schema`, `__type`) liefern exakt die für den jeweiligen Vertrag sichtbaren Typen – 0 Informationsleck interner Domänen.
- **Dynamisches Routing:**
  - Auflösung über HTTP-Header `X-Gateway-Contract`, Query-Parameter `?contract=...`, Sub-Pfad `/graphql/contracts/{contract}` oder Principal-Claims.
  - Nicht freigegebene Felder schlagen bei der AST-Validierung mit `FIELD_NOT_FOUND` fehl (Fail-Closed).

## 3. Business Value
- Single Source of Truth: Ein einziger Supergraph bedient sicher beliebig viele Zielgruppen.
- Beseitigt die Notwendigkeit für teure Apollo GraphOS Contracts-Lizenzen.
