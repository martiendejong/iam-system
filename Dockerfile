# ── Stage 1: build ────────────────────────────────────────────────────────────
# Needs .NET 10 SDK, Node.js 22, and git (for the Hazina project reference).
# Hazina.Security.ApiKeys is a ProjectReference resolved from HAZINA_ROOT; we
# clone the repo here the same way the CI workflow does (see .github/workflows/ci.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl gnupg git \
    && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

ARG HAZINA_REF=develop
RUN git clone --depth=1 --branch ${HAZINA_REF} https://github.com/martiendejong/Hazina.git /hazina
ENV HAZINA_ROOT=/hazina/

ARG VITE_BASE_PATH=/
ENV VITE_BASE_PATH=${VITE_BASE_PATH}

WORKDIR /repo

# Restore first (layer cache: only invalidated when .csproj / nuget / Directory.Build.props change)
COPY nuget.config ./
COPY Directory.Build.props ./
COPY src/IAM.API/nuget-local/ ./src/IAM.API/nuget-local/
COPY src/IAM.Core/IAM.Core.csproj               ./src/IAM.Core/
COPY src/IAM.Infrastructure/IAM.Infrastructure.csproj ./src/IAM.Infrastructure/
COPY src/IAM.API/IAM.API.csproj                 ./src/IAM.API/
RUN dotnet restore src/IAM.API/IAM.API.csproj

# Copy everything else and publish
# admin-ui/ lives at repo root; PublishAdminUi runs npm install + npm run build there
COPY . .
RUN dotnet publish src/IAM.API/IAM.API.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ── Stage 2: runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app
COPY --from=build /app/publish .

# Caddy runs on a Docker bridge; trust the entire RFC-1918 172.x range so
# X-Forwarded-For / X-Forwarded-Proto reach the rate limiter correctly.
# Override per environment via docker-compose environment section.
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080
ENTRYPOINT ["dotnet", "IAM.API.dll"]
