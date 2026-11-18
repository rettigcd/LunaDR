using System.Buffers.Binary;
using System.Text;

namespace RipDvd.Core.Matroska;

/// <summary>
/// Minimal EBML reader for inspecting Matroska files.
///
/// Every element is: ID (1-4 bytes, a "variable-length integer" that keeps its length-marker
/// bits), size (1-8 bytes, a variable-length integer with the marker bit removed; all ones
/// means "unknown size"), then that many bytes of data. Master elements contain elements.
/// </summary>
public sealed class EbmlReader(Stream stream) {
	public const long UnknownSize = -1;

	public Stream Stream { get; } = stream;

	public readonly record struct Header(uint Id, long Size, long DataStart, int HeaderLength) {
		public long End => Size == UnknownSize ? long.MaxValue : DataStart + Size;
	}

	/// <summary>Reads an element header at the current position, or null at end of stream.</summary>
	public Header? ReadHeader() {
		long start = Stream.Position;
		int first = Stream.ReadByte();
		if (first < 0) return null;
		int idLength = LeadingLength(first);
		if (idLength > 4) throw new InvalidDataException($"Bad element ID at {start}.");
		uint id = (uint)first;
		for (int i = 1; i < idLength; i++) id = (id << 8) | (uint)Stream.ReadByte();

		int sizeFirst = Stream.ReadByte();
		int sizeLength = LeadingLength(sizeFirst);
		if (sizeLength > 8) throw new InvalidDataException($"Bad element size at {start}.");
		long size = sizeFirst & (0xFF >> sizeLength);
		bool allOnes = size == (0xFF >> sizeLength);
		for (int i = 1; i < sizeLength; i++) {
			int b = Stream.ReadByte();
			size = (size << 8) | (uint)b;
			allOnes &= b == 0xFF;
		}
		long dataStart = Stream.Position;
		return new Header(id, allOnes ? UnknownSize : size, dataStart, (int)(dataStart - start));
	}

	public byte[] ReadData(Header h) {
		var data = new byte[h.Size];
		Stream.Position = h.DataStart;
		Stream.ReadExactly(data);
		return data;
	}

	public void Skip(Header h) => Stream.Position = h.End;

	/// <summary>Number of bytes in a variable-length integer, from the position of its first 1 bit.</summary>
	public static int LeadingLength(int firstByte) {
		for (int i = 0; i < 8; i++)
			if ((firstByte & (0x80 >> i)) != 0) return i + 1;
		return 9;
	}

	/// <summary>Reads a variable-length integer (marker removed) from a span; returns value and length.</summary>
	public static (long Value, int Length) ReadVint(ReadOnlySpan<byte> b) {
		int length = LeadingLength(b[0]);
		long value = b[0] & (0xFF >> length);
		for (int i = 1; i < length; i++) value = (value << 8) | b[i];
		return (value, length);
	}

	public static ulong ToUInt(ReadOnlySpan<byte> d) {
		ulong v = 0;
		foreach (byte b in d) v = (v << 8) | b;
		return v;
	}

	public static long ToInt(ReadOnlySpan<byte> d) {
		if (d.Length == 0) return 0;
		long v = (sbyte)d[0];
		for (int i = 1; i < d.Length; i++) v = (v << 8) | d[i];
		return v;
	}

	public static double ToFloat(ReadOnlySpan<byte> d) => d.Length switch {
		4 => BinaryPrimitives.ReadSingleBigEndian(d),
		8 => BinaryPrimitives.ReadDoubleBigEndian(d),
		0 => 0,
		_ => double.NaN,
	};

	public static string ToText(ReadOnlySpan<byte> d) => Encoding.UTF8.GetString(d).TrimEnd('\0');
}
