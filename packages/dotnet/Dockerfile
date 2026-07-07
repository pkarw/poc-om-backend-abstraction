# Multi-stage build: one image serves both api and worker (different command).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first for layer caching. Copy the solution + every project file (each
# ported module adds a project, so copy them all rather than listing each).
COPY OpenMercato.sln ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN find . -name '*.cs' -not -name '*.csproj' -delete 2>/dev/null; \
    dotnet restore OpenMercato.sln

COPY . .
RUN dotnet publish src/OpenMercato.Api -c Release -o /out/api
RUN dotnet publish src/OpenMercato.Worker -c Release -o /out/worker

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /out/api ./api
COPY --from=build /out/worker ./worker
EXPOSE 8080

# Default: api host (applies EF Core migrations on startup, then serves).
# The worker service overrides this with: dotnet /app/worker/OpenMercato.Worker.dll
CMD ["dotnet", "/app/api/OpenMercato.Api.dll"]
