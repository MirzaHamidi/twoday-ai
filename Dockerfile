FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ConsoleApp1.csproj ./
RUN dotnet restore ConsoleApp1.csproj

COPY Program.cs ./
RUN dotnet publish ConsoleApp1.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DATA_DIR=/data

RUN mkdir -p /data && chown -R app:app /app /data

COPY --from=build --chown=app:app /app/publish ./

USER app
ENTRYPOINT ["dotnet", "ConsoleApp1.dll"]
