FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY .dockerignore .dockerignore
COPY nuget.config ./
COPY src/GiteaAiSummarizer/GiteaAiSummarizer.csproj src/GiteaAiSummarizer/
COPY tests/GiteaAiSummarizer.Tests/GiteaAiSummarizer.Tests.csproj tests/GiteaAiSummarizer.Tests/
RUN dotnet restore src/GiteaAiSummarizer/GiteaAiSummarizer.csproj --configfile /src/nuget.config
COPY . .
RUN dotnet publish src/GiteaAiSummarizer/GiteaAiSummarizer.csproj -c Release -o /app --no-restore

# Runtime is the SDK image (not aspnet) so file-based C# skill scripts can run via `dotnet <file>.cs`.
# Simplest way to demo a C# skill script; for production, pre-publish the script and go back to aspnet.
FROM mcr.microsoft.com/dotnet/sdk:10.0
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 wget \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY --from=build /src/src/GiteaAiSummarizer/Templates ./Templates/
# Pre-warm: compile each C# skill script once so the first agent call doesn't pay the build cost.
RUN for f in Templates/Skills/*/scripts/*.cs; do if [ -e "$f" ]; then dotnet build "$f"; fi; done
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "GiteaAiSummarizer.dll"]
