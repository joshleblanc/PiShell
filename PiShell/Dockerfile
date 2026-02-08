FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

# Install Node.js 20 from NodeSource and pi
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && npm install -g @mariozechner/pi-coding-agent \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user and grant access to /app
RUN useradd -m pishell \
    && chown -R pishell:pishell /app

# Copy agent config files into pishell's home (available during debug and production)
COPY --chown=pishell:pishell AgentFiles/ /home/pishell/

ENV HOME=/home/pishell

EXPOSE 8080
EXPOSE 8081

USER pishell

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
ENV PI_CODING_AGENT_DIR=/home/pishell

ENTRYPOINT ["dotnet", "PiShell.dll"]
