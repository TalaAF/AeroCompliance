FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN dotnet restore src/AeroCompliance.ApiService/AeroCompliance.ApiService.csproj -a $TARGETARCH
RUN dotnet publish src/AeroCompliance.ApiService/AeroCompliance.ApiService.csproj \
    --configuration Release --no-restore --output /app/publish /p:UseAppHost=false -a $TARGETARCH

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
COPY --from=build /app/publish .
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=12 \
    CMD curl --fail --silent http://127.0.0.1:8080/health >/dev/null || exit 1
ENTRYPOINT ["dotnet", "AeroCompliance.ApiService.dll"]
