# -------------------------
# Build
# -------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore ./OverWatchELD.VtcBot.csproj
RUN dotnet publish ./OverWatchELD.VtcBot.csproj -c Release -r linux-x64 --self-contained false -o /out

# -------------------------
# Run (IMPORTANT: ASP.NET runtime, not plain runtime)
# -------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /out ./

# Railway provides PORT. Bind to it on 0.0.0.0.
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}

# Railway will still route even without EXPOSE, but it helps.
EXPOSE 8080

ENTRYPOINT ["./OverWatchELD.VtcBot"]
