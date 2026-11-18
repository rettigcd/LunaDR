# rip-dvd: working instructions

This file holds the standing instructions for anyone (human or AI agent) working on this repo,
on any computer. Read it first.

## Goal
A .NET application that rips movies and TV episodes from a DVD into MKV files. It should work
roughly like MakeMKV. **When a design decision has to be made, do what MakeMKV does** unless
there is a good reason not to (note the reason in the log). Reading MakeMKV documentation, or
running an installed MakeMKV (`makemkvcon`) to compare results, is allowed.

## How work is done
- Work autonomously. Do as much as possible without asking the owner. Only stop for decisions
	that really belong to the owner; record those as open questions.
- Everything is written in C# / .NET (no Python) so the owner can read it.
- **Probe tool:** `src/DvdProbe` is a command-line app used for probing and investigating
	(reading discs, IFO files, testing ideas). When a probe command is no longer needed, mark it
	`[Obsolete]` and label it "old" in its help text. **Do not delete it**; the owner wants to see it.

## The two documents that drive development
1. **[docs/current-plan.md](docs/current-plan.md)**: the live plan. Sections:
	- **Status**: where things stand right now.
	- **Design decisions**: decisions currently in force (short; reasons are in the log).
	- **Next steps**: ordered list of what to do next.
	- **Open questions**: unresolved questions.
	Keep it current: edit it in place, it is not a history.
2. **[docs/log.md](docs/log.md)**: append-only history. Each entry starts with a timestamp
	(`## yyyy-MM-dd HH:mm`, local time) and records steps completed, questions answered and
	decisions made, with their reasons. Never rewrite old entries; add new ones at the bottom.

### Rules
- When a step is completed, add an entry to the log and update Status/Next steps in the plan.
- When a decision is made, add it to the plan's Design decisions and log it with the reason.
- **When an open question is answered, remove it from current-plan.md and record the question
	and its answer in the log.**
- Commit after each meaningful step so the history is readable.

## Environment notes
- Windows working tree uses CRLF (`core.autocrlf=true`); new files should match.
- **Formatting (owner's rule, all files):** indent with tabs (tab width 4). Open braces go
	at the end of the line that starts the block (`if (x) {`, `class Foo {`, `} else {`),
	never on a line by themselves. The rules are in [.editorconfig](.editorconfig); after
	editing C#, run `dotnet format whitespace RipDvd.slnx` (or `--verify-no-changes` to check).
- **Comments and `<summary>` docs (owner's rule):** wrap at up to ~140 characters (about 50%
	longer than the ~95 used at first), and start each new sentence on a new line.
- Build: `dotnet build` from the repo root. Run the probe: `dotnet run --project src/DvdProbe -- <command>`.
