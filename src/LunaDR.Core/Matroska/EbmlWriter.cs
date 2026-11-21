using System.Buffers.Binary;
using System.Text;

namespace LunaDR.Core.Matroska;

/// <summary>
/// Writes EBML elements to a stream. Master elements are usually built in a
/// <see cref="MemoryStream"/> first so their size is known, then written with
/// <see cref="Master(uint, ReadOnlySpan{byte})"/>.
/// </summary>
public sealed class EbmlWriter(Stream stream) {
	public Stream Stream { get; } = stream;

	public long Position => Stream.Position;

	/// <summary>Writes an element ID (IDs already include their length-marker bits).</summary>
	public void Id(uint id) {
		if (id > 0xFFFFFF) Stream.WriteByte((byte)(id >> 24));
		if (id > 0xFFFF) Stream.WriteByte((byte)(id >> 16));
		if (id > 0xFF) Stream.WriteByte((byte)(id >> 8));
		Stream.WriteByte((byte)id);
	}

	/// <summary>Writes a size as a variable-length integer, in the fewest bytes (or exactly <paramref name="length"/>).</summary>
	public void Size(long size, int length = 0) {
		if (length == 0) {
			length = 1;
			// A length-n vint holds up to 2^(7n) - 2 (all ones is reserved for "unknown").
			while (length < 8 && size >= (1L << (7 * length)) - 1) length++;
		}
		Span<byte> b = stackalloc byte[8];
		long v = size | (1L << (7 * length));   // set the length-marker bit
		for (int i = length - 1; i >= 0; i--) { b[i] = (byte)v; v >>= 8; }
		Stream.Write(b[..length]);
	}

	public void UInt(uint id, ulong value) {
		Span<byte> b = stackalloc byte[8];
		BinaryPrimitives.WriteUInt64BigEndian(b, value);
		int skip = 0;
		while (skip < 7 && b[skip] == 0) skip++;
		Binary(id, b[skip..]);
	}

	public void Int(uint id, long value) {
		Span<byte> b = stackalloc byte[8];
		BinaryPrimitives.WriteInt64BigEndian(b, value);
		int skip = 0;
		// Drop leading bytes that are pure sign extension.
		while (skip < 7 && ((b[skip] == 0 && (b[skip + 1] & 0x80) == 0) || (b[skip] == 0xFF && (b[skip + 1] & 0x80) != 0))) skip++;
		Binary(id, b[skip..]);
	}

	public void Float(uint id, double value) {
		Span<byte> b = stackalloc byte[8];
		BinaryPrimitives.WriteDoubleBigEndian(b, value);
		Binary(id, b);
	}

	public void String(uint id, string value) => Binary(id, Encoding.UTF8.GetBytes(value));

	/// <summary>Matroska dates are nanoseconds since 2001-01-01T00:00:00 UTC.</summary>
	public void Date(uint id, DateTime utc) {
		long ns = (utc - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks * 100;
		Span<byte> b = stackalloc byte[8];
		BinaryPrimitives.WriteInt64BigEndian(b, ns);
		Binary(id, b);
	}

	public void Binary(uint id, ReadOnlySpan<byte> data) {
		Id(id);
		Size(data.Length);
		Stream.Write(data);
	}

	public void Master(uint id, ReadOnlySpan<byte> content) => Binary(id, content);

	/// <summary>Builds a master element's content with a nested writer, then writes it.</summary>
	public void Master(uint id, Action<EbmlWriter> build) {
		var ms = new MemoryStream();
		build(new EbmlWriter(ms));
		Master(id, ms.GetBuffer().AsSpan(0, (int)ms.Length));
	}

	/// <summary>Writes a Void element occupying exactly <paramref name="totalBytes"/> bytes (at least 2).</summary>
	public void Void(int totalBytes) {
		if (totalBytes < 2) throw new ArgumentOutOfRangeException(nameof(totalBytes));
		// ID (1 byte) + size; use an 8-byte size field when there's room so the math is simple.
		int sizeLength = totalBytes >= 9 ? 8 : 1;
		int dataLength = totalBytes - 1 - sizeLength;
		Id(MatroskaIds.Void);
		Size(dataLength, sizeLength);
		Stream.Write(new byte[dataLength]);
	}
}
