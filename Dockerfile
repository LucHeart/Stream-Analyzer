FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

COPY ["StreamAnalyzer/StreamAnalyzer.csproj", "StreamAnalyzer/"]
RUN dotnet restore "StreamAnalyzer/StreamAnalyzer.csproj"

COPY . .
WORKDIR "/src/StreamAnalyzer"


RUN dotnet publish "StreamAnalyzer.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:9.0-alpine
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "LucHeart.StreamAnalyzer.dll"]