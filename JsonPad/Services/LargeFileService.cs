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

                state = ConsumeUtf8JsonChunk(buffer, read, isFinalBlock: false, state);
                if (fileLength > 0)
                {
                    progress?.Report((double)stream.Position / fileLength);
                }
            }

            _ = ConsumeUtf8JsonChunk(Array.Empty<byte>(), 0, isFinalBlock: true, state);

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

    public static async Task<long> FindTextInRangeAsync(
        string path,
        string term,
        long startByteInclusive,
        long endByteExclusive,
        bool ignoreCase,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(term))
        {
            return -1;
        }

        var info = new FileInfo(path);
        var fileLength = info.Length;
        if (fileLength <= 0)
        {
            return -1;
        }

        var start = Math.Clamp(startByteInclusive, 0, fileLength);
        var end = Math.Clamp(endByteExclusive, 0, fileLength);
        if (end <= start)
        {
            return -1;
        }

        var needle = Encoding.UTF8.GetBytes(term);
        if (needle.Length == 0)
        {
            return -1;
        }

        var chunkSize = BufferSize * 4;
        var overlap = Math.Max(0, needle.Length - 1);
        var buffer = new byte[chunkSize + overlap];

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            useAsync: true);

        stream.Seek(start, SeekOrigin.Begin);
        long position = start;
        var carry = 0;

        while (position < end)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var toRead = (int)Math.Min(chunkSize, end - position);
            var read = await stream.ReadAsync(buffer.AsMemory(carry, toRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var total = carry + read;
            var index = IndexOfBytes(buffer, total, needle, ignoreCase);
            if (index >= 0)
            {
                var matchByte = position - carry + index;
                if (matchByte >= start && matchByte < end)
                {
                    progress?.Report((double)Math.Min(matchByte + needle.Length, fileLength) / fileLength);
                    return matchByte;
                }
            }

            carry = Math.Min(overlap, total);
            if (carry > 0)
            {
                Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
            }

            position += read;
            progress?.Report((double)position / fileLength);
        }

        return -1;
    }

    private static JsonReaderState ConsumeUtf8JsonChunk(
        byte[] buffer,
        int bytesRead,
        bool isFinalBlock,
        JsonReaderState state)
    {
        var reader = new Utf8JsonReader(
            new ReadOnlySpan<byte>(buffer, 0, bytesRead),
            isFinalBlock,
            state);
        while (reader.Read())
        {
        }

        return reader.CurrentState;
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

    private static int IndexOfBytes(byte[] haystack, int haystackLength, byte[] needle, bool ignoreCase)
    {
        if (needle.Length == 0 || haystackLength < needle.Length)
        {
            return -1;
        }

        var last = haystackLength - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            if (BytesMatchAt(haystack, i, needle, ignoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool BytesMatchAt(byte[] haystack, int offset, byte[] needle, bool ignoreCase)
    {
        for (var i = 0; i < needle.Length; i++)
        {
            var left = haystack[offset + i];
            var right = needle[i];
            if (ignoreCase)
            {
                left = ToLowerAscii(left);
                right = ToLowerAscii(right);
            }

            if (left != right)
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToLowerAscii(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z'
            ? (byte)(value + 32)
            : value;
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
