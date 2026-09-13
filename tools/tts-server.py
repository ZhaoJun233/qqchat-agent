#!/usr/bin/env python3
"""Piper TTS 旁路服务：GET /speak?text=…&voice=…&speed=… → 直接返回 wav。

为什么要这层包装：Piper 的 CLI 是一次性的（读 stdin 写文件），
而机器人要的是"给一段文本、拿一段音频"的 HTTP 调用；
包一层之后两个容器之间只靠一个 URL 就够了（NapCat 也能直接下这个 URL）。

约定：
  • 音色 = /data/<voice>.onnx（换音色就是换这个文件）
  • speed 是"倍数"（1.0 = 原速），内部换算成 piper 的 --length-scale（越大越慢）
  • 只处理短句（默认上限 300 字），长文本应当由机器人自己拒绝发语音
"""

import json
import os
import subprocess
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

VOICE_DIR = os.environ.get("VOICE_DIR", "/data")
PORT = int(os.environ.get("PORT", "5000"))
DEFAULT_VOICE = os.environ.get("DEFAULT_VOICE", "zh_CN-huayan-medium")
MAX_CHARS = int(os.environ.get("MAX_CHARS", "300"))
SPEAK_TIMEOUT = int(os.environ.get("SPEAK_TIMEOUT", "90"))

# piper 不是线程安全的（同一模型并发推理会抢资源），串行化最稳
_lock = threading.Lock()


def synthesize(text: str, voice: str, speed: float) -> bytes:
    model = os.path.join(VOICE_DIR, voice + ".onnx")
    if not os.path.exists(model):
        raise FileNotFoundError(model)

    length_scale = str(round(1.0 / max(0.5, min(2.0, speed)), 3))
    out = tempfile.mktemp(suffix=".wav")
    try:
        with _lock:
            proc = subprocess.run(
                ["python", "-m", "piper", "-m", model, "-f", out, "--length-scale", length_scale],
                input=text.encode("utf-8"),
                capture_output=True,
                timeout=SPEAK_TIMEOUT,
            )
        if proc.returncode != 0:
            raise RuntimeError(proc.stderr.decode("utf-8", "replace")[-300:] or "piper 退出码非 0")
        with open(out, "rb") as fh:
            return fh.read()
    finally:
        if os.path.exists(out):
            os.unlink(out)


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
            voices = sorted(
                f[:-5] for f in os.listdir(VOICE_DIR) if f.endswith(".onnx")
            ) if os.path.isdir(VOICE_DIR) else []
            self._json(200, {"ok": True, "voices": voices, "default": DEFAULT_VOICE})
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
            wav = synthesize(text, voice, speed)
        except FileNotFoundError as exc:
            self._json(404, {"error": f"音色不存在：{exc}", "hint": "音色就是 /data/<name>.onnx"})
            return
        except Exception as exc:  # noqa: BLE001
            self._json(500, {"error": str(exc)})
            return

        self.send_response(200)
        self.send_header("Content-Type", "audio/wav")
        self.send_header("Content-Length", str(len(wav)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(wav)

    def log_message(self, fmt: str, *args) -> None:  # 只留一行摘要，日志别刷屏
        print("[tts] " + (fmt % args), flush=True)


if __name__ == "__main__":
    print(f"[tts] 监听 0.0.0.0:{PORT}，音色目录 {VOICE_DIR}，默认音色 {DEFAULT_VOICE}", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
