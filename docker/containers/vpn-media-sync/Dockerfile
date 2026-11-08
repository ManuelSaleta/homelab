# ==============================================================================
# Multi-Stage Dockerfile for vpn-media-sync (.NET 10 Single-File + Alpine)
# Stage 1: Compiles the .NET 10 orchestrator into a self-contained single-file musl binary
# Stage 2: Minimal Alpine runtime with rsync, openssh-client, ffprobe, & iproute2
# ==============================================================================

# Stage 1: .NET 10 Builder
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS builder
WORKDIR /src

COPY src/VpnMediaSync/VpnMediaSync.csproj ./src/VpnMediaSync/
RUN dotnet restore ./src/VpnMediaSync/VpnMediaSync.csproj -r linux-musl-x64

COPY src/VpnMediaSync/ ./src/VpnMediaSync/
RUN dotnet publish ./src/VpnMediaSync/VpnMediaSync.csproj -c Release -r linux-musl-x64 -o /out

# Stage 2: Minimal Alpine Runtime (UID 1000 matching homelab & micro-nas)
FROM alpine:3.21
RUN apk add --no-cache \
      rsync \
      openssh-client \
      ffmpeg \
      iproute2 \
      tzdata \
      libstdc++ \
    && adduser -D -u 1000 appuser \
    && mkdir -p /app /state /downloads /home/appuser/.ssh \
    && chown -R appuser:appuser /app /state /downloads /home/appuser/.ssh

WORKDIR /app
COPY --from=builder /out/vpn-media-sync /usr/local/bin/vpn-media-sync
COPY --from=builder /out/appsettings.json /app/appsettings.json

USER appuser
ENTRYPOINT ["/usr/local/bin/vpn-media-sync"]
