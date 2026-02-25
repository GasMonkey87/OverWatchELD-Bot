FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .

# Publish framework-dependent
RUN dotnet publish ./OverWatchELD.VtcBot.csproj -c Release -o /out --self-contained false
RUN echo "=== /out contents ===" && ls -la /out

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out ./
RUN echo "=== /app contents ===" && ls -la /app

# Run whatever dll exists (avoids name mismatch forever)
CMD ["sh", "-c", "DLL=$(ls /app/*.dll | head -n 1); echo Running $DLL; dotnet $DLL"]
