FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

# Configure apt to use HTTPS sources
RUN echo "deb https://archive.ubuntu.com/ubuntu noble main restricted universe multiverse" > /etc/apt/sources.list && \
    echo "deb https://archive.ubuntu.com/ubuntu noble-updates main restricted universe multiverse" >> /etc/apt/sources.list && \
    echo "deb https://archive.ubuntu.com/ubuntu noble-security main restricted universe multiverse" >> /etc/apt/sources.list

# Install Node.js 20 from NodeSource, git, and pi
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    git \
    tmux \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && npm install -g @mariozechner/pi-coding-agent \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user and grant access to /app
RUN useradd -m picrust \
    && chown -R picrust:picrust /app

# Copy agent config files to a staging directory (bind mount will shadow /home/picrust)
COPY --chown=picrust:picrust AgentFiles/ /opt/agent-files/

ENV HOME=/home/picrust
ENV PI_CODING_AGENT_DIR=/app/AgentFiles

EXPOSE 8080
EXPOSE 8081
EXPOSE 18789

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIG=Release
WORKDIR /src

# Copy project file and restore
COPY *.csproj ./
RUN dotnet restore

# Copy source and build
COPY . ./
RUN dotnet build -c ${BUILD_CONFIG} -o /app/build

FROM build AS publish
ARG BUILD_CONFIG=Release
RUN dotnet publish -c ${BUILD_CONFIG} -o /app/publish --no-restore -p:PublishSingleFile=false

FROM base AS final
WORKDIR /app

# Copy published .NET application (includes appsettings.json)
COPY --from=publish /app/publish .

# Environment variables
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV PI_CODING_AGENT_DIR=/home/picrust

# Seed agent files into the bind-mounted volume on startup
COPY --chown=picrust:picrust entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

ENTRYPOINT ["/app/entrypoint.sh"]
