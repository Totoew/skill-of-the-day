FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json .
COPY backend/Directory.Packages.props backend/
COPY backend/Services/SkillService backend/Services/SkillService
COPY observability/ServiceDefaults observability/ServiceDefaults
RUN dotnet publish backend/Services/SkillService/SkillService.Api/SkillService.Api.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "SkillService.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "SkillService.Api.dll"]
