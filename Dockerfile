FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
WORKDIR /src/HDYMonitor
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
RUN mkdir -p /data
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "HDYMonitor.dll"]
