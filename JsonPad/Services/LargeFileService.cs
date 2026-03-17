using System.IO;
using System.Text;
using System.Text.Json;

namespace JsonPad.Services;

public sealed record PagedChunk(string Text, long StartByte, long EndByte, long FileLength);

public static class LargeFileService
{
    private const int BufferSize = 1024 * 256;

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
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
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

    public static async Task StreamTextAsync(
        string path,
        Func<string, Task> onChunk,
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

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await onChunk(new string(buffer, 0, read)).ConfigureAwait(false);
            if (length > 0)
            {
                progress?.Report((double)stream.Position / length);
            }
        }

        progress?.Report(1.0);
    }

    public static async Task<PagedChunk> ReadPageAsync(
        string path,
        long startByte,
        int pageSizeBytes,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var fileLength = info.Length;
        if (fileLength <= 0)
        {
            return new PagedChunk(string.Empty, 0, 0, 0);
        }

        var safeStart = Math.Clamp(startByte, 0, Math.Max(0, fileLength - 1));
        var toRead = (int)Math.Min(pageSizeBytes, fileLength - safeStart);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            useAsync: true);

        stream.Seek(safeStart, SeekOrigin.Begin);
        var byteBuffer = new byte[toRead];
        var totalRead = 0;
        while (totalRead < toRead)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(
                byteBuffer.AsMemory(totalRead, toRead - totalRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        var text = DecodeUtf8Page(byteBuffer, totalRead, safeStart == 0);
        return new PagedChunk(text, safeStart, safeStart + totalRead, fileLength);
    }

    public static async Task<JsonValidationResult> ValidateJsonStreamAsync(
        string path,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            var fileLength = info.Length;
            var state = new JsonReaderState(new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow
            });

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                useAsync: true);

            var buffer = new byte[BufferSize];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var reader = new Utf8JsonReader(
                    new ReadOnlySpan<byte>(buffer, 0, read),
                    isFinalBlock: false,
                    state);
                while (reader.Read())
                {
                }

                state = reader.CurrentState;
                if (fileLength > 0)
                {
                    progress?.Report((double)stream.Position / fileLength);
                }
            }

            var finalReader = new Utf8JsonReader(ReadOnlySpan<byte>.Empty, isFinalBlock: true, state);
            while (finalReader.Read())
            {
            }

            progress?.Report(1.0);
            return new JsonValidationResult(true, "JSON is valid.");
        }
        catch (JsonException ex)
        {
            var message =
                $"Invalid JSON at line {ex.LineNumber}, position {ex.BytePositionInLine}: {ex.Message}";
            return new JsonValidationResult(false, message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new JsonValidationResult(false, $"Validation failed: {ex.Message}");
        }
    }

    private static string DecodeUtf8Page(byte[] bytes, int count, bool isFirstPage)
    {
        if (count == 0)
        {
            return string.Empty;
        }

        var offset = 0;
        if (isFirstPage &&
            count >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            offset = 3;
        }

        var length = Math.Max(0, count - offset);
        return Encoding.UTF8.GetString(bytes, offset, length);
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
            await writer.WriteAsync(text.AsMemory(index, chunkSize), cancellationToken).ConfigureAwait(false);
            index += chunkSize;

            if (total > 0)
            {
                progress?.Report((double)index / total);
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
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
