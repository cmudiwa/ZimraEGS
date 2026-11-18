# Fiscal Managerio: one ASP.NET Core 8 program with its own SQLite database.
# Everything that must survive (database, key ring, logs, backups) lives in /data: keep it on a volume.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ZimraEGS/ZimraEGS.csproj ZimraEGS/
RUN dotnet restore ZimraEGS/ZimraEGS.csproj
COPY ZimraEGS/ ZimraEGS/
RUN dotnet publish ZimraEGS/ZimraEGS.csproj -c Release -o /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
# The image's non-root "app" user owns the data folder, so a new volume starts with the right owner.
RUN mkdir -p /data && chown app:app /data
ENV DataDirectory=/data \
    HOME=/data \
    ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production
USER app
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "ZimraEGS.dll"]
