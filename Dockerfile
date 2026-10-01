FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/AgendaBot.Api/AgendaBot.Api.csproj src/AgendaBot.Api/
RUN dotnet restore src/AgendaBot.Api/AgendaBot.Api.csproj
COPY src/ src/
RUN dotnet publish src/AgendaBot.Api -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# La imagen ya trae ICU y tzdata, que hacen falta para es-DO y America/Santo_Domingo.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AgendaBot.Api.dll"]
