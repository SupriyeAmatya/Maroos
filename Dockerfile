# Maroos web app. Build from the repository root:
#   docker build -t maroos .
# Runs on port 8080 as a non-root user. Configure with environment variables (see DEPLOY.md).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Set by BuildKit (amd64 or arm64). Publishing for it keeps only that platform's native libraries
# (SQLite, SkiaSharp) instead of every platform's, which would add ~500 MB.
ARG TARGETARCH
WORKDIR /src

# Restore first so package downloads are cached until a project file changes.
COPY Maroos.slnx ./
COPY Maroos.Core/Maroos.Core.csproj Maroos.Core/
COPY Maroos.Web/Maroos.Web.csproj Maroos.Web/
RUN dotnet restore Maroos.Web/Maroos.Web.csproj -a $TARGETARCH

COPY Maroos.Core/ Maroos.Core/
COPY Maroos.Web/ Maroos.Web/
# No --no-restore here: the Blazor framework script (_framework/blazor.web.js) comes from a package the SDK only
# adds during a publish-time restore. Without it, no page can go interactive.
RUN dotnet publish Maroos.Web/Maroos.Web.csproj -c Release -a $TARGETARCH --self-contained false -o /app/publish /p:UseAppHost=false
RUN test -f /app/publish/wwwroot/_framework/blazor.web.js

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Production defaults; the host supplies the connection string, bucket and secrets.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Database__Provider=Postgres \
    Uploads__Storage=S3 \
    DOTNET_RUNNING_IN_CONTAINER=true

COPY --from=build /app/publish .

# The aspnet image ships a non-root "app" user.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Maroos.Web.dll"]
