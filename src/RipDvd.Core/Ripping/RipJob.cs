namespace RipDvd.Core.Ripping;

/// <summary>
/// One title to rip as part of a job, or (with <see cref="Cells"/>) a list of chosen cells joined into one file; then TitleNumber is 0.
/// <see cref="Sectors"/> (its size on the disc) weights the progress and time estimates.
/// </summary>
public sealed record RipJobItem(int TitleNumber, string OutputPath, long Sectors) {
	/// <summary>Cell numbers as in the cell map (see <see cref="CellSelection"/>), in playback order; null for a title.</summary>
	public IReadOnlyList<int>? Cells { get; init; }

	/// <summary>"Title 6" or "Cells 23-27", for messages.</summary>
	public string Name => Cells is { } c ? $"Cells {CellSelection.Format(c)}" : $"Title {TitleNumber}";

	public static RipJobItem ForCells(IReadOnlyList<int> cells, string outputPath, long sectors) => new(0, outputPath, sectors) { Cells = cells };
}

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
	/// <paramref name="itemFinished"/> is called as each title finishes (or fails), so a window can list results during the job.
	/// </summary>
	public List<RipJobItemResult> Run(Action<RipJobProgress>? progress = null, CancellationToken cancel = default,
		Action<RipJobItemResult>? itemFinished = null) {
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
				// The total is an estimate for interleaved (multi-angle) titles, so the fraction is capped.
				double titleFraction = p.Phase == RipPhase.Done ? 1 : titleTotal == 0 ? 0 : Math.Min(0.999, (double)p.SectorsDone / titleTotal);
				long overallDone = finishedSectors + (long)(titleFraction * item.Sectors);
				TimeSpan? Remaining(long sectors) => sectorsPerSecond > 0 ? TimeSpan.FromSeconds(Math.Max(0, sectors) / sectorsPerSecond) : null;

				progress?.Invoke(new RipJobProgress(
					index, Items.Count, item, p,
					titleFraction, Remaining(titleTotal - p.SectorsDone),
					allSectors == 0 ? 0 : (double)overallDone / allSectors, Remaining(allSectors - overallDone),
					sectorsPerSecond * 2048));
			}

			try {
				var ripper = item.Cells is { } cells ? TitleRipper.ForCells(videoTsFolder, cells) : new TitleRipper(videoTsFolder, item.TitleNumber);
				var result = ripper.Rip(item.OutputPath, OnProgress, options: options, cancel: cancel);
				results.Add(new RipJobItemResult(item, result, null));
				itemFinished?.Invoke(results[^1]);
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException) {
				results.Add(new RipJobItemResult(item, null, ex));
				itemFinished?.Invoke(results[^1]);
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
