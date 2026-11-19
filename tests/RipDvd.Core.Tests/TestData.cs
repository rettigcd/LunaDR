using System.Buffers.Binary;
using RipDvd.Core.Dvd;
using RipDvd.Core.Ifo;

namespace RipDvd.Core.Tests;

/// <summary>Made-up disc data for tests: nav packs, cells, and a sector source that serves them.</summary>
internal static class TestData {
	public const int SectorSize = VobReader.SectorSize;

	/// <summary>
	/// A navigation pack sector (layout in <see cref="NavPack"/>): PCI with the sector number and presentation times,
	/// DSI with the VOBU end, VOB/cell id and the interleaving (SML_PBI) fields.
	/// </summary>
	public static byte[] NavPack(uint lbn, uint vobuEnd, int vobId, int cellId,
		int ilvuCategory = 0, uint ilvuEnd = 0, uint nextIlvu = 0, uint startPtm = 0, uint endPtm = 0) {
		var s = new byte[SectorSize];
		s[2] = 1;
		s[3] = 0xBA;   // pack header
		s[0x28] = 1;
		s[0x29] = 0xBF;   // PCI packet (private stream 2), substream 0x00 at 0x2C
		s[0x402] = 1;
		s[0x403] = 0xBF;
		s[0x406] = 0x01;   // DSI packet, substream 0x01
		const int pci = 0x2D, dsi = 0x407;
		BinaryPrimitives.WriteUInt32BigEndian(s.AsSpan(pci), lbn);
		BinaryPrimitives.WriteUInt32BigEndian(s.AsSpan(pci + 12), startPtm);
		BinaryPrimitives.WriteUInt32BigEndian(s.AsSpan(pci + 16), endPtm);
		BinaryPrimitives.WriteUInt32BigEndian(s.AsSpan(dsi + 4), lbn);
		BinaryPrimitives.WriteUInt32BigEndian(s.AsSpan(dsi + 8), vobuEnd);
		BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(dsi + 24), (ushort)vobId);
		s[dsi + 27] = (byte)cellId;
		BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(dsi + 32), (ushort)ilvuCategory);
		BinaryPrimitives.WriteUInt32BigEndian(s.AsSpan(dsi + 34), ilvuEnd);
		BinaryPrimitives.WriteUInt32BigEndian(s.AsSpan(dsi + 38), nextIlvu);
		return s;
	}

	/// <summary>A cell of a PGC. Angle blocks: blockType 1, blockMode 1 = first angle, 2 = middle, 3 = last.</summary>
	public static Cell Cell(int vobId, int cellId, uint first, uint last, int seconds = 1,
		int blockType = 0, int blockMode = 0, bool interleaved = false) => new() {
			VobId = vobId,
			CellId = cellId,
			FirstSector = first,
			LastSector = last,
			PlaybackTime = new DvdTime(0, 0, seconds, 0, 30),
			BlockType = blockType,
			BlockMode = blockMode,
			Interleaved = interleaved,
		};
}

/// <summary>
/// Sectors in memory. Sectors not set explicitly are MPEG-2 pack starts holding their own sector number at byte 8 (so tests can see which were returned).
/// Sectors in <see cref="Bad"/> fail like a damaged disc (ERROR_CRC); <see cref="FailWith"/> makes every read fail with another error.
/// </summary>
internal sealed class FakeSectorSource(long sectorCount) : ISectorSource {
	private readonly Dictionary<long, byte[]> _sectors = [];

	public long SectorCount { get; } = sectorCount;
	public HashSet<long> Bad { get; } = [];
	public IOException? FailWith { get; set; }

	/// <summary>How often each sector was read on its own (one-sector reads), to check the retries.</summary>
	public Dictionary<long, int> SingleReads { get; } = [];

	public void Set(long sector, byte[] data) => _sectors[sector] = data;

	public void Read(long sector, int count, Span<byte> buffer) {
		if (FailWith is not null) throw FailWith;
		if (count == 1) SingleReads[sector] = SingleReads.GetValueOrDefault(sector) + 1;
		for (int i = 0; i < count; i++) {
			long s = sector + i;
			if (Bad.Contains(s)) throw new IOException($"bad sector {s}", BadSectorSource.CrcErrorHResult);
			var target = buffer.Slice(i * TestData.SectorSize, TestData.SectorSize);
			if (_sectors.TryGetValue(s, out var data)) data.CopyTo(target);
			else {
				target.Clear();
				target[2] = 1;
				target[3] = 0xBA;   // an MPEG-2 pack header, so the reader keeps the sector
				BinaryPrimitives.WriteInt64LittleEndian(target[8..], s);
			}
		}
	}
}
