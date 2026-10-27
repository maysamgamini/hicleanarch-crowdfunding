# The Payments service's image. Build from the repository root:
#   docker build -f src/Services/Payments.Host/Dockerfile -t crowdfunding-payments .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig NuGet.config BuildingBlocks.slnx Contracts.slnx ./
COPY src/ src/
COPY events/ events/
RUN dotnet pack BuildingBlocks.slnx -c Release -o artifacts/packages
# And the modules' Contracts, which they build against as packages too.
RUN dotnet pack Contracts.slnx -c Release -o artifacts/packages
RUN dotnet publish src/Services/Payments.Host/Payments.Host.csproj -c Release -o /app
# The CA bundle the database's certificates chain to, as in the monolith's image: the connection
# string asks Npgsql to check the server against it (SSL Mode=VerifyFull).
ADD https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem /rds/global-bundle.pem
RUN chmod 644 /rds/global-bundle.pem

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --from=build /rds/global-bundle.pem /usr/local/share/rds/global-bundle.pem
USER $APP_UID
# 8080: the public endpoints, reached through the gateway; 8081: the internal one, for the monolith only.
ENV ASPNETCORE_HTTP_PORTS=8080;8081
EXPOSE 8080 8081
ENTRYPOINT ["dotnet", "CrowdFunding.Payments.Host.dll"]
