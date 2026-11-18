using System.Buffers.Binary;

namespace RipDvd.Core.Ifo;

/// <summary>
/// Reads big-endian numbers out of an IFO file. All multi-byte numbers in DVD IFO files
/// are big-endian. Offsets are byte offsets into the whole file.
/// </summary>
internal sealed class BigEndianReader(byte[] data) {
	public const int SectorSize = 2048;

	public byte[] Data { get; } = data;

	public byte U8(int offset) => Data[offset];
	public ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(offset, 2));
	public uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(offset, 4));
	public ReadOnlySpan<byte> Bytes(int offset, int length) => Data.AsSpan(offset, length);

	public string Ascii(int offset, int length) =>
		System.Text.Encoding.ASCII.GetString(Data, offset, length).TrimEnd('\0', ' ');

	/// <summary>Many IFO tables are located by a sector number relative to the start of the IFO.</summary>
	public static int SectorToOffset(uint sector) => checked((int)sector * SectorSize);
}
