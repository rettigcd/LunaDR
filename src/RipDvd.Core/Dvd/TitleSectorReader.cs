using RipDvd.Core.Css;
using RipDvd.Core.Ifo;

namespace RipDvd.Core.Dvd;

/// <summary>
/// Reads a title's sectors in playback order: cell by cell as listed in the PGC, each cell
/// a run of sectors in the title set's VOBs. Sectors are descrambled with the title key.
///
/// Angle blocks: only the first angle's cells are played (cells with BlockType 1 and
/// BlockMode other than 1 are skipped). Interleaved multi-angle titles are not handled yet.
/// </summary>
public sealed class TitleSectorReader {
	private readonly VobReader _reader;
	private readonly IReadOnlyList<Cell> _cells;
	private readonly CssKey? _key;

	public TitleSectorReader(VobReader reader, IReadOnlyList<Cell> cells, CssKey? key) {
		_reader = reader;
		_cells = cells.Where(c => c.BlockType != 1 || c.BlockMode == 1).ToList();
		_key = key;
	}

	public IReadOnlyList<Cell> Cells => _cells;

	public long TotalSectors => _cells.Sum(c => c.SectorCount);

	/// <summary>One sector, the cell it belongs to, and its sector number in the VOBs.</summary>
	public readonly record struct Sector(Cell Cell, long SectorNumber, ReadOnlyMemory<byte> Data);

	/// <summary>
	/// Enumerate all sectors of the title. Each yielded sector's Data is only valid until the
	/// next item is requested (buffers are reused).
	/// </summary>
	public IEnumerable<Sector> ReadAll(int sectorsPerRead = 512) {
		// A background thread reads ahead (up to 8 chunks of 1 MB) so the drive keeps streaming while the caller processes.
		// Without this the drive sits idle during processing.
		using var full = new System.Collections.Concurrent.BlockingCollection<(Cell Cell, long First, int Count, byte[] Data)>(boundedCapacity: 8);
		using var cancel = new CancellationTokenSource();
		var producer = Task.Run(() => {
			try {
				foreach (var cell in _cells) {
					long s = cell.FirstSector;
					while (s <= cell.LastSector) {
						int n = (int)Math.Min(sectorsPerRead, cell.LastSector - s + 1);
						var data = new byte[n * VobReader.SectorSize];
						_reader.Read(s, n, data);
						full.Add((cell, s, n, data), cancel.Token);
						s += n;
					}
				}
			} catch (OperationCanceledException) {
				// The consumer stopped early; nothing to do.
			} finally {
				full.CompleteAdding();
			}
		});

		try {
			foreach (var (cell, first, count, data) in full.GetConsumingEnumerable()) {
				for (int i = 0; i < count; i++) {
					var mem = data.AsMemory(i * VobReader.SectorSize, VobReader.SectorSize);
					if (_key is { } key) Css.Css.Unscramble(key, mem.Span);
					yield return new Sector(cell, first + i, mem);
				}
			}
			producer.GetAwaiter().GetResult();   // surface read errors
		} finally {
			cancel.Cancel();   // caller stopped early: let the reader thread finish
		}
	}

	/// <summary>
	/// Find the title key by scanning the title set's VOBs from the start until a candidate is
	/// validated. Returns null if the VOBs contain no scrambled sectors (not CSS protected).
	/// </summary>
	public static CssKey? FindKey(VobReader reader, int maxSectors = 400_000) {
		var finder = new TitleKeyFinder();
		var buffer = new byte[64 * VobReader.SectorSize];
		long limit = Math.Min(reader.SectorCount, maxSectors);
		for (long s = 0; s < limit; s += 64) {
			int n = (int)Math.Min(64, limit - s);
			reader.Read(s, n, buffer);
			for (int i = 0; i < n; i++)
				if (finder.Offer(buffer.AsSpan(i * VobReader.SectorSize, VobReader.SectorSize))) return finder.Key;
		}
		return finder.Finish();
	}
}
