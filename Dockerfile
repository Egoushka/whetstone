# whetstone serve as a container. The CLI commands run from the same image: docker exec <container> dotnet /app/Whetstone.Server.dll <command>.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Whetstone/Whetstone.csproj src/Whetstone/
COPY src/Whetstone.Server/Whetstone.Server.csproj src/Whetstone.Server/
RUN dotnet restore src/Whetstone.Server
COPY schemas/ schemas/
COPY src/ src/
RUN dotnet publish src/Whetstone.Server -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
# /data holds <WHETSTONE_USER>/whetstone.db. $APP_UID is the image's non-root user; a volume mounted here must be writable by it.
RUN mkdir /data && chown "$APP_UID" /data
USER $APP_UID
ENV WHETSTONE_DATA_DIR=/data WHETSTONE_USER=owner
# Set WHETSTONE_API_KEY and WHETSTONE_ALLOWED_HOSTS (the host names clients put in the URL) when running it.
EXPOSE 7340
# The runtime image has no curl; bash opens the TCP connection itself. Host 127.0.0.1 is always allowed.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/7340 && printf 'GET /health HTTP/1.0\\r\\nHost: 127.0.0.1\\r\\n\\r\\n' >&3 && head -n1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "/app/Whetstone.Server.dll"]
CMD ["serve", "--listen", "0.0.0.0", "--port", "7340"]
