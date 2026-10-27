# ==============================================================================
# Multi-Stage Dockerfile for vpn-media-sync (.NET 10 Native AOT + Alpine)
# Stage 1: Compiles the .NET 10 orchestrator into a self-contained musl binary
# Stage 2: Minimal Alpine runtime with rsync, openssh-client, ffprobe, & iproute2
# ==============================================================================

# Stage 1: .NET 10 Native AOT Builder (Unconfigured / Ready for src/ scaffolding)
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS builder
RUN apk add --no-cache clang build-base zlib-dev
WORKDIR /src

# Placeholder until .NET 10 project (src/VpnMediaSync) is scaffolded:
# COPY src/VpnMediaSync/VpnMediaSync.csproj ./
# RUN dotnet restore -r linux-musl-x64
# COPY src/VpnMediaSync/ ./
# RUN dotnet publish -c Release -r linux-musl-x64 -o /out

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
# COPY --from=builder /out/VpnMediaSync /usr/local/bin/vpn-media-sync

USER appuser
ENTRYPOINT ["/bin/sh", "-c", "echo 'vpn-media-sync container unconfigured (awaiting .NET 10 binary)'; sleep infinity"]
