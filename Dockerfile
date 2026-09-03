FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["GenAiProject.csproj", "./"]
RUN dotnet restore "GenAiProject.csproj"
COPY . .
RUN dotnet publish "GenAiProject.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "GenAiProject.dll"]