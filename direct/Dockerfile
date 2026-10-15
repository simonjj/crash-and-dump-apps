FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

RUN apt-get update \
    && apt-get install -y --no-install-recommends g++ \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY CoreDumpRepro.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /out --no-restore \
    && g++ -O0 -g -shared -fPIC native/crash.cpp -o /out/libnativecrash.so

FROM mcr.microsoft.com/dotnet/aspnet:10.0

USER root
RUN groupadd --gid 1001 crashapp \
    && useradd --uid 1001 --gid 1001 --home-dir /home/crashapp --create-home crashapp \
    && mkdir -p /app /dumps \
    && chown -R 1001:1001 /app /dumps

WORKDIR /app
COPY --from=build --chown=1001:1001 /out/ .

ENV ASPNETCORE_URLS=http://+:8080 \
    LD_LIBRARY_PATH=/app \
    DOTNET_DbgEnableMiniDump=1 \
    DOTNET_DbgMiniDumpType=2 \
    DOTNET_DbgMiniDumpName=/dumps/direct.%h.%e.%p.%t.dmp \
    DOTNET_EnableCrashReport=1 \
    DOTNET_CreateDumpDiagnostics=1

USER 1001
EXPOSE 8080

CMD ["dotnet", "CoreDumpRepro.dll"]
