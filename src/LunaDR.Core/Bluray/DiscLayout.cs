using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LunaDR.Core.Bluray;

/// <summary>
/// Where a clip file lies on the disc: the pieces ("extents") of the file and the disc sector each starts at, as Windows reports them.
/// A drive's speed depends on where on the disc it reads (it is faster towards the outer edge of each layer),
/// so the progress log records this to let the "time left" estimate be tuned to the disc's layout.
/// </summary>
internal static class DiscLayout {
	/// <summary>One piece of a file: it starts at <see cref="FileSector"/> of the file and at <see cref="DiscSector"/> of the disc, and is <see cref="Sectors"/> long.</summary>
	public readonly record struct Extent(long FileSector, long DiscSector, long Sectors);

	private const uint FsctlGetRetrievalPointers = 0x00090073;
	private const int ErrorMoreData = 234;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref long input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

	/// <summary>
	/// The extents of a file, or null where Windows cannot tell (another system, or a drive that does not say).
	/// On a UDF disc one allocation unit is one sector of 2048 bytes.
	/// </summary>
	public static List<Extent>? Extents(string path) {
		if (!OperatingSystem.IsWindows()) return null;
		try {
			using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			var extents = new List<Extent>();
			var buffer = new byte[64 * 1024];
			long start = 0;
			while (true) {
				bool ok = DeviceIoControl(file, FsctlGetRetrievalPointers, ref start, sizeof(long), buffer, buffer.Length, out _, IntPtr.Zero);
				if (!ok && Marshal.GetLastWin32Error() != ErrorMoreData) return extents.Count > 0 ? extents : null;
				int count = BitConverter.ToInt32(buffer, 0);
				long previous = BitConverter.ToInt64(buffer, 8);   // StartingVcn
				for (int i = 0; i < count; i++) {
					long next = BitConverter.ToInt64(buffer, 16 + i * 16);
					long lcn = BitConverter.ToInt64(buffer, 24 + i * 16);
					if (lcn >= 0) extents.Add(new Extent(previous, lcn, next - previous));
					previous = next;
				}
				if (ok || count == 0) return extents;
				start = previous;
			}
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			return null;
		}
	}

	/// <summary>A line for the progress log: the volume's size and the clip's extents as file-sector:disc-sector:length.</summary>
	public static string Describe(string discRoot, string clipPath) {
		string volume = "unknown";
		try {
			volume = $"{new DriveInfo(Path.GetPathRoot(Path.GetFullPath(discRoot))!).TotalSize / 2048} sectors";
		} catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) {
		}
		var extents = Extents(clipPath);
		string pieces = extents is null ? "unavailable" : string.Join(" ", extents.Select(e => $"{e.FileSector}:{e.DiscSector}:{e.Sectors}"));
		return $"disc layout: volume {volume}; clip {Path.GetFileName(clipPath)} extents (file sector:disc sector:length) {pieces}";
	}
}
