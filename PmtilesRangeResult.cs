using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

public sealed class PmtilesRangeResult : IActionResult
{
    private readonly string _filePath;
    private readonly string _contentType;

    public PmtilesRangeResult(string filePath, string contentType = "application/octet-stream")
    {
        _filePath = filePath;
        _contentType = contentType;
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        var request = context.HttpContext.Request;

        var fi = new FileInfo(_filePath);
        if (!fi.Exists)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        response.Headers[HeaderNames.AcceptRanges] = "bytes";
        response.ContentType = _contentType;

        long total = fi.Length;

        if (!request.Headers.TryGetValue(HeaderNames.Range, out var rangeHeader) ||
            !RangeHeaderValue.TryParse(rangeHeader.ToString(), out var range) ||
            range == null ||
            range.Ranges.Count == 0)
        {
            response.ContentLength = total;
            await using var fs = File.OpenRead(_filePath);
            await fs.CopyToAsync(response.Body);
            return;
        }

        // Single-range only (enough for PMTiles)
        var r = range.Ranges.First();
        long start = r.From ?? 0;
        long end = r.To ?? (total - 1);
        if (start < 0) start = 0;
        if (end >= total) end = total - 1;

        long length = (end - start) + 1;
        response.StatusCode = StatusCodes.Status206PartialContent;
        response.Headers[HeaderNames.ContentRange] = $"bytes {start}-{end}/{total}";
        response.ContentLength = length;

        await using var fs2 = File.OpenRead(_filePath);
        fs2.Seek(start, SeekOrigin.Begin);

        // copy exact bytes
        var buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int read = await fs2.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (read <= 0) break;
            await response.Body.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }
}
