namespace RipDvd.Core.Dvd;

/// <summary>
/// Reads 2048-byte sectors from a title set's title VOBs (VTS_xx_1.VOB, VTS_xx_2.VOB, ...),
/// treating them as one continuous run of sectors. Cell sector numbers in the IFO are
/// relative to the start of this run.
/// </summary>
public sealed class VobReader : IDisposable
{
    public const int SectorSize = 2048;

    private readonly List<(FileStream Stream, long FirstSector, long SectorCount)> _parts = [];

    public long SectorCount { get; }

    public VobReader(IEnumerable<string> vobFiles)
    {
        long next = 0;
        foreach (var path in vobFiles)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
            long count = fs.Length / SectorSize;
            _parts.Add((fs, next, count));
            next += count;
        }
        SectorCount = next;
    }

    /// <summary>Opens VTS_nn_1.VOB, VTS_nn_2.VOB, ... in the given VIDEO_TS folder.</summary>
    public static VobReader ForTitleSet(string videoTsFolder, int titleSetNumber)
    {
        var files = new List<string>();
        for (int i = 1; i <= 9; i++)
        {
            string p = Path.Combine(videoTsFolder, $"VTS_{titleSetNumber:00}_{i}.VOB");
            if (!File.Exists(p)) break;
            files.Add(p);
        }
        return new VobReader(files);
    }

    /// <summary>Read <paramref name="count"/> sectors starting at <paramref name="sector"/> into the buffer.</summary>
    public void Read(long sector, int count, Span<byte> buffer)
    {
        int done = 0;
        while (done < count)
        {
            long s = sector + done;
            var part = _parts.First(p => s >= p.FirstSector && s < p.FirstSector + p.SectorCount);
            int n = (int)Math.Min(count - done, part.FirstSector + part.SectorCount - s);
            part.Stream.Position = (s - part.FirstSector) * SectorSize;
            part.Stream.ReadExactly(buffer.Slice(done * SectorSize, n * SectorSize));
            done += n;
        }
    }

    public void Dispose()
    {
        foreach (var p in _parts) p.Stream.Dispose();
    }
}
