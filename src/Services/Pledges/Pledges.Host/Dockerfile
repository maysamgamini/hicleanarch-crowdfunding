# The Pledges service's image (lesson s5-l10: the old monolith's host, which runs Pledges alone). Build from
# the repository root: docker build -f src/Services/Pledges/Pledges.Host/Dockerfile -t crowdfunding-pledges .
# The same image serves the API and, with the argument `migrate`, runs as the one-off migration task.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig NuGet.config BuildingBlocks.slnx Contracts.slnx ./
COPY src/ src/
COPY events/ events/
# The modules use the BuildingBlocks as packages: pack them into the local feed first.
RUN dotnet pack BuildingBlocks.slnx -c Release -o artifacts/packages
# And the modules' Contracts, which they build against as packages too.
RUN dotnet pack Contracts.slnx -c Release -o artifacts/packages
RUN dotnet publish src/Services/Pledges/Pledges.Host/Pledges.Host.csproj -c Release -o /app
# The CA bundle the database's certificates chain to: the connection strings ask Npgsql to check the
# server against it (SSL Mode=VerifyFull). Fetched from AWS over HTTPS; readable by the app's user.
ADD https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem /rds/global-bundle.pem
RUN chmod 644 /rds/global-bundle.pem

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --from=build /rds/global-bundle.pem /usr/local/share/rds/global-bundle.pem
USER $APP_UID
# 8080 for its endpoints. It serves nothing for other services, so no 8081.
EXPOSE 8080
ENTRYPOINT ["dotnet", "CrowdFunding.Pledges.Host.dll"]
