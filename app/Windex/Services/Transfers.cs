// The local half of file transfers (docs/contract.md "Transfers"): which dropped or pasted items
// can be uploaded, a pasted picture saved as a PNG the core uploads (and then deletes), file
// names for downloads, and sizes in words.
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Windex.Services;

/// <summary>A local file to upload, the name it is stored under, and whether the core deletes it afterwards.</summary>
public sealed record UploadFile(string Path, string Name, bool Temporary = false);

public static class TransferFiles
{
    /// <summary>The files among dropped or pasted items, or why they can't be uploaded (folders, virtual files).</summary>
    public static (List<UploadFile> Files, string? Refusal) FromItems(IReadOnlyList<IStorageItem> items)
    {
        var folders = items.Where(i => i.IsOfType(StorageItemTypes.Folder)).ToList();
        if (folders.Count > 0)
            return ([], folders.Count == 1
                ? $"{folders[0].Name} is a folder. Only files can be uploaded."
                : $"{folders.Count} of these are folders. Only files can be uploaded.");
        var files = new List<UploadFile>();
        foreach (var item in items)
        {
            // Items without a path (a phone, a zip's contents) exist only inside their app.
            if (string.IsNullOrEmpty(item.Path)) return ([], $"{item.Name} is not a file on this PC. Copy it to a folder first.");
            files.Add(new UploadFile(item.Path, item.Name));
        }
        return (files, files.Count == 0 ? "There is nothing to upload." : null);
    }

    /// <summary>A pasted or dropped picture as a PNG in %TEMP%, named like "screenshot-20261118-161502.png" on the server.</summary>
    public static async Task<UploadFile> SavePictureAsync(RandomAccessStreamReference picture)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Windex-paste-{Guid.NewGuid():N}.png");
        try
        {
            using var input = await picture.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(input);
            // Straight BGRA: a clipboard DIB without alpha comes out opaque.
            using var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
            using (var output = File.Create(path).AsRandomAccessStream())
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetSoftwareBitmap(pixels);
                await encoder.FlushAsync();
            }
            return new UploadFile(path, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png", Temporary: true);
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>A file name Windows accepts for a remote file (the last element of its path).</summary>
    public static string LocalName(string remotePath)
    {
        string name = remotePath.TrimEnd('/').Split('/')[^1];
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return name.Length == 0 ? "download" : name;
    }

    /// <summary>"12.3 MB", as Explorer counts (1 KB = 1024 bytes).</summary>
    public static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} {(bytes == 1 ? "byte" : "bytes")}" : value >= 100 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    /// <summary>A working directory as the user thinks of it: "~/src/site" under the home folder.</summary>
    public static string ShortPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "~";
        foreach (var home in new[] { "/home/", "/Users/" })
            if (path.StartsWith(home, StringComparison.Ordinal))
            {
                int slash = path.IndexOf('/', home.Length);
                return slash < 0 ? "~" : "~" + path[slash..];
            }
        return path == "/root" ? "~" : path.StartsWith("/root/", StringComparison.Ordinal) ? "~" + path[5..] : path;
    }
}
