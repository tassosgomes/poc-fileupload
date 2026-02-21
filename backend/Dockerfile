FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["UploadPoc.sln", "./"]
COPY ["1-Services/UploadPoc.API/UploadPoc.API.csproj", "1-Services/UploadPoc.API/"]
COPY ["2-Application/UploadPoc.Application/UploadPoc.Application.csproj", "2-Application/UploadPoc.Application/"]
COPY ["3-Domain/UploadPoc.Domain/UploadPoc.Domain.csproj", "3-Domain/UploadPoc.Domain/"]
COPY ["4-Infra/UploadPoc.Infra/UploadPoc.Infra.csproj", "4-Infra/UploadPoc.Infra/"]
COPY ["5-Tests/UploadPoc.UnitTests/UploadPoc.UnitTests.csproj", "5-Tests/UploadPoc.UnitTests/"]

RUN dotnet restore "UploadPoc.sln"

COPY . .

RUN dotnet publish "1-Services/UploadPoc.API/UploadPoc.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["dotnet", "UploadPoc.API.dll"]
