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

# 3) Chạy
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
COPY --from=web /web/dist ./wwwroot
ENV ASPNETCORE_URLS=http://0.0.0.0:5080
EXPOSE 5080
ENTRYPOINT ["dotnet", "LisServer.dll"]
