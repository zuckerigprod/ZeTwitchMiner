using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace ZeTwitchMiner.Core;

public sealed class ImageCache(HttpClient http)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private readonly Dictionary<string, Task<Bitmap?>> _memory = new();

    // Картинки уменьшаются до нужной ширины при декодировании, чтобы не держать в памяти оригиналы
    public Task<Bitmap?> GetAsync(string url, int width, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return Task.FromResult<Bitmap?>(null);
        var key = url + "|" + width;
        if (!_memory.TryGetValue(key, out var task))
            _memory[key] = task = LoadAsync(url, width, ct);
        return task;
    }

    private async Task<Bitmap?> LoadAsync(string url, int width, CancellationToken ct)
    {
        var file = Path.Combine(AppPaths.ImageCache, Hash(url) + ".img");
        try
        {
            if (!File.Exists(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > Lifetime)
            {
                var bytes = await http.GetByteArrayAsync(url, ct);
                await File.WriteAllBytesAsync(file, bytes, ct);
            }
            return await Task.Run(() =>
            {
                using var stream = File.OpenRead(file);
                return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
            }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Debug($"Image failed: {url} ({ex.Message})");
            _memory.Remove(url + "|" + width);
            return null;
        }
    }

    public void Trim()
    {
        // Сбрасываем ссылки, чтобы GC мог забрать картинки ушедших кампаний
        _memory.Clear();
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.ImageCache))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > Lifetime * 2) File.Delete(f);
        }
        catch
        {
        }
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(s)));
}
