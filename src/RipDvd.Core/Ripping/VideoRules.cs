using RipDvd.Core.Matroska;

namespace RipDvd.Core.Ripping;

/// <summary>
/// ReferenceBlocks like MakeMKV's: a P picture references the previous anchor (I or P); a B picture the two previous anchors in decode order.
/// References never reach back past a key frame: the B pictures right after it (shown before it, in an open GOP) reference only the key frame
/// (checked against MakeMKV: all 6,119 references of a test title identical; a first version let them reference the previous GOP).
/// Feed the pictures in decode order.
/// </summary>
internal sealed class VideoReferences {
	private long? _lastAnchor, _olderAnchor;   // presentation times of the last two I/P pictures, in decode order

	/// <summary>The presentation times this picture references; then remembers it if it is an anchor.</summary>
	public IReadOnlyList<long> Next(char pictureType, bool isKeyFrame, long pts) {
		IReadOnlyList<long> refs = pictureType switch {
			'P' when _lastAnchor is { } a => [a],
			'B' when _olderAnchor is { } o && _lastAnchor is { } a => [o, a],
			'B' when _lastAnchor is { } a => [a],
			_ => [],
		};
		if (isKeyFrame) {
			_olderAnchor = null;
			_lastAnchor = pts;
		} else if (pictureType is 'I' or 'P') {
			_olderAnchor = _lastAnchor;
			_lastAnchor = pts;
		}
		return refs;
	}
}

/// <summary>
/// Clusters as MakeMKV makes them: a new cluster at a key frame at least 400 ms after the current cluster's key frame, or where a chapter starts;
/// otherwise the GOP joins the current cluster (short GOPs).
/// (The cluster's timestamp is its earliest block, truncated to ms, see <see cref="MkvWriter.StartCluster"/>; its cue uses the key frame's time.)
/// </summary>
internal sealed class ClusterRule(IReadOnlyList<MkvChapter> chapters) {
	public const long MinClusterNs = 400_000_000;
	private long? _clusterKeyPts;   // key frame that started the current cluster

	/// <summary>True if a GOP starting with a key frame at <paramref name="keyFramePts"/> starts a new cluster (and then remembers it).</summary>
	public bool StartsCluster(long keyFramePts) {
		long previous = _clusterKeyPts ?? long.MinValue;
		bool chapterStarts = chapters.Any(c => c.StartNs > previous + 1_000_000 && c.StartNs <= keyFramePts + 1_000_000);
		if (_clusterKeyPts is not null && keyFramePts - previous < MinClusterNs && !chapterStarts) return false;
		_clusterKeyPts = keyFramePts;
		return true;
	}
}

/// <summary>Small timing rules of a rip, kept apart so they can be tested.</summary>
internal static class RipTiming {
	/// <summary>
	/// MKV Duration like MakeMKV: the end of whichever audio/video track runs longest (on the film disc the audio ends 9 ms after the video;
	/// a video-only end gave 7,999,991 instead of 8,000,000 ms). Subtitle tracks don't count. The writer truncates it to whole ms.
	/// </summary>
	public static long DurationNs(IEnumerable<MkvTrack> tracks) => tracks.Where(t => t.Type != MkvTrackType.Subtitle).Max(t => t.LastEndNs);
}
