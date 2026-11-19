using System.IO;
using RipDvd.Core.Ripping;

namespace RipDvd.Gui.ViewModels;

/// <summary>One finished title in the progress view: the file written (with its length and any warnings) or the error.</summary>
public sealed class ResultRow(RipJobItemResult result) {
	public bool Succeeded => result.Error is null;
	public string Icon => Succeeded ? "✓" : "✗";

	public string Text {
		get {
			if (result.Error is { } error) return $"{result.Item.Name} failed: {error.Message}";
			var r = result.Result!;
			var length = TimeSpan.FromTicks(r.DurationNs / 100);
			string text = $"{Path.GetFileName(r.OutputPath)}   ({(int)length.TotalHours}:{length.Minutes:00}:{length.Seconds:00}, ripped in {r.Elapsed.TotalSeconds:F0} s)";
			return r.Warnings.Count == 0 ? text : $"{text}   warning: {string.Join("; ", r.Warnings)}";
		}
	}
}
