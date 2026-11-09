# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ClaimsApproval.sln ./
COPY src/ClaimsApproval.Api/ClaimsApproval.Api.csproj src/ClaimsApproval.Api/
COPY tests/ClaimsApproval.Tests/ClaimsApproval.Tests.csproj tests/ClaimsApproval.Tests/
RUN dotnet restore src/ClaimsApproval.Api/ClaimsApproval.Api.csproj
COPY bpmn ./bpmn
COPY src ./src
RUN dotnet publish src/ClaimsApproval.Api -c Release -o /app --no-restore

# Run as a non-root user on the slim ASP.NET runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ClaimsApproval.Api.dll"]
