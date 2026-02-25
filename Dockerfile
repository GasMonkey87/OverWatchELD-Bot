FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .

# show repo contents (confirms csproj name + casing)
RUN echo "=== /src contents ===" && ls -la

# publish
RUN dotnet publish ./OverWatchELD.VtcBot.csproj -c Release -o /out

# show publish output (this will reveal the real DLL name)
RUN echo "=== /out contents ===" && ls -la /out

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out ./

# show runtime folder contents
RUN echo "=== /app contents ===" && ls -la /app

# TEMP: keep your current CMD for now
CMD ["dotnet", "/app/OverWatchELD.VtcBot.dll"]
