using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

        long total = fi.Length;

        response.Headers[HeaderNames.AcceptRanges] = "bytes";
        response.ContentType = _contentType;

        // No Range header => send entire file
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

        // PMTiles is happy with single-range responses
        var r = range.Ranges.First();

        long start = r.From ?? 0;
        long end = r.To ?? (total - 1);

        if (start < 0) start = 0;
        if (end >= total) end = total - 1;

        // Range not satisfiable
        if (end < start || start >= total)
        {
            response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            response.Headers[HeaderNames.ContentRange] = $"bytes */{total}";
            return;
        }

        long length = (end - start) + 1;

        response.StatusCode = StatusCodes.Status206PartialContent;
        response.Headers[HeaderNames.ContentRange] = $"bytes {start}-{end}/{total}";
        response.ContentLength = length;

        await using var fs2 = File.OpenRead(_filePath);
        fs2.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[64 * 1024];
        long remaining = length;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await fs2.ReadAsync(buffer.AsMemory(0, toRead));
            if (read <= 0) break;

            await response.Body.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }
}
