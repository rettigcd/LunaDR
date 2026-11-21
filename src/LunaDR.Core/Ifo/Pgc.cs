namespace LunaDR.Core.Ifo;

/// <summary>
/// Program Chain: an ordered list of cells to play. Programs are groups of cells (chapters
/// point at programs). Each cell is a run of sectors in the title set's VOB files.
/// </summary>
public sealed class Pgc {
	public int Number { get; init; }                // 1-based within the VTS PGCIT
	public bool IsEntryPgc { get; init; }           // entry point of a title
	public int TitleNumber { get; init; }           // VTS title number (TTN) this PGC belongs to
	public DvdTime PlaybackTime { get; init; }
	public int NextPgc { get; init; }
	public int PreviousPgc { get; init; }
	/// <summary>Per audio stream (index = stream number in VTS attributes): available, and the MPEG substream id.</summary>
	public IReadOnlyList<AudioStreamControl> AudioControl { get; init; } = [];
	public IReadOnlyList<SubpictureStreamControl> SubpictureControl { get; init; } = [];
	/// <summary>16 YCrCb palette entries for subpictures (0x00YYCrCb).</summary>
	public IReadOnlyList<uint> Palette { get; init; } = [];
	/// <summary>Program n starts at cell ProgramEntryCells[n-1] (1-based cell numbers).</summary>
	public IReadOnlyList<int> ProgramEntryCells { get; init; } = [];
	public IReadOnlyList<Cell> Cells { get; init; } = [];

	internal static Pgc Parse(BigEndianReader r, int o, int number, bool isEntry, int titleNumber) {
		int programCount = r.U8(o + 0x02);
		int cellCount = r.U8(o + 0x03);

		var audio = new List<AudioStreamControl>(8);
		for (int i = 0; i < 8; i++) {
			ushort v = r.U16(o + 0x0C + i * 2);
			audio.Add(new AudioStreamControl((v & 0x8000) != 0, (v >> 8) & 0x07));
		}

		var subs = new List<SubpictureStreamControl>(32);
		for (int i = 0; i < 32; i++) {
			uint v = r.U32(o + 0x1C + i * 4);
			subs.Add(new SubpictureStreamControl(
				Available: (v & 0x8000_0000) != 0,
				Stream4x3: (int)(v >> 24) & 0x1F,
				StreamWide: (int)(v >> 16) & 0x1F,
				StreamLetterbox: (int)(v >> 8) & 0x1F,
				StreamPanScan: (int)v & 0x1F));
		}

		var palette = new List<uint>(16);
		for (int i = 0; i < 16; i++) palette.Add(r.U32(o + 0xA4 + i * 4));

		int programMapOffset = r.U16(o + 0xE6);
		int cellPlaybackOffset = r.U16(o + 0xE8);
		int cellPositionOffset = r.U16(o + 0xEA);

		var programs = new List<int>(programCount);
		for (int i = 0; i < programCount && programMapOffset != 0; i++)
			programs.Add(r.U8(o + programMapOffset + i));

		var cells = new List<Cell>(cellCount);
		for (int i = 0; i < cellCount && cellPlaybackOffset != 0; i++) {
			int c = o + cellPlaybackOffset + i * 24;
			int p = o + cellPositionOffset + i * 4;
			byte cat = r.U8(c);
			cells.Add(new Cell {
				Number = i + 1,
				BlockMode = (cat >> 6) & 3,
				BlockType = (cat >> 4) & 3,
				SeamlessPlay = (cat & 0x08) != 0,
				Interleaved = (cat & 0x04) != 0,
				StcDiscontinuity = (cat & 0x02) != 0,
				SeamlessAngle = (cat & 0x01) != 0,
				StillTime = r.U8(c + 2),
				CellCommand = r.U8(c + 3),
				PlaybackTime = DvdTime.Parse(r.Bytes(c + 4, 4)),
				FirstSector = r.U32(c + 8),
				FirstIlvuEndSector = r.U32(c + 12),
				LastVobuStartSector = r.U32(c + 16),
				LastSector = r.U32(c + 20),
				VobId = cellPositionOffset != 0 ? r.U16(p) : 0,
				CellId = cellPositionOffset != 0 ? r.U8(p + 3) : 0,
			});
		}

		return new Pgc {
			Number = number,
			IsEntryPgc = isEntry,
			TitleNumber = titleNumber,
			PlaybackTime = DvdTime.Parse(r.Bytes(o + 0x04, 4)),
			NextPgc = r.U16(o + 0x9C),
			PreviousPgc = r.U16(o + 0x9E),
			AudioControl = audio,
			SubpictureControl = subs,
			Palette = palette,
			ProgramEntryCells = programs,
			Cells = cells,
		};
	}
}

/// <summary>
/// One cell: a contiguous run of sectors. Sector numbers are relative to the start of the
/// title set's title VOBs (VTS_xx_1.VOB onwards, treated as one continuous file).
/// </summary>
public sealed class Cell {
	public int Number { get; init; }
	/// <summary>0 = not in angle block, 1 = first cell of block, 2 = middle, 3 = last.</summary>
	public int BlockMode { get; init; }
	/// <summary>0 = normal, 1 = angle block.</summary>
	public int BlockType { get; init; }
	public bool SeamlessPlay { get; init; }
	public bool Interleaved { get; init; }
	public bool StcDiscontinuity { get; init; }
	public bool SeamlessAngle { get; init; }
	public int StillTime { get; init; }
	public int CellCommand { get; init; }
	public DvdTime PlaybackTime { get; init; }
	public uint FirstSector { get; init; }
	public uint FirstIlvuEndSector { get; init; }
	public uint LastVobuStartSector { get; init; }
	public uint LastSector { get; init; }
	public int VobId { get; init; }
	public int CellId { get; init; }

	public long SectorCount => (long)LastSector - FirstSector + 1;
}

public readonly record struct AudioStreamControl(bool Available, int StreamNumber);

public readonly record struct SubpictureStreamControl(
	bool Available, int Stream4x3, int StreamWide, int StreamLetterbox, int StreamPanScan);
