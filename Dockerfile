FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish ./OverWatchELD.VtcBot.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out ./
RUN chmod +x /app/OverWatchELD.VtcBot

CMD ["/app/OverWatchELD.VtcBot"]
