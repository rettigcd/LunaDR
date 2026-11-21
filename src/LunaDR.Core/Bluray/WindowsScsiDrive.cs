using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace LunaDR.Core.Bluray;

/// <summary>
/// Sends MMC commands (INQUIRY, GET CONFIGURATION) to an optical drive on Windows with IOCTL_SCSI_PASS_THROUGH on the volume (\\.\E:).
/// Used by the probe to see what a Blu-ray drive reports (BD read, AACS version, bus encryption).
/// The SCSI_PASS_THROUGH layout (x64) is from the Windows SDK (ntddscsi.h): 56 bytes, then our sense and data buffers.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsScsiDrive : IDisposable {
	private const uint IoctlScsiPassThrough = 0x0004D004;   // CTL_CODE(IOCTL_SCSI_BASE, 0x401, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
	private const int HeaderSize = 56, SenseSize = 32, DataOffset = HeaderSize + SenseSize;

	private readonly SafeFileHandle _handle;

	/// <param name="driveLetter">e.g. "E"</param>
	public WindowsScsiDrive(string driveLetter) {
		_handle = CreateFile($@"\\.\{driveLetter.TrimEnd(':', '\\')}:", 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */, 3 /* FILE_SHARE_READ | WRITE */,
			IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
		if (_handle.IsInvalid) throw new IOException($"Cannot open drive {driveLetter}: {new Win32Exception().Message}");
	}

	/// <summary>INQUIRY: vendor, product and firmware revision.</summary>
	public (string Vendor, string Product, string Revision) Inquiry() {
		var data = Command([0x12, 0, 0, 0, 96, 0], 96);
		string Text(int from, int length) => System.Text.Encoding.ASCII.GetString(data, from, length).Trim();
		return (Text(8, 8), Text(16, 16), Text(32, 4));
	}

	/// <summary>
	/// GET CONFIGURATION (RT = 0: all features): the current profile (media type) and each feature descriptor (code, current flag, data).
	/// </summary>
	public (int CurrentProfile, List<(int Code, bool Current, byte[] Data)> Features) GetConfiguration() {
		const int size = 65_530;
		var data = Command([0x46, 0, 0, 0, 0, 0, 0, size >> 8, size & 0xFF, 0], size);
		int length = (data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]) + 4;
		int profile = data[6] << 8 | data[7];
		var features = new List<(int, bool, byte[])>();
		for (int p = 8; p + 4 <= Math.Min(length, data.Length);) {
			int code = data[p] << 8 | data[p + 1];
			bool current = (data[p + 2] & 1) != 0;
			int n = data[p + 3];
			features.Add((code, current, data.AsSpan(p + 4, Math.Min(n, data.Length - p - 4)).ToArray()));
			p += 4 + n;
		}
		return (profile, features);
	}

	/// <summary>Runs one data-in command and returns the data buffer; throws with the sense data when the drive reports an error.</summary>
	public byte[] Command(byte[] cdb, int dataLength) {
		var buffer = new byte[DataOffset + dataLength];
		BitConverter.TryWriteBytes(buffer.AsSpan(0), (ushort)HeaderSize);   // Length
		buffer[6] = (byte)cdb.Length;                                      // CdbLength
		buffer[7] = SenseSize;                                             // SenseInfoLength
		buffer[8] = 1;                                                     // DataIn = SCSI_IOCTL_DATA_IN
		BitConverter.TryWriteBytes(buffer.AsSpan(12), dataLength);         // DataTransferLength
		BitConverter.TryWriteBytes(buffer.AsSpan(16), 20);                 // TimeOutValue (s)
		BitConverter.TryWriteBytes(buffer.AsSpan(24), (long)DataOffset);   // DataBufferOffset
		BitConverter.TryWriteBytes(buffer.AsSpan(32), HeaderSize);         // SenseInfoOffset
		cdb.CopyTo(buffer, 36);
		if (!DeviceIoControl(_handle, IoctlScsiPassThrough, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero))
			throw new IOException($"SCSI command 0x{cdb[0]:X2} failed: {new Win32Exception().Message}");
		byte status = buffer[2];   // ScsiStatus
		if (status != 0) {
			int key = buffer[HeaderSize + 2] & 0x0F, asc = buffer[HeaderSize + 12], ascq = buffer[HeaderSize + 13];
			throw new IOException($"SCSI command 0x{cdb[0]:X2}: status {status}, sense key {key:X}, ASC/ASCQ {asc:X2}/{ascq:X2}");
		}
		return buffer[DataOffset..];
	}

	public void Dispose() => _handle.Dispose();

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize, byte[] outBuffer, int outSize,
		out int returned, IntPtr overlapped);
}
