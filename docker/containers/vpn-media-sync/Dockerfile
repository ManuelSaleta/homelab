# ==============================================================================
# Multi-Stage Dockerfile for vpn-media-sync (.NET 10 Single-File + Alpine)
# Stage 1: Installs Sync.Sharp from NuGet (ChaoticDreams) and compiles a
#          self-contained single-file musl binary
# Stage 2: Minimal Alpine runtime with rsync, openssh-client, ffprobe, & iproute2
# ==============================================================================

# Stage 1: .NET 10 Builder
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS builder
ARG SYNC_SHARP_VERSION=1.0.*
WORKDIR /src

RUN dotnet new console -n SyncRunner && \
    dotnet add SyncRunner package Sync.Sharp --version "${SYNC_SHARP_VERSION}" && \
    printf 'return await Sync.Sharp.SyncSharpHost.RunAsync(args);\n' > SyncRunner/Program.cs && \
    dotnet publish SyncRunner/SyncRunner.csproj \
      -c Release \
      -r linux-musl-x64 \
      -p:PublishSingleFile=true \
      -p:SelfContained=true \
      -p:InvariantGlobalization=true \
      -p:AssemblyName=vpn-media-sync \
      -o /out

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
COPY appsettings.example.json /app/appsettings.json

USER appuser
ENTRYPOINT ["/usr/local/bin/vpn-media-sync"]
