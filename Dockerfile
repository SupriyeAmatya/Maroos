# =====================================================================================================
# FILE: Dockerfile
# PURPOSE: Builds the Maroos web app into a container image (~430 MB).
# It's a "multi-stage" build:
#   stage 1 "build"   — the big .NET SDK image compiles and publishes the app;
#   stage 2 "runtime" — the small ASP.NET runtime image receives only the published output.
# So the final image contains no SDK, no source code and no tests.
# =====================================================================================================
# Maroos web app. Build from the repository root:
#   docker build -t maroos .
# Runs on port 8080 as a non-root user. Configure with environment variables (see DEPLOY.md).

# ---- Stage 1: build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Set by BuildKit (amd64 or arm64). Publishing for it keeps only that platform's native libraries
# (SQLite, SkiaSharp) instead of every platform's, which would add ~500 MB.
ARG TARGETARCH
WORKDIR /src

# Restore first so package downloads are cached until a project file changes.
# (Docker caches each step; copying only the project files means a code change doesn't re-download packages.)
COPY Maroos.slnx ./
COPY Maroos.Core/Maroos.Core.csproj Maroos.Core/
COPY Maroos.Web/Maroos.Web.csproj Maroos.Web/
RUN dotnet restore Maroos.Web/Maroos.Web.csproj -a $TARGETARCH

# Now the source code (the tests project is excluded by .dockerignore).
COPY Maroos.Core/ Maroos.Core/
COPY Maroos.Web/ Maroos.Web/
# No --no-restore here: the Blazor framework script (_framework/blazor.web.js) comes from a package the SDK only
# adds during a publish-time restore. Without it, no page can go interactive.
# Framework-dependent (--self-contained false): the runtime image already has .NET. UseAppHost=false: no native launcher.
RUN dotnet publish Maroos.Web/Maroos.Web.csproj -c Release -a $TARGETARCH --self-contained false -o /app/publish /p:UseAppHost=false
# Fail the build if blazor.web.js is missing (it once went missing, and then no page could go interactive).
RUN test -f /app/publish/wwwroot/_framework/blazor.web.js

# ---- Stage 2: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Production defaults; the host supplies the connection string, bucket and secrets.
# (Database__Provider -> PostgreSQL; Uploads__Storage -> S3 bucket; DOTNET_RUNNING_IN_CONTAINER skips the
# app's own HTTPS redirect, because the hosting platform's proxy handles HTTPS.)
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Database__Provider=Postgres \
    Uploads__Storage=S3 \
    DOTNET_RUNNING_IN_CONTAINER=true

# Take only the published app from the build stage.
COPY --from=build /app/publish .

# The aspnet image ships a non-root "app" user.
# (Running as non-root limits the damage if the app were ever compromised.)
USER $APP_UID
EXPOSE 8080
# Start the app.
ENTRYPOINT ["dotnet", "Maroos.Web.dll"]
