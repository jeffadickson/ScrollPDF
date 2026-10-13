# ScrollPDF

Windows .NET 8 scrolling capture utility with a staged capture, analysis and PDF writer pipeline.

## Build

```powershell
dotnet restore ScrollPDF.sln
dotnet build ScrollPDF.sln -c Release
dotnet test ScrollPDF.sln -c Release
dotnet run --project ScrollPDF/ScrollPDF.csproj
```

Select capture region, start, scroll slowly down, stop to save a multipage PDF.

The document-boundary detector recognizes gray viewer gutters between white pages, as illustrated by the IPC example. The image is a regression fixture; copyrighted content is not MIT licensed.

MIT licensed project code. See docs for limitations.
