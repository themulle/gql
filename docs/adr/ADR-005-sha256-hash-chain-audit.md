# ADR-005: Revisionssichere Audit-Protokollierung mit SHA-256 Hash-Verkettung (Dual Storage)

## Status
Akzeptiert

## Kontext
Compliance-Vorgaben (GoBD, SOX, DSGVO) fordern den manipulationssicheren Nachweis aller Berechtigungsentscheidungen, Widerrufe und Zugriffe auf sensible Daten. Logs in rein textuellen Dateien oder ungeschützten relationalen Tabellen können nachträglich modifiziert oder gelöscht werden.

## Entscheidung
Wir setzen ein **Dual-Storage-Modell mit kryptografischer Hash-Kette** um:
1. **Ebene 1 (Transaktional in Governance-DB):** Tabelle `AUDIT_LOG_ENTRIES` speichert Events unveränderlich. Jeder Eintrag enthält den Hash des vorherigen Eintrags (`prev_hash`) und berechnet seinen eigenen Hash:
   `entry_hash = SHA256(prev_hash | occurred_at | event_type | actor_sid | target_table | decision | trace_id | details_json)`.
   Ein Integritätsprüfungs-Job validiert die Kette kontinuierlich.
2. **Ebene 2 (Search & SIEM):** Über das Outbox-Pattern werden Events asynchron nach Elasticsearch / SIEM gestreamt.
3. **DSGVO-Konformität:** Audit-Logs enthalten nur pseudonyme SIDs (`actor_sid`). Die Personenauflösung liegt in einer getrennten, löschbaren Tabelle (`AUDIT_ACTOR_DIRECTORY`).

## Konsequenzen
### Positiv
- Vollständige Revisionssicherheit: Nachträgliche Manipulationen oder Auslassungen brechen die Kette mathematisch nachweisbar.
- DSGVO-Recht auf Vergessenwerden erfüllbar, ohne die Audit-Integrität zu zerstören.
- Schnelle Analyse in Kibana/Elasticsearch bei gleichzeitiger rechtssicherer Verankerung in der DB.

### Negativ
- Sequenzielle Abhängigkeit bei der Hash-Berechnung (wird durch Transaktions-Sperren im Schreibpfad koordiniert).
