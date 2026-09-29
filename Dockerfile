# ==============================================================================
# GqlGateway - Getting Started Container
# Multi-protocol Enterprise GraphQL Gateway with Embedded Microsoft Garnet Cache
# ==============================================================================

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Standard-Konfiguration für schlüsselfertigen Sofortstart (8080 HTTP / 8081 HTTPS)
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_HTTPS_PORTS=8081 \
    ASPNETCORE_ENVIRONMENT=Development \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    Gateway__Caching__Garnet__EnableEmbeddedServer=true \
    Gateway__Caching__Garnet__Host=127.0.0.1 \
    Gateway__Caching__Garnet__Port=3278

# Offizielle .NET Container Ports
EXPOSE 8080 8081

# Kopiere die vom CI/CD-Runner publizierten Artefakte
COPY dist/publish/ ./

# Non-Root User für Container-Härtung
USER $APP_UID

ENTRYPOINT ["dotnet", "GqlGateway.Api.dll"]
