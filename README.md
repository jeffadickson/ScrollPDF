# ScrollPDF

Windows desktop tool for capturing long documents while scrolling, recognizing visible page boundaries, and saving a multi-page PDF.

## Build and run

Requires Windows 10/11 and .NET 8 SDK.

```powershell
dotnet restore ScrollPDF.sln
dotnet build ScrollPDF.sln -c Release
dotnet test ScrollPDF.sln -c Release
dotnet run --project ScrollPDF/ScrollPDF.csproj
```

Choose **Select capture region**, drag over the scrolling document, press **Start**, scroll **slowly downward**, then **Stop**.

## Architecture

- UI: Windows Forms
- Capture worker: Windows GDI capture at ~5.7 frames/sec
- Analysis worker: displacement estimation and new-strip extraction
- PDF worker: boundary detection, pagination, PDFsharp-GDI output
- Bounded channels: 4 frames, 12 strips; backpressure instead of dropped queues

The PDF document has one writer. The GDI+ API is Windows-only.

## Boundary detection

Gray viewer gutter between white document pages. The IPC screenshot fixture is covered by `IpcDocumentBoundary_is_detected`. Further screenshots can be regression fixtures.

## Limitations

Slow downward scrolling with overlap is required. Cross-strip page breaks are not yet detected by a rolling window, and abrupt scroll jumps are not recoverable. PDF pages contain raster images rather than searchable text. Thread-failure propagation needs hardening. Windows CI runs builds and tests.

## License

MIT for project code. Copyrighted third-party fixture images remain subject to their own rights.