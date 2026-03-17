using System.IO;
using System.Text;

namespace JsonPad.Services;

public static class LargeFileService
{
    private const int BufferSize = 1024 * 64;

    public static async Task<string> ReadTextAsync(
        string path,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        var length = fileInfo.Length;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            useAsync: true);

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[BufferSize];
        var builder = length > 0 && length < int.MaxValue
            ? new StringBuilder((int)length)
            : new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            builder.Append(buffer, 0, read);
            if (length > 0)
            {
                progress?.Report((double)stream.Position / length);
            }
        }

        progress?.Report(1.0);
        return builder.ToString();
    }

    public static async Task WriteTextAsync(
        string path,
        string text,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);

        var total = text.Length;
        var index = 0;
        while (index < total)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkSize = Math.Min(BufferSize, total - index);
            await writer.WriteAsync(text.AsMemory(index, chunkSize), cancellationToken);
            index += chunkSize;

            if (total > 0)
            {
                progress?.Report((double)index / total);
            }
        }

        await writer.FlushAsync(cancellationToken);
        progress?.Report(1.0);
    }

    public static void WriteText(
        string path,
        string text,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: false);
        using var writer = new StreamWriter(stream, Encoding.UTF8);

        var total = text.Length;
        var index = 0;
        while (index < total)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkSize = Math.Min(BufferSize, total - index);
            writer.Write(text.AsSpan(index, chunkSize));
            index += chunkSize;

            if (total > 0)
            {
                progress?.Report((double)index / total);
            }
        }

        writer.Flush();
        progress?.Report(1.0);
    }
}
