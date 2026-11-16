# =============================================================================
# Jarvis.Sharp - Multi-Stage Container Build
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project definitions for optimal layer caching
COPY Jarvis.Sharp.slnx .
COPY src/Jarvis.Sharp/Jarvis.Sharp.csproj src/Jarvis.Sharp/
COPY tests/Jarvis.Sharp.Tests/Jarvis.Sharp.Tests.csproj tests/Jarvis.Sharp.Tests/

RUN dotnet restore Jarvis.Sharp.slnx

# Copy source tree and execute tests during build
COPY src/ src/
COPY tests/ tests/

RUN dotnet test tests/Jarvis.Sharp.Tests/Jarvis.Sharp.Tests.csproj -c Release --no-restore
RUN dotnet publish src/Jarvis.Sharp/Jarvis.Sharp.csproj -c Release -o /app/publish --no-restore

# =============================================================================
# Minimal Runtime Stage (Ubuntu Noble Chiseled - Non-Root)
# =============================================================================
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS final
WORKDIR /app

COPY --from=build /app/publish .

# Use non-root user built into chiseled images
USER $APP_UID

ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

ENTRYPOINT ["dotnet", "jarvis-sharp.dll"]
