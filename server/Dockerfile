# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["server/server.csproj", "server/"]
RUN dotnet restore "./server/server.csproj"
COPY . .
WORKDIR "/src/server"
RUN dotnet build "./server.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./server.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Builds an EF Core migrations bundle: one executable that applies pending migrations.
# dotnet-ef version is pinned in .config/dotnet-tools.json (must match EF Core 8.0.x).
FROM build AS migrations-build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
RUN dotnet tool restore \
 && dotnet tool run dotnet-ef migrations bundle \
      --project server/server.csproj \
      --configuration $BUILD_CONFIGURATION \
      --output /app/efbundle

# One-shot container run by docker-compose.prod.yml before the API starts.
# Reads the same ConnectionStrings__DefaultConnection variable as the API.
FROM base AS migrator
WORKDIR /app
COPY --from=migrations-build /app/efbundle .
ENTRYPOINT ["/bin/sh", "-c", "exec ./efbundle --connection \"$ConnectionStrings__DefaultConnection\""]

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "server.dll"]
