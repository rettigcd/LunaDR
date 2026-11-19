using RipDvd.Core.Css;
using RipDvd.Core.Ifo;

namespace RipDvd.Core.Dvd;

/// <summary>
/// Reads a title's sectors in playback order: cell by cell as listed in the PGC, each cell
/// a run of sectors in the title set's VOBs. Sectors are descrambled with the title key.
///
/// Angle blocks: only the first angle's cells are played (cells with BlockType 1 and BlockMode other than 1 are skipped).
///
/// Interleaved cells (multi-angle blocks, seamless branching): the cells of the block share one sector range, cut into
/// interleaved units (ILVUs) that take turns: angle 1, angle 2, angle 1, ...
/// Reading such a cell straight from its first to its last sector would also play the other angles' units (each scene twice, slightly different).
/// So for these cells we walk VOBU by VOBU: each nav pack gives the VOBU's end; at the last VOBU of an ILVU it gives the start of this cell's
/// next ILVU (DSI SML_PBI fields, see <see cref="NavPack"/>), and we jump there.
/// </summary>
public sealed class TitleSectorReader {
	private readonly ISectorSource _reader;
	private readonly IReadOnlyList<Cell> _cells;
	private readonly CssKey? _key;

	public TitleSectorReader(ISectorSource reader, IReadOnlyList<Cell> cells, CssKey? key) {
		_reader = reader;
		_cells = PlayedCells(cells);
		_key = key;
		CellSectors = EstimateCellSectors(cells);
		TotalSectors = CellSectors.Sum();
	}

	public IReadOnlyList<Cell> Cells => _cells;

	/// <summary>Sectors that will be read (estimated for interleaved cells; see <see cref="EstimateSectors"/>).</summary>
	public long TotalSectors { get; }

	/// <summary>Sectors that will be read from each of <see cref="Cells"/> (estimated for interleaved cells).</summary>
	public IReadOnlyList<long> CellSectors { get; }

	/// <summary>The cells that are played: all except the cells of angles other than the first.</summary>
	public static List<Cell> PlayedCells(IReadOnlyList<Cell> cells) => cells.Where(c => c.BlockType != 1 || c.BlockMode == 1).ToList();

	/// <summary>
	/// Sectors read when playing these cells (first angle only).
	/// An interleaved cell's range also holds the other angles' units; its own share is estimated as the range divided by the number of angles in its block.
	/// </summary>
	public static long EstimateSectors(IReadOnlyList<Cell> cells) => EstimateCellSectors(cells).Sum();

	/// <summary>The estimate of <see cref="EstimateSectors"/> for each played cell, in order.</summary>
	public static List<long> EstimateCellSectors(IReadOnlyList<Cell> cells) {
		var result = new List<long>();
		for (int i = 0; i < cells.Count; i++) {
			var c = cells[i];
			if (c.BlockType == 1 && c.BlockMode != 1) continue;
			if (!c.Interleaved) {
				result.Add(c.SectorCount);
				continue;
			}
			int angles = 1;
			if (c.BlockType == 1)
				while (i + angles < cells.Count && cells[i + angles].BlockType == 1 && cells[i + angles].BlockMode is 2 or 3) angles++;
			result.Add(c.SectorCount / angles);
		}
		return result;
	}

	/// <summary>
	/// One sector, the cell it belongs to, and its sector number in the VOBs.
	/// <see cref="SkippedBefore"/>: sectors skipped because of read errors just before this one (0 normally).
	/// </summary>
	public readonly record struct Sector(Cell Cell, long SectorNumber, ReadOnlyMemory<byte> Data, long SkippedBefore = 0);

	/// <summary>How often an unreadable sector is tried again before it is skipped.</summary>
	public const int ReadRetries = 3;

	/// <summary>A stretch of sectors that could not be read and was skipped: where it starts and how many sectors of the cell were lost.</summary>
	public sealed record SkippedRange(Cell Cell, long FirstSector, long Sectors);

	private readonly List<SkippedRange> _skipped = [];

	/// <summary>Stretches skipped because of read errors (damaged disc), filled in while <see cref="ReadAll"/> runs.</summary>
	public IReadOnlyList<SkippedRange> Skipped {
		get {
			lock (_skipped) return _skipped.ToList();
		}
	}

	/// <summary>
	/// Enumerate all sectors of the title. Each yielded sector's Data is only valid until the
	/// next item is requested (buffers are reused).
	///
	/// Damaged discs (like MakeMKV): a sector that fails with a media error (<see cref="VobReader.IsMediaError"/>) is tried
	/// <see cref="ReadRetries"/> more times; if it still fails, reading continues at the next VOBU of the cell that can be read
	/// (each VOBU starts with a nav pack and a key frame, so playback recovers there). The skipped stretch is listed in <see cref="Skipped"/>
	/// and marked on the next sector (<see cref="Sector.SkippedBefore"/>). Other errors (e.g. the drive refusing encrypted sectors) still stop the rip.
	/// </summary>
	public IEnumerable<Sector> ReadAll(int sectorsPerRead = 512) {
		// A background thread reads ahead (up to 8 chunks of 1 MB) so the drive keeps streaming while the caller processes.
		// Without this the drive sits idle during processing.
		using var full = new System.Collections.Concurrent.BlockingCollection<(Cell Cell, long First, int Count, byte[] Data, long SkippedBefore)>(boundedCapacity: 8);
		using var cancel = new CancellationTokenSource();
		var producer = Task.Run(() => {
			long pendingSkip = 0;   // sectors skipped since the last chunk handed over

			bool TryRead(long sector, int count, Span<byte> buffer) {
				try {
					_reader.Read(sector, count, buffer);
					return true;
				} catch (IOException ex) when (VobReader.IsMediaError(ex)) {
					return false;
				}
			}

			// Reads count sectors; on a media error, re-reads sector by sector with retries. Returns how many sectors from the start were read.
			int ReadWithRetry(long s, int count, byte[] data) {
				if (TryRead(s, count, data)) return count;
				for (int i = 0; i < count; i++) {
					var one = data.AsSpan(i * VobReader.SectorSize, VobReader.SectorSize);
					bool ok = false;
					for (int attempt = 0; attempt <= ReadRetries && !ok; attempt++) {
						cancel.Token.ThrowIfCancellationRequested();
						ok = TryRead(s + i, 1, one);
					}
					if (!ok) return i;
				}
				return count;
			}

			// Reads from..to. Returns -1 when all was read, else the first sector that could not be read.
			long ReadRange(Cell cell, long from, long to) {
				for (long s = from; s <= to;) {
					int n = (int)Math.Min(sectorsPerRead, to - s + 1);
					var data = new byte[n * VobReader.SectorSize];
					int good = ReadWithRetry(s, n, data);
					if (good > 0) {
						full.Add((cell, s, good, data, pendingSkip), cancel.Token);
						pendingSkip = 0;
					}
					if (good < n) return s + good;
					s += n;
				}
				return -1;
			}

			// After a read error: the next nav pack of this cell at or after 'from' that can be read, or -1 if none before the cell ends.
			long NextReadableVobu(Cell cell, long from) {
				var buffer = new byte[VobReader.SectorSize];
				for (long s = from; s <= cell.LastSector; s++) {
					cancel.Token.ThrowIfCancellationRequested();
					if (!TryRead(s, 1, buffer) || !NavPack.IsNavPack(buffer)) continue;
					var nav = NavPack.Parse(buffer);
					if (nav.VobId == cell.VobId && nav.CellId == cell.CellId) return s;   // (interleaved: skips the other angles' units)
				}
				return -1;
			}

			void Skip(Cell cell, long first, long sectors) {
				lock (_skipped) _skipped.Add(new SkippedRange(cell, first, sectors));
				pendingSkip += sectors;
			}

			try {
				foreach (var cell in _cells) {
					if (!cell.Interleaved) {
						long s = cell.FirstSector;
						while (s <= cell.LastSector) {
							long failed = ReadRange(cell, s, cell.LastSector);
							if (failed < 0) break;
							long next = NextReadableVobu(cell, failed + 1);
							Skip(cell, failed, (next < 0 ? cell.LastSector + 1 : next) - failed);
							if (next < 0) break;
							s = next;
						}
						continue;
					}
					// Interleaved: VOBU by VOBU, jumping over the other angles' units.
					var navSector = new byte[VobReader.SectorSize];
					long v = cell.FirstSector;
					while (v <= cell.LastSector) {
						if (ReadWithRetry(v, 1, navSector) == 0) {   // nav packs are never scrambled
							long next = NextReadableVobu(cell, v + 1);
							Skip(cell, v, (next < 0 ? cell.LastSector + 1 : next) - v);
							if (next < 0) break;
							v = next;
							continue;
						}
						if (!NavPack.IsNavPack(navSector))
							throw new InvalidDataException($"Interleaved cell {cell.VobId}/{cell.CellId}: no navigation pack at sector {v}.");
						var nav = NavPack.Parse(navSector);
						if (nav.VobId != cell.VobId || nav.CellId != cell.CellId)
							throw new InvalidDataException($"Interleaved cell {cell.VobId}/{cell.CellId}: sector {v} belongs to cell {nav.VobId}/{nav.CellId}.");
						long end = v + nav.VobuEndAddress;
						long failed = ReadRange(cell, v, end);
						if (failed >= 0) Skip(cell, failed, end - failed + 1);   // the rest of this VOBU; the nav pack says where to go next
						if (nav.InIlvu && nav.VobuEndAddress >= nav.IlvuEndAddress) {
							// Last VOBU of this unit: continue at this cell's next unit (none: end of the cell).
							if (nav.NextIlvuStart is 0 or 0x7FFFFFFF) break;
							v += nav.NextIlvuStart;
						} else {
							v = end + 1;
						}
					}
				}
			} catch (OperationCanceledException) {
				// The consumer stopped early; nothing to do.
			} finally {
				full.CompleteAdding();
			}
		});

		try {
			foreach (var (cell, first, count, data, skippedBefore) in full.GetConsumingEnumerable()) {
				for (int i = 0; i < count; i++) {
					var mem = data.AsMemory(i * VobReader.SectorSize, VobReader.SectorSize);
					if (_key is { } key) Css.Css.Unscramble(key, mem.Span);
					yield return new Sector(cell, first + i, mem, i == 0 ? skippedBefore : 0);
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
	public static CssKey? FindKey(ISectorSource reader, int maxSectors = 400_000, CancellationToken cancel = default) {
		var finder = new TitleKeyFinder();
		var buffer = new byte[64 * VobReader.SectorSize];
		long limit = Math.Min(reader.SectorCount, maxSectors);
		for (long s = 0; s < limit; s += 64) {
			cancel.ThrowIfCancellationRequested();
			int n = (int)Math.Min(64, limit - s);
			reader.Read(s, n, buffer);
			for (int i = 0; i < n; i++)
				if (finder.Offer(buffer.AsSpan(i * VobReader.SectorSize, VobReader.SectorSize))) return finder.Key;
		}
		return finder.Finish();
	}
}
