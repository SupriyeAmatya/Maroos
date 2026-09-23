# Maroos web app. Build from the repository root:
#   docker build -t maroos .
# Runs on port 8080 as a non-root user. Configure with environment variables (see DEPLOY.md).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so package downloads are cached until a project file changes.
COPY Maroos.slnx ./
COPY Maroos.Core/Maroos.Core.csproj Maroos.Core/
COPY Maroos.Web/Maroos.Web.csproj Maroos.Web/
RUN dotnet restore Maroos.Web/Maroos.Web.csproj

COPY Maroos.Core/ Maroos.Core/
COPY Maroos.Web/ Maroos.Web/
RUN dotnet publish Maroos.Web/Maroos.Web.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

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
