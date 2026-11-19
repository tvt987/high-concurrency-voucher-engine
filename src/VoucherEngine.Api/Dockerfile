FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/VoucherEngine.Domain/VoucherEngine.Domain.csproj src/VoucherEngine.Domain/
COPY src/VoucherEngine.Application/VoucherEngine.Application.csproj src/VoucherEngine.Application/
COPY src/VoucherEngine.Infrastructure/VoucherEngine.Infrastructure.csproj src/VoucherEngine.Infrastructure/
COPY src/VoucherEngine.Api/VoucherEngine.Api.csproj src/VoucherEngine.Api/
RUN dotnet restore src/VoucherEngine.Api/VoucherEngine.Api.csproj

COPY src/ src/
RUN dotnet publish src/VoucherEngine.Api/VoucherEngine.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "VoucherEngine.Api.dll"]
