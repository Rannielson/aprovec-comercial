FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Recorrencia.Db/Recorrencia.Db.csproj src/Recorrencia.Db/
RUN dotnet restore src/Recorrencia.Db/Recorrencia.Db.csproj
COPY src/Recorrencia.Db/ src/Recorrencia.Db/
RUN dotnet publish src/Recorrencia.Db/Recorrencia.Db.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Recorrencia.Db.dll"]
