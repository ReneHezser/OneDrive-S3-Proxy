FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/OneDriveS3Proxy/OneDriveS3Proxy.csproj src/OneDriveS3Proxy/
RUN dotnet restore src/OneDriveS3Proxy/OneDriveS3Proxy.csproj
COPY src/OneDriveS3Proxy/ src/OneDriveS3Proxy/
RUN dotnet publish src/OneDriveS3Proxy/OneDriveS3Proxy.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
ENTRYPOINT ["dotnet", "OneDriveS3Proxy.dll"]
