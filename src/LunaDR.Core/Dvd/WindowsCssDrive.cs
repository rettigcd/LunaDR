using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using LunaDR.Core.Css;

namespace LunaDR.Core.Dvd;

/// <summary>
/// The CSS key commands of a DVD drive on Windows, through DeviceIoControl on the volume (\\.\E:).
/// Windows wraps the MMC REPORT KEY / SEND KEY commands as IOCTL_DVD_START_SESSION / READ_KEY / SEND_KEY / END_SESSION,
/// each taking a DVD_COPY_PROTECT_KEY structure: KeyLength, SessionId, KeyType, KeyFlags (4 bytes each), Parameters (8 bytes),
/// then the key data. Layout and codes from the Windows SDK (ntddcdvd.h).
/// The Linux equivalent would use ioctl(DVD_AUTH) on /dev/sr0 (see the plan's Platform section).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCssDrive : ICssDrive {
	private const uint IoctlDvdStartSession = 0x00335000;   // CTL_CODE(FILE_DEVICE_DVD, 0x400, METHOD_BUFFERED, FILE_READ_ACCESS)
	private const uint IoctlDvdReadKey = 0x00335004;        // 0x401
	private const uint IoctlDvdSendKey = 0x00335008;        // 0x402
	private const uint IoctlDvdEndSession = 0x0033500C;     // 0x403
	private const uint IoctlDvdReadStructure = 0x00335140;  // 0x450

	private const int HeaderLength = 24;

	private enum KeyType { Challenge = 0x01, BusKey1 = 0x02, BusKey2 = 0x03, DiskKey = 0x80 }

	private readonly SafeFileHandle _handle;

	/// <param name="driveLetter">e.g. "E"</param>
	public WindowsCssDrive(string driveLetter) {
		_handle = CreateFile($@"\\.\{driveLetter.TrimEnd(':', '\\')}:", 0x80000000 /* GENERIC_READ */, 3 /* FILE_SHARE_READ | WRITE */,
			IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
		if (_handle.IsInvalid) throw new IOException($"Cannot open drive {driveLetter}: {new Win32Exception().Message}");
	}

	public int StartSession() {
		var buffer = new byte[4];
		Ioctl(IoctlDvdStartSession, buffer, buffer, "start an authentication session");
		return BitConverter.ToInt32(buffer);
	}

	/// <summary>
	/// The disc's copy protection type from its copyright descriptor (IOCTL_DVD_READ_STRUCTURE, format DvdCopyrightDescriptor):
	/// 0 = none, 1 = CSS/CPPM, 2 = CPRM. libdvdcss reads the same byte to decide whether a disc is scrambled.
	/// Input DVD_READ_STRUCTURE: BlockByteOffset (8), Format (4), SessionId (4), LayerNumber (1), padded to 24 bytes.
	/// Output: a 4-byte header, then CopyrightProtectionType, RegionManagementInformation, 2 reserved bytes.
	/// </summary>
	public int ReadCopyrightProtectionType() {
		var input = new byte[24];
		BitConverter.TryWriteBytes(input.AsSpan(8), 1);   // DvdCopyrightDescriptor
		var output = new byte[8];
		Ioctl(IoctlDvdReadStructure, input, output, "read the disc's copyright descriptor");
		return output[4];
	}

	public void EndSession(int agid) => Ioctl(IoctlDvdEndSession, BitConverter.GetBytes(agid), [], "end the authentication session");

	public void SendChallenge(int agid, ReadOnlySpan<byte> challenge) => Send(agid, KeyType.Challenge, challenge, 12);
	public byte[] ReadKey1(int agid) => Read(agid, KeyType.BusKey1, 8)[..5];
	public byte[] ReadChallenge(int agid) => Read(agid, KeyType.Challenge, 12)[..10];
	public void SendKey2(int agid, ReadOnlySpan<byte> key) => Send(agid, KeyType.BusKey2, key, 8);
	public byte[] ReadDiscKey(int agid) => Read(agid, KeyType.DiskKey, 2048);

	private byte[] Read(int agid, KeyType type, int dataLength) {
		var buffer = Header(agid, type, dataLength);
		Ioctl(IoctlDvdReadKey, buffer, buffer, $"read the {type} key");
		return buffer[HeaderLength..];
	}

	private void Send(int agid, KeyType type, ReadOnlySpan<byte> data, int dataLength) {
		var buffer = Header(agid, type, dataLength);
		data.CopyTo(buffer.AsSpan(HeaderLength));
		Ioctl(IoctlDvdSendKey, buffer, buffer, $"send the {type} key");
	}

	private static byte[] Header(int agid, KeyType type, int dataLength) {
		var b = new byte[HeaderLength + dataLength];
		BitConverter.TryWriteBytes(b.AsSpan(0), b.Length);
		BitConverter.TryWriteBytes(b.AsSpan(4), agid);
		BitConverter.TryWriteBytes(b.AsSpan(8), (int)type);
		return b;
	}

	private void Ioctl(uint code, byte[] input, byte[] output, string what) {
		if (!DeviceIoControl(_handle, code, input, input.Length, output, output.Length, out _, IntPtr.Zero))
			throw new IOException($"The DVD drive refused to {what}: {new Win32Exception().Message}");
	}

	public void Dispose() => _handle.Dispose();

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize, byte[] outBuffer, int outSize,
		out int returned, IntPtr overlapped);
}

/// <summary>Unlocks a DVD drive for reading scrambled sectors, where the operating system needs it.</summary>
public static class DriveUnlocker {
	/// <summary>
	/// Whether the disc in the drive holding <paramref name="videoTsFolder"/> says it is CSS-protected (its copyright descriptor);
	/// null when that can't be asked (not an optical drive, not Windows, or the drive refuses), e.g. for a VIDEO_TS folder on a hard disk.
	/// </summary>
	public static bool? IsCssProtected(string videoTsFolder) {
		if (!OperatingSystem.IsWindows()) return null;
		string? root = Path.GetPathRoot(Path.GetFullPath(videoTsFolder));
		if (root is null || root.Length < 2 || root[1] != ':') return null;
		try {
			if (new DriveInfo(root).DriveType != DriveType.CDRom) return null;
			using var drive = new WindowsCssDrive(root[..1]);
			return drive.ReadCopyrightProtectionType() != 0;
		} catch (IOException) {
			return null;
		}
	}

	/// <summary>
	/// If <paramref name="videoTsFolder"/> is on an optical drive (Windows), run the CSS handshake.
	/// Returns null on success or when not needed, else a message describing why it failed (reading may still work).
	/// </summary>
	public static string? TryUnlock(string videoTsFolder) {
		if (!OperatingSystem.IsWindows()) return null;   // Linux: ioctl(DVD_AUTH) on /dev/sr0, not written yet
		string? root = Path.GetPathRoot(Path.GetFullPath(videoTsFolder));
		if (root is null || root.Length < 2 || root[1] != ':') return null;
		try {
			if (new DriveInfo(root).DriveType != DriveType.CDRom) return null;
			using var drive = new WindowsCssDrive(root[..1]);
			CssAuthentication.Unlock(drive);
			return null;
		} catch (IOException ex) {
			return ex.Message;
		}
	}
}
