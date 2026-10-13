#!/usr/bin/env python3
"""Decode a lossless ScrollPDF MKV to numbered PNGs for offline reconstruction.

Usage: python tools/video_to_frames.py capture.mkv decoded/
Requires ffmpeg on PATH. Does not perform page stitching by itself.
"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess


def decode(src: Path, out: Path) -> dict:
    ffmpeg = shutil.which("ffmpeg")
    ffprobe = shutil.which("ffprobe")
    if not ffmpeg or not ffprobe:
        raise RuntimeError("FFmpeg and ffprobe must be installed and on PATH")
    if not src.is_file():
        raise FileNotFoundError(src)
    out.mkdir(parents=True, exist_ok=True)
    if any(out.iterdir()):
        raise RuntimeError(f"Output directory must be empty: {out}")
    probe = subprocess.run(
        [ffprobe, "-v", "error", "-select_streams", "v:0",
         "-show_entries", "stream=codec_name,pix_fmt,width,height,avg_frame_rate",
         "-of", "json", str(src)],
        check=True, capture_output=True, text=True)
    info = json.loads(probe.stdout)
    subprocess.run(
        [ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error",
         "-i", str(src), "-map", "0:v:0", "-vsync", "0",
         str(out / "frame-%07d.png")], check=True)
    files = sorted(out.glob("frame-*.png"))
    result = {"source": str(src), "frames": len(files), "streams": info.get("streams", [])}
    (out / "video_metadata.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("video", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    print(json.dumps(decode(args.video, args.output), indent=2))
