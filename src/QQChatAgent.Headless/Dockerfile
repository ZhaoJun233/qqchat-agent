# syntax=docker/dockerfile:1

# ══════════════════════════════════════════════════════════════
#  QQ Chat Agent（Headless）—— 多阶段构建
#  构建上下文必须是仓库根目录：
#     docker build -f src/QQChatAgent.Headless/Dockerfile -t qqchat-agent .
# ══════════════════════════════════════════════════════════════

# ---------- 构建阶段 ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 先只拷 csproj：本项目零 NuGet 依赖，这一层可长期命中缓存
COPY src/QQChatAgent.Headless/QQChatAgent.Headless.csproj src/QQChatAgent.Headless/
RUN dotnet restore src/QQChatAgent.Headless/QQChatAgent.Headless.csproj

# 再拷源码
COPY src/QQChatAgent.Headless/ src/QQChatAgent.Headless/

# UseAppHost=false：镜像里直接用 dotnet 启动，免得多带一个 apphost 可执行文件
RUN dotnet publish src/QQChatAgent.Headless/QQChatAgent.Headless.csproj \
        -c Release \
        --no-restore \
        -o /app/publish \
        /p:UseAppHost=false \
 && rm -rf /app/publish/*.pdb

# ---------- 运行阶段 ----------
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

# 时区数据：消息时间戳按本地时区显示（否则容器 UTC 会让模型的“现在几点”判断错位）
RUN apt-get update \
 && apt-get install -y --no-install-recommends tzdata \
 && rm -rf /var/lib/apt/lists/*

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    TZ=Asia/Shanghai \
    QQCHAT_DATA_DIR=/data \
    QQCHAT_HEALTH_PORT=8080 \
    QQCHAT_HEALTH_BIND=+ \
    QQCHAT_LOG_FILE=1

WORKDIR /app
COPY --from=build /app/publish/ /app/

# 非 root 运行（uid 固定，便于宿主机挂载目录授权）
RUN useradd --create-home --uid 10001 --shell /usr/sbin/nologin qqchat \
 && mkdir -p /data \
 && chown -R qqchat:qqchat /data /app
USER qqchat

# /data：设置、会话、人物档案、日志
VOLUME ["/data"]
# 8080=健康检查；3001=反向 WS 模式下机器人监听协议端连入
EXPOSE 8080 3001

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["dotnet", "/app/QQChatAgent.Headless.dll", "--health"]

ENTRYPOINT ["dotnet", "/app/QQChatAgent.Headless.dll"]
