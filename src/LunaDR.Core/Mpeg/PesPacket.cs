namespace LunaDR.Core.Mpeg;

/// <summary>What kind of elementary stream a DVD PES packet belongs to.</summary>
public enum StreamKind {
	Video,          // stream id 0xE0
	Ac3,            // private stream 1, substream 0x80-0x87
	Dts,            // private stream 1, substream 0x88-0x8F
	Lpcm,           // private stream 1, substream 0xA0-0xA7
	MpegAudio,      // stream id 0xC0-0xC7
	Subpicture,     // private stream 1, substream 0x20-0x3F
	Navigation,     // private stream 2 (0xBF): PCI / DSI
	Other,
}

/// <summary>
/// Identifies one elementary stream on a DVD. <see cref="Number"/> is the stream's index
/// within its kind: e.g. AC3 substream 0x81 is (Ac3, 1); it matches the stream number in
/// the PGC audio/subpicture control tables.
/// </summary>
public readonly record struct StreamId(StreamKind Kind, int Number) {
	public override string ToString() => $"{Kind}#{Number}";
}

/// <summary>
/// One PES packet's payload with its timestamps (90 kHz ticks), after the DVD-specific
/// private-stream headers have been removed.
/// </summary>
public sealed class PesPacket {
	public required StreamId Stream { get; init; }
	public long? Pts { get; init; }
	public long? Dts { get; init; }
	public required ReadOnlyMemory<byte> Payload { get; init; }
	/// <summary>For AC3/DTS/LPCM: number of audio frames that start in this packet.</summary>
	public int FrameCount { get; init; }
	/// <summary>For AC3/DTS/LPCM: 1-based offset into Payload of the first frame the PTS applies to (0 = none).</summary>
	public int FirstAccessUnit { get; init; }
}
