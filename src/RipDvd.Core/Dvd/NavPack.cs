using System.Buffers.Binary;

namespace RipDvd.Core.Dvd;

/// <summary>
/// The navigation pack that starts every VOBU (Video Object Unit, ~0.4–1 s of video).
/// It is never scrambled. Layout of the 2048-byte sector:
///   0x000  pack header (14 bytes)
///   0x00E  system header (00 00 01 BB, 24 bytes)
///   0x026  PCI packet  (00 00 01 BF, substream 0x00 at 0x2C, PCI data from 0x2D)
///   0x400  DSI packet  (00 00 01 BF, substream 0x01 at 0x406, DSI data from 0x407)
/// </summary>
public sealed class NavPack
{
    private const int Pci = 0x2D;
    private const int Dsi = 0x407;

    /// <summary>Sector number of this nav pack, relative to the title VOBs (from PCI).</summary>
    public uint Lbn { get; init; }
    /// <summary>Presentation start/end time of the VOBU's video, 90 kHz ticks.</summary>
    public uint StartPtm { get; init; }
    public uint EndPtm { get; init; }
    /// <summary>Last sector of this VOBU, relative to this nav pack.</summary>
    public uint VobuEndAddress { get; init; }
    public int VobId { get; init; }
    public int CellId { get; init; }

    public static bool IsNavPack(ReadOnlySpan<byte> s) =>
        s[0] == 0 && s[1] == 0 && s[2] == 1 && s[3] == 0xBA &&
        s[0x26] == 0 && s[0x27] == 0 && s[0x28] == 1 && s[0x29] == 0xBF && s[0x2C] == 0x00 &&
        s[0x400] == 0 && s[0x401] == 0 && s[0x402] == 1 && s[0x403] == 0xBF && s[0x406] == 0x01;

    public static NavPack Parse(ReadOnlySpan<byte> s)
    {
        if (!IsNavPack(s)) throw new InvalidDataException("Not a navigation pack.");
        return new NavPack
        {
            Lbn = BinaryPrimitives.ReadUInt32BigEndian(s[(Pci + 0)..]),
            StartPtm = BinaryPrimitives.ReadUInt32BigEndian(s[(Pci + 12)..]),
            EndPtm = BinaryPrimitives.ReadUInt32BigEndian(s[(Pci + 16)..]),
            VobuEndAddress = BinaryPrimitives.ReadUInt32BigEndian(s[(Dsi + 8)..]),
            // DSI_GI: scr(4) lbn(4) vobu_ea(4) 1stref_ea(4) 2ndref_ea(4) 3rdref_ea(4) vobu_vob_idn(2) reserved(1) vobu_c_idn(1) c_eltm(4)
            VobId = BinaryPrimitives.ReadUInt16BigEndian(s[(Dsi + 24)..]),
            CellId = s[Dsi + 27],
        };
    }
}
