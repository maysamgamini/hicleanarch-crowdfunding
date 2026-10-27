# The one image of the monolith. The same image serves the API and, with the argument
# `migrate`, runs as the one-off migration task.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig NuGet.config BuildingBlocks.slnx ./
COPY src/ src/
# The modules use the BuildingBlocks as packages: pack them into the local feed first.
RUN dotnet pack BuildingBlocks.slnx -c Release -o artifacts/packages
RUN dotnet publish src/Host/Host.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "CrowdFunding.Host.dll"]
