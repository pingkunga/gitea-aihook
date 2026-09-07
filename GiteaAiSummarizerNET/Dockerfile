FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY .dockerignore .dockerignore
COPY nuget.config ./
COPY src/GiteaAiSummarizer/GiteaAiSummarizer.csproj src/GiteaAiSummarizer/
COPY tests/GiteaAiSummarizer.Tests/GiteaAiSummarizer.Tests.csproj tests/GiteaAiSummarizer.Tests/
RUN dotnet restore src/GiteaAiSummarizer/GiteaAiSummarizer.csproj --configfile /src/nuget.config
COPY . .
RUN dotnet publish src/GiteaAiSummarizer/GiteaAiSummarizer.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY --from=build /src/src/GiteaAiSummarizer/Templates ./Templates/
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "GiteaAiSummarizer.dll"]
