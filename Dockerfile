# The one image of the monolith. The same image serves the API and, with the argument
# `migrate`, runs as the one-off migration task.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig NuGet.config BuildingBlocks.slnx ./
COPY src/ src/
# The modules use the BuildingBlocks as packages: pack them into the local feed first.
RUN dotnet pack BuildingBlocks.slnx -c Release -o artifacts/packages
RUN dotnet publish src/Host/Host.csproj -c Release -o /app
# The CA bundle the database's certificates chain to: the connection strings ask Npgsql to check the
# server against it (SSL Mode=VerifyFull). Fetched from AWS over HTTPS; readable by the app's user.
ADD https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem /rds/global-bundle.pem
RUN chmod 644 /rds/global-bundle.pem

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --from=build /rds/global-bundle.pem /usr/local/share/rds/global-bundle.pem
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "CrowdFunding.Host.dll"]
