# STRIDE Threat Model (DOK-07): C# GraphQL Enterprise Gateway

**Dokumenten-Status:** Freigegeben / Normativ  
**Referenzen:** [requirements.md](file:///root/gql/requirements.md), [gatewayconfig.md](file:///root/gql/gatewayconfig.md)

---

## 1. Systemübersicht & Vertrauensgrenzen (Trust Boundaries)

```
[ Unauthenticated Zone / Internet / LAN ]
                   |
     (TB-1: Network / Pre-Auth)
                   v
[ Pre-Auth IP Rate Limiter & Reverse Proxy ]
                   |
     (TB-2: Negotiate / Kerberos Auth)
                   v
[ Gateway Pod: Authentication & Authorization Middleware ]
                   |
     (TB-3: Distributed Cache & Event Bus)
                   v
[ Redis L2 & In-Process Channels (Epoch Validation) ]
                   |
     (TB-4: Data Access / Trusted Subsystem)
                   v
[ Governance DB (SQLite/MSSQL) & Read-Only Source Databases ]
```

---

## 2. STRIDE-Bedrohungsanalyse & Gegenmaßnahmen

### 2.1 Spoofing (Identitätsanmaßung)
- **Bedrohung 1.1: Spoofing von Benutzer-Identitätsheadern (`X-Test-User-Sid`).**
  - *Gefahr:* Ein Angreifer sendet gefälschte Header, um sich als privilegierter Benutzer oder Data Owner auszugeben.
  - *Gegenmaßnahme:* Startup-Validierung in `Program.cs` (`ValidateOnStart()`): `EnableTestAuthHandler` führt in Produktionsumgebungen (`builder.Environment.IsProduction()`) zum **sofortigen Boot-Absturz**. In Produktion akzeptiert das Gateway ausschließlich verifizierte Protokolle (ForwardAuth, Entra ID / AD FS Bearer, Basic Auth oder Kerberos).
- **Bedrohung 1.2: Spoofing von Traefik ForwardAuth-Headern (`X-Forwarded-User`, `X-Forwarded-Groups`).**
  - *Gefahr:* Ein Angreifer im internen Netzwerk sendet direkt gefälschte `X-Forwarded-*`-Header an das Gateway, um Identitäten oder Rollen vorzutäuschen.
  - *Gegenmaßnahme:* Multi-Faktor-Netzwerk- und Secret-Validierung in `ForwardAuthAuthenticationHandler`:
    1. *Proxy-IP-Einschränkung:* Ist `RequireTrustedProxy = true`, werden Anfragen abgewiesen, wenn die Remote-IP nicht exakt in `TrustedNetworks` (CIDR-Subnetze der Traefik-Pods) oder `TrustedProxies` liegt.
    2. *Timing-sicheres Shared Secret:* Abgleich des Headers `X-Forwarded-Secret` mit einem über Key Vault bereitgestellten Secret (`SharedSecretKeyVaultRef`) via `CryptographicOperations.FixedTimeEquals` verhindert Replay- und Spoofing-Attacken.
- **Bedrohung 1.3: Timing-Angriffe auf Passwörter bei HTTP Basic Authentication.**
  - *Gefahr:* Angreifer leiten Passwörter über Laufzeitunterschiede bei Stringvergleichen ab.
  - *Gegenmaßnahme:* `BasicAuthenticationHandler` verwendet ausschließlich konstante Laufzeitvergleiche (`CryptographicOperations.FixedTimeEquals`) auf UTF-8 Byte-Ebene und unterstützt SHA-256 Passwort-Hashes.
- **Bedrohung 1.4: Ticket-Manipulation (Kerberos PAC Spoofing).**
  - *Gefahr:* Gefälschte Gruppenmitgliedschaften im Kerberos-Ticket.
  - *Gegenmaßnahme:* Kerberos-Validierung gegen das Active Directory mit Signaturprüfung der Privilege Attribute Certificate (PAC). Aufgelöste Gruppen werden maximal für `GroupCacheTtlMinutes` (Standard 5 min) gecacht.

### 2.2 Tampering (Daten- und Manipulationsschutz)
- **Bedrohung 2.1: SQL-Injection über dynamische GraphQL-Filter.**
  - *Gefahr:* Ein Angreifer injiziert SQL-Befehle über `where: { ... }` Argumente.
  - *Gegenmaßnahme:* `SqlFilterProvider` validiert alle Tabellen- und Spaltenbezeichner strikt gegen eine Regex-Whitelist (`^[a-zA-Z_][a-zA-Z0-9_]*$`) und den Metadatenkatalog `TableMetadata.HasColumn(...)`. Unbekannte Felder werfen sofort eine Exception. Alle Filterwerte werden ausnahmslos als typisierte SQL-Parameter (`@p1`, `$1`) gebunden.
- **Bedrohung 2.2: Manipulation des Audit-Trails.**
  - *Gefahr:* Ein böswilliger Administrator ändert oder löscht Einträge in `AUDIT_LOG_ENTRIES`.
  - *Gegenmaßnahme:* Kryptografische SHA-256 Hashverkettung (`entry_hash = SHA256(prev_hash | payload)`). Jede Modifikation oder Auslassung bricht die mathematische Kette nachweisbar. Entzug von `UPDATE`- und `DELETE`-Rechten auf DB-Ebene.
- **Bedrohung 2.3: Cache-Poisoning im Consent-Cache.**
  - *Gefahr:* Ein Angreifer manipuliert L1/L2 Cache-Einträge, um unberechtigten Zugriff zu erhalten.
  - *Gegenmaßnahme:* Jeder Cache-Eintrag speichert die `epoch`, mit der er berechnet wurde. Vor Rückgabe validiert `IEpochValidationService` atomar gegen die Governance-DB.

### 2.3 Repudiation (Nicht-Abstreitbarkeit)
- **Bedrohung 3.1: Abstreiten von Lesezugriffen auf sensible Daten.**
  - *Gefahr:* Ein Nutzer streitet ab, sensible Gehalts- oder Kundendaten abgefragt zu haben.
  - *Gegenmaßnahme:* Transaktionales Audit-Logging (Stufe A) für alle Zugriffe auf als `HIGH` markierte Tabellen mit `ActorSid`, Zeitstempel, Tabelle und W3C-Trace-ID.
- **Bedrohung 3.2: Abstreiten von Consent-Genehmigungen.**
  - *Gefahr:* Ein Data Owner bestreitet die Freigabe eines Zugriffsantrags.
  - *Gegenmaßnahme:* Genehmigungen (`APPROVAL_STEPS`) speichern die authentifizierte `ApproverSid`, Entscheidung und exakten Zeitstempel im revisionssicheren Speicher.

### 2.4 Information Disclosure (Offenlegung von Informationen)
- **Bedrohung 4.1: Ausspähen von Tabellennamen via Schema-Introspection.**
  - *Gefahr:* Angreifer rekonstruieren vertrauliche Unternehmensstrukturen über GraphQL-Introspection.
  - *Gegenmaßnahme:* Introspection und Hot Chocolate Banana Cake Pop sind in Produktion standardmäßig deaktiviert (`EnableIntrospection: false`, `EnableBananaCakePop: false`).
- **Bedrohung 4.2: Inferenz-Lecks über Filter/Sortierung auf maskierten Feldern (F-CONS-07 Regel 5).**
  - *Gefahr:* Ein Nutzer filtert auf `where: { salary: { gt: 100000 } }`. Anhand der Treffermenge erfährt er das Gehalt, obwohl die Spalte für ihn als `MASK` definiert ist.
  - *Gegenmaßnahme:* F-CONS-07 Regel 5 verbietet Filter, Sortierung und Gruppierung auf Spalten, deren effektive Stufe nicht `CLEAR` ist.
- **Bedrohung 4.3: Stack-Trace- und Exception-Lecks.**
  - *Gefahr:* Fehlerhafte Abfragen geben DB-Verbindungsdaten oder interne Pfade an den Client zurück.
  - *Gegenmaßnahme:* `ErrorSanitizingFilter` fängt unbehandelte Exceptions ab, loggt sie geschützt im Server-Log und gibt dem Client nur neutrale Codes (`INTERNAL_SERVER_ERROR`).

### 2.5 Denial of Service (Verfügbarkeitsangriffe)
- **Bedrohung 5.1: Kerberos-Handshake-Flooding (Negotiate DoS).**
  - *Gefahr:* Ressourcenüberlastung des Gateways und Domain Controllers durch unvollständige Auth-Handshakes.
  - *Gegenmaßnahme:* `PreAuthIpRateLimitingMiddleware` begrenzt eingehende Verbindungen pro IP-Adresse **vor** dem Eintritt in die Windows-Negotiate-Middleware.
- **Bedrohung 5.2: Komplexe / rekursive GraphQL-Queries ("Billion Laughs" für GraphQL).**
  - *Gefahr:* Tief geschachtelte Relationen oder gigantische Queries blockieren CPU und RAM.
  - *Gegenmaßnahme:* Hot Chocolate Query Depth Enforcement (`MaxAllowedExecutionDepth: 10`), Query Complexity Analyzer (Limit: 1500), erzwungenes Keyset-Paging (max. 250 Zeilen) und hartes Timeout (`QueryTimeoutSeconds: 30`).
- **Bedrohung 5.3: Überlastung durch einzelne Benutzer-SIDs.**
  - *Gefahr:* Ein kompromittiertes Benutzerkonto sendet tausende Abfragen pro Sekunde.
  - *Gegenmaßnahme:* `PostAuthSidRateLimitingMiddleware` implementiert einen Token-Bucket-Limiter pro SID mit konfigurierbarer Kapazität und Nachfüllrate.

### 2.6 Elevation of Privilege (Rechteausweitung)
- **Bedrohung 6.1: Selbst-Genehmigung von Rechten (Vier-Augen-Bypass).**
  - *Gefahr:* Ein Antragsteller genehmigt seinen eigenen Antrag auf hochsensible Daten.
  - *Gegenmaßnahme:* Strikte Durchsetzung der Funktionstrennung in `SqliteGovernanceRepository.ApproveConsentRequestStepAsync`: `req.RequesterSid == approverSid` wirft eine `InvalidOperationException`.
- **Bedrohung 6.2: Kompromittierung des technischen DB-Dienstkontos (Blast Radius).**
  - *Gefahr:* Angreifer erlangt Dienstkonto-Zugangsdaten und greift direkt auf die Quelldatenbank zu.
  - *Gegenmaßnahme:* Das Dienstkonto besitzt auf der Quelldatenbank ausschließlich `SELECT`-Berechtigungen auf freigegebene Schemas/Tabellen. Keine DDL-, Schreib- oder Server-Admin-Rechte. Netzwerk-Isolation (nur Zugriffe von Gateway-Pod-IPs).

---

## 3. Fazit & Risikoverbleib
Alle STRIDE-Kategorien sind durch mehrstufige, software- und architekturseitige Schutzmechanismen (Defense-in-Depth) abgedeckt. Das Restrisiko bei verzögerter Übernahme von AD-Gruppenänderungen ist im Risikoregister (R-04) erfasst und durch `GroupCacheTtlMinutes: 5` zeitlich eng begrenzt.
