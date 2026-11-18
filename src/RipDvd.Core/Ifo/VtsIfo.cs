namespace RipDvd.Core.Ifo;

/// <summary>
/// VTS_xx_0.IFO: a Video Title Set. Describes the streams (video/audio/subpicture
/// attributes), the program chains (PGCs) that say which cells to play in which order,
/// and the chapter table (PTT_SRPT) that maps chapters to programs inside PGCs.
/// </summary>
public sealed class VtsIfo
{
    public required int TitleSetNumber { get; init; }
    public required int Version { get; init; }
    public required uint LastSectorOfTitleSet { get; init; }
    public required uint TitleVobsStartSector { get; init; }   // relative to start of the VTS
    public required VideoAttributes Video { get; init; }
    public required IReadOnlyList<AudioAttributes> Audio { get; init; }
    public required IReadOnlyList<SubpictureAttributes> Subpictures { get; init; }
    /// <summary>Per VTS title (TTN, 1-based index into this list + 1): its chapters.</summary>
    public required IReadOnlyList<IReadOnlyList<PartOfTitle>> Chapters { get; init; }
    public required IReadOnlyList<Pgc> Pgcs { get; init; }

    public static VtsIfo Parse(byte[] ifo, int titleSetNumber)
    {
        var r = new BigEndianReader(ifo);
        string id = r.Ascii(0x00, 12);
        if (id != "DVDVIDEO-VTS") throw new InvalidDataException($"Not a VTS IFO (identifier '{id}').");

        var audio = new List<AudioAttributes>();
        int audioCount = r.U16(0x202);
        for (int i = 0; i < audioCount; i++) audio.Add(AudioAttributes.Parse(r.Bytes(0x204 + i * 8, 8)));

        var subs = new List<SubpictureAttributes>();
        int subCount = r.U16(0x254);
        for (int i = 0; i < subCount; i++) subs.Add(SubpictureAttributes.Parse(r.Bytes(0x256 + i * 6, 6)));

        return new VtsIfo
        {
            TitleSetNumber = titleSetNumber,
            Version = r.U16(0x20),
            LastSectorOfTitleSet = r.U32(0x0C),
            TitleVobsStartSector = r.U32(0xC4),
            Video = VideoAttributes.Parse(r.Bytes(0x200, 2)),
            Audio = audio,
            Subpictures = subs,
            Chapters = ParsePttSrpt(r, BigEndianReader.SectorToOffset(r.U32(0xC8))),
            Pgcs = ParsePgcit(r, BigEndianReader.SectorToOffset(r.U32(0xCC))),
        };
    }

    /// <summary>PTT_SRPT: for each title in this VTS, a list of (PGC number, program number) = chapters.</summary>
    private static List<IReadOnlyList<PartOfTitle>> ParsePttSrpt(BigEndianReader r, int start)
    {
        int titleCount = r.U16(start);
        int endAddress = (int)r.U32(start + 4);          // last byte of the table, relative to start
        var offsets = new int[titleCount];
        for (int i = 0; i < titleCount; i++) offsets[i] = (int)r.U32(start + 8 + i * 4);

        var result = new List<IReadOnlyList<PartOfTitle>>(titleCount);
        for (int i = 0; i < titleCount; i++)
        {
            int from = offsets[i];
            int to = i + 1 < titleCount ? offsets[i + 1] : endAddress + 1;
            var list = new List<PartOfTitle>();
            for (int p = from; p + 4 <= to; p += 4)
                list.Add(new PartOfTitle(r.U16(start + p), r.U16(start + p + 2)));
            result.Add(list);
        }
        return result;
    }

    /// <summary>PGCIT: the table of program chains used by titles in this VTS.</summary>
    private static List<Pgc> ParsePgcit(BigEndianReader r, int start)
    {
        int count = r.U16(start);
        var pgcs = new List<Pgc>(count);
        for (int i = 0; i < count; i++)
        {
            int e = start + 8 + i * 8;
            byte category = r.U8(e);
            int pgcOffset = start + (int)r.U32(e + 4);
            pgcs.Add(Pgc.Parse(r, pgcOffset, i + 1,
                isEntry: (category & 0x80) != 0,
                titleNumber: category & 0x7F));
        }
        return pgcs;
    }
}

/// <summary>A chapter: start at program <see cref="ProgramNumber"/> of PGC <see cref="PgcNumber"/>.</summary>
public readonly record struct PartOfTitle(int PgcNumber, int ProgramNumber);
