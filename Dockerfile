# ==============================================================================
# MirageDiary Production Multi-Stage Dockerfile (.NET 10 LTS / Linux x64)
# ==============================================================================

# ------------------------------------------------------------------------------
# Stage 1: Build & Publish
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 1. Copy solution & project files for optimized layer caching
COPY MirageDiary.slnx ./
COPY MirageDiary.Core/MirageDiary.Core.csproj MirageDiary.Core/
COPY MirageDiary.Server/MirageDiary.Server.csproj MirageDiary.Server/

# 2. Restore NuGet packages
RUN dotnet restore MirageDiary.slnx

# 3. Copy source trees
COPY MirageDiary.Core/ MirageDiary.Core/
COPY MirageDiary.Server/ MirageDiary.Server/

# 4. Compile and publish Release binaries
WORKDIR /src/MirageDiary.Server
RUN dotnet publish MirageDiary.Server.csproj \
    -c Release \
    -r linux-x64 \
    --no-restore \
    -o /app/publish

# ------------------------------------------------------------------------------
# Stage 2: Hardened Production Runtime
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# 1. Install system utilities and Russian Trusted Root Certificates (Минцифры / ГУЦ)
# This guarantees out-of-the-box TLS trust when communicating with Gosuslugi and FGIS MySchool.
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    sqlite3 \
    tzdata \
    && mkdir -p /usr/local/share/ca-certificates \
    && (curl -fsSL -k "https://gu-st.ru/content/lkgov/cacerts/russian_trusted_root_ca.cer" -o /usr/local/share/ca-certificates/russian_trusted_root_ca.crt || true) \
    && (curl -fsSL -k "https://gu-st.ru/content/lkgov/cacerts/russian_trusted_sub_ca.cer" -o /usr/local/share/ca-certificates/russian_trusted_sub_ca.crt || true) \
    && update-ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# 2. Copy compiled application artifacts
COPY --from=build /app/publish /app

# 3. Create persistent directories and configure non-root user permissions
RUN mkdir -p /app/data /app/data/backups \
    && chown -R app:app /app

# 4. Production Environment Defaults
ENV ASPNETCORE_URLS="http://+:5000" \
    ASPNETCORE_ENVIRONMENT="Production" \
    DOTNET_RUNNING_IN_CONTAINER="true" \
    ConnectionStrings__DefaultConnection="Data Source=/app/data/miragediary.db" \
    MIRAGE_SQLITE_CONNECTION="Data Source=/app/data/miragediary.db" \
    TZ="Asia/Yekaterinburg"

# 5. Service Port
EXPOSE 5000

# 6. Container Healthcheck
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fs http://localhost:5000/privacy || exit 1

# 7. Non-root execution for security hardening
USER app

ENTRYPOINT ["dotnet", "MirageDiary.Server.dll"]
