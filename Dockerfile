FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .

# Build Linux apphost (not self-contained)
RUN dotnet publish ./OverWatchELD.VtcBot.csproj -c Release -r linux-x64 --self-contained false -o /out

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out ./

# Run the published executable (correct entrypoint)
RUN chmod +x /app/OverWatchELD.VtcBot || true
CMD ["/app/OverWatchELD.VtcBot"]
