using LunaDR.Core.Bluray;

namespace LunaDR.Core.Tests;

/// <summary>Spotting a BD+ disc (a BDSVM folder with BD+ code), whose video is deliberately damaged until the BD+ virtual machine repairs it.</summary>
public class BdPlusTests {
	private static string MakeDisc(Action<string>? fill = null) {
		string root = Path.Combine(Path.GetTempPath(), "bdplus-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(root, "BDMV"));
		fill?.Invoke(root);
		return root;
	}

	private static void WriteSvm(string root, string name, byte[] content) {
		Directory.CreateDirectory(Path.Combine(root, "BDSVM"));
		File.WriteAllBytes(Path.Combine(root, "BDSVM", name), content);
	}

	[Fact]
	public void BdsvmFilesStartingWithTheBdPlusMagic_AreBdPlus() {
		string root = MakeDisc(r => WriteSvm(r, "00000.svm", [.. "BDSVM_CC"u8, 1, 0, 0, 0x20, 0, 7]));
		try {
			Assert.True(BdScanner.HasBdPlus(root));
		} finally {
			Directory.Delete(root, true);
		}
	}

	[Fact]
	public void AnyOfTheSvmFilesCounts() {
		string root = MakeDisc(r => {
			WriteSvm(r, "00000.svm", new byte[40]);
			WriteSvm(r, "00001.svm", [.. "BDSVM_CC"u8, 1]);
		});
		try {
			Assert.True(BdScanner.HasBdPlus(root));
		} finally {
			Directory.Delete(root, true);
		}
	}

	[Fact]
	public void NoBdsvmFolder_OrOtherContent_IsNotBdPlus() {
		string none = MakeDisc();
		string other = MakeDisc(r => WriteSvm(r, "00000.svm", "something else entirely"u8.ToArray()));
		string tiny = MakeDisc(r => WriteSvm(r, "00000.svm", "BDS"u8.ToArray()));
		try {
			Assert.False(BdScanner.HasBdPlus(none));
			Assert.False(BdScanner.HasBdPlus(other));
			Assert.False(BdScanner.HasBdPlus(tiny));
			Assert.False(BdScanner.HasBdPlus(Path.Combine(none, "does-not-exist")));
		} finally {
			foreach (var d in new[] { none, other, tiny }) Directory.Delete(d, true);
		}
	}
}
