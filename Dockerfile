FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AutoReparos.Execution.Api/AutoReparos.Execution.Api.csproj AutoReparos.Execution.Api/
COPY AutoReparos.Execution.Application/AutoReparos.Execution.Application.csproj AutoReparos.Execution.Application/
COPY AutoReparos.Execution.Domain/AutoReparos.Execution.Domain.csproj AutoReparos.Execution.Domain/
COPY AutoReparos.Execution.Infrastructure/AutoReparos.Execution.Infrastructure.csproj AutoReparos.Execution.Infrastructure/
RUN dotnet restore AutoReparos.Execution.Api/AutoReparos.Execution.Api.csproj
COPY . .
RUN dotnet publish AutoReparos.Execution.Api/AutoReparos.Execution.Api.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AutoReparos.Execution.Api.dll"]
