FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY . .
RUN dotnet publish Voip.SignalingServer/Voip.SignalingServer.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENV PORT=10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "Voip.SignalingServer.dll"]
