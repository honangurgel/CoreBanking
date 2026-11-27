FROM mcr.microsoft.com/dotnet/sdk:10.0 AS source
WORKDIR /src
COPY src/ src/

FROM source AS api-build
RUN dotnet publish src/CoreBanking.Api/CoreBanking.Api.csproj -c Release -o /out /p:UseAppHost=false

FROM source AS worker-build
RUN dotnet publish src/CoreBanking.Worker/CoreBanking.Worker.csproj -c Release -o /out /p:UseAppHost=false

FROM source AS simulator-build
RUN dotnet publish src/CoreBanking.Simulator/CoreBanking.Simulator.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
USER $APP_UID

FROM runtime AS api
COPY --from=api-build /out/ .
ENTRYPOINT ["dotnet", "CoreBanking.Api.dll"]

FROM runtime AS worker
COPY --from=worker-build /out/ .
ENTRYPOINT ["dotnet", "CoreBanking.Worker.dll"]

FROM runtime AS simulator
COPY --from=simulator-build /out/ .
ENTRYPOINT ["dotnet", "CoreBanking.Simulator.dll"]
