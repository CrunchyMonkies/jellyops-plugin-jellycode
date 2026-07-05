# Runs the locally-built Jellyfin v12 server with the Distributed Transcoding plugin and an in-container
# worker. Headless (no web client) — drive it via the HTTP API. The build context is assembled by
# docker/run.sh (it stages the published server, worker, and plugin alongside this file).
FROM mcr.microsoft.com/dotnet/aspnet:10.0

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg fonts-dejavu-core curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Published Jellyfin server (built from ../jellyfin-src).
COPY server/ /jellyfin/

# The transcoding worker.
COPY worker/ /worker/

# The plugin (only our assemblies + Grpc/Protobuf; host assemblies were stripped at build).
# JSON-array form is required because the destination path contains a space.
COPY ["plugin/", "/config/plugins/Distributed Transcoding_0.0.1.0/"]

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENV JELLYFIN_DATA_DIR=/config
# 8096 = Jellyfin HTTP API, 9090 = plugin gRPC mesh listener.
EXPOSE 8096 9090

ENTRYPOINT ["/entrypoint.sh"]
