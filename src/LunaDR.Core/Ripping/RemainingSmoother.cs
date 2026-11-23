namespace LunaDR.Core.Ripping;

/// <summary>
/// Calms the "time left" shown to the user.
/// A new estimate (sectors left ÷ speed) jumps around as the speed changes from second to second.
/// Instead of showing it directly, the shown value counts down with the clock and is pulled towards each new estimate:
/// shown = (shown before − time passed) + a share of the difference to the new estimate,
/// the share growing with the time since the last one (<see cref="TimeConstant"/> is the time in which it closes about 63% of a difference).
/// </summary>
internal sealed class RemainingSmoother {
	/// <summary>How slowly the shown value follows the estimates.</summary>
	public static readonly TimeSpan TimeConstant = TimeSpan.FromSeconds(30);

	private TimeSpan? _shown;
	private TimeSpan _at;

	/// <summary>
	/// The value to show at <paramref name="now"/> (any steadily running clock, e.g. the reading time) for the new estimate <paramref name="estimate"/>.
	/// Null in, null out (not enough measured yet); an estimate of zero (finished) is shown as zero at once.
	/// </summary>
	public TimeSpan? Next(TimeSpan now, TimeSpan? estimate) {
		if (estimate is not { } e) {
			_shown = null;
			return null;
		}
		if (_shown is not { } shown || e <= TimeSpan.Zero) {
			_shown = e;
			_at = now;
			return e;
		}
		double passed = Math.Max(0, (now - _at).TotalSeconds);
		double predicted = Math.Max(0, shown.TotalSeconds - passed);
		double share = 1 - Math.Exp(-passed / TimeConstant.TotalSeconds);
		_shown = TimeSpan.FromSeconds(predicted + share * (e.TotalSeconds - predicted));
		_at = now;
		return _shown;
	}
}
