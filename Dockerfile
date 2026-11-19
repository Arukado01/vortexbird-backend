FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/VortexBird.API/VortexBird.API.csproj
RUN dotnet publish src/VortexBird.API/VortexBird.API.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false
# Official public RDS trust anchors are included in the immutable application image.
# A failed download fails the build; do not disable certificate validation as a fallback.
ADD https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem /tmp/aws-rds-global-bundle.pem
RUN test -s /tmp/aws-rds-global-bundle.pem && grep -q -- '-----BEGIN CERTIFICATE-----' /tmp/aws-rds-global-bundle.pem && chmod 0644 /tmp/aws-rds-global-bundle.pem && sha256sum /tmp/aws-rds-global-bundle.pem
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /tmp/aws-rds-global-bundle.pem /app/certs/aws-rds-global-bundle.pem
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "VortexBird.API.dll"]
