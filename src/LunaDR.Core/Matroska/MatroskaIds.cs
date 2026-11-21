namespace LunaDR.Core.Matroska;

/// <summary>How an element's data is interpreted.</summary>
public enum EbmlType { Master, UInt, Int, Float, String, Utf8, Date, Binary }

/// <summary>
/// Matroska / EBML element IDs (with their marker bits, as written in the file), their names
/// and types. From the Matroska specification (RFC 9559).
/// </summary>
public static class MatroskaIds {
	// EBML header
	public const uint Ebml = 0x1A45DFA3, EbmlVersion = 0x4286, EbmlReadVersion = 0x42F7, EbmlMaxIdLength = 0x42F2,
		EbmlMaxSizeLength = 0x42F3, DocType = 0x4282, DocTypeVersion = 0x4287, DocTypeReadVersion = 0x4285;
	// Global
	public const uint Void = 0xEC, Crc32 = 0xBF;
	// Segment and top-level
	public const uint Segment = 0x18538067, SeekHead = 0x114D9B74, Info = 0x1549A966, Tracks = 0x1654AE6B,
		Cluster = 0x1F43B675, Cues = 0x1C53BB6B, Chapters = 0x1043A770, Tags = 0x1254C367, Attachments = 0x1941A469;
	// SeekHead
	public const uint Seek = 0x4DBB, SeekId = 0x53AB, SeekPosition = 0x53AC;
	// Info
	public const uint TimestampScale = 0x2AD7B1, Duration = 0x4489, DateUtc = 0x4461, Title = 0x7BA9,
		MuxingApp = 0x4D80, WritingApp = 0x5741, SegmentUid = 0x73A4;
	// Tracks
	public const uint TrackEntry = 0xAE, TrackNumber = 0xD7, TrackUid = 0x73C5, TrackType = 0x83, FlagEnabled = 0xB9,
		FlagDefault = 0x88, FlagForced = 0x55AA, FlagLacing = 0x9C, DefaultDuration = 0x23E383, Name = 0x536E,
		Language = 0x22B59C, LanguageBcp47 = 0x22B59D, CodecId = 0x86, CodecPrivate = 0x63A2, CodecName = 0x258688,
		CodecDelay = 0x56AA, SeekPreRoll = 0x56BB, MinCache = 0x6DE7, MaxBlockAdditionId = 0x55EE,
		Video = 0xE0, PixelWidth = 0xB0, PixelHeight = 0xBA, DisplayWidth = 0x54B0, DisplayHeight = 0x54BA,
		DisplayUnit = 0x54B2, FlagInterlaced = 0x9A, FieldOrder = 0x9D, AspectRatioType = 0x54B3,
		PixelCropTop = 0x54BB, PixelCropBottom = 0x54AA, PixelCropLeft = 0x54CC, PixelCropRight = 0x54DD,
		Audio = 0xE1, SamplingFrequency = 0xB5, Channels = 0x9F, BitDepth = 0x6264,
		ContentEncodings = 0x6D80, ContentEncoding = 0x6240, ContentCompression = 0x5034,
		ContentCompAlgo = 0x4254, ContentCompSettings = 0x4255;
	// Cluster
	public const uint Timestamp = 0xE7, SimpleBlock = 0xA3, BlockGroup = 0xA0, Block = 0xA1, BlockDuration = 0x9B,
		ReferenceBlock = 0xFB, Position = 0xA7, PrevSize = 0xAB;
	// Cues
	public const uint CuePoint = 0xBB, CueTime = 0xB3, CueTrackPositions = 0xB7, CueTrack = 0xF7,
		CueClusterPosition = 0xF1, CueRelativePosition = 0xF0, CueDuration = 0xB2;
	// Chapters
	public const uint EditionEntry = 0x45B9, EditionUid = 0x45BC, EditionFlagHidden = 0x45BD, EditionFlagDefault = 0x45DB,
		EditionFlagOrdered = 0x45DD, ChapterAtom = 0xB6, ChapterUid = 0x73C4, ChapterStringUid = 0x5654,
		ChapterTimeStart = 0x91, ChapterTimeEnd = 0x92, ChapterFlagHidden = 0x98, ChapterFlagEnabled = 0x4598,
		ChapterDisplay = 0x80, ChapString = 0x85, ChapLanguage = 0x437C, ChapCountry = 0x437E;
	// Tags
	public const uint Tag = 0x7373, Targets = 0x63C0, TargetTypeValue = 0x68CA, TargetType = 0x63CA, TagTrackUid = 0x63C5,
		SimpleTag = 0x67C8, TagName = 0x45A3, TagLanguage = 0x447A, TagDefault = 0x4484, TagString = 0x4487, TagBinary = 0x4485;

	private static readonly Dictionary<uint, (string Name, EbmlType Type)> Known = new() {
		[Ebml] = ("EBML", EbmlType.Master),
		[EbmlVersion] = ("EBMLVersion", EbmlType.UInt),
		[EbmlReadVersion] = ("EBMLReadVersion", EbmlType.UInt),
		[EbmlMaxIdLength] = ("EBMLMaxIDLength", EbmlType.UInt),
		[EbmlMaxSizeLength] = ("EBMLMaxSizeLength", EbmlType.UInt),
		[DocType] = ("DocType", EbmlType.String),
		[DocTypeVersion] = ("DocTypeVersion", EbmlType.UInt),
		[DocTypeReadVersion] = ("DocTypeReadVersion", EbmlType.UInt),
		[Void] = ("Void", EbmlType.Binary),
		[Crc32] = ("CRC-32", EbmlType.Binary),
		[Segment] = ("Segment", EbmlType.Master),
		[SeekHead] = ("SeekHead", EbmlType.Master),
		[Info] = ("Info", EbmlType.Master),
		[Tracks] = ("Tracks", EbmlType.Master),
		[Cluster] = ("Cluster", EbmlType.Master),
		[Cues] = ("Cues", EbmlType.Master),
		[Chapters] = ("Chapters", EbmlType.Master),
		[Tags] = ("Tags", EbmlType.Master),
		[Attachments] = ("Attachments", EbmlType.Master),
		[Seek] = ("Seek", EbmlType.Master),
		[SeekId] = ("SeekID", EbmlType.Binary),
		[SeekPosition] = ("SeekPosition", EbmlType.UInt),
		[TimestampScale] = ("TimestampScale", EbmlType.UInt),
		[Duration] = ("Duration", EbmlType.Float),
		[DateUtc] = ("DateUTC", EbmlType.Date),
		[Title] = ("Title", EbmlType.Utf8),
		[MuxingApp] = ("MuxingApp", EbmlType.Utf8),
		[WritingApp] = ("WritingApp", EbmlType.Utf8),
		[SegmentUid] = ("SegmentUID", EbmlType.Binary),
		[TrackEntry] = ("TrackEntry", EbmlType.Master),
		[TrackNumber] = ("TrackNumber", EbmlType.UInt),
		[TrackUid] = ("TrackUID", EbmlType.UInt),
		[TrackType] = ("TrackType", EbmlType.UInt),
		[FlagEnabled] = ("FlagEnabled", EbmlType.UInt),
		[FlagDefault] = ("FlagDefault", EbmlType.UInt),
		[FlagForced] = ("FlagForced", EbmlType.UInt),
		[FlagLacing] = ("FlagLacing", EbmlType.UInt),
		[DefaultDuration] = ("DefaultDuration", EbmlType.UInt),
		[Name] = ("Name", EbmlType.Utf8),
		[Language] = ("Language", EbmlType.String),
		[LanguageBcp47] = ("LanguageBCP47", EbmlType.String),
		[CodecId] = ("CodecID", EbmlType.String),
		[CodecPrivate] = ("CodecPrivate", EbmlType.Binary),
		[CodecName] = ("CodecName", EbmlType.Utf8),
		[CodecDelay] = ("CodecDelay", EbmlType.UInt),
		[SeekPreRoll] = ("SeekPreRoll", EbmlType.UInt),
		[MinCache] = ("MinCache", EbmlType.UInt),
		[MaxBlockAdditionId] = ("MaxBlockAdditionID", EbmlType.UInt),
		[Video] = ("Video", EbmlType.Master),
		[PixelWidth] = ("PixelWidth", EbmlType.UInt),
		[PixelHeight] = ("PixelHeight", EbmlType.UInt),
		[DisplayWidth] = ("DisplayWidth", EbmlType.UInt),
		[DisplayHeight] = ("DisplayHeight", EbmlType.UInt),
		[DisplayUnit] = ("DisplayUnit", EbmlType.UInt),
		[FlagInterlaced] = ("FlagInterlaced", EbmlType.UInt),
		[FieldOrder] = ("FieldOrder", EbmlType.UInt),
		[AspectRatioType] = ("AspectRatioType", EbmlType.UInt),
		[PixelCropTop] = ("PixelCropTop", EbmlType.UInt),
		[PixelCropBottom] = ("PixelCropBottom", EbmlType.UInt),
		[PixelCropLeft] = ("PixelCropLeft", EbmlType.UInt),
		[PixelCropRight] = ("PixelCropRight", EbmlType.UInt),
		[Audio] = ("Audio", EbmlType.Master),
		[SamplingFrequency] = ("SamplingFrequency", EbmlType.Float),
		[Channels] = ("Channels", EbmlType.UInt),
		[BitDepth] = ("BitDepth", EbmlType.UInt),
		[ContentEncodings] = ("ContentEncodings", EbmlType.Master),
		[ContentEncoding] = ("ContentEncoding", EbmlType.Master),
		[ContentCompression] = ("ContentCompression", EbmlType.Master),
		[ContentCompAlgo] = ("ContentCompAlgo", EbmlType.UInt),
		[ContentCompSettings] = ("ContentCompSettings", EbmlType.Binary),
		[Timestamp] = ("Timestamp", EbmlType.UInt),
		[SimpleBlock] = ("SimpleBlock", EbmlType.Binary),
		[BlockGroup] = ("BlockGroup", EbmlType.Master),
		[Block] = ("Block", EbmlType.Binary),
		[BlockDuration] = ("BlockDuration", EbmlType.UInt),
		[ReferenceBlock] = ("ReferenceBlock", EbmlType.Int),
		[Position] = ("Position", EbmlType.UInt),
		[PrevSize] = ("PrevSize", EbmlType.UInt),
		[CuePoint] = ("CuePoint", EbmlType.Master),
		[CueTime] = ("CueTime", EbmlType.UInt),
		[CueTrackPositions] = ("CueTrackPositions", EbmlType.Master),
		[CueTrack] = ("CueTrack", EbmlType.UInt),
		[CueClusterPosition] = ("CueClusterPosition", EbmlType.UInt),
		[CueRelativePosition] = ("CueRelativePosition", EbmlType.UInt),
		[CueDuration] = ("CueDuration", EbmlType.UInt),
		[EditionEntry] = ("EditionEntry", EbmlType.Master),
		[EditionUid] = ("EditionUID", EbmlType.UInt),
		[EditionFlagHidden] = ("EditionFlagHidden", EbmlType.UInt),
		[EditionFlagDefault] = ("EditionFlagDefault", EbmlType.UInt),
		[EditionFlagOrdered] = ("EditionFlagOrdered", EbmlType.UInt),
		[ChapterAtom] = ("ChapterAtom", EbmlType.Master),
		[ChapterUid] = ("ChapterUID", EbmlType.UInt),
		[ChapterStringUid] = ("ChapterStringUID", EbmlType.Utf8),
		[ChapterTimeStart] = ("ChapterTimeStart", EbmlType.UInt),
		[ChapterTimeEnd] = ("ChapterTimeEnd", EbmlType.UInt),
		[ChapterFlagHidden] = ("ChapterFlagHidden", EbmlType.UInt),
		[ChapterFlagEnabled] = ("ChapterFlagEnabled", EbmlType.UInt),
		[ChapterDisplay] = ("ChapterDisplay", EbmlType.Master),
		[ChapString] = ("ChapString", EbmlType.Utf8),
		[ChapLanguage] = ("ChapLanguage", EbmlType.String),
		[ChapCountry] = ("ChapCountry", EbmlType.String),
		[Tag] = ("Tag", EbmlType.Master),
		[Targets] = ("Targets", EbmlType.Master),
		[TargetTypeValue] = ("TargetTypeValue", EbmlType.UInt),
		[TargetType] = ("TargetType", EbmlType.String),
		[TagTrackUid] = ("TagTrackUID", EbmlType.UInt),
		[SimpleTag] = ("SimpleTag", EbmlType.Master),
		[TagName] = ("TagName", EbmlType.Utf8),
		[TagLanguage] = ("TagLanguage", EbmlType.String),
		[TagDefault] = ("TagDefault", EbmlType.UInt),
		[TagString] = ("TagString", EbmlType.Utf8),
		[TagBinary] = ("TagBinary", EbmlType.Binary),
	};

	public static (string Name, EbmlType Type) Describe(uint id) =>
		Known.TryGetValue(id, out var d) ? d : ($"Unknown(0x{id:X})", EbmlType.Binary);
}
