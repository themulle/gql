# Konfigurationshandbuch – GraphQL Enterprise Gateway

Dieses Handbuch bietet eine vollständige, praxiserprobte Referenz aller Konfigurationsoptionen des **GraphQL Enterprise Gateway**, inklusive Validierungsregeln, Sicherheitsinvariablen, Umgebungsvariablen, Kubernetes-Konfiguration und Profilen (Entwicklung vs. Produktion).

---

## 1. Architektur der Konfiguration

Die Konfiguration basiert auf dem .NET 10 Options-Pattern (`IOptions<GatewayOptions>`) mit strenger Validierung beim Anwendungsstart (**Fail-Fast** via `ValidateOnStart()`). 

### 1.1 Hierarchie & Laderangfolge
Konfigurationswerte werden in folgender Priorität ausgewertet (spätere Quellen überschreiben frühere):
1. **`appsettings.json`**: Basis-Konfiguration für Standardeinstellungen.
2. **`appsettings.{Environment}.json`**: Umgebungsabhängige Überschreibungen (z. B. `appsettings.Development.json` oder `appsettings.Production.json`).
3. **Umgebungsvariablen**: Container- und Host-Konfiguration (Syntax: doppelte Unterstriche `Gateway__Section__Property`).
4. **Secrets Provider**: Key Vault / Environment Secret Fallback für sensible kryptografische Schlüssel.

### 1.2 Startup-Validierung & Sicherheitsinvariablen
Beim Hochfahren des Hosts (`Program.cs`) führt `GatewayServiceCollectionExtensions.AddGatewayOptions` rekursive DataAnnotation- und semantische Sicherheitsprüfungen durch. Schlägt eine Bedingung fehl, bricht der Startprozess mit einer `ValidationException` sofort ab:

| Regel-ID | Prüfung | Fehlerbedingung |
| :--- | :--- | :--- |
| **NF-HA-01a** | `ShutdownTimeoutSeconds >= QueryTimeoutSeconds + 10` | Shutdown muss mindestens 10 Sekunden mehr Puffer als der längste Query-Timeout haben. |
| **NF-HA-01b** | `TerminationGracePeriodSeconds >= DrainDelaySeconds + ShutdownTimeoutSeconds + 10` | Kubelet Grace Period muss den gesamten Drain- und Shutdown-Zyklus abdecken. |
| **NF-SEC-01** | `environment.IsDevelopment() \|\| !Authentication.EnableTestAuthHandler` | Der `TestAuthHandler` (Header-basiertes SID-Impersonation) ist außerhalb von `Development` **strikt verboten**. |
| **NF-SEC-03** | Außerhalb von Development: `!string.IsNullOrWhiteSpace(HmacSecretKeyVaultRef)` und nicht gleich Test-Defaults | HMAC-Salts müssen in Staging/Produktion aus einem sicheren Secret-Store stammen. |

---

## 2. Vollständige Referenz der Konfigurationssektionen (`Gateway:*`)

Alle gateway-spezifischen Optionen befinden sich unter dem Hauptknoten `"Gateway"`.

### 2.1 `HighAvailability` (Hochverfügbarkeit & Graceful Shutdown)

Steuert das Verkehrs-Draining bei Rolling Deployments und Pod-Terminierungen.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `DrainDelaySeconds` | `int` | `1 .. 30` | `5` | Wartezeit nach Eingang des SIGTERM-Signals, bevor aktive Verbindungen geschlossen werden (erlaubt K8s Endpoints-Controller das Propagieren des Unready-Zustands). |
| `QueryTimeoutSeconds` | `int` | `5 .. 120` | `30` | Maximal zulässige Ausführungsdauer eines GraphQL-Requests. |
| `ShutdownTimeoutSeconds` | `int` | `10 .. 180` | `40` | Maximale Zeitspanne, die aktiven Anfragen eingeräumt wird, um regulär zu beenden. |
| `TerminationGracePeriodSeconds` | `int` | `20 .. 300` | `60` | Kubelet Pod-Grace-Period-Korridor zur Vermeidung von abruptem SIGKILL. |

```json
"HighAvailability": {
  "DrainDelaySeconds": 5,
  "QueryTimeoutSeconds": 30,
  "ShutdownTimeoutSeconds": 40,
  "TerminationGracePeriodSeconds": 60
}
```

---

### 2.2 `Authentication` (Windows Kerberos & Identity)

Konfiguriert die Authentifizierung gegen Active Directory.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Domain` | `string` | Gültiger FQDN | `"CORP.LOCAL"` | Active Directory Domäne. |
| `ServicePrincipalName` | `string` | SPN-Format | `"HTTP/gql-gateway.corp.local"` | Kerberos Service Principal Name für SPNEGO/Negotiate. |
| `RequireKerberosOnly` | `bool` | `true \| false` | `true` | Erzwingt Kerberos und lehnt NTLM-Downgrades ab. |
| `GroupCacheTtlMinutes` | `int` | `1 .. 60` | `5` | TTL für den lokalen Cache aufgelöster Windows-Gruppen-SIDs. |
| `EnableTestAuthHandler` | `bool` | `true \| false` | `false` | Ermöglicht `X-Test-User-Sid`-Header zur Simulation von Identitäten (**nur in Development erlaubt!**). |

```json
"Authentication": {
  "Domain": "CORP.LOCAL",
  "ServicePrincipalName": "HTTP/gql-gateway.corp.local",
  "RequireKerberosOnly": true,
  "GroupCacheTtlMinutes": 5,
  "EnableTestAuthHandler": false
}
```

---

### 2.3 `GovernanceDb` (Katalog- & Consent-Datenbank)

Speicherort für Metadaten, Freigaben, Delegationen, Vier-Augen-Genehmigungen und Audit-Logs.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Provider` | `string` | `"SqlServer"`, `"PostgreSql"`, `"Sqlite"` | `"SqlServer"` | Datenbank-Treiber für die Governance-Verwaltung. |
| `ConnectionString` | `string` | ADO.NET ConnStr | `"Data Source=governance.db;Cache=Shared"` | Verbindungszeichenfolge zur Governance-DB. |
| `CommandTimeoutSeconds` | `int` | `1 .. 60` | `15` | Timeout für Governance-SQL-Statements. |
| `EnableOutboxProcessor` | `bool` | `true \| false` | `true` | Startet den asynchronen Outbox-Worker für Invalidation-Events. |
| `SeedDemoData` | `bool` | `true \| false` | `true` | Initialisiert Demo-Tabellen und Standard-Governance-Regeln bei leerer DB. |

```json
"GovernanceDb": {
  "Provider": "Sqlite",
  "ConnectionString": "Data Source=governance.db;Cache=Shared",
  "CommandTimeoutSeconds": 15,
  "EnableOutboxProcessor": true,
  "SeedDemoData": false
}
```

---

### 2.4 `Caching` (Zweistufiges Caching & Epochen-Validierung)

Steuert den L1 In-Memory Cache, L2 Redis und die Konsistenzprüfung.

#### `Caching.L1MemoryCache`
| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `SizeLimitMb` | `int` | `16 .. 4096` | `512` | Maximale In-Memory-Cache-Größe in Megabyte. |
| `DefaultTtlMinutes` | `int` | `1 .. 120` | `10` | Standard-Gültigkeit zwischengespeicherter Consent-Entscheidungen. |
| `SensitiveTableTtlSeconds` | `int` | `1 .. 600` | `60` | Verkürzte TTL für als `IsSensitive = true` markierte Tabellen. |

#### `Caching.Redis`
| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Configuration` | `string` | StackExchange.Redis ConnStr | `"localhost:6379,abortConnect=false"` | Redis Host- und Verbindungsparameter. |
| `InstanceName` | `string` | Prefix | `"GqlGateway:"` | Schlüsselpräfix für Multi-Gateway-Cluster. |
| `InvalidationChannel` | `string` | Kanalname | `"consent:invalidations"` | Redis Pub/Sub Kanal für Epochen-Änderungen. |
| `ConnectTimeoutMs` | `int` | `100 .. 10000` | `2000` | Verbindungs-Timeout in Millisekunden. |
| `SyncTimeoutMs` | `int` | `100 .. 10000` | `1000` | Synchroner Lese-/Schreib-Timeout in ms. |

#### `Caching.EpochValidation`
| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `FailClosedOnSensitiveTables` | `bool` | `true \| false` | `true` | Zero-Trust: Bei Nichterreichbarkeit des Epochen-Stores wird der Zugriff auf sensible Tabellen blockiert. |
| `DegradedMaxStalenessSeconds` | `int` | `1 .. 300` | `30` | Maximale Staleness für unkritische Tabellen bei Redis-Ausfall. |
| `PipelinedMGetEnabled` | `bool` | `true \| false` | `true` | Nutzt Redis Batch-Pipelining zur Reduktion von Roundtrips. |

```json
"Caching": {
  "L1MemoryCache": {
    "SizeLimitMb": 512,
    "DefaultTtlMinutes": 10,
    "SensitiveTableTtlSeconds": 60
  },
  "Redis": {
    "Configuration": "redis-cluster.corp.local:6379,abortConnect=false,ssl=true",
    "InstanceName": "GqlGatewayProd:",
    "InvalidationChannel": "consent:invalidations",
    "ConnectTimeoutMs": 2000,
    "SyncTimeoutMs": 1000
  },
  "EpochValidation": {
    "FailClosedOnSensitiveTables": true,
    "DegradedMaxStalenessSeconds": 30,
    "PipelinedMGetEnabled": true
  }
}
```

---

### 2.5 `RateLimiting` (DDoS- & Missbrauchsschutz)

Kombiniert Pre-Authentication IP-Limiting mit Token-Bucket-Verbrauch pro Windows-Benutzer-SID.

#### `RateLimiting.PreAuthIpRateLimit`
| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `PermitLimit` | `int` | `1 .. 100000` | `100` | Maximal erlaubte Requests pro IP-Adresse innerhalb des Fensters. |
| `WindowSeconds` | `int` | `1 .. 3600` | `60` | Zeitfenster in Sekunden für das IP-Rate-Limit. |
| `QueueLimit` | `int` | `>= 0` | `0` | Warteschlangengröße vor Zurückweisung mit `HTTP 429`. |

#### `RateLimiting.PostAuthSidRateLimit`
| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `TokenBucketCapacity` | `int` | `10 .. 100000` | `500` | Maximale Token-Kapazität pro authentifizierter User-SID. |
| `TokensPerSecond` | `int` | `1 .. 10000` | `50` | Nachfüllrate des Token-Buckets pro Sekunde. |
| `MaxCostPerMinute` | `int` | `100 .. 1000000` | `10000` | Maximales GraphQL-Komplexitätsbudget pro Minute pro Benutzer. |

```json
"RateLimiting": {
  "PreAuthIpRateLimit": {
    "PermitLimit": 100,
    "WindowSeconds": 60,
    "QueueLimit": 0
  },
  "PostAuthSidRateLimit": {
    "TokenBucketCapacity": 500,
    "TokensPerSecond": 50,
    "MaxCostPerMinute": 10000
  }
}
```

---

### 2.6 `GraphQL` (Engine- & Abfrageschutz)

Steuert Hot Chocolate 14 Parameter, Komplexitätsgrenzen und Anti-CSRF-Prüfungen.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `EndpointPath` | `string` | URL-Pfad | `"/graphql"` | Relativer Endpunkt-Pfad für GraphQL-Abfragen. |
| `MaxAllowedExecutionDepth` | `int` | `1 .. 25` | `10` | Maximale Abfragetiefe zur Vermeidung zyklischer DoS-Queries. |
| `MaxAllowedComplexity` | `int` | `100 .. 10000` | `1500` | Maximales statisches Query-Kostenbudget. |
| `EnableIntrospection` | `bool` | `true \| false` | `false` | Schema-Introspektion (`__schema`). In Produktion deaktivieren! |
| `PersistedQueriesOnly` | `bool` | `true \| false` | `false` | Erlaubt nur vorregistrierte Hash-basierte Abfragen. |
| `EnableBananaCakePop` | `bool` | `true \| false` | `false` | Nitro Banana Cake Pop GraphQL IDE im Browser. In Produktion deaktivieren! |
| `MaxResponseRows` | `int` | `100 .. 100000` | `5000` | Maximal zulässige Zeilenanzahl im Response-Budget. |
| `MaxResponseBytes` | `long` | `1 MB .. 100 MB` | `10485760` (10 MB) | Maximales Byte-Budget für GraphQL-Antworten. |
| `MaxInClauseBatchSize` | `int` | `10 .. 10000` | `500` | Maximale Batch-Größe für DataLoader `IN`-Prädikate. |
| `TrustedOrigins` | `List<string>` | URLs / `"*"` | `[]` | CORS- und Anti-CSRF-Erlaubnisliste für `Origin`/`Referer`-Header. |

```json
"GraphQL": {
  "EndpointPath": "/graphql",
  "MaxAllowedExecutionDepth": 10,
  "MaxAllowedComplexity": 1500,
  "EnableIntrospection": false,
  "PersistedQueriesOnly": false,
  "EnableBananaCakePop": false,
  "MaxResponseRows": 5000,
  "MaxResponseBytes": 10485760,
  "MaxInClauseBatchSize": 500,
  "TrustedOrigins": [
    "https://portal.corp.local",
    "https://analytics.corp.local"
  ]
}
```

> [!IMPORTANT]
> **Anti-CSRF Preflight Schutz**: Jede eingehende POST- oder GET-Abfrage an `/graphql` erfordert zwingend den Header `GraphQL-Preflight: 1` oder `X-Requested-With`. Browser-Anfragen ohne diesen Header werden mit `HTTP 400 Bad Request` abgewiesen.

---

### 2.7 `DataMasking` (Kryptografische Pseudonymisierung & Maskierung)

Konfiguriert die deterministische Pseudonymisierung (`HMAC_SHA256`) sowie Maskierungs-Caches.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `HmacKeyId` | `string` | Bezeichner | `"key-2026-q1"` | Schlüssel-ID zur Unterstützung von Key-Rotationen. |
| `HmacSecretKeyVaultRef` | `string` | Secret-Name | `"DEV_INSECURE_...` | Name des Secrets in Azure Key Vault / HashiCorp Vault. |
| `MaskingCacheTtlHours` | `int` | `1 .. 168` | `24` | Gültigkeitsdauer des Caches für vorberechnete Maskierungsregeln. |

```json
"DataMasking": {
  "HmacKeyId": "key-2026-q1",
  "HmacSecretKeyVaultRef": "GQL-GATEWAY-HMAC-SECRET-KEY",
  "MaskingCacheTtlHours": 24
}
```

---

### 2.8 `Audit` (Unveränderliche SHA256-Audit-Hash-Chain)

Protokolliert Datenzugriffe manipulationssicher in einer kryptografischen Hash-Kette.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `TierAEnabled` | `bool` | `true \| false` | `true` | Tier-A Auditierung: Synchrones Schreiben sensibler Zugriffe mit SHA256-Verkettung. |
| `TierBAggregationWindowSeconds` | `int` | `1 .. 3600` | `60` | Aggregationsintervall für unkritische Tier-B Massenzugriffe. |
| `AuditLogRetentionDays` | `int` | `1 .. 7300` | `3650` (10 Jahre) | Gesetzliche Aufbewahrungsfrist für Prüfprotokolle. |
| `VerifyHashChainIntervalHours` | `int` | `1 .. 168` | `24` | Zyklische Integritätsprüfung der gesamten Prüfkette im Hintergrund. |
| `ElasticsearchSinkUrl` | `string` | URL | `""` | Optionaler sekundärer Sink für SIEM-Systeme (Splunk / Elasticsearch). |

```json
"Audit": {
  "TierAEnabled": true,
  "TierBAggregationWindowSeconds": 60,
  "AuditLogRetentionDays": 3650,
  "VerifyHashChainIntervalHours": 24,
  "ElasticsearchSinkUrl": "https://siem.corp.local:9200"
}
```

---

### 2.9 `ReverseProxy` (Forwarded Headers & Proxy-Netzwerke)

Schützt vor IP-Spoofing hinter Load Balancern (K8s Ingress, F5, Envoy, NGINX).

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Enabled` | `bool` | `true \| false` | `true` | Aktiviert die Auswertung von `X-Forwarded-For` und `X-Forwarded-Proto`. |
| `KnownNetworks` | `List<string>` | CIDR-Notation | `["127.0.0.1/32", "::1/128"]` | Vertrauenswürdige IP-Netzwerke (z. B. internes Pod-Subnetz). |
| `KnownProxies` | `List<string>` | IP-Adressen | `[]` | Feste IP-Adressen vorgelagerter Reverse Proxies. |

```json
"ReverseProxy": {
  "Enabled": true,
  "KnownNetworks": [
    "127.0.0.1/32",
    "::1/128",
    "10.244.0.0/16"
  ],
  "KnownProxies": [
    "10.0.1.50"
  ]
}
```

---

### 2.10 `OpenMetadata` (Governance-Katalog-Synchronisation & Webhooks)

Automatische Synchronisation von Schema-Metadaten, Klassifikations-Tags (`PII.*`) und Ownership aus OpenMetadata.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Enabled` | `bool` | `true \| false` | `false` | Aktiviert den OpenMetadata-Sync-Background-Worker. |
| `ServerUrl` | `string` | URL | `"http://localhost:8585/api/v1"` | Basis-URL der OpenMetadata REST API. |
| `AuthToken` | `string` | JWT / Token | `""` | Bearer-Token für die OpenMetadata API. |
| `WebhookSecret` | `string` | Secret | `""` | HMAC-SHA256 Secret zur Validierung eingehender Webhooks unter `/api/webhooks/openmetadata`. |
| `ServiceFilter` | `string` | Service-Name | `""` | Filtert Tabellen auf einen bestimmten Datenservice. |
| `SyncIntervalMinutes` | `int` | `1 .. 1440` | `30` | Intervall des Hintergrund-Pollings. |
| `TagToMaskingRuleMap` | `Map` | Tag -> MaskingType | *Siehe unten* | Zuordnung von OpenMetadata Klassifikations-Tags zu Maskierungsregeln. |
| `TeamToGroupSidMap` | `Map` | Team -> SID | `{}` | Übersetzung von OpenMetadata Teams in Active Directory Gruppen-SIDs. |
| `UserToUserSidMap` | `Map` | User -> SID | `{}` | Übersetzung von OpenMetadata Usernames in Windows User-SIDs. |

```json
"OpenMetadata": {
  "Enabled": true,
  "ServerUrl": "https://openmetadata.corp.local/api/v1",
  "AuthToken": "eyJhbGciOi...",
  "WebhookSecret": "OM-WEBHOOK-HMAC-SECRET-2026",
  "ServiceFilter": "enterprise_dw",
  "SyncIntervalMinutes": 30,
  "TagToMaskingRuleMap": {
    "PII.Sensitive": "REDACT",
    "PII.Email": "MASK_EMAIL",
    "PII.Pseudonym": "HMAC_SHA256",
    "PersonalData.Personal": "REDACT"
  },
  "TeamToGroupSidMap": {
    "FinanceAnalytics": "S-1-5-21-5001",
    "DataScienceTeam": "S-1-5-21-5002"
  },
  "UserToUserSidMap": {
    "john.doe": "S-1-5-21-1001"
  }
}
```

---

### 2.11 `Plugins` (Isolierte C#-Konnektoren)

Verwaltet dynamische C#-Erweiterungen (`IHttpDataSourcePlugin`) in isolierten `AssemblyLoadContext`-Instanzen.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Directory` | `string` | Verzeichnispfad | `"plugins"` | Relativer oder absoluter Pfad zum Plugin-Ordner mit DLLs. |
| `EnableHotReload` | `bool` | `true \| false` | `false` | Ermöglicht das Neuladen von Plugins zur Laufzeit ohne Neustart. |

```json
"Plugins": {
  "Directory": "/var/gql-gateway/plugins",
  "EnableHotReload": false
}
```

---

## 3. Umgebungsvariablen & Kubernetes-Deployment

In Containern und Cloud-Umgebungen (Kubernetes, Docker) werden Konfigurationswerte über Umgebungsvariablen mit doppelten Unterstrichen (`__`) überschrieben.

### 3.1 Wichtige Umgebungsvariablen-Referenz

```bash
# ASP.NET Core Hosting & Environment
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:5000

# High Availability
Gateway__HighAvailability__DrainDelaySeconds=5
Gateway__HighAvailability__QueryTimeoutSeconds=30
Gateway__HighAvailability__ShutdownTimeoutSeconds=40
Gateway__HighAvailability__TerminationGracePeriodSeconds=60

# Authentifizierung & Sicherheit
Gateway__Authentication__Domain=CORP.LOCAL
Gateway__Authentication__ServicePrincipalName=HTTP/gateway.corp.local
Gateway__Authentication__RequireKerberosOnly=true
Gateway__Authentication__EnableTestAuthHandler=false

# Governance-Datenbank
Gateway__GovernanceDb__Provider=SqlServer
Gateway__GovernanceDb__ConnectionString="Server=sql-ha.corp.local;Database=Governance;Integrated Security=SSPI;TrustServerCertificate=True;"

# Caching & Redis Cluster
Gateway__Caching__Redis__Configuration="redis-ha.corp.local:6379,abortConnect=false,ssl=true,password=SecretRedisPass!"
Gateway__Caching__EpochValidation__FailClosedOnSensitiveTables=true

# Secrets & Schlüssel
Gateway__DataMasking__HmacKeyId=key-2026-q1
Gateway__DataMasking__HmacSecretKeyVaultRef=GQL-HMAC-SECRET-KEY

# GraphQL & Origin-Sicherheit
Gateway__GraphQL__EnableIntrospection=false
Gateway__GraphQL__EnableBananaCakePop=false
Gateway__GraphQL__TrustedOrigins__0=https://bi.corp.local
Gateway__GraphQL__TrustedOrigins__1=https://portal.corp.local

# OpenMetadata
Gateway__OpenMetadata__Enabled=true
Gateway__OpenMetadata__ServerUrl=https://openmetadata.corp.local/api/v1
Gateway__OpenMetadata__AuthToken=eyJhbGciOi...
Gateway__OpenMetadata__WebhookSecret=MyWebhookHmacSecretKey
```

### 3.2 Beispiel Kubernetes Deployment & ConfigMap

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: gql-gateway
  namespace: data-governance
spec:
  replicas: 3
  selector:
    matchLabels:
      app: gql-gateway
  template:
    metadata:
      labels:
        app: gql-gateway
    spec:
      terminationGracePeriodSeconds: 60
      containers:
        - name: gateway
          image: registry.corp.local/gql-gateway:latest
          ports:
            - containerPort: 5000
          resources:
            requests:
              cpu: "1000m"
              memory: "1Gi"
            limits:
              cpu: "4000m"
              memory: "2Gi"
          lifecycle:
            preStop:
              exec:
                command: ["/bin/sh", "-c", "sleep 5"]
          livenessProbe:
            httpGet:
              path: /health/live
              port: 5000
            initialDelaySeconds: 5
            periodSeconds: 10
          readinessProbe:
            httpGet:
              path: /health/ready
              port: 5000
            initialDelaySeconds: 5
            periodSeconds: 5
          envFrom:
            - configMapRef:
                name: gql-gateway-config
            - secretRef:
                name: gql-gateway-secrets
```

---

## 4. Konfigurationsprofile im Vergleich

| Parameter | Development (`appsettings.Development.json`) | Production (`appsettings.Production.json`) |
| :--- | :--- | :--- |
| `Logging:LogLevel:Default` | `Debug` | `Information` / `Warning` |
| `Authentication:EnableTestAuthHandler` | `true` (erlaubt Test-Header) | `false` (**Zwingend vorgeschrieben**) |
| `Authentication:RequireKerberosOnly` | `false` | `true` |
| `GovernanceDb:Provider` | `Sqlite` (In-Memory `:memory:`) | `SqlServer` oder `PostgreSql` |
| `Caching:EpochValidation:FailClosed` | `false` | `true` (Zero-Trust Fail-Closed) |
| `GraphQL:EnableIntrospection` | `true` | `false` |
| `GraphQL:EnableBananaCakePop` | `true` (Nitro IDE im Browser) | `false` |
| `GraphQL:PersistedQueriesOnly` | `false` | `true` (empfohlen) |
| `DataMasking:HmacSecretKeyVaultRef` | `"DEV_INSECURE_TEST_KEY_ONLY"` | Key Vault Secret Referenz (**Validiert**) |
| `Audit:TierBAggregationWindowSeconds` | `5` Sekunden | `60` Sekunden |

---

## 5. Checkliste für den Produktions-Rollout

Vor Freigabe einer neuen Produktivumgebung sind folgende Punkte zu verifizieren:

- [ ] **Auth-Sicherheit**: `Gateway:Authentication:EnableTestAuthHandler` steht auf `false`.
- [ ] **Kryptografie**: `Gateway:DataMasking:HmacSecretKeyVaultRef` verweist auf ein valides Key Vault Secret und nicht auf Dev-Defaults.
- [ ] **Introspektion**: `Gateway:GraphQL:EnableIntrospection` und `EnableBananaCakePop` sind auf `false` gesetzt.
- [ ] **Zero-Trust Fail-Closed**: `Gateway:Caching:EpochValidation:FailClosedOnSensitiveTables` ist auf `true`.
- [ ] **Anti-CSRF & CORS**: `Gateway:GraphQL:TrustedOrigins` enthält nur verifizierte Domänen (kein Wildcard `*` in Produktion!).
- [ ] **High Availability**: K8s `terminationGracePeriodSeconds` ist größer als `DrainDelaySeconds + ShutdownTimeoutSeconds + 10s`.
- [ ] **Proxy-Sicherheit**: `Gateway:ReverseProxy:KnownNetworks` schränkt vertrauenswürdige IPs auf tatsächliche Ingress-Controller ein.
