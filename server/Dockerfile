FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/ObsidianSync.Server/ObsidianSync.Server.csproj src/ObsidianSync.Server/
RUN dotnet restore src/ObsidianSync.Server/ObsidianSync.Server.csproj

COPY src/ObsidianSync.Server/ src/ObsidianSync.Server/
RUN dotnet publish src/ObsidianSync.Server/ObsidianSync.Server.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV DATA_PATH=/data
EXPOSE 8080
VOLUME ["/data"]

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ObsidianSync.Server.dll"]
