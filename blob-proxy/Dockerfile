# blob-proxy: a pass-through proxy for Azure Blob Storage (no credentials, no cache). Build context: blob-proxy/
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY BlobProxy/BlobProxy.csproj BlobProxy/
RUN dotnet restore BlobProxy/BlobProxy.csproj
COPY BlobProxy/ BlobProxy/
RUN dotnet publish BlobProxy/BlobProxy.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /out .
ENV PORT=8080 DOTNET_gcServer=1
EXPOSE 8080
LABEL org.opencontainers.image.source="https://github.com/lancec-codebox/appbox" \
      org.opencontainers.image.description="blob-proxy: pass-through proxy for Azure Blob Storage that counts traffic; holds no credentials" \
      org.opencontainers.image.licenses="MIT"
ENTRYPOINT ["dotnet", "blob-proxy.dll"]
