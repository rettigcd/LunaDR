namespace RipDvd.Core.Ripping;

/// <summary>DVDs store ISO 639-1 two-letter codes; Matroska wants ISO 639-2 three-letter codes.</summary>
public static class Languages {
	private static readonly Dictionary<string, string> TwoToThree = new(StringComparer.OrdinalIgnoreCase) {
		["en"] = "eng",
		["fr"] = "fre",
		["es"] = "spa",
		["de"] = "ger",
		["it"] = "ita",
		["pt"] = "por",
		["nl"] = "dut",
		["sv"] = "swe",
		["da"] = "dan",
		["no"] = "nor",
		["fi"] = "fin",
		["is"] = "ice",
		["ja"] = "jpn",
		["zh"] = "chi",
		["ko"] = "kor",
		["ru"] = "rus",
		["pl"] = "pol",
		["cs"] = "cze",
		["sk"] = "slo",
		["hu"] = "hun",
		["el"] = "gre",
		["tr"] = "tur",
		["he"] = "heb",
		["ar"] = "ara",
		["hi"] = "hin",
		["th"] = "tha",
		["ro"] = "rum",
		["bg"] = "bul",
		["hr"] = "hrv",
		["sr"] = "srp",
		["sl"] = "slv",
		["uk"] = "ukr",
		["ca"] = "cat",
		["eu"] = "baq",
		["gl"] = "glg",
		["ga"] = "gle",
		["la"] = "lat",
		["id"] = "ind",
		["ms"] = "may",
		["vi"] = "vie",
		["et"] = "est",
		["lv"] = "lav",
		["lt"] = "lit",
		["fa"] = "per",
		["ta"] = "tam",
		["te"] = "tel",
	};

	/// <summary>"en" → "eng"; null/unknown → "und" (undetermined).</summary>
	public static string ToIso6392(string? twoLetter) =>
		twoLetter is not null && TwoToThree.TryGetValue(twoLetter, out var three) ? three : "und";
}
