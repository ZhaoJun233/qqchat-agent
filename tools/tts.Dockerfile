# Piper TTS 旁路服务的镜像定义（约 490MB —— 所以单独一个容器，不塞进机器人镜像）
#
# 构建（在仓库根目录）：
#     docker build -t piper-tts:latest -f tools/tts.Dockerfile tools/
#
# 跑起来（docker-compose.yml 里已经有 tts 服务，这里只是它依赖的镜像怎么来的）：
#     docker compose up -d tts
#
# 首次准备音色模型（约 63MB，落到 ./tts/，容器里就是 /data）：
#     cd /opt/qqchat/tts
#     docker run --rm -v "$PWD:/data" --entrypoint python piper-tts:latest \
#         -m piper.download_voices zh_CN-huayan-medium --download-dir /data
# 换音色就是换一个 onnx：把 <voice>.onnx 与 <voice>.onnx.json 放进 ./tts/ 即可
# （中文现成音色：zh_CN-huayan-medium / zh_CN-huayan-x_low / zh_CN-xiao_ya-medium / zh_CN-chaowen-medium）。
# 验证（不经过机器人，直接问服务）：
#     curl -s http://127.0.0.1:5010/health
#     curl -s -o /tmp/t.wav "http://127.0.0.1:5010/speak?text=%E4%BD%A0%E5%A5%BD&voice=zh_CN-huayan-medium&speed=1.0"
FROM python:3.11-slim

RUN pip install --no-cache-dir piper-tts

# 底镜像的入口是 piper CLI（默认 python -m piper）；
# compose 里用 command 覆盖成 HTTP 旁路脚本（/app/tts-server.py）。
ENTRYPOINT ["python", "-m", "piper"]
