FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .

# Build framework-dependent for Linux container
RUN dotnet publish ./OverWatchELD.VtcBot.csproj -c Release -o /out --self-contained false

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out ./

CMD ["dotnet", "/app/OverWatchELD.VtcBot.dll"]
