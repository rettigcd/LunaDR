using System.Buffers.Binary;
using System.Text;

namespace RipDvd.Core.Bluray;

public enum BdStreamKind { Video, Audio, Subtitle, Other }

/// <summary>
/// One elementary stream of a play item (from the playlist's STN table): its PID in the clip's transport stream, coding type and attributes.
/// Coding types: 0x02 MPEG-2, 0x1B H.264, 0xEA VC-1, 0x20 MVC, 0x24 HEVC video; 0x80 LPCM, 0x81 AC3, 0x82 DTS, 0x83 TrueHD, 0x84/0xA1 E-AC3,
/// 0x85 DTS-HD HR, 0x86 DTS-HD MA, 0xA2 DTS-HD (secondary) audio; 0x90 PGS, 0x92 text subtitles.
/// </summary>
public sealed record BdStream(int Pid, int CodingType, int Format, int Rate, string? Language) {
	public BdStreamKind Kind => CodingType switch {
		0x01 or 0x02 or 0x1B or 0xEA or 0x20 or 0x24 => BdStreamKind.Video,
		0x03 or 0x04 or 0x80 or 0x81 or 0x82 or 0x83 or 0x84 or 0x85 or 0x86 or 0xA1 or 0xA2 => BdStreamKind.Audio,
		0x90 or 0x92 => BdStreamKind.Subtitle,
		_ => BdStreamKind.Other,
	};

	public string CodecName => CodingType switch {
		0x01 => "MPEG-1",
		0x02 => "MPEG-2",
		0x1B => "H.264",
		0xEA => "VC-1",
		0x20 => "MVC",
		0x24 => "HEVC",
		0x03 or 0x04 => "MPEG audio",
		0x80 => "LPCM",
		0x81 => "AC3",
		0x82 => "DTS",
		0x83 => "TrueHD",
		0x84 or 0xA1 => "E-AC3",
		0x85 => "DTS-HD HR",
		0x86 => "DTS-HD MA",
		0xA2 => "DTS-HD",
		0x90 => "PGS",
		0x92 => "Text",
		_ => $"0x{CodingType:X2}",
	};

	/// <summary>Video: "1080p", "720p", ... from the format code.</summary>
	public string VideoFormat => Format switch {
		1 => "480i",
		2 => "576i",
		3 => "480p",
		4 => "1080i",
		5 => "720p",
		6 => "1080p",
		7 => "576p",
		8 => "2160p",
		_ => "?",
	};

	/// <summary>Video frame rate (frames per second) from the rate code.</summary>
	public double FrameRate => Rate switch { 1 => 24000 / 1001.0, 2 => 24, 3 => 25, 4 => 30000 / 1001.0, 6 => 50, 7 => 60000 / 1001.0, _ => 0 };

	/// <summary>Audio: presentation type 1 = mono, 3 = stereo, 6 = multi-channel, 12 = stereo + multi-channel.</summary>
	public string ChannelLayout => Format switch { 1 => "mono", 3 => "stereo", 6 or 12 => "multi-channel", _ => "" };

	/// <summary>Audio sampling rate in Hz from the rate code (codes 12 and 14 carry a 48 kHz core).</summary>
	public int SampleRate => Rate switch { 1 => 48_000, 4 or 14 => 96_000, 5 or 12 => 192_000, _ => 0 };
}

/// <summary>A play item: part of one clip (in/out time on the clip's timeline, 45 kHz ticks) with the streams the player may use.</summary>
public sealed record BdPlayItem(string ClipId, uint InTime, uint OutTime, int ConnectionCondition, int Angles,
	IReadOnlyList<BdStream> Video, IReadOnlyList<BdStream> Audio, IReadOnlyList<BdStream> Subtitles) {
	/// <summary>Subtitle streams for picture-in-picture (secondary) video, listed apart in the STN table; not ripped (the secondary video isn't either).</summary>
	public IReadOnlyList<BdStream> PipSubtitles { get; init; } = [];

	public long DurationNs => (long)(OutTime - InTime) * 1_000_000_000 / 45_000;
}

/// <summary>A playlist mark; type 1 = entry mark (a chapter), time on the clip timeline of play item <see cref="PlayItem"/> (45 kHz).</summary>
public sealed record BdMark(int Type, int PlayItem, uint Time);

/// <summary>
/// A Blu-ray playlist (BDMV/PLAYLIST/nnnnn.mpls): the play items played in order, and the marks (chapters). On Blu-ray, a title is a playlist.
/// Layout from libbluray's mpls_parse.c: "MPLS" + version, then the offsets of the PlayList, PlayListMark and extension sections.
/// </summary>
public sealed class Mpls {
	public required string Name { get; init; }               // e.g. "00800"
	public required IReadOnlyList<BdPlayItem> Items { get; init; }
	public required IReadOnlyList<BdMark> Marks { get; init; }

	public long DurationNs => Items.Sum(i => i.DurationNs);

	/// <summary>Where each play item starts on the playlist's timeline (ns).</summary>
	public long[] ItemStartsNs() {
		var starts = new long[Items.Count];
		for (int i = 1; i < Items.Count; i++) starts[i] = starts[i - 1] + Items[i - 1].DurationNs;
		return starts;
	}

	/// <summary>
	/// Chapter start times (ns) on the playlist's timeline, from the entry marks; marks at the same time count once and a mark at the very end is dropped.
	/// </summary>
	public List<long> ChapterStartsNs() {
		var starts = ItemStartsNs();
		var result = new List<long>();
		foreach (var m in Marks.Where(m => m.Type == 1 && m.PlayItem < Items.Count)) {
			var item = Items[m.PlayItem];
			long t = starts[m.PlayItem] + ((long)m.Time - item.InTime) * 1_000_000_000 / 45_000;
			if (t < 0 || t >= DurationNs - 1_000_000_000) continue;
			if (result.Count == 0 || t > result[^1]) result.Add(t);
		}
		if (result.Count == 0 || result[0] != 0) result.Insert(0, 0);
		return result;
	}

	public static Mpls Parse(byte[] b, string name) {
		if (Encoding.ASCII.GetString(b, 0, 4) != "MPLS") throw new InvalidDataException($"{name}.mpls is not a playlist file");
		int listPos = (int)Be32(b, 8), markPos = (int)Be32(b, 12);

		var items = new List<BdPlayItem>();
		int numItems = Be16(b, listPos + 6);
		int p = listPos + 10;
		for (int i = 0; i < numItems; i++) {
			int length = Be16(b, p);
			items.Add(ParsePlayItem(b, p + 2));
			p += 2 + length;
		}

		var marks = new List<BdMark>();
		int numMarks = Be16(b, markPos + 4);
		for (int i = 0, m = markPos + 6; i < numMarks; i++, m += 14)
			marks.Add(new BdMark(b[m + 1], Be16(b, m + 2), Be32(b, m + 4)));

		return new Mpls { Name = name, Items = items, Marks = marks };
	}

	private static BdPlayItem ParsePlayItem(byte[] b, int p) {
		string clip = Encoding.ASCII.GetString(b, p, 5);
		int flags = Be16(b, p + 9);
		bool multiAngle = (flags & 0x10) != 0;
		int connection = flags & 0x0F;
		uint inTime = Be32(b, p + 12), outTime = Be32(b, p + 16);
		int q = p + 20 + 8 + 1 + 1 + 2;   // UO mask, random access flag, still mode, still time
		int angles = 1;
		if (multiAngle) {
			angles = b[q];
			q += 2 + (angles - 1) * 10;
		}

		// STN table: length, 2 reserved, counts of video / audio / PG / IG / secondary audio / secondary video / PiP PG, 5 reserved; then the streams.
		int numVideo = b[q + 4], numAudio = b[q + 5], numPg = b[q + 6], numPipPg = b[q + 10];
		int s = q + 16;
		List<BdStream> Read(int count) {
			var list = new List<BdStream>();
			for (int i = 0; i < count; i++) {
				int entryLength = b[s];
				int type = b[s + 1];
				int pid = type switch { 1 => Be16(b, s + 2), 2 => Be16(b, s + 4), _ => Be16(b, s + 3) };
				s += 1 + entryLength;
				int attrLength = b[s];
				int coding = b[s + 1];
				int format = 0, rate = 0;
				string? language = null;
				var kind = new BdStream(0, coding, 0, 0, null).Kind;
				if (kind == BdStreamKind.Video) {
					format = b[s + 2] >> 4;
					rate = b[s + 2] & 0x0F;
				} else if (kind == BdStreamKind.Audio) {
					format = b[s + 2] >> 4;
					rate = b[s + 2] & 0x0F;
					language = Encoding.ASCII.GetString(b, s + 3, 3);
				} else if (coding == 0x90 || coding == 0x91) {
					language = Encoding.ASCII.GetString(b, s + 2, 3);
				} else if (coding == 0x92) {
					language = Encoding.ASCII.GetString(b, s + 3, 3);
				}
				s += 1 + attrLength;
				list.Add(new BdStream(pid, coding, format, rate, language));
			}
			return list;
		}
		var video = Read(numVideo);
		var audio = Read(numAudio);
		var subtitles = Read(numPg);
		var pipSubtitles = Read(numPipPg);
		return new BdPlayItem(clip, inTime, outTime, connection, angles, video, audio, subtitles) { PipSubtitles = pipSubtitles };
	}

	internal static uint Be32(byte[] b, int p) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
	internal static int Be16(byte[] b, int p) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
}

/// <summary>
/// Clip information (BDMV/CLIPINF/nnnnn.clpi), the parts the ripper needs: the clip's presentation time range (from the sequence info),
/// used to estimate how much of the clip file a play item covers. Layout from libbluray's clpi_parse.c.
/// </summary>
public sealed class Clpi {
	/// <summary>Presentation time ranges (45 kHz) of the clip's STC sequences.</summary>
	public required IReadOnlyList<(uint Start, uint End)> Sequences { get; init; }

	public long DurationTicks => Sequences.Sum(s => (long)s.End - s.Start);

	public static Clpi Parse(byte[] b) {
		if (Encoding.ASCII.GetString(b, 0, 4) != "HDMV") throw new InvalidDataException("not a clip information file");
		int seq = (int)Mpls.Be32(b, 8);
		var sequences = new List<(uint, uint)>();
		int numAtc = b[seq + 5];
		int p = seq + 6;
		for (int a = 0; a < numAtc; a++) {
			int numStc = b[p + 4];
			p += 6;
			for (int s = 0; s < numStc; s++) {
				sequences.Add((Mpls.Be32(b, p + 6), Mpls.Be32(b, p + 10)));
				p += 14;
			}
		}
		return new Clpi { Sequences = sequences };
	}
}
