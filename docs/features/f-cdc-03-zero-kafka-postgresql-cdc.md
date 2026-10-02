# F-CDC-03: Zero-Kafka PostgreSQL CDC via Logical Streaming Replication

**Status:** Geplant (Welle 1 / Q4 2026)  
**Komponenten:** `PostgreSqlLogicalReplicationService.cs`, `WalMessageDecoder.cs`  
**Referenzen:** [`implementation-plan-welle-1-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-welle-1-2026-10-02.md)

---

## 1. Übersicht & Problemstellung
Echtzeit-Streaming über Apache Kafka und Debezium scheitert in vielen Abteilungen an den hohen Infrastruktur- und Betriebskosten ("The Kafka Barrier"). Bisher deckte das Gateway diesen Bypass nur für MSSQL Change Tracking (`F-CDC-02`) ab.

## 2. Architektur & Umsetzung
- Direkter PostgreSQL Logical Replication Client im Gateway über das native `pgoutput`-Streaming-Protokoll (`Npgsql.Replication`).
- Automatisches Lifecycle-Management des Replikations-Slots mit regelmäßigem LSN-Commit (Schutz vor WAL-Wachstum).
- WAL-Änderungen (`INSERT`, `UPDATE`, `DELETE`) werden ohne Message-Broker direkt in mandantengefilterte GraphQL-Subscriptions oder Server-Sent Events überführt.
- In-Stream RLS- und Mandantenfilterung vor der Auslieferung an Clients.

## 3. Business Value
- Schließt die Lücke für Cloud-native PostgreSQL-, Supabase- und Aurora-Umgebungen bei minimaler TCO.
- Vollständige Unabhängigkeit von externen Brokern (Kafka, ZooKeeper) für Realtime-Streaming.
