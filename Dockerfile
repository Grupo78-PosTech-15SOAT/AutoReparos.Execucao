FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AutoReparos.Execucao.Api/AutoReparos.Execucao.Api.csproj AutoReparos.Execucao.Api/
COPY AutoReparos.Execucao.Application/AutoReparos.Execucao.Application.csproj AutoReparos.Execucao.Application/
COPY AutoReparos.Execucao.Domain/AutoReparos.Execucao.Domain.csproj AutoReparos.Execucao.Domain/
COPY AutoReparos.Execucao.Infrastructure/AutoReparos.Execucao.Infrastructure.csproj AutoReparos.Execucao.Infrastructure/
RUN dotnet restore AutoReparos.Execucao.Api/AutoReparos.Execucao.Api.csproj
COPY . .
RUN dotnet publish AutoReparos.Execucao.Api/AutoReparos.Execucao.Api.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AutoReparos.Execucao.Api.dll"]
