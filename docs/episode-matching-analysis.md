# Analysis: naming ripped TV episodes from TheTVDB

Question (owner, 2026-11-18): is it possible to match the titles ripped from a TV-season DVD to their episodes on TheTVDB,
so files can be named like the Plex library already is (`s04e01 - Broken Circle.mkv`)?

**Short answer: yes, with one piece of help from the user per disc set.**
Which disc titles are episodes, and their order, can be found from the disc. Which episode numbers they are cannot always be
worked out from one disc alone: the user may need to confirm the series/season and, from disc 2 on, the first episode number.
TheTVDB's own API needs a paid subscription; there are free alternatives with weaker DVD-order support.

## 1. What the disc tells us

| Signal | Example (Dead Zone S4 D1) | Use |
|---|---|---|
| Volume label | `DEAD_ZONE_S4_D1` | Series name, season and disc number, when the label follows a pattern. Labels are not standardized (e.g. `A_VERY_LONG_ENGAGEMENT_DISC1`, `LOST_IN_TRANSLATION`, or empty). |
| Titles and their lengths | 4 titles of 42:33–42:59, 4 of 2–4 min | Episode-length titles vs extras: on TV sets the episodes cluster around one length. |
| Title order | titles 1–4 | Disc order = episode order on season sets (checked below). |
| Chapters, audio, CC | 9 chapters, 5.1 audio, CC on episodes only | Extra hints to tell episodes from extras. |

**Checked on the test disc:** our rip of title 1 is byte-identical (all audio and caption payloads, duration) to
`D:\TV Shows\The Dead Zone (2002)\Season 4\s04e01 - Broken Circle.mkv` in the owner's Plex library.
So disc 1, titles 1–4 are S04E01–E04, in order.

What the disc does **not** tell us: the series' official name, and for disc 2 onwards the number of the first episode on it
(it depends on how many episodes earlier discs hold; sets are often 4 + 4 + 4 + 3, but not always).

## 2. Episode data sources

| Source | Access | Episode data | DVD order | Notes |
|---|---|---|---|---|
| **TheTVDB** API v4 | Project API key + each user's subscriber PIN (about $12/year per user; check current price). Tokens last a month. | Episode `name`, `number`, `seasonNumber`, `aired`, `runtime` (whole minutes, may be empty) | **Yes**: season types `default`, `dvd`, `absolute`, `alternate`, `altdvd`, … | Best fit for DVD sets (episodes in DVD order). Search by name (`/search?query=…&type=series`). |
| **TVmaze** API | Free, no key (rate limited) | Name, season, number, airdate, runtime | No (aired order only) | Fine when DVD order = aired order (most shows). License terms not yet verified. |
| **TMDB** API | Free API key (non-commercial; terms not yet verified) | Name, season, number, runtime | Sometimes, via "episode groups" | Coverage of DVD order varies by show. |
| **TheDiscDB** (thediscdb.com) | Website (API and identification method not verified) | Maps a specific disc's titles to episodes/extras | Disc-specific | Could identify the exact disc and its extras; needs further investigation. |
| **Owner's own library** (D:\TV Shows) | Local | Exact match by content | n/a | Only for discs already ripped (re-rips, checking); proves the mapping, as above. |

Runtime is a weak signal: TheTVDB gives whole minutes, often the same nominal value for every episode, while one disc's
episodes differ by seconds (42:33 vs 42:54). It is useful to tell episodes from extras and to sanity-check, not to tell episodes apart.
**Order is the main signal.**

## 3. Proposed matching

1. **Find the episodes on the disc:** titles within a few minutes of the most common long length, in disc title order;
	skip duplicates (same cells as another title, e.g. a "play all" title) and extras.
2. **Find the series and season:**
	parse the volume label (`<NAME>_S<n>_D<m>`, `<NAME>_SEASON<n>_DISC<m>`, …) and search the source by name;
	if that fails or is ambiguous, the user gives `--series="The Dead Zone" --season=4` (the search shows candidates with year).
3. **Find the first episode number:** disc 1 → episode 1; disc *m* → by default (m−1) × (episodes on this disc), which the user
	can override with `--first-episode=N`. The dry run shows the proposed mapping before ripping.
4. **Check:** episode count left in the season ≥ episodes on the disc; runtimes plausible (within ~5 min).
	Warn instead of guessing when they don't fit.
5. **Name files** like the owner's Plex library: `<Series> (<year>)/Season N/sNNeMM - <Episode name>.mkv`, or just
	`sNNeMM - <Episode name>.mkv` in the output folder; extras keep `<Disc name> - title NN.mkv`.

Rough effort: series/season/episode lookup client + matching + CLI options + tests: one to two working sessions.

## 4. Risks

- **Episode numbering on later discs** without the earlier discs: needs `--first-episode` when discs hold uneven numbers of episodes.
- **"Play all" titles and multi-episode titles** (two episodes as one title, or one long title with all episodes as chapters):
	need detection (shared cells / duration ≈ multiple episodes); rare on newer sets, common on some older ones.
- **DVD order differs from aired order** for some shows (e.g. episodes aired out of production order): TheTVDB's `dvd` order
	handles it; TVmaze cannot.
- **Volume labels** are often unhelpful (empty, truncated, or a product code), so the series often needs to be given once.
- **TheTVDB terms:** needs a project key and the user's own subscription PIN; a key must not be shared publicly in the repo.

## 5. Decisions for the owner

1. Which source: TheTVDB (paid subscription, has DVD order) or a free one (TVmaze/TMDB, aired order), or TheTVDB with a free fallback?
2. File naming for episodes: the Plex-library style `sNNeMM - Episode name.mkv` (optionally in `Series (year)/Season N/` folders)?

Sources: [TheTVDB API v4 (GitHub)](https://github.com/thetvdb/v4-api), [TheTVDB API spec](https://thetvdb.github.io/v4-api/swagger.yml),
[TheTVDB FAQ on keys and PINs](https://support.thetvdb.com/kb/faq.php?id=81), [TheTVDB developer FAQ](https://support.thetvdb.com/kb/faq.php?cid=12),
[TVmaze API (publicapis.io)](https://publicapis.io/tv-maze-api), [TMDB episode groups (tinyMediaManager)](https://www.tinymediamanager.org/blog/episode-groups/),
[TheDiscDB](https://thediscdb.com), [TheDiscDB announcement (MakeMKV forum)](https://forum.makemkv.com/forum/viewtopic.php?p=109559).
