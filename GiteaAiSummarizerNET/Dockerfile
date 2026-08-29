FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget python3 python-is-python3 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY --from=build /src/Templates ./Templates/
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV PYTHONUTF8=1
ENTRYPOINT ["dotnet", "GiteaAiSummarizer.dll"]
