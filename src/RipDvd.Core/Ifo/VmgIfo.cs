namespace RipDvd.Core.Ifo;

/// <summary>
/// VIDEO_TS.IFO: the Video Manager. Holds the disc-wide table of titles (TT_SRPT),
/// which says, for each title the player can show, which title set (VTS) holds it.
/// </summary>
public sealed class VmgIfo
{
    public required string Identifier { get; init; }        // "DVDVIDEO-VMG"
    public required int Version { get; init; }
    public required int NumberOfTitleSets { get; init; }
    public required string ProviderId { get; init; }
    public required IReadOnlyList<TitleEntry> Titles { get; init; }

    public static VmgIfo Parse(byte[] ifo)
    {
        var r = new BigEndianReader(ifo);
        string id = r.Ascii(0x00, 12);
        if (id != "DVDVIDEO-VMG") throw new InvalidDataException($"Not a VMG IFO (identifier '{id}').");

        // Table of titles (TT_SRPT): sector given at 0xC4.
        int tt = BigEndianReader.SectorToOffset(r.U32(0xC4));
        int count = r.U16(tt);
        var titles = new List<TitleEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int e = tt + 8 + i * 12;
            titles.Add(new TitleEntry
            {
                TitleNumber = i + 1,
                PlaybackType = r.U8(e),
                NumberOfAngles = r.U8(e + 1),
                NumberOfChapters = r.U16(e + 2),
                ParentalMask = r.U16(e + 4),
                TitleSetNumber = r.U8(e + 6),
                TitleSetTitleNumber = r.U8(e + 7),
                TitleSetStartSector = r.U32(e + 8),
            });
        }

        return new VmgIfo
        {
            Identifier = id,
            Version = r.U16(0x20),
            NumberOfTitleSets = r.U16(0x3E),
            ProviderId = r.Ascii(0x40, 32),
            Titles = titles,
        };
    }
}

/// <summary>One row of TT_SRPT: a title as the DVD player's "title" button sees it.</summary>
public sealed class TitleEntry
{
    public int TitleNumber { get; init; }           // 1-based, disc-wide
    public byte PlaybackType { get; init; }
    public int NumberOfAngles { get; init; }
    public int NumberOfChapters { get; init; }      // "parts of title" (PTTs)
    public ushort ParentalMask { get; init; }
    public int TitleSetNumber { get; init; }        // which VTS_xx
    public int TitleSetTitleNumber { get; init; }   // title number inside that VTS (TTN)
    public uint TitleSetStartSector { get; init; }  // absolute sector of VTS_xx_0.IFO on the disc
}
