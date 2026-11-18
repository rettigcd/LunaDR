namespace RipDvd.Core.Ifo;

/// <summary>VTS video attributes (2 bytes at 0x200 in VTS_xx_0.IFO).</summary>
public sealed record VideoAttributes(
    int MpegVersion,          // 1 or 2
    string TvSystem,          // "NTSC" or "PAL"
    string AspectRatio,       // "4:3" or "16:9"
    int PermittedDisplay,     // bit flags: pan-scan / letterbox not permitted
    bool Line21Field1,        // closed captions present in field 1 (in GOP user data)
    bool Line21Field2,
    int Width,
    int Height,
    bool Letterboxed,
    bool FilmMode)
{
    public static VideoAttributes Parse(ReadOnlySpan<byte> b)
    {
        int mpeg = ((b[0] >> 6) & 3) == 0 ? 1 : 2;
        bool pal = ((b[0] >> 4) & 3) == 1;
        string aspect = ((b[0] >> 2) & 3) switch { 0 => "4:3", 3 => "16:9", var x => $"?{x}" };
        int height = pal ? 576 : 480;
        int width = ((b[1] >> 2) & 3) switch { 0 => 720, 1 => 704, 2 => 352, _ => 352 };
        if (((b[1] >> 2) & 3) == 3) height /= 2;
        return new VideoAttributes(mpeg, pal ? "PAL" : "NTSC", aspect, b[0] & 3,
            (b[1] & 0x80) != 0, (b[1] & 0x40) != 0, width, height, (b[1] & 0x02) != 0, (b[1] & 0x01) != 0);
    }
}

/// <summary>Audio stream attributes (8 bytes each, starting at 0x204 in VTS_xx_0.IFO).</summary>
public sealed record AudioAttributes(
    string Coding,            // AC3, MPEG1, MPEG2, LPCM, DTS
    bool MultichannelExtension,
    string? Language,         // ISO 639-1 two-letter code, e.g. "en"
    int SampleRate,
    int Channels,
    int CodeExtension)        // 1 normal, 2 visually impaired, 3 director's comments, 4 alt. comments
{
    public static AudioAttributes Parse(ReadOnlySpan<byte> b)
    {
        string coding = (b[0] >> 5) switch
        {
            0 => "AC3", 2 => "MPEG1", 3 => "MPEG2", 4 => "LPCM", 6 => "DTS", var x => $"?{x}",
        };
        bool hasLang = ((b[0] >> 2) & 3) == 1;
        string? lang = hasLang && b[2] != 0 ? $"{(char)b[2]}{(char)b[3]}" : null;
        int rate = ((b[1] >> 4) & 3) == 1 ? 96000 : 48000;
        return new AudioAttributes(coding, (b[0] & 0x10) != 0, lang, rate, (b[1] & 7) + 1, b[5]);
    }
}

/// <summary>Subpicture (bitmap subtitle) stream attributes (6 bytes each, starting at 0x256).</summary>
public sealed record SubpictureAttributes(
    string? Language,
    int CodeExtension)        // 1 normal, 2 large, 3 children, 5 normal captions, 9 forced, 13 director's comments...
{
    public static SubpictureAttributes Parse(ReadOnlySpan<byte> b)
    {
        bool hasLang = (b[0] & 3) == 1;
        string? lang = hasLang && b[2] != 0 ? $"{(char)b[2]}{(char)b[3]}" : null;
        return new SubpictureAttributes(lang, b[5]);
    }
}
