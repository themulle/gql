# 🔐 Security Review – GqlGateway (.NET 10)

**Datum:** 2026-09-28  
**Reviewer:** C# Application Security Expert (Subagent)  
**Projekt:** `/root/gql` – GqlGateway.sln  
**Framework:** ASP.NET Core / .NET 10, HotChocolate 14.1, Casbin.NET 2.21, EF-less SQLite/PostgreSQL ADO.NET  
**Scope:** 374 C#-Quelldateien in 6 Produktionsprojekten

---

## Zusammenfassung der Befunde

| Schweregrad | Anzahl |
|---|---|
| 🔴 KRITISCH | 2 |
| 🟠 HOCH | 3 |
| 🟡 MITTEL | 4 |
| 🟢 NIEDRIG / Informativ | 5 |

---

## 🔴 KRITISCHE Befunde

### CRIT-01 – Unsigned Plugin DLL Execution (Arbitrary Code Execution)

**Beschreibung:**  
Der `PluginManager` lädt alle `.dll`-Dateien aus einem konfigurierten Verzeichnis (`plugins/`) mittels `Assembly.LoadFromAssemblyPath` ohne jegliche kryptografische Signaturprüfung. Schreibt ein Angreifer oder ein kompromittiertes CI/CD-System eine beliebige DLL in dieses Verzeichnis, wird sie beim Serverstart oder Reload vollständig im Kontext des Gateway-Prozesses ausgeführt – was zur vollständigen Remote Code Execution (RCE) führt.

**Betroffene Dateien:**
- `/root/gql/src/GqlGateway.Infrastructure/Plugins/PluginManager.cs` – `LoadPluginsFromDirectory()` (Zeilen 41–119)
- `/root/gql/src/GqlGateway.Application/Extensibility/DynamicPluginAssemblyLoadContext.cs` (Zeilen 58–75)

**Details:**  
Der Path-Traversal-Schutz (Z. 61–65 in `PluginManager`) verhindert nur, dass Symlinks aus dem Plugin-Verzeichnis heraus zeigen. Es gibt keine Prüfung auf:
- Digitale Signaturen (Authenticode, StrongName)
- Hashwert-Manifeste
- Integritätscheck mit Key-Vault-referenzierten Schlüsseln

**Lösungsvorschlag:**

```csharp
// SHA-256 Manifest-Prüfung (manifest.json im Plugin-Verzeichnis)
// { "plugins": [{ "file": "MyPlugin.dll", "sha256": "abc123..." }] }

private static void VerifyPluginIntegrity(string dllPath, Dictionary<string, string> manifest)
{
    var filename = Path.GetFileName(dllPath);
    if (!manifest.TryGetValue(filename, out var expectedHash))
        throw new SecurityException($"Plugin '{filename}' ist nicht im Integrity-Manifest verzeichnet.");

    var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dllPath)));
    if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expectedHash.ToUpperInvariant()),
            Convert.FromHexString(actualHash)))
    {
        throw new SecurityException($"Integritätsprüfung fehlgeschlagen für Plugin '{filename}'.");
    }
}
```

> **CAUTION:** Dies ist die schwerwiegendste Lücke im System. Das Plugin-System öffnet eine vollständige RCE-Angriffsfläche, wenn das `plugins/`-Verzeichnis von Angreifern beschreibbar ist oder über ein kompromittiertes Deployment manipuliert werden kann.

---

### CRIT-02 – SQL-Injection via RLS-Filter-Template-Interpolation (Casbin)

**Beschreibung:**  
Die Methode `InterpolateRlsFilter()` in `CasbinEnforcementService` ersetzt Platzhalter wie `${user_sid}`, `${tenant}`, `${department}` etc. direkt durch Claims-Werte – **ohne SQL-Parameterisierung oder Escaping**. Der resultierende SQL-String wird direkt als WHERE-Klausel in den Datenbankbefehl injiziert (`whereParts.Add($"({context.AccessDecision.CombinedRowFilterSql})")` in `SqlDataSourceExecutor.cs:130`).

**Betroffene Dateien:**
- `/root/gql/src/GqlGateway.Application/Governance/CasbinEnforcementService.cs` – `InterpolateRlsFilter()` (Z. 332–362)
- `/root/gql/src/GqlGateway.Application/Services/SqlDataSourceExecutor.cs` – WHERE-Klausel (Z. 128–131)

**Angriffsszenario:**
```
Department-Claim: Finance') OR ('1'='1
→ erzeugt: WHERE (dept = 'Finance') OR ('1'='1')
→ Bypasses alle Zeilenfilter, vollständiger Datenzugriff
```

**Lösungsvorschlag:**
```csharp
// Claim-Werte mit striktem Whitelist-Regex validieren
private static readonly Regex SafeClaimValueRegex = new(@"^[a-zA-Z0-9\-_.@]{1,256}$", RegexOptions.Compiled);

private static string SanitizeClaimForSql(string? value, string claimName)
{
    if (string.IsNullOrEmpty(value)) return string.Empty;
    if (!SafeClaimValueRegex.IsMatch(value))
        throw new SecurityException($"Claim '{claimName}' enthält ungültige Zeichen für SQL-Interpolation.");
    return value;
}
// Besser noch: RLS-Filter-Templates nur mit Identifier-Platzhaltern; Werte immer als DbParameter.
```

> **CAUTION:** Diese Lücke betrifft den zentralen Zero-Trust-RLS-Pfad. Ein erfolgreicher Exploit umgeht alle Zeilenfilter und ermöglicht vollständigen Datenbankzugriff.

---

## 🟠 HOHE Befunde

### HIGH-01 – DangerousAcceptAnyServerCertificateValidator ohne Startup-Validation-Guard

**Beschreibung:**  
`danger_allow_untrusted_certificates = true` deaktiviert global für alle HttpClients die TLS-Zertifikatsvalidierung. Diese Option fehlt in der `ValidateGatewayOptions()`-Startup-Validation (anders als `EnableTestAuthHandler` oder `danger_allow_anonymous_access`).

**Betroffene Datei:**
- `/root/gql/src/GqlGateway.Api/Extensions/GatewayServiceCollectionExtensions.cs` – Zeilen 252–261

**Lösungsvorschlag:**
```csharp
// In AddGatewayOptions() Validate-Chain ergänzen:
.Validate(opts =>
    environment.IsDevelopment() || !opts.AreUntrustedCertificatesAllowed,
    "Sicherheitsverletzung: danger_allow_untrusted_certificates darf AUSSCHLIESSLICH in der Development-Umgebung true sein!")
```

---

### HIGH-02 – Wildcard CORS (`TrustedOrigins: ["*"]`) deaktiviert CSRF-Schutz vollständig

**Beschreibung:**  
Wenn `TrustedOrigins` den Eintrag `"*"` enthält (`IsAllCorsAllowed = true`), werden alle Origin/Referer-Prüfungen und der GraphQL-Preflight-Header-Check für alle Endpunkte übersprungen. Dies ermöglicht CSRF-Angriffe.

**Betroffene Dateien:**
- `/root/gql/src/GqlGateway.Api/Extensions/GatewayApplicationBuilderExtensions.cs` – Zeilen 63–80
- `/root/gql/src/GqlGateway.Api/Extensions/GatewayServiceCollectionExtensions.cs` – Z. 450

**Lösungsvorschlag:**
```csharp
.Validate(opts =>
    environment.IsDevelopment() || !opts.GraphQL.TrustedOrigins.Contains("*"),
    "Sicherheitsverletzung: TrustedOrigins '*' (Wildcard-CORS) ist in Produktion verboten!")
```

---

### HIGH-03 – SSRF-Schutz greift nicht bei ITSM/CDN/Catalog-HttpClients

**Beschreibung:**  
Der ConnectCallback-basierte SSRF-Schutz gilt nur für den `DeclarativeHttp`-Named-Client. `ServiceNowTableApiClient`, `JiraCloudRestClient`, `CloudflareCdnPurgeService`, `FastlyCdnPurgeService`, `PurviewDataCatalogClient`, `CollibraDataCatalogClient` etc. nutzen reguläre HttpClients ohne ConnectCallback-basierten TOCTOU-sicheren Schutz.

**Betroffene Datei:**
- `/root/gql/src/GqlGateway.Api/Extensions/GatewayServiceCollectionExtensions.cs` – Zeilen 350–373

**Lösungsvorschlag:**
```csharp
public class SsrfProtectionHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri != null)
            DeclarativeHttpDataSourceExecutor.ValidateUrl(request.RequestUri);
        return await base.SendAsync(request, ct);
    }
}

services.AddTransient<SsrfProtectionHandler>();
services.AddHttpClient<ServiceNowTableApiClient>()
    .AddHttpMessageHandler<SsrfProtectionHandler>();
// ... für alle externen Clients
```

---

## 🟡 MITTLERE Befunde

### MED-01 – Fehlende HTTP Security Response Headers

**Beschreibung:**  
Es fehlen: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Content-Security-Policy`, `Permissions-Policy`, `Referrer-Policy`. Nur HSTS ist aktiv (Non-Dev).

**Betroffene Datei:**
- `/root/gql/src/GqlGateway.Api/Extensions/GatewayApplicationBuilderExtensions.cs` – Zeilen 40–54

**Lösungsvorschlag:**
```csharp
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    context.Response.Headers.Append("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'");
    await next();
});
```

---

### MED-02 – Casbin `eval(p.sub_rule)` mit unvollständiger Blacklist-Validierung

**Beschreibung:**  
`ValidateSubRuleTokens()` nutzt eine ~20-Token-Blacklist. Blacklists sind grundsätzlich unvollständig. Whitelist-Ansatz wäre sicherer.

**Betroffene Datei:**
- `/root/gql/src/GqlGateway.Application/Governance/CasbinEnforcementService.cs` – Zeilen 104–127

**Lösungsvorschlag:**
```csharp
private static readonly Regex SafeSubRulePattern = new(
    @"^[a-zA-Z0-9_.\s()|&!=<>',\[\]""]+$",
    RegexOptions.Compiled);
if (subRule.Length > 500)
    throw new ArgumentException("sub_rule überschreitet maximale Länge.");
if (!SafeSubRulePattern.IsMatch(subRule))
    throw new ArgumentException("sub_rule enthält nicht erlaubte Zeichen.");
```

---

### MED-03 – `appsettings.Development.json` mit `EnableTestAuthHandler: true`

**Beschreibung:**  
Bei irrtümlichem Staging-Deployment mit `ASPNETCORE_ENVIRONMENT=Development` ist Authentifizierung vollständig deaktiviert. Startup-Validation fängt dies zwar ab – aber nur, wenn der Environment-Name korrekt ist.

**Empfehlung:** CI/CD-Pipeline muss sicherstellen, dass Production/Staging-Deployments nie mit `Development`-Environment-Konfiguration starten. Pipeline-Check: `if appsettings contains EnableTestAuthHandler: true → block`.

---

### MED-04 – Plugin-Reload ohne explizite Autorisierungsprüfung

**Beschreibung:**  
`DynamicPluginAssemblyLoadContext` und `PluginManager` führen keine Berechtigungsprüfung des aufrufenden Kontexts durch. Falls ein API-Endpunkt einen Reload triggert, muss dieser mit `RequireRole("Gateway.Admin")` oder äquivalent geschützt sein.

**Betroffene Datei:**
- `/root/gql/src/GqlGateway.Application/Extensibility/DynamicPluginAssemblyLoadContext.cs`

---

## 🟢 NIEDRIG / Informativ

### LOW-01 – ✅ Timing-sichere Vergleiche korrekt implementiert
`CryptographicOperations.FixedTimeEquals()` korrekt bei: PBKDF2-Passwortvergleichen (inkl. Dummy-Computation für User-Enumeration-Schutz), ForwardAuth-Shared-Secret, Webhook-HMAC. **Vorbildlich.**

### LOW-02 – ✅ SSRF-Schutz für DeclarativeHttp vorbildlich
Mehrschichtiger Schutz: Schema-Validierung, Metadata-Service-Blocklist, DNS-Auflösung + IP-Check (RFC 1918/Loopback/LinkLocal/IPv6 ULA), Redirect-Verfolgung mit erneuter Validierung.

### LOW-03 – ✅ Startup-Sicherheitsvalidierungen umfangreich
`AddGatewayOptions()` mit `.ValidateOnStart()` – Fail-fast-Prinzip für gefährliche Konfigurationsflags.

### LOW-04 – ✅ Keine unsicheren Deserializer
Kein `BinaryFormatter`, kein `Newtonsoft.Json TypeNameHandling.All/Auto`. Ausschließlich `System.Text.Json` in Standardkonfiguration.

### LOW-05 – Informativ: `.gitignore` schützt keine Secrets explizit
`.gitignore` enthält keine Einträge für `*.pfx`, `*.pem`, `*.key`, `.env`. Empfehlung: Explizit aufnehmen.

---

## Security-Audit-Checkliste (ausgefüllt)

| # | Prüfpunkt | Status | Hinweis |
|---|---|---|---|
| 1 | Datenbankabfragen ausnahmslos parameterisiert? | ⚠️ **Teilweise** | ADO.NET-Parameterisierung korrekt; **RLS-Filter-Interpolation unsicher** → CRIT-02 |
| 2 | Vertrauliche Vergleiche mit `FixedTimeEquals`? | ✅ **Ja** | Passwort, HMAC, ForwardAuth-Secret – alle korrekt |
| 3 | `Process.Start` oder dynamische Code-Ausführung? | ⚠️ **Risiko** | Kein `Process.Start`; **unsigniertes Plugin-Loading** → CRIT-01 |
| 4 | Endpunkte standardmäßig authentifiziert? | ✅ **Ja** | GraphQL/Management mit `.RequireAuthorization()`. Health/Webhooks explizit `AllowAnonymous` |
| 5 | Secrets aus Quellcode ferngehalten? | ✅ **Ja** | Keine Produktions-Secrets im Code. Dev-Konfiguration nur Platzhalter |
| 6 | Externe URLs gegen SSRF abgesichert? | ⚠️ **Teilweise** | `DeclarativeHttp` ✅ vorbildlich. **ITSM/CDN/Catalog-Clients ohne ConnectCallback-Guard** → HIGH-03 |

---

## Gesamtbewertung

```
Security-Score: 7.2 / 10

Stärken:
  ✅ Timing-sicherer Kryptografie-Einsatz (FixedTimeEquals überall)
  ✅ Umfangreiche Zero-Trust RLS-Architektur
  ✅ Gute Startup-Validierungen für gefährliche Konfigurationsflags
  ✅ Moderner Tech-Stack ohne gefährliche Deserializer
  ✅ CSRF-Schutz via GraphQL-Preflight-Header
  ✅ Vorbildlicher SSRF-Schutz für DeclarativeHttp-Client

Kritische Schwächen:
  🔴 Plugin-System ohne Signaturprüfung → RCE-Risiko
  🔴 RLS-Filter-Interpolation ohne SQL-Escaping → SQL-Injection
  🟠 TLS-Bypass-Option ohne Startup-Validation-Guard
  🟠 CSRF-Schutz durch CORS-Wildcard deaktivierbar
  🟠 SSRF-Schutz unvollständig (ITSM/CDN/Catalog-Clients)
  🟡 HTTP Security Headers fehlen

Empfohlene Priorisierung:
  Sprint 1 (Sofort): CRIT-01 Plugin-Signing, CRIT-02 RLS-SQL-Parameterisierung
  Sprint 2 (Kurzfristig): HIGH-01 TLS-Guard, HIGH-02 CORS-Wildcard, HIGH-03 SSRF-Handler
  Sprint 3 (Mittelfristig): MED-01 Security Headers, MED-02 Casbin Whitelist
  Ongoing: MED-03 CI/CD-Checks, LOW-05 .gitignore-Erweiterung
```
