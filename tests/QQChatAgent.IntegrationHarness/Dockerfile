# syntax=docker/dockerfile:1
# 集成测试容器：在容器网络里扮演「假 NapCat + 假大模型」，验证真正跑在容器里的机器人。
#   docker build -f tests/QQChatAgent.IntegrationHarness/Dockerfile -t qqchat-harness .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY tests/QQChatAgent.IntegrationHarness/QQChatAgent.IntegrationHarness.csproj tests/QQChatAgent.IntegrationHarness/
RUN dotnet restore tests/QQChatAgent.IntegrationHarness/QQChatAgent.IntegrationHarness.csproj
COPY tests/QQChatAgent.IntegrationHarness/ tests/QQChatAgent.IntegrationHarness/
RUN dotnet publish tests/QQChatAgent.IntegrationHarness/QQChatAgent.IntegrationHarness.csproj \
        -c Release --no-restore -o /app /p:UseAppHost=false \
 && rm -rf /app/*.pdb

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/ /app/
ENTRYPOINT ["dotnet", "/app/QQChatAgent.IntegrationHarness.dll"]
