FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .

# Print what files are in the repo (helps confirm csproj name/path)
RUN ls -la

# Publish using the csproj name that exists in your repo
RUN dotnet publish ./OverWatchELD.VtcBot.csproj -c Release -o /out

# Print what got published
RUN ls -la /out

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

COPY --from=build /out ./

# Print what files exist in /app at runtime image build time
RUN ls -la /app

CMD ["dotnet", "/app/OverWatchELD.VtcBot.dll"]
