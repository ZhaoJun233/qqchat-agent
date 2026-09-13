#!/usr/bin/env python3
"""Piper TTS 旁路服务：GET /speak?text=…&voice=…&speed=… → 直接返回 wav。

为什么要这层包装：Piper 的 CLI 是一次性的（读 stdin 写文件），
而机器人要的是"给一段文本、拿一段音频"的 HTTP 调用；
包一层之后两个容器之间只靠一个 URL 就够了（NapCat 也能直接下这个 URL）。

⚠ 这里用的是 **库调用 + 常驻缓存**，不是每次请求起一个 piper 进程：
  • 新音色（如 zh_CN-xiao_ya-medium，phoneme_type=pinyin）要先加载 159MB 的 G2PW
    多音字模型 + BERT 分词器，进程模型下每句话都要重来一遍（1 核机器上十几秒）；
  • 常驻进程里 PiperVoice 会自己缓存 ChinesePhonemizer（voice._chinese_phonemizer），
    所以同一音色的第二句开始只有推理开销。
  代价：Piper 不是线程安全的 —— 全部合成走一把大锁（本来就是 CPU 串行）。

约定：
  • 音色 = /data/<voice>.onnx（换音色就是换这个文件，要配上同名 .onnx.json）
  • pinyin 类音色还要 /data/g2pW/（多音字表；见 tools/tts.Dockerfile）
  • speed 是"倍数"（1.0 = 原速），内部换算成 piper 的 --length-scale（越大越慢）
  • 只处理短句（默认上限 300 字），长文本应当由机器人自己拒绝发语音
"""

import io
import json
import os
import threading
import traceback
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

VOICE_DIR = os.environ.get("VOICE_DIR", "/data")
PORT = int(os.environ.get("PORT", "5000"))
DEFAULT_VOICE = os.environ.get("DEFAULT_VOICE", "zh_CN-huayan-medium")
MAX_CHARS = int(os.environ.get("MAX_CHARS", "300"))

# piper 不是线程安全的（同一模型并发推理会抢资源），串行化最稳
_synth_lock = threading.Lock()
# 已加载的音色：voice name → PiperVoice（含它内部缓存的 phonemizer）
_voices = {}
_voices_lock = threading.Lock()


def list_voices() -> list[str]:
    """可选音色 = 有 .onnx 且**有同名 .onnx.json** 的文件。

    为什么必须看 json：音色目录里还住着一些“不是音色”的 onnx
    （多音字消歧用的 g2pw.onnx 就住在 /data/g2pW/ 下，但万一有人塞到根目录），
    只看后缀会把它们也当成音色列出来，用户选了就报错。
    模型配置 json 才是“这是一个 piper 音色”的标志。
    """
    if not os.path.isdir(VOICE_DIR):
        return []
    return sorted(
        f[:-5]
        for f in os.listdir(VOICE_DIR)
        if f.endswith(".onnx") and os.path.exists(os.path.join(VOICE_DIR, f + ".json"))
    )


def model_path(voice: str) -> str:
    return os.path.join(VOICE_DIR, voice + ".onnx")


def get_voice(voice: str):
    """取（必要时加载并缓存）一个 PiperVoice。"""
    with _voices_lock:
        cached = _voices.get(voice)
        if cached is not None:
            return cached

        path = model_path(voice)
        if not os.path.exists(path) or not os.path.exists(path + ".json"):
            raise FileNotFoundError(path)

        from piper.voice import PiperVoice  # 延迟到用时才 import（启动更快）

        loaded = PiperVoice.load(path)
        _voices[voice] = loaded
        return loaded


def synthesize(text: str, voice: str, speed: float) -> bytes:
    from piper import SynthesisConfig

    loaded = get_voice(voice)
    length_scale = 1.0 / max(0.5, min(2.0, speed))

    syn_config = None
    try:
        syn_config = SynthesisConfig(length_scale=length_scale)
    except Exception:  # 不同版本字段可能不同，退回到模型自带参数
        syn_config = None

    buf = io.BytesIO()
    # 用 wave 写进内存（PiperVoice.synthesize_wav 需要一个 Wave_write 对象）。
    # 注意：这里**不要**用 with 包住——万一失败，wave 的 close() 会抛
    # "# channels not specified"，把真正的异常吞掉（排查了整整一轮）。
    wav = wave.open(buf, "wb")
    try:
        with _synth_lock:
            loaded.synthesize_wav(text, wav, syn_config=syn_config)
    finally:
        try:
            wav.close()
        except Exception:
            pass
    return buf.getvalue()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _json(self, code: int, payload: dict) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:  # noqa: N802 (stdlib 约定)
        url = urlparse(self.path)

        if url.path == "/health":
            self._json(200, {
                "ok": True,
                "voices": list_voices(),
                "default": DEFAULT_VOICE,
                "loaded": sorted(_voices.keys()),
            })
            return

        if url.path != "/speak":
            self._json(404, {"error": "用法：GET /speak?text=…&voice=…&speed=1.0（或 /health）"})
            return

        q = parse_qs(url.query)
        text = (q.get("text") or [""])[0].strip()
        voice = (q.get("voice") or [DEFAULT_VOICE])[0].strip() or DEFAULT_VOICE
        try:
            speed = float((q.get("speed") or ["1.0"])[0])
        except ValueError:
            speed = 1.0

        if not text:
            self._json(400, {"error": "缺少 text"})
            return
        if len(text) > MAX_CHARS:
            self._json(400, {"error": f"文本太长（{len(text)} > {MAX_CHARS}），长文本请改用文字"})
            return

        try:
            wav_bytes = synthesize(text, voice, speed)
        except FileNotFoundError as exc:
            self._json(404, {
                "error": f"音色不存在：{exc}",
                "hint": "音色就是 /data/<name>.onnx（要配上同名的 .onnx.json）",
                "voices": list_voices(),
            })
            return
        except Exception as exc:  # noqa: BLE001
            # 把真实调用栈的最后几行带上：这一层踩过“异常被 wave.close() 吞掉”的坑
            tail = "".join(traceback.format_exception_only(type(exc), exc)).strip()
            self._json(500, {"error": tail, "traceback": traceback.format_exc()[-800:]})
            return

        self.send_response(200)
        self.send_header("Content-Type", "audio/wav")
        self.send_header("Content-Length", str(len(wav_bytes)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(wav_bytes)

    def log_message(self, fmt: str, *args) -> None:  # 只留一行摘要，日志别刷屏
        print("[tts] " + (fmt % args), flush=True)


if __name__ == "__main__":
    print(f"[tts] 监听 0.0.0.0:{PORT}，音色目录 {VOICE_DIR}，默认音色 {DEFAULT_VOICE}", flush=True)
    print(f"[tts] 现在就有的音色：{list_voices() or '(无)'}", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
