# F-AI-07: Dynamic Semantic Schema Pruning & Just-in-Time MCP Tools

**Status:** Geplant (Welle 1 / Q4 2026)  
**Komponenten:** `SemanticToolPruner.cs`, `ToolEmbeddingIndex.cs`, `McpEndpoints.cs`  
**Referenzen:** [`implementation-plan-welle-1-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-welle-1-2026-10-02.md)

---

## 1. Übersicht & Problemstellung
Große Enterprise-Supergraphs mit hunderten Typen sprengen das Token-Budget im System-Prompt von LLMs (30.000 bis 60.000 Tokens nur für Werkzeugsignaturen). Dies führt zu hohen Inferenzkosten, Latenzen und Fehlentscheidungen der Agenten.

## 2. Architektur & Umsetzung
- Vektorbasierte Vorfilterung zur Laufzeit: Das Gateway vergleicht den Benutzer-Prompt mit semantischen Metadaten aus dbt und OpenMetadata.
- Dynamische Injektion von nur 5 bis 10 Werkzeugen, die für die Anfrage relevant sind.
- Einhaltung eines strikten Token-Budgets (z. B. max. 4.000 Tokens für Tool-Definitionen).
- Prompt-Injection-Schutz: Reines Vektor-Distanz-Matching, keine Ausführung von Prompt-Direktiven im Tool-Selector.

## 3. Business Value
- Bis zu 80 % Ersparnis bei System-Prompt-Tokens.
- Deutlich reduzierte Time-to-First-Token (TTFT) und signifikant höhere Erfolgsquote autonomer Agenten.
