# Lossless video capture experiment

ScrollPDF now offers **Experimental video recording (30 FPS, lossless)** as an alternative to its adaptive PNG recorder.

## Windows prerequisites

Install FFmpeg and confirm it is on PATH:

```powershell
winget install --id Gyan.FFmpeg -e
ffmpeg -version
ffmpeg -hide_banner -encoders | findstr /i libx264rgb
```

Close/reopen the app after changing PATH. Select a document rectangle, select the video mode, choose an MKV destination, press Start and scroll. Press Stop to finalize.

## Backend

FFmpeg `gdigrab` reads the selected rectangle, `libx264rgb -qp 0 -preset ultrafast` encodes lossless RGB H.264 and a Matroska container stores the recording. There is no chroma subsampling. The resulting file is a **capture intermediate**, not the PDF.

Video capture uses FFmpeg installed on your machine. It is not yet based on Windows Graphics Capture or hardware video encoding. The experimental video backend runs in a separate process; this is distinct from the existing C# PNG queue.

## Decode to a folder for offline Python stitching

```powershell
python tools/video_to_frames.py "C:\\captures\\sample.mkv" "C:\\captures\\decoded"
```

Alternatively, run:

```powershell
ffmpeg -i sample.mkv -vsync 0 decoded/frame-%07d.png
```

The offline Python stitcher can then consume the ordered decoded PNG frames. This initial release does **not** automatically reconstruct a PDF inside the Windows UI; that integration is a separate milestone.

## Benchmark plan

Use the same scrolling document and viewer zoom for:
1. Adaptive PNG at 20 FPS;
2. Lossless video at 30 FPS.

Compare real capture duration, total frames, measured frame intervals (using `ffprobe -show_frames`), input storage size, recovered page count, registration confidence, and missing-content diagnostics. The reference PDF supplied for earlier tests can validate reconstructed pages; the source document must not be committed to the public repository.

## Limitations

- Requires FFmpeg available on PATH with gdigrab and libx264rgb.
- 30 FPS is a target, not a guaranteed measured capture rate.
- A video frame captured after a large scroll jump cannot recover skipped document content.
- Verify that the selected window remains visible and at fixed zoom.

## Process Recording in Windows GUI

The capture dashboard includes **Process Recording**. After recording ends, select the MKV, choose an output folder, and let the Python worker create `reconstructed.pdf` and `diagnostics.json`. Requires a Python installation accessible through `py -3` and packages `opencv-python numpy Pillow reportlab`.

```powershell
py -3 -m pip install opencv-python numpy Pillow reportlab
```

**Current quality gate:** reconstructed page count alone does not establish pixel-perfect capture. The 689-frame reference recording reconstructs 12 pages, but the die photograph on page 9 contains a visible horizontal seam. A full-resolution offset refinement test did not change that output. Do not treat this prototype as producing a validated faithful copy until seam validation and image-ground-truth comparison are implemented.
