FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
# версии пакетов заданы централизованно в Directory.Packages.props —
# без этого файла restore не видит версий (NU1604) и сборка падает на отсутствующих типах
COPY Directory.Packages.props TaskApi.csproj ./
RUN dotnet restore TaskApi.csproj
COPY . .
RUN dotnet publish TaskApi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
# образы aspnet по умолчанию слушают порт 8080
ENTRYPOINT ["dotnet", "TaskApi.dll"]
