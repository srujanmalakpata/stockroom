# syntax=docker/dockerfile:1

# Base images are build arguments so they can be pinned by digest or pulled from a mirror.
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:8.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:8.0

# ---- build: restore (cached by project files) then publish ----
FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Inventory.Domain/Inventory.Domain.csproj src/Inventory.Domain/packages.lock.json src/Inventory.Domain/
COPY src/Inventory.Application/Inventory.Application.csproj src/Inventory.Application/packages.lock.json src/Inventory.Application/
COPY src/Inventory.Infrastructure/Inventory.Infrastructure.csproj src/Inventory.Infrastructure/packages.lock.json src/Inventory.Infrastructure/
COPY src/Inventory.Api/Inventory.Api.csproj src/Inventory.Api/packages.lock.json src/Inventory.Api/
RUN dotnet restore src/Inventory.Api/Inventory.Api.csproj --locked-mode
COPY src/ src/
RUN dotnet publish src/Inventory.Api/Inventory.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# ---- runtime: ASP.NET Core runtime only, runs as the image's non-root "app" user ----
FROM ${RUNTIME_IMAGE} AS runtime
WORKDIR /app
# Writable directory for the default SQLite database; production points ConnectionStrings__Inventory at PostgreSQL.
RUN mkdir /data && chown "$APP_UID" /data
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Inventory="Data Source=/data/inventory.db"
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Inventory.Api.dll"]
