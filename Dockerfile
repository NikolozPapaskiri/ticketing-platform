# ---------- build stage: full SDK, never shipped ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore with ONLY the project files first: this layer is cached until a .csproj changes,
# so day-to-day code edits skip the whole package download.
COPY TicketingPlatform.sln global.json ./
COPY src/TicketingPlatform.Domain/TicketingPlatform.Domain.csproj src/TicketingPlatform.Domain/
COPY src/TicketingPlatform.Application/TicketingPlatform.Application.csproj src/TicketingPlatform.Application/
COPY src/TicketingPlatform.Infrastructure/TicketingPlatform.Infrastructure.csproj src/TicketingPlatform.Infrastructure/
COPY src/TicketingPlatform.Api/TicketingPlatform.Api.csproj src/TicketingPlatform.Api/
RUN dotnet restore src/TicketingPlatform.Api/TicketingPlatform.Api.csproj

COPY src/ src/
RUN dotnet publish src/TicketingPlatform.Api/TicketingPlatform.Api.csproj \
    -c Release -o /app /p:UseAppHost=false --no-restore

# ---------- runtime stage: aspnet runtime only, no SDK, no compilers ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Writable app data root for the non-root runtime user. Named Docker volumes inherit this
# ownership on first use; Kubernetes sets fsGroup for mounted volumes.
RUN mkdir -p /var/ticketing/files && chown -R $APP_UID:$APP_UID /var/ticketing

# Non-root: the base image ships an unprivileged 'app' user; a container escape lands
# without root. Port 8080 because binding <1024 needs privileges we just gave up.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "TicketingPlatform.Api.dll"]
