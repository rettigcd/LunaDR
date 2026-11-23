namespace LunaDR.Core.Ripping;

/// <summary>
/// The "time left" as text in whole seconds (rounded, no fractions): "3:21:20", "12:05", "0:45"; null (not measured yet) is "estimating".
/// </summary>
public static class TimeLeftText {
	public static string Format(TimeSpan? left) {
		if (left is not { } t) return "estimating";
		long seconds = (long)Math.Round(Math.Max(0, t.TotalSeconds), MidpointRounding.AwayFromZero);
		return seconds >= 3600 ? $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
	}
}
