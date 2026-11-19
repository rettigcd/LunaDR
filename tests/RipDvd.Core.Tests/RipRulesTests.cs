using RipDvd.Core.Captions;
using RipDvd.Core.Ifo;
using RipDvd.Core.Matroska;
using RipDvd.Core.Mpeg;
using RipDvd.Core.Ripping;

namespace RipDvd.Core.Tests;

/// <summary>The rules a rip follows to match MakeMKV's files: references, clusters, duration, chapters, audio gaps, caption timing.</summary>
public class RipRulesTests {
	private const long Ms = 1_000_000;

	[Fact]
	public void References_PUsesPreviousAnchor_BUsesTwoPreviousAnchors() {
		var refs = new VideoReferences();
		Assert.Empty(refs.Next('I', isKeyFrame: true, 0));
		Assert.Equal([0L], refs.Next('P', false, 100));
		Assert.Equal([0L, 100L], refs.Next('B', false, 33));
		Assert.Equal([100L], refs.Next('P', false, 200));
		Assert.Equal([100L, 200L], refs.Next('B', false, 133));
	}

	[Fact]
	public void References_NeverReachBackPastAKeyFrame() {
		// Regression (log 14:58): the B pictures right after a key frame (open GOP) first referenced the previous GOP too (6,651 vs MakeMKV's 6,119).
		var refs = new VideoReferences();
		refs.Next('I', true, 0);
		refs.Next('P', false, 100);
		refs.Next('I', true, 500);
		Assert.Equal([500L], refs.Next('B', false, 433));
		Assert.Equal([500L], refs.Next('B', false, 466));
		Assert.Equal([500L], refs.Next('P', false, 600));
	}

	[Fact]
	public void Clusters_StartAtKeyFramesAtLeast400msApart() {
		var rule = new ClusterRule([]);
		Assert.True(rule.StartsCluster(0));
		Assert.False(rule.StartsCluster(300 * Ms));   // short GOP joins the cluster
		Assert.True(rule.StartsCluster(400 * Ms));
		Assert.False(rule.StartsCluster(799 * Ms));
		Assert.True(rule.StartsCluster(800 * Ms));
	}

	[Fact]
	public void Clusters_StartWhereAChapterStarts() {
		var rule = new ClusterRule([new MkvChapter(0, 250 * Ms, "1"), new MkvChapter(250 * Ms, 900 * Ms, "2")]);
		Assert.True(rule.StartsCluster(0));
		Assert.True(rule.StartsCluster(250 * Ms));    // only 250 ms later, but chapter 2 starts here
		Assert.False(rule.StartsCluster(500 * Ms));
	}

	[Fact]
	public void Duration_IsTheEndOfTheLongestAudioOrVideoTrack() {
		// Regression (log 2026-11-18): the video-only end gave 7,999,991 ms where MakeMKV (and the audio) say 8,000,000.
		var video = new MkvTrack { Number = 1, Type = MkvTrackType.Video, CodecId = "V_MPEG2" };
		var audio = new MkvTrack { Number = 2, Type = MkvTrackType.Audio, CodecId = "A_AC3" };
		var subtitle = new MkvTrack { Number = 3, Type = MkvTrackType.Subtitle, CodecId = "S_TEXT/UTF8" };
		video.LastEndNs = 7_999_991 * Ms;
		audio.LastEndNs = 8_000_000 * Ms;
		subtitle.LastEndNs = 9_000_000 * Ms;   // subtitles don't count
		Assert.Equal(8_000_000 * Ms, RipTiming.DurationNs([video, audio, subtitle]));
	}

	[Fact]
	public void Chapters_DontCountOtherAnglesCells() {
		// Regression (Beauty and the Beast title 16): chapters ran to 1:59 in a 1:31 title because angle-2 cells were counted.
		var pgc = new Pgc {
			Cells = [
				TestData.Cell(1, 1, 0, 9, seconds: 2),
				TestData.Cell(2, 1, 10, 99, seconds: 10, blockType: 1, blockMode: 1, interleaved: true),
				TestData.Cell(3, 1, 12, 101, seconds: 10, blockType: 1, blockMode: 3, interleaved: true),
				TestData.Cell(4, 1, 102, 120, seconds: 5),
			],
			ProgramEntryCells = [1, 2, 4],
		};
		var chapters = TitleRipper.PgcChapters(pgc);
		long Seconds(int s) => new DvdTime(0, 0, s, 0, 30).ToNanoseconds();
		Assert.Equal([0, Seconds(2), Seconds(12)], chapters.Select(c => c.StartNs));
		Assert.Equal(Seconds(17), chapters[^1].EndNs);
	}

	private static AudioFrame Frame(long pts) => new() { Data = [1], Pts = pts };

	[Fact]
	public void AudioGapShorterThanAFrame_IsClosedAndTheShiftKept() {
		// Regression (Beauty and the Beast, 50:41): a 17 ms gap at a seamless join; MakeMKV closes it and keeps the shift.
		var continuity = new TitleRipper.AudioContinuity(new Ac3Framer());   // AC3 frame: 32 ms
		Assert.Equal(0, continuity.Place(Frame(0)).Pts);
		Assert.Equal(32 * Ms, continuity.Place(Frame(32 * Ms)).Pts);
		Assert.Equal(64 * Ms, continuity.Place(Frame(81 * Ms)).Pts);    // 17 ms gap closed
		Assert.Equal(96 * Ms, continuity.Place(Frame(113 * Ms)).Pts);   // shift kept
	}

	[Fact]
	public void AudioGapOfAFrameOrMore_IsKept() {
		var continuity = new TitleRipper.AudioContinuity(new Ac3Framer());
		continuity.Place(Frame(0));
		Assert.Equal(500 * Ms, continuity.Place(Frame(500 * Ms)).Pts);   // real silence
		Assert.Equal(532 * Ms, continuity.Place(Frame(532 * Ms)).Pts);
	}

	[Fact]
	public void Captions_AreTimedOnePairPerFrameSlot_NotPerPicture() {
		// Regression (log 2026-11-18): with 3:2 pulldown a GOP shows fewer pictures than 1/29.97 s frames; timing per picture was 50-67 ms late.
		// Pop-on caption "HI": load (RCL), text, End Of Caption, sent as 4 pairs in the first picture's user data.
		var gop = new List<VideoFrame> {
			new() { Data = [], PictureType = 'I', TemporalReference = 0, Fields = 3, IsKeyFrame = true, Pts = 1000 * Ms,
				Captions = [(0x14, 0x20), (0x14, 0x20), (0x48, 0x49), (0x14, 0x2F)] },
			new() { Data = [], PictureType = 'B', TemporalReference = 1, Fields = 2, IsKeyFrame = false, Pts = 1050 * Ms },
		};
		var decoder = new Eia608Decoder();
		TitleRipper.FeedCaptions(decoder, gop);
		decoder.Finish(5000 * Ms);
		var caption = Assert.Single(decoder.TakeEvents());
		// End Of Caption is the 4th pair: GOP start + 3 frames of 1001/30000 s.
		Assert.Equal(1000 * Ms + VideoFrame.FieldsToNs(6), caption.StartNs);
		Assert.Contains("HI", caption.Text);
	}
}
