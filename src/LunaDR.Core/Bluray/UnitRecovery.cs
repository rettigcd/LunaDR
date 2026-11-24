using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LunaDR.Core.Bluray;

/// <summary>
/// Decrypts a unit; if that fails because the drive handed back corrupted data (a damaged disc often does so without any read error),
/// reads the unit again straight from the disc, up to <see cref="Attempts"/> times, and keeps the first read that decrypts.
/// </summary>
internal static class UnitRecovery {
	public const int Attempts = 3;

	/// <summary>
	/// After this many units in a row that failed all their re-reads, re-reading is given up for the rest of the rip,
	/// because each re-read of a bad spot is slow and it clearly does not help.
	/// </summary>
	public const int GiveUpAfter = 50;

	/// <summary>
	/// Decrypts the unit in place; <paramref name="reread"/> fills it with a fresh read from the disc and returns false if the read failed.
	/// Returns false if the unit could not be decrypted (it is then garbage and must be dropped).
	/// </summary>
	public static bool Decrypt(AacsDecryptor aacs, Span<byte> unit, long position, ReadStats health, Func<long, Span<byte>, bool> reread, Action retrying) {
		if (aacs.DecryptUnit(unit)) return true;
		if (!health.GaveUpRereading) {
			for (int attempt = 1; attempt <= Attempts; attempt++) {
				health.RetryPosition = position;
				health.RetryAttempt = attempt;
				retrying();
				if (reread(position, unit) && unit.IndexOfAnyExcept((byte)0) >= 0 && aacs.DecryptUnit(unit)) {
					health.RetryAttempt = 0;
					health.Recovered++;
					health.FailedInARow = 0;
					return true;
				}
			}
			health.RetryAttempt = 0;
			if (++health.FailedInARow >= GiveUpAfter) health.GaveUpRereading = true;
		}
		health.Undecryptable++;
		return false;
	}
}

/// <summary>
/// Reads single units straight from the disc, past the Windows file cache (which would hand back the same corrupted bytes again).
/// The file is opened on the first read.
/// </summary>
internal sealed class UnbufferedFile(string path) : IDisposable {
	private const FileOptions NoBuffering = (FileOptions)0x20000000;   // FILE_FLAG_NO_BUFFERING

	private SafeFileHandle? _file;
	private readonly byte[] _raw = GC.AllocateUninitializedArray<byte>(AacsDecryptor.UnitSize + 4096, pinned: true);   // needs an aligned buffer

	public bool TryRead(long position, Span<byte> destination) {
		try {
			_file ??= File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, OperatingSystem.IsWindows() ? NoBuffering : FileOptions.None);
			long address = Marshal.UnsafeAddrOfPinnedArrayElement(_raw, 0);
			var buffer = _raw.AsSpan((int)((4096 - address % 4096) % 4096), AacsDecryptor.UnitSize);
			int got = 0;
			while (got < buffer.Length) {
				int n = RandomAccess.Read(_file, buffer[got..], position + got);
				if (n == 0) return false;
				got += n;
			}
			buffer.CopyTo(destination);
			return true;
		} catch (IOException) {
			return false;
		}
	}

	public void Dispose() => _file?.Dispose();
}
