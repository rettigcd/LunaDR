using System.Text;

namespace RipDvd.Core.Ripping;

/// <summary>
/// Builds the CodecPrivate of a Matroska S_VOBSUB track: the header part of a VobSub .idx file, as text.
/// Players need the frame size and the 16-color palette; the palette comes from the title's PGC, which stores colors as YCrCb.
/// </summary>
public static class VobSubHeader {
	public static string Build(int width, int height, IReadOnlyList<uint> pgcPalette) {
		var sb = new StringBuilder();
		sb.Append($"size: {width}x{height}\n");
		sb.Append("palette: ");
		sb.Append(string.Join(", ", pgcPalette.Select(c => YCrCbToRgbHex(c))));
		sb.Append('\n');
		return sb.ToString();
	}

	/// <summary>PGC palette entry 0x00YYCrCb → "rrggbb" using the ITU-R BT.601 conversion.</summary>
	public static string YCrCbToRgbHex(uint entry) {
		double y = (entry >> 16) & 0xFF;
		double cr = ((entry >> 8) & 0xFF) - 128.0;
		double cb = (entry & 0xFF) - 128.0;
		int r = Clamp(y + 1.402 * cr);
		int g = Clamp(y - 0.344136 * cb - 0.714136 * cr);
		int b = Clamp(y + 1.772 * cb);
		return $"{r:x2}{g:x2}{b:x2}";
	}

	private static int Clamp(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);
}
