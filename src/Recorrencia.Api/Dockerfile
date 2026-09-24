FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Recorrencia.Db/Recorrencia.Db.csproj src/Recorrencia.Db/
COPY src/Recorrencia.Domain/Recorrencia.Domain.csproj src/Recorrencia.Domain/
COPY src/Recorrencia.Api/Recorrencia.Api.csproj src/Recorrencia.Api/
RUN dotnet restore src/Recorrencia.Api/Recorrencia.Api.csproj
COPY src/ src/
RUN dotnet publish src/Recorrencia.Api/Recorrencia.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Recorrencia.Api.dll"]
