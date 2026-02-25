# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .

# IMPORTANT: use the exact csproj filename shown in your GitHub root
RUN dotnet publish ./OverWatchELD.VtcBot.csproj -c Release -o /out

# Runtime
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

COPY --from=build /out ./

CMD ["dotnet", "/app/OverWatchELD.VtcBot.dll"]
