# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish ./OverWatchELD.VtcBot.csproj -c Release -r linux-x64 --self-contained false -o /out

# Run (NO ASP.NET required)
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out .
CMD ["./OverWatchELD.VtcBot"]
