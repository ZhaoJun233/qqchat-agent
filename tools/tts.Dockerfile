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
#
# pinyin 类音色（xiao_ya）额外要一份 G2PW 数据，放**子目录 g2pW/**（约 113MB 压缩 / 150MB 解压）：
#     cd /opt/qqchat/tts
#     mkdir -p g2pW && curl -L -o /tmp/g2pw.tar.gz \
#       'https://huggingface.co/datasets/rhasspy/piper-checkpoints/resolve/main/zh/zh_CN/_resources/g2pw.tar.gz?download=true'
#     tar -xzf /tmp/g2pw.tar.gz -C g2pW && rm /tmp/g2pw.tar.gz
#     # 光有上面那份还不够：piper 1.8 还要三个查表文件（来自 g2pW 项目本体）
#     for f in bopomofo_to_pinyin_wo_tune_dict.json char_bopomofo_dict.json bert-base-chinese_s2t_dict.txt; do
#       curl -sL -O "https://raw.githubusercontent.com/GitYCC/g2pW/master/g2pw/$f"
#     done
#   ⚠ 三个坑（2026-09-13 全踩了一遍）：
#     1) 目录名必须是 g2pW（PiperVoice 拼的是 download_dir/"g2pW"）；
#     2) download_dir 默认是**容器的 cwd** —— compose 里必须写 working_dir: /data，
#        否则 piper 去 /g2pW 找个空，报 “Chinese lookup tables not found in /g2pW”；
#     3) 缺那三个查表文件时报的是同一条“lookup tables not found”，别只盯着 tar 包。
#   BERT 分词器（bert-base-chinese，约 1MB）首次合成时从 HF 下载，缓存到 HF_HOME（compose 里指到 /data/hf）。
# 服务器磁盘很紧（8.8G 用了 96%）：装之前先 `df -h /`，别把它塞满 —— 2026-09-13 撞了两次 100%。
# 验证（不经过机器人，直接问服务）：
#     curl -s http://127.0.0.1:5010/health
#     curl -s -o /tmp/t.wav "http://127.0.0.1:5010/speak?text=%E4%BD%A0%E5%A5%BD&voice=zh_CN-huayan-medium&speed=1.0"
FROM python:3.11-slim

# piper-tts 本体 + 中文音色的两套依赖：
#   unicode-rbnf  ：把数字/日期读成中文（piper/phonemize_chinese.py 直接 import）
#   transformers  ：pinyin 类音色要的 BERT 分词器（只用到 BertTokenizer，不需要 torch）
#   sentence-stream：按句切分（同样只在 pinyin 路径用到）
# 注：espeak 类中文音色（zh_CN-huayan-medium）不需要这些；
#     pinyin 类（zh_CN-xiao_ya-medium）除此之外还要 /data/g2pW/ 那份多音字数据（见文末）。
RUN pip install --no-cache-dir piper-tts unicode-rbnf transformers sentence-stream

# 底镜像的入口是 piper CLI（默认 python -m piper）；
# compose 里用 command 覆盖成 HTTP 旁路脚本（/app/tts-server.py）。
ENTRYPOINT ["python", "-m", "piper"]
