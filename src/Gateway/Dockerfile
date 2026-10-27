# The gateway's image: a small proxy in front of the monolith. Built from the repository root:
#   docker build -f src/Gateway/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Gateway/ src/Gateway/
RUN dotnet publish src/Gateway/Gateway.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "CrowdFunding.Gateway.dll"]
