FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY backend/LoveCapsule.Api.csproj backend/
RUN dotnet restore backend/LoveCapsule.Api.csproj
COPY backend/ backend/
RUN dotnet publish backend/LoveCapsule.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN apt-get update \
	&& apt-get install -y --no-install-recommends curl \
	&& rm -rf /var/lib/apt/lists/* \
	&& mkdir -p /app/data /app/wwwroot/uploads
COPY --from=build /app/publish .
EXPOSE 5034
ENTRYPOINT ["dotnet", "LoveCapsule.Api.dll"]
