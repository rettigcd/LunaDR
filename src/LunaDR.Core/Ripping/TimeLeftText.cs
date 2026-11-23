namespace LunaDR.Core.Ripping;

/// <summary>
/// The "time left" as text in whole seconds (rounded, no fractions): "3:21:20", "12:05", "0:45"; null (not measured yet) is "estimating".
/// </summary>
public static class TimeLeftText {
	/// <summary>When the job should finish, as a 12-hour clock time with AM / PM (the day name uses <paramref name="culture"/>): "2:35 PM", "tomorrow 1:15 AM", "Mon 1:15 AM"; null while there is no estimate.</summary>
	public static string? FinishText(DateTime now, TimeSpan? left, IFormatProvider? culture = null) {
		if (left is not { } t) return null;
		var finish = now + t;
		string time = finish.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);   // always 12-hour with AM / PM, whatever the PC's time format
		int days = (finish.Date - now.Date).Days;
		return days <= 0 ? time : days == 1 ? "tomorrow " + time : finish.ToString("ddd ", culture) + time;
	}

	public static string Format(TimeSpan? left) {
		if (left is not { } t) return "estimating";
		long seconds = (long)Math.Round(Math.Max(0, t.TotalSeconds), MidpointRounding.AwayFromZero);
		return seconds >= 3600 ? $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
	}
}
