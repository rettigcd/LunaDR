namespace RipDvd.Core.Ripping;

/// <summary>One title to rip as part of a job. <see cref="Sectors"/> (its size on the disc) weights the progress and time estimates.</summary>
public sealed record RipJobItem(int TitleNumber, string OutputPath, long Sectors);

/// <summary>
/// Progress of a job: the title being ripped (index of count) and its own progress, plus the job as a whole.
/// Remaining times are null until enough has been read to measure the drive's speed.
/// </summary>
public sealed record RipJobProgress(
	int Index, int Count, RipJobItem Item, RipProgress Title,
	double TitleFraction, TimeSpan? TitleRemaining,
	double OverallFraction, TimeSpan? OverallRemaining,
	double BytesPerSecond);

/// <summary>What happened to one item: its result, or the error that stopped it.</summary>
public sealed record RipJobItemResult(RipJobItem Item, RipResult? Result, Exception? Error);

/// <summary>
/// Rips a list of titles in order and reports progress for the current title and for the whole job.
///
/// Remaining time = sectors still to read ÷ the measured read speed.
/// The speed counts only the time spent reading (the Ripping phase), not drive unlocking and key finding,
/// and is measured over the whole job so far, so it settles after the first seconds.
/// A title that fails is recorded and the job continues with the next one; cancelling stops the job.
/// </summary>
public sealed class RipJob(string videoTsFolder, IReadOnlyList<RipJobItem> items, RipOptions? options = null) {
	/// <summary>Minimum reading time before a speed (and so a remaining time) is reported.</summary>
	private static readonly TimeSpan MinimumMeasureTime = TimeSpan.FromSeconds(3);

	public IReadOnlyList<RipJobItem> Items { get; } = items;

	/// <summary>
	/// Runs the job. Returns one result per item that was started; on cancellation the current title's partial file is removed
	/// and <see cref="OperationCanceledException"/> is thrown.
	/// </summary>
	public List<RipJobItemResult> Run(Action<RipJobProgress>? progress = null, CancellationToken cancel = default) {
		var results = new List<RipJobItemResult>();
		long allSectors = Items.Sum(i => i.Sectors);
		long finishedSectors = 0;                  // sectors of the titles already done (or failed)
		long sectorsReadBefore = 0;                // sectors read while ripping earlier titles
		TimeSpan readingTimeBefore = TimeSpan.Zero;  // time spent reading earlier titles

		for (int index = 0; index < Items.Count; index++) {
			cancel.ThrowIfCancellationRequested();
			var item = Items[index];
			TimeSpan? readingStartedAt = null;     // Elapsed of this title when its Ripping phase began
			RipProgress? last = null;

			void OnProgress(RipProgress p) {
				last = p;
				if (p.Phase == RipPhase.Ripping && readingStartedAt is null) readingStartedAt = p.Elapsed;
				TimeSpan reading = readingTimeBefore + (readingStartedAt is { } s ? p.Elapsed - s : TimeSpan.Zero);
				long read = sectorsReadBefore + p.SectorsDone;
				double sectorsPerSecond = reading >= MinimumMeasureTime && read > 0 ? read / reading.TotalSeconds : 0;

				long titleTotal = p.SectorsTotal > 0 ? p.SectorsTotal : item.Sectors;
				double titleFraction = p.Phase == RipPhase.Done ? 1 : titleTotal == 0 ? 0 : (double)p.SectorsDone / titleTotal;
				long overallDone = finishedSectors + (long)(titleFraction * item.Sectors);
				TimeSpan? Remaining(long sectors) => sectorsPerSecond > 0 ? TimeSpan.FromSeconds(Math.Max(0, sectors) / sectorsPerSecond) : null;

				progress?.Invoke(new RipJobProgress(
					index, Items.Count, item, p,
					titleFraction, Remaining(titleTotal - p.SectorsDone),
					allSectors == 0 ? 0 : (double)overallDone / allSectors, Remaining(allSectors - overallDone),
					sectorsPerSecond * 2048));
			}

			try {
				var result = new TitleRipper(videoTsFolder, item.TitleNumber).Rip(item.OutputPath, OnProgress, options: options, cancel: cancel);
				results.Add(new RipJobItemResult(item, result, null));
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException) {
				results.Add(new RipJobItemResult(item, null, ex));
			}

			// Carry this title's reading into the speed measurement for the next ones.
			if (last is { } l && readingStartedAt is { } started) {
				readingTimeBefore += l.Elapsed - started;
				sectorsReadBefore += l.SectorsDone;
			}
			finishedSectors += item.Sectors;
		}
		return results;
	}
}
