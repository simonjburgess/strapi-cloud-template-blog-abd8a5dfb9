FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/OctopusDashboard/OctopusDashboard.csproj src/OctopusDashboard/
RUN dotnet restore src/OctopusDashboard/OctopusDashboard.csproj
COPY src/OctopusDashboard/ src/OctopusDashboard/
RUN dotnet publish src/OctopusDashboard/OctopusDashboard.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# Hosts that set PORT override this (see Program.cs).
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "OctopusDashboard.dll"]
