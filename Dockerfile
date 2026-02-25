FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .

# Force .dll (no native apphost)
RUN dotnet publish ./OverWatchELD.VtcBot.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out ./

CMD ["dotnet", "/app/OverWatchELD.VtcBot.dll"]
