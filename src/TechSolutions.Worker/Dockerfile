# Build context: repository root. Example: az acr build -r <acr> -t <image>:<tag> -f src/TechSolutions.Worker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/TechSolutions.Worker/TechSolutions.Worker.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "TechSolutions.Worker.dll"]
