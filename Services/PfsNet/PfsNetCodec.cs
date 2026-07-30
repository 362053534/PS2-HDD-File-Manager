namespace PfsExplorerLite.Services.PfsNet;

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

internal static class PfsNetCodec
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static void WriteHeader(Span<byte> dest, uint command, uint payloadLength, int result = 0)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dest[..4], command);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(4, 4), payloadLength);
        BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(8, 4), result);
    }

    public static void ReadHeader(ReadOnlySpan<byte> src, out uint command, out uint payloadLength, out int result)
    {
        command = BinaryPrimitives.ReadUInt32LittleEndian(src[..4]);
        payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4, 4));
        result = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(8, 4));
    }

    public static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Utf8.GetBytes(value ?? string.Empty);
        if (bytes.Length > ushort.MaxValue)
            throw new InvalidOperationException("字符串超过 uint16 长度上限。");
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    public static string ReadString(BinaryReader reader)
    {
        var len = reader.ReadUInt16();
        var bytes = reader.ReadBytes(len);
        if (bytes.Length != len)
            throw new EndOfStreamException("字符串载荷不完整。");
        return Utf8.GetString(bytes);
    }

    public static async Task WriteExactAsync(NetworkStream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(buffer, cancellationToken);
    }

    public static async Task ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            int read;
            try
            {
                read = await stream.ReadAsync(buffer[offset..], timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("等待 PfsNet 响应超时。");
            }

            if (read <= 0)
                throw new IOException("与 PfsNet 的连接已断开。");
            offset += read;
        }
    }
}
