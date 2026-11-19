namespace RipDvd.Core.Mpeg;

/// <summary>One MPEG-2 coded picture, ready to be stored as one Matroska block.</summary>
public sealed class VideoFrame {
	public required byte[] Data { get; init; }
	public required char PictureType { get; init; }        // 'I', 'P' or 'B'
	public required int TemporalReference { get; init; }    // display order within the GOP
	/// <summary>Displayed fields: 2 normally, 3 with repeat_first_field (3:2 pulldown).</summary>
	public required int Fields { get; init; }
	/// <summary>Starts with a sequence or GOP header and is an I picture: a random-access point.</summary>
	public required bool IsKeyFrame { get; init; }
	/// <summary>Closed-caption byte pairs (CC1/CC2 field) found in this picture's user data, in display order of the GOP; usually only on the first picture of a GOP.</summary>
	public List<(byte, byte)> Captions { get; init; } = [];
	/// <summary>Presentation time in nanoseconds on the title timeline.</summary>
	public long Pts { get; set; }
	public long DurationNs => FieldsToNs(Fields);

	/// <summary>NTSC fields → nanoseconds, exactly (one field = 1001/60000 s), truncated.</summary>
	public static long FieldsToNs(long fields) => fields * 1_001_000_000_000 / 60_000;

	/// <summary>
	/// A picture's offset within its GOP, in nanoseconds: whole 90 kHz ticks (1501.5 per field, truncated), as MakeMKV places pictures.
	/// It differs from <see cref="FieldsToNs"/> only for an odd number of fields (3:2 pulldown), by half a tick, which decides the rounding to whole
	/// milliseconds: A Very Long Engagement title 5 had one picture in every other GOP 1 ms earlier than MakeMKV's (log 2026-11-19).
	/// </summary>
	public static long FieldOffsetNs(long fields) => fields * 3003 / 2 * 100_000 / 9;
}

/// <summary>
/// Splits an MPEG-2 video elementary stream into pictures and gives every picture a
/// presentation time.
///
/// A picture runs from its first header (sequence header 00 00 01 B3, GOP header B8, or
/// picture header 00) up to the next of those that follows the picture's slices.
/// The PES PTS applies to the first picture whose picture start code begins in that packet.
/// Not every picture has a PTS, so times are computed per GOP: pictures are put in display
/// order (temporal_reference), each lasts 2 or 3 fields, and the run is anchored on a picture
/// whose PTS is known.
/// </summary>
public sealed class Mpeg2VideoFramer {
	private readonly List<byte> _es = [];                // bytes not yet emitted
	private readonly List<(long Offset, long Pts)> _ptsMarks = [];   // ES offset of packet start → PTS (title ns)
	private long _esBase;                                // ES offset of _es[0]
	private int _scanPos;                                // next index in _es to scan for start codes
	private int _frameStart = -1;                        // index in _es where the current frame starts
	private bool _frameHasPicture;
	private char _type;
	private int _temporalRef;
	private int _fields = 2;
	private bool _startsWithHeader;
	private long? _framePts;

	private readonly List<VideoFrame> _gop = [];
	private readonly List<long?> _gopPts = [];
	private long _nextPtsGuess;
	private readonly List<VideoFrame> _ready = [];

	/// <summary>Sequence header and its extensions (the bytes before the first GOP header): Matroska CodecPrivate.</summary>
	public byte[]? SequenceHeader { get; private set; }

	/// <summary>When set, DVD closed-caption user data is taken out of each picture and returned in <see cref="VideoFrame.Captions"/>.</summary>
	public bool ExtractCaptions { get; set; }
	public int Width { get; private set; }
	public int Height { get; private set; }
	/// <summary>aspect_ratio_information: 2 = 4:3, 3 = 16:9.</summary>
	public int AspectCode { get; private set; }

	/// <summary>Feed one video PES payload. <paramref name="ptsNs"/> is on the title timeline, or null.</summary>
	public void Feed(ReadOnlySpan<byte> payload, long? ptsNs) {
		if (ptsNs is { } p) _ptsMarks.Add((_esBase + _es.Count, p));
		foreach (byte b in payload) _es.Add(b);
		Scan(final: false);
	}

	/// <summary>Frames completed so far, in decode order. Call after Feed/Flush and consume them.</summary>
	public List<VideoFrame> TakeReady() {
		var r = new List<VideoFrame>(_ready);
		_ready.Clear();
		return r;
	}

	public void Flush() {
		Scan(final: true);
		if (_frameStart >= 0 && _frameHasPicture) EmitFrame(_es.Count);
		FinishGop();
	}

	private void Scan(bool final) {
		// Leave room to read header fields. Re-check every step: emitting a frame shrinks the buffer.
		int margin = final ? 4 : 12;
		for (; _scanPos < _es.Count - margin; _scanPos++) {
			if (_es[_scanPos] != 0 || _es[_scanPos + 1] != 0 || _es[_scanPos + 2] != 1) continue;
			byte code = _es[_scanPos + 3];
			int i = _scanPos;

			if (code is 0xB3 or 0xB8 or 0x00) {
				// A header that starts a new picture ends the previous one (if it had a picture).
				if (_frameStart >= 0 && _frameHasPicture) {
					EmitFrame(i);
					i = _scanPos;   // emitted bytes were removed from the buffer
				}
				if (_frameStart < 0) {
					_frameStart = i;
					_frameHasPicture = false;
					_startsWithHeader = code != 0x00;
					_fields = 2;
					_framePts = null;
				}
			}

			switch (code) {
				case 0xB3:
					Width = (_es[i + 4] << 4) | (_es[i + 5] >> 4);
					Height = ((_es[i + 5] & 0x0F) << 8) | _es[i + 6];
					AspectCode = _es[i + 7] >> 4;
					break;
				case 0xB8:
					if (_frameStart >= 0 && SequenceHeader is null && _startsWithHeader && _es[_frameStart + 3] == 0xB3)
						SequenceHeader = _es.GetRange(_frameStart, i - _frameStart).ToArray();
					FinishGop();
					break;
				case 0x00:
					_frameHasPicture = true;
					_temporalRef = (_es[i + 4] << 2) | (_es[i + 5] >> 6);
					_type = ((_es[i + 5] >> 3) & 7) switch { 1 => 'I', 2 => 'P', 3 => 'B', _ => '?' };
					_framePts = TakePts(_esBase + i);
					break;
				case 0xB5:
					// Picture coding extension (id 8): repeat_first_field is bit 1 of byte 3.
					if ((_es[i + 4] >> 4) == 8 && _frameHasPicture && (_es[i + 7] & 0x02) != 0) _fields = 3;
					break;
			}
		}
	}

	/// <summary>The PTS of the PES packet in which the picture start code at <paramref name="esOffset"/> begins.</summary>
	private long? TakePts(long esOffset) {
		long? pts = null;
		int used = 0;
		for (; used < _ptsMarks.Count && _ptsMarks[used].Offset <= esOffset; used++) pts = _ptsMarks[used].Pts;
		// A PTS belongs to the first picture starting in its packet; marks before it are consumed.
		if (used > 0 && pts is not null) {
			_ptsMarks.RemoveRange(0, used);
			return pts;
		}
		return null;
	}

	private void EmitFrame(int end) {
		var data = _es.GetRange(_frameStart, end - _frameStart).ToArray();
		var captions = ExtractCaptions ? Captions.DvdCaptionData.ExtractAndStrip(ref data) : [];
		var frame = new VideoFrame {
			Data = data,
			Captions = captions,
			PictureType = _type,
			TemporalReference = _temporalRef,
			Fields = _fields,
			IsKeyFrame = _type == 'I' && _startsWithHeader,
		};
		_gop.Add(frame);
		_gopPts.Add(_framePts);

		// Drop emitted bytes from the buffer.
		_es.RemoveRange(0, end);
		_esBase += end;
		_scanPos -= end;
		_frameStart = -1;
		_frameHasPicture = false;
	}

	/// <summary>Assign presentation times to the pictures of the finished GOP and release them.</summary>
	private void FinishGop() {
		if (_gop.Count == 0) return;
		// Display order and each picture's offset from the GOP's first displayed picture,
		// counted in fields and converted to ns only at the end so rounding can't accumulate.
		var display = Enumerable.Range(0, _gop.Count).OrderBy(i => _gop[i].TemporalReference).ToList();
		var offset = new long[_gop.Count];
		long fields = 0;
		foreach (int i in display) { offset[i] = VideoFrame.FieldOffsetNs(fields); fields += _gop[i].Fields; }
		long t = VideoFrame.FieldsToNs(fields);

		// Anchor on the first picture (decode order) that has a PTS.
		long start = _nextPtsGuess;
		for (int i = 0; i < _gop.Count; i++) {
			if (_gopPts[i] is { } p) { start = p - offset[i]; break; }
		}
		for (int i = 0; i < _gop.Count; i++) _gop[i].Pts = start + offset[i];
		_nextPtsGuess = start + t;

		_ready.AddRange(_gop);
		_gop.Clear();
		_gopPts.Clear();
	}
}
