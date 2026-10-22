namespace PfsExplorerLite.Services.PfsNet;

/// <summary>通过 PfsNet READ_SECTORS 提供 APA 表校验用的扇区源。</summary>
internal sealed class PfsNetApaSectorSource(PfsNetClient client, long totalBytes) : IApaSectorSource
{
    private const int MaxSectorsPerRequest = 256;

    public long TotalBytes { get; } = totalBytes;

    public async Task ReadSectorsAsync(ulong lba, int count, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        var bytes = checked(count * ApaPartitionTableReader.SectorSize);
        if (destination.Length < bytes)
            throw new ArgumentException("目标缓冲区过小。", nameof(destination));

        var offset = 0;
        var remaining = count;
        var nextLba = lba;
        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, MaxSectorsPerRequest);
            var payload = await client.ReadSectorsAsync(nextLba, (uint)chunk, cancellationToken);
            payload.AsSpan().CopyTo(destination.Span.Slice(offset, payload.Length));
            offset += payload.Length;
            nextLba += (ulong)chunk;
            remaining -= chunk;
        }
    }
}
