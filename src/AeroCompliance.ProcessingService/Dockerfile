FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN dotnet restore src/AeroCompliance.ProcessingService/AeroCompliance.ProcessingService.csproj -a $TARGETARCH
RUN dotnet publish src/AeroCompliance.ProcessingService/AeroCompliance.ProcessingService.csproj \
    --configuration Release --no-restore --output /app/publish /p:UseAppHost=false -a $TARGETARCH

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AeroCompliance.ProcessingService.dll"]
