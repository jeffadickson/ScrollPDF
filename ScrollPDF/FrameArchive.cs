using System.IO.Compression;
using System.Security.Cryptography;

namespace ScrollPDF;

public static class FrameArchive
{
    // Called after the capture worker and image writer have both finished.
    public static Task<string> PackageAsync(string folder, string output, IProgress<int>? progress = null)
        => Task.Run(() => Package(folder, output, progress));

    private static string Package(string folder, string output, IProgress<int>? progress)
    {
        var paths = Directory.GetFiles(folder, "frame-*.png")
            .Concat(new[] { "region.txt", "completed.txt" }.Select(n => Path.Combine(folder,n)).Where(File.Exists))
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();
        if(paths.Length == 0) throw new InvalidDataException("No screenshot frames");
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if(File.Exists(output)) throw new IOException("Archive exists: " + output);
        var staging = output + ".partial";
        if(File.Exists(staging)) throw new IOException("Partial archive exists: " + staging);
        using(var zip = ZipFile.Open(staging, ZipArchiveMode.Create))
        {
            foreach(var path in paths)
                zip.CreateEntryFromFile(path,Path.GetFileName(path),CompressionLevel.NoCompression);
        }
        using(var zip = ZipFile.OpenRead(staging))
        {
            for(int i=0;i<paths.Length;i++)
            {
                using var original = File.OpenRead(paths[i]);
                using var zipped = (zip.GetEntry(Path.GetFileName(paths[i]))
                    ?? throw new InvalidDataException("Missing ZIP entry")).Open();
                if(!SHA256.HashData(original).SequenceEqual(SHA256.HashData(zipped)))
                    throw new InvalidDataException("ZIP verification failed");
                progress?.Report((i+1)*100/paths.Length);
            }
        }
        File.Move(staging, output);
        // Delete ONLY the dedicated capture staging directory after a verified ZIP
        // is at its final destination. Never delete user-selected directories.
        if (!Path.GetFileName(folder).StartsWith("ScrollPDF-",StringComparison.Ordinal) ||
            !Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing cleanup outside ScrollPDF temporary capture directory");
        Directory.Delete(folder, recursive: true);
        return output;
    }
}
