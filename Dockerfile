FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .

# Force Linux publish, framework-dependent
RUN dotnet publish ./OverWatchELD.VtcBot.csproj -c Release -r linux-x64 --self-contained false -o /out

RUN echo "=== /out contents ===" && ls -la /out

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out ./

RUN echo "=== /app contents ===" && ls -la /app

# If a DLL exists, run it; otherwise run the executable named like the project
CMD ["sh", "-c", "if ls /app/*.dll 1>/dev/null 2>&1; then DLL=$(ls /app/*.dll | head -n 1); echo Running $DLL; dotnet $DLL; else echo No DLL found. Running apphost if present...; ls -la /app; chmod +x /app/OverWatchELD.VtcBot 2>/dev/null || true; /app/OverWatchELD.VtcBot; fi"]
