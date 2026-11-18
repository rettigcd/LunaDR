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
	public IEnumerable<Sector> ReadAll(int sectorsPerRead = 256) {
		var buffer = new byte[sectorsPerRead * VobReader.SectorSize];
		foreach (var cell in _cells) {
			long s = cell.FirstSector;
			while (s <= cell.LastSector) {
				int n = (int)Math.Min(sectorsPerRead, cell.LastSector - s + 1);
				_reader.Read(s, n, buffer);
				for (int i = 0; i < n; i++) {
					var mem = buffer.AsMemory(i * VobReader.SectorSize, VobReader.SectorSize);
					if (_key is { } key) Css.Css.Unscramble(key, mem.Span);
					yield return new Sector(cell, s + i, mem);
				}
				s += n;
			}
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
