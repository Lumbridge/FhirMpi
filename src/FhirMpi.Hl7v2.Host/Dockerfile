FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/FhirMpi.Hl7v2.Host/FhirMpi.Hl7v2.Host.csproj --locked-mode
RUN dotnet publish src/FhirMpi.Hl7v2.Host/FhirMpi.Hl7v2.Host.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
EXPOSE 2575
ENTRYPOINT ["dotnet", "FhirMpi.Hl7v2.Host.dll"]
