# Server LIS Connect: web (React) + API + agent API trong một image.
# docker build -t lis-server .

# 1) Build giao diện web
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# 2) Publish server .NET
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Lis.Core/ src/Lis.Core/
COPY src/Lis.Data/ src/Lis.Data/
COPY src/Lis.Server/ src/Lis.Server/
RUN dotnet publish src/Lis.Server/Lis.Server.csproj -c Release -o /app --no-self-contained

# 3) Publish tệp cài agent cho Windows (LisAgent.exe một tệp tự chứa + version.txt).
# Tham số publish giống scripts/publish-agent.sh — đổi một nơi thì đổi cả nơi kia.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS agent
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Lis.Core/ src/Lis.Core/
COPY src/Lis.Data/ src/Lis.Data/
COPY src/Lis.Connections/ src/Lis.Connections/
COPY src/Lis.Agent/ src/Lis.Agent/
RUN dotnet publish src/Lis.Agent/Lis.Agent.csproj -c Release -r win-x64 --self-contained true \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
      -p:DebugType=None -p:DebugSymbols=false -o /agent \
 && dotnet msbuild src/Lis.Agent/Lis.Agent.csproj -getProperty:Version | tr -d '[:space:]' > /agent/version.txt \
 && test -s /agent/version.txt \
 && find /agent -type f ! -name LisAgent.exe ! -name version.txt -delete

# 4) Chạy
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
COPY --from=agent /agent ./agent
COPY --from=web /web/dist ./wwwroot
ENV ASPNETCORE_URLS=http://0.0.0.0:5080
ENV AGENT_DIST_DIR=/app/agent
EXPOSE 5080
ENTRYPOINT ["dotnet", "LisServer.dll"]
