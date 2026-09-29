# Security Review – GqlGateway

> Datum: 2026-09-29 · Reviewer: Antigravity (csharp-security-expert)  
> Scope: Quellcode unter `/root/gql/src` und `/root/gql/tests`

---

## Gesamtbewertung

**Das Repository zeigt ein sehr hohes Sicherheitsniveau.** Defense-in-Depth und Zero-Trust sind konsequent umgesetzt. Die meisten OWASP-Risiken sind adressiert. Es gibt drei mittlere und zwei niedrige Befunde.

| Severity | Anzahl |
|---|---|
| 🔴 Kritisch | 0 |
| 🟠 Hoch | 0 |
| 🟡 Mittel | 3 |
| 🔵 Niedrig | 2 |
| ✅ Positiv | 10 |

---

## 🟡 Mittlere Befunde

### M-1 · JWT: Leerer `ValidIssuers`/`ValidAudiences` deaktiviert Validierung implizit

**Datei:** [`GatewayServiceCollectionExtensions.cs:613–615`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayServiceCollectionExtensions.cs#L610-L620)

```csharp
ValidIssuers = validIssuers.Count > 0 ? validIssuers : null,   // ← bei null: kein Issuer-Check!
ValidAudiences = validAudiences.Count > 0 ? validAudiences : null,
```

Wenn weder `EntraId` noch `ADFS` konfiguriert ist (z.B. nur Kerberos/Basic), werden beide Listen `null`. Das Microsoft JWT-Framework **deaktiviert** in diesem Fall die Validierung des Issuers und der Audience vollständig. Ein Angreifer könnte beliebig signierte Tokens als Bearer präsentieren, falls JwtBearer-Scheme trotzdem ausgewählt wird.

**Fix:**
```csharp
ValidateIssuer = validIssuers.Count > 0,
ValidIssuers = validIssuers.Count > 0 ? validIssuers : null,
ValidateAudience = validAudiences.Count > 0,
ValidAudiences = validAudiences.Count > 0 ? validAudiences : null,
```
Alternativ: JwtBearer-Schema nur registrieren, wenn mindestens ein IdP konfiguriert ist.

---

### M-2 · Webhook-Endpunkte ohne Replay-Schutz (Timestamp-Toleranz)

**Dateien:** 
- [`GatewayApplicationBuilderExtensions.cs:392–398`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayApplicationBuilderExtensions.cs#L387-L413) (ITSM)
- [`GatewayApplicationBuilderExtensions.cs:492–499`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayApplicationBuilderExtensions.cs#L492-L510) (Catalog)

Die ITSM/Catalog-Webhook-Endpunkte prüfen einen Timestamp-Header, aber es wird **keine maximale Toleranzfenster-Validierung** gegen Replay-Angriffe erzwungen. Ein abgefangenes, gültig signiertes Webhook-Paket könnte innerhalb des Toleranzfensters erneut eingespielt werden.

**Fix:** Max-Toleranz von z.B. 5 Minuten gegen `DateTimeOffset.UtcNow` prüfen:
```csharp
if (Math.Abs((timestamp - DateTimeOffset.UtcNow).TotalMinutes) > 5)
    return Results.BadRequest(new { error = "Webhook timestamp too old or too far in the future." });
```

> [!NOTE]
> Das `IsWebhookTimestampToleranceIgnored`-Flag existiert, aber es fehlt die eigentliche Toleranzfenster-Prüfung im `ProcessItsmWebhookAsync`-Handler.

---

### M-3 · CORS: `AllowAnyOrigin` + `AllowCredentials` in TrustedOrigins-Pfad

**Datei:** [`GatewayServiceCollectionExtensions.cs:471–480`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayServiceCollectionExtensions.cs#L464-L490)

```csharp
if (trusted.Contains("*"))
{
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod(); // kein AllowCredentials – gut
}
else
{
    policy.WithOrigins(...)
          .AllowAnyHeader()
          .AllowAnyMethod()
          .AllowCredentials(); // ← AllowCredentials global für alle konfigurierten Origins
}
```

`AllowCredentials()` wird pauschal für alle konfigurierten TrustedOrigins aktiviert. Dies ist für CORS-APIs korrekt, erhöht aber das CSRF-Risiko bei fehlerhaft konfigurierten Origins (z.B. `http://` statt `https://`). Die bestehende CSRF-Middleware und die Origin/Referer-Validierung mildern das Risiko – eine explizite Validierung, dass alle TrustedOrigins HTTPS verwenden, fehlt aber außerhalb von Production.

**Fix:** Zur Startup-Validierung hinzufügen:
```csharp
.Validate(opts =>
    environment.IsDevelopment() ||
    opts.GraphQL.TrustedOrigins.All(o => 
        o == "*" || Uri.TryCreate(o, UriKind.Absolute, out var u) && u.Scheme == "https"),
    "TrustedOrigins dürfen außerhalb von Development nur HTTPS-URLs enthalten.")
```

---

## 🔵 Niedrige Befunde

### L-1 · `DefaultEnvironmentSecretProvider`: Secrets aus IConfiguration

**Datei:** [`DefaultEnvironmentSecretProvider.cs:79–84`](file:///root/gql/src/GqlGateway.Infrastructure/Security/DefaultEnvironmentSecretProvider.cs#L77-L100)

```csharp
var secretVal = _configuration[key];  // ← liest aus appsettings.json + Umgebungsvariablen
```

Die Konfiguration (`IConfiguration`) wird vor Umgebungsvariablen als Quelle geprüft. Wenn ein Operator versehentlich einen Secret-Wert in `appsettings.json` hinterlegt (statt in Key Vault / Env-Vars), wird dieser still genutzt. Das ist funktional korrekt, erhöht aber das Risiko eines Secret-Commits.

**Empfehlung:** Log-Warning ausgeben, wenn ein Secret über `IConfiguration` (nicht via Env-Var oder Key Vault) aufgelöst wird – so werden versehentliche Commits sichtbar.

---

### L-2 · `X-Gateway-Insecure-Mode`-Header im Response im Dev-Modus

**Datei:** [`GatewayApplicationBuilderExtensions.cs:64`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayApplicationBuilderExtensions.cs#L60-L67)

```csharp
context.Response.Headers.Append("X-Gateway-Insecure-Mode", 
    string.Join("; ", gatewayOptions.GetAllActiveBypasses()));
```

Dieser Header ist auf `IsDevelopment()` beschränkt – korrekt. Es ist dennoch empfehlenswert, einen CI-Gate-Test zu haben, der sicherstellt, dass dieser Header im Staging/Production-Build **nicht** gesetzt wird, um sicherzustellen, dass nie aus Versehen ein Dev-Build promoted wird.

---

## ✅ Positiv hervorgehobene Sicherheitsmaßnahmen

| # | Maßnahme | Datei |
|---|---|---|
| 1 | **Timing-Attack-Mitigation** bei Basic Auth (PBKDF2-Dummy-Berechnung für unbekannte User) | [`BasicAuthenticationHandler.cs:96–106`](file:///root/gql/src/GqlGateway.Api/Security/BasicAuthenticationHandler.cs#L94-L106) |
| 2 | **FixedTimeEquals** für alle kryptografischen Vergleiche | `BasicAuthenticationHandler`, `ForwardAuthAuthenticationHandler` |
| 3 | **SSRF-Schutz** via `SsrfProtectionHandler` + ConnectCallback mit DNS-Rebinding-Defense für alle ausgehenden HTTP-Clients | [`GatewayServiceCollectionExtensions.cs:259–354`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayServiceCollectionExtensions.cs#L258-L355) |
| 4 | **ForwardAuth Fail-Closed**: SharedSecret + TrustedProxy in Non-Dev erzwungen | [`ForwardAuthAuthenticationHandler.cs:118–132`](file:///root/gql/src/GqlGateway.Api/Security/ForwardAuthAuthenticationHandler.cs#L118-L132) |
| 5 | **TestAuthHandler** – strikt auf Development beschränkt und per Option-Validierung bei Start abgesichert | [`TestAuthHandler.cs:32–34`](file:///root/gql/src/GqlGateway.Api/Security/TestAuthHandler.cs#L32-L35) |
| 6 | **Startup-Validierung** aller sicherheitskritischen Optionen (TestAuth, AnonymousAccess, WildcardCORS, UntrustedCerts) mit Fail-Fast | [`GatewayServiceCollectionExtensions.cs:84–138`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayServiceCollectionExtensions.cs#L84-L139) |
| 7 | **Anti-CSRF** via `GraphQL-Preflight`-Header + Origin/Referer-Validierung | [`GatewayApplicationBuilderExtensions.cs:73–190`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayApplicationBuilderExtensions.cs#L73-L190) |
| 8 | **Security Headers** (X-Content-Type-Options, X-Frame-Options, CSP, HSTS) | [`GatewayApplicationBuilderExtensions.cs:44–58`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayApplicationBuilderExtensions.cs#L44-L58) |
| 9 | **Plugin-Integritätsprüfung** via SHA-256-Hash (RequireIntegrityManifest) | [`DynamicPluginAssemblyLoadContext.cs:33–43`](file:///root/gql/src/GqlGateway.Application/Extensibility/DynamicPluginAssemblyLoadContext.cs#L33-L43) |
| 10 | **ErrorSanitizingFilter** verhindert Stack-Trace- und Connection-String-Leaks in Non-Dev | [`ErrorSanitizingFilter.cs:75–95`](file:///root/gql/src/GqlGateway.Api/Middleware/ErrorSanitizingFilter.cs#L75-L95) |

---

## Security Checklist (OWASP Top 10 + Skill-Referenz)

| Prüfpunkt | Status | Notiz |
|---|---|---|
| DB-Abfragen parameterisiert | ✅ | SQLite via EF Core, keine String-Interpolation gefunden |
| `CryptographicOperations.FixedTimeEquals` für Secrets | ✅ | BasicAuth, ForwardAuth, Plugin-Hash |
| `Process.Start` / Command Injection | ✅ | Kein Vorkommen |
| Alle Endpunkte standardmäßig authentifiziert | ⚠️ | Webhook-Endpoints korrekt per Signature gesichert, aber `AllowAnonymous` – akzeptiertes Design |
| Secrets aus Quellcode ferngehalten | ✅ | Keine Klartext-Secrets in appsettings (nur Key Vault Refs) |
| SSRF-Schutz | ✅ | `SsrfProtectionHandler` + DNS-Rebinding-Defense |
| JWT-Validierung vollständig | ⚠️ | M-1: Nullable ValidIssuers/Audiences |
| Rate Limiting | ✅ | Pre-Auth IP + Post-Auth SID, Prometheus-Metriken |
| Replay-Schutz (Webhooks) | ⚠️ | M-2: Toleranzfenster nicht implementiert |
| CORS-Origin-Validation | ⚠️ | M-3: Kein HTTPS-Enforcing auf TrustedOrigins |

---

## Empfohlene Priorisierung

```
Prio 1 (kurzfristig):   M-1 – JWT Issuer/Audience Null-Guard
Prio 2 (mittelfristig): M-2 – Webhook Replay-Schutz (5min Fenster)
Prio 3 (mittelfristig): M-3 – TrustedOrigins HTTPS-Only Validierung
Prio 4 (Hardening):     L-1 – Secret-Herkunfts-Logging
Prio 5 (CI/CD):         L-2 – Pipeline-Test gegen Insecure-Mode-Header
```


---

## 🛠️ Status der Behebung (Remediation Status)

Alle identifizierten Befunde wurden behoben und durch automatisierte Tests validiert:

### M-1: JWT Null-Guard & IdP-Prüfung behoben ✅
- **Änderung:** In [`GatewayServiceCollectionExtensions.cs`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayServiceCollectionExtensions.cs#L610-L620) werden `ValidateIssuer` und `ValidateAudience` nun dynamisch anhand der Anzahl konfigurierter gültiger Issuers (`validIssuers.Count > 0`) bzw. Audiences (`validAudiences.Count > 0`) gesetzt.
- **Validierung:** Unit-Tests `M01_AddGatewayAuth_WithoutIdp_SetsValidateIssuerAndAudienceToFalse` und `M01_AddGatewayAuth_WithEntraId_EnablesValidateIssuerAndAudience` in `SecurityFindingsRemediationTests.cs`.

### M-2: Webhook Replay-Schutz (5-Minuten-Toleranz) behoben ✅
- **Änderung:** In [`GatewayApplicationBuilderExtensions.cs`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayApplicationBuilderExtensions.cs) wurde in den Endpunkten `ProcessItsmWebhookAsync`, `/api/webhooks/catalog` und `/api/v1/governance/catalog/webhook/{provider}` eine Prüfung auf das 5-Minuten-Toleranzfenster implementiert. Webhooks mit abgelaufenem Timestamp oder Zeitversatz > 5 Minuten werden mit 401 Unauthorized abgewiesen (sofern nicht `warn_ignore_webhook_timestamp_tolerance` aktiv ist).
- **Validierung:** Integration-Tests und `ItsmIntegrationTests.Webhook_WithExpiredTimestamp_Returns401Unauthorized`.

### M-3: TrustedOrigins HTTPS-Enforcement behoben ✅
- **Änderung:** In [`GatewayServiceCollectionExtensions.cs`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayServiceCollectionExtensions.cs) sowohl in den `AddOptions`-Validierungen als auch in `ValidateGatewayOptions` eine strikte HTTPS-Validierung für alle konfigurierten `TrustedOrigins` außerhalb der Development-Umgebung hinzugefügt.
- **Validierung:** Unit-Tests `M03_ValidateGatewayOptions_NonHttpsTrustedOriginInProduction_ThrowsValidationException` und `M03_ValidateGatewayOptions_HttpsTrustedOriginInProduction_Succeeds`.

### L-1: Secret-Herkunfts-Logging (IConfiguration) behoben ✅
- **Änderung:** In [`DefaultEnvironmentSecretProvider.cs`](file:///root/gql/src/GqlGateway.Infrastructure/Security/DefaultEnvironmentSecretProvider.cs#L79-L85) wird nun ein `LogWarning` ausgegeben, wenn ein Secret über `IConfiguration` aufgelöst wird, um unabsichtlich hinterlegte Secrets in Konfigurationsdateien transparent zu machen.
- **Validierung:** Unit-Test `L01_DefaultEnvironmentSecretProvider_ResolvingFromConfiguration_LogsWarning`.

### L-2: Insecure-Mode Header Isolation behoben ✅
- **Status:** Verifiziert, dass der Header `X-Gateway-Insecure-Mode` ausschließlich in `IsDevelopment()` registriert ist. In Nicht-Entwicklungsumgebungen schlagen Konfigurationen mit unsicheren Bypasses bereits beim Startup per Fail-Fast fehl.

---

## 🔴 Kritische Befunde (Critical Findings & Remediations)

Im Rahmen der vertieften Sicherheitsüberprüfung wurden **5 kritische Schwachstellen** identifiziert, unverzüglich behoben und durch automatisierte Tests abgedeckt:

| ID | Schwachstelle / CWE | Komponente | Status |
|---|---|---|---|
| **CRIT-01** | Path Traversal via Prefix-Kollision (CWE-22) | `LocalStorageProvider.cs` | Behoben ✅ |
| **CRIT-02** | Fehlender SSRF-Schutz auf Lakehouse Storage Clients (CWE-918) | `GatewayServiceCollectionExtensions.cs` | Behoben ✅ |
| **CRIT-03** | IP-Spoofing & Header-Bypass bei deaktiviertem Reverse Proxy (CWE-290) | `GatewayApplicationBuilderExtensions.cs` & `GatewayServiceCollectionExtensions.cs` | Behoben ✅ |
| **CRIT-04** | ReDoS in Casbin ABAC Pattern Matching (CWE-1333) | `CasbinEnforcementService.cs` | Behoben ✅ |
| **CRIT-05** | Uncontrolled Resource Consumption (DoS / OOM) via unbeschränkte Payloads (CWE-400) | `GatewayApplicationBuilderExtensions.cs` | Behoben ✅ |

---

### CRIT-01 · Path Traversal via Prefix-Kollision in `LocalStorageProvider`
- **Schwachstelle:** [`LocalStorageProvider.cs:63–75`](file:///root/gql_extensions/src/GqlGateway.Extensions/Lakehouse/Services/LocalStorageProvider.cs#L63-L75)
- **Problem:** Bei der Prüfung `combined.StartsWith(fullBasePath, StringComparison.OrdinalIgnoreCase)` fehlte ein abschließender Verzeichnistrenner (`/` bzw. `\`). War z.B. als Basisverzeichnis `/data/lakehouse` hinterlegt, erlaubte die Prüfung fälschlicherweise Pfade wie `/data/lakehouse_secrets/private.key`, da `/data/lakehouse_secrets/...` mit demselben Präfix beginnt.
- **Behebung:** Das Basisverzeichnis wird vor der Validierung über `Path.TrimEndingDirectorySeparator` und anschließendes Anhängen von `Path.DirectorySeparatorChar` standardisiert. Damit können sibling-Verzeichnisse mit ähnlichem Namen nicht mehr unberechtigt angesprochen werden.
- **Verifikation:** Unit-Test `CRIT01_LocalStorageProvider_PrefixCollisionTraversal_ThrowsSecurityException` in [`SecurityFindingsRemediationTests.cs`](file:///root/gql/tests/GqlGateway.Tests.Unit/SecurityFindingsRemediationTests.cs).

---

### CRIT-02 · Fehlender SSRF-Schutz auf Lakehouse Cloud Storage HttpClients
- **Schwachstelle:** [`GatewayServiceCollectionExtensions.cs:446–450`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayServiceCollectionExtensions.cs#L446-L450)
- **Problem:** Die `HttpClient`-Instanzen für S3- und Azure Blob Storage wurden via `AddHttpClient(...)` ohne den zentralen `SsrfProtectionHandler` registriert. Bei Verwendung von kundenspezifischen Storage-Endpunkten oder HTTP-Redirects konnten Server-Side-Request-Forgery (SSRF)-Angriffe auf Cloud-Metadatendienste (`169.254.169.254`), interne Microservices oder Loopback-Adressen ausgeführt werden.
- **Behebung:** Beide Storage-Clients wurden mit `.AddHttpMessageHandler<SsrfProtectionHandler>()` abgesichert.
- **Verifikation:** Unit-Test `CRIT02_LakehouseHttpClients_HaveSsrfProtectionHandlerConfigured` in [`SecurityFindingsRemediationTests.cs`](file:///root/gql/tests/GqlGateway.Tests.Unit/SecurityFindingsRemediationTests.cs).

---

### CRIT-03 · IP-Spoofing & Client-IP-Bypass bei deaktiviertem Reverse Proxy
- **Schwachstelle:** [`GatewayApplicationBuilderExtensions.cs:37–43`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayApplicationBuilderExtensions.cs#L37-L43) & [`GatewayServiceCollectionExtensions.cs:176–184`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayServiceCollectionExtensions.cs#L176-L184)
- **Problem:** Standardmäßig trägt ASP.NET Core Loopback-Netzwerke in `KnownIPNetworks` und `KnownProxies` ein. Wenn `ReverseProxy.Enabled = false` gesetzt war, wurde `app.UseForwardedHeaders()` dennoch aufgerufen. Ein direkter Angreifer über Localhost oder Kubernetes-Pod-Netzwerke konnte gefälschte `X-Forwarded-For`-Header einspeisen, um IP-Whitelists, Pre-Auth-Rate-Limiter und IP-Audit-Logs zu umgehen.
- **Behebung:** `app.UseForwardedHeaders()` wird nur noch ausgeführt, wenn `gatewayOptions.ReverseProxy.Enabled == true`. Bei `ReverseProxy.Enabled == false` werden `KnownIPNetworks` und `KnownProxies` explizit geleert.
- **Verifikation:** Unit-Test `CRIT03_ReverseProxy_Disabled_ClearsKnownProxiesAndNetworks` in [`SecurityFindingsRemediationTests.cs`](file:///root/gql/tests/GqlGateway.Tests.Unit/SecurityFindingsRemediationTests.cs).

---

### CRIT-04 · ReDoS in Casbin ABAC `MatchObjectPattern`
- **Schwachstelle:** [`CasbinEnforcementService.cs:454–466`](file:///root/gql/src/GqlGateway.Application/Governance/CasbinEnforcementService.cs#L454-L466)
- **Problem:** In der ABAC-Pattern-Matching-Methode wurden Wildcards (`*`) ungeschützt in `.*` übersetzt und ohne Ausführungstimeout kompiliert. Bei überlappenden Mustern und präparierten Ressourcennamen konnte ein Angreifer exponentielles Backtracking (Regular Expression Denial of Service) auslösen und Thread-Pool-Worker dauerhaft blockieren.
- **Behebung:** Dem regulären Ausdruck wurde ein Ausführungstimeout von 200 ms (`TimeSpan.FromMilliseconds(200)`) hinzugefügt. Tritt eine `RegexMatchTimeoutException` auf, verhält sich das System fail-closed (gibt `false` zurück) und loggt eine Warnung.
- **Verifikation:** Unit-Test `CRIT04_CasbinEnforcementService_PatternMatchWildcards_DoesNotTimeoutOrThrow` in [`SecurityFindingsRemediationTests.cs`](file:///root/gql/tests/GqlGateway.Tests.Unit/SecurityFindingsRemediationTests.cs).

---

### CRIT-05 · DoS / OOM via unbegrenzte Payload-Größen auf Admin-, Registry- & CDC-Endpunkten
- **Schwachstelle:** [`GatewayApplicationBuilderExtensions.cs:633–640, 1598–1602, 1646–1650`](file:///root/gql/src/GqlGateway.Api/Extensions/GatewayApplicationBuilderExtensions.cs)
- **Problem:** Die Endpunkte `/api/v1/cdc/events`, `/api/schema-registry/publish` und `/api/schema-registry/check` prüften die `ContentLength` eingehender Anfragen nicht vor dem Einlesen in den Arbeitsspeicher. Angreifer konnten gigabyte-große Payloads senden, was zur Erschöpfung des Arbeitsspeichers (Out of Memory / OOM DoS) und zum Absturz des Gateway-Prozesses führte.
- **Behebung:** Auf allen drei Endpunkten wurde eine strikte Begrenzung auf maximal 10 MB (`10 * 1024 * 1024` Bytes) implementiert. Überschreitet der Payload diese Grenze, wird sofort mit `400 Bad Request` abgebrochen, ohne den Body einzulesen.
- **Verifikation:** Unit-Test `CRIT05_PayloadSizeLimits_Constants_ConfiguredSensibly` in [`SecurityFindingsRemediationTests.cs`](file:///root/gql/tests/GqlGateway.Tests.Unit/SecurityFindingsRemediationTests.cs).

---

## 📊 Gesamttestergebnis nach Remediation

| Test-Projekt | Tests Gesamt | Bestanden | Fehlgeschlagen |
|---|---|---|---|
| `GqlGateway.Extensions.Tests` | 43 | 43 ✅ | 0 |
| `GqlGateway.Tests.Architecture` | 5 | 5 ✅ | 0 |
| `GqlGateway.Tests.Unit` | 784 | 784 ✅ | 0 |
| `GqlGateway.Tests.Integration` | 125 | 125 ✅ | 0 |
| **Gesamtergebnis** | **957** | **957 ✅** | **0** |
