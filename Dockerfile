# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY src/AgentHub.slnx ./
COPY src/AgentHub.Server/ ./AgentHub.Server/
COPY src/AgentHub.Cli/ ./AgentHub.Cli/

RUN dotnet publish AgentHub.Server/AgentHub.Server.csproj \
    -c Release \
    -o /app/publish \
    --no-self-contained

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "AgentHub.Server.dll"]
