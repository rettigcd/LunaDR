using System.Text;

namespace RipDvd.Core.Captions;

/// <summary>A caption shown from <see cref="StartNs"/> to <see cref="EndNs"/>.</summary>
public sealed record CaptionEvent(long StartNs, long EndNs, string Text);

/// <summary>
/// Decodes EIA-608 ("line 21") closed captions, channel CC1, into timed text.
///
/// The decoder keeps two 15×32 character screens, as a caption decoder does:
/// "displayed" (on screen) and "non-displayed" (being prepared).
/// Pop-on captions are written off screen and swapped in by End Of Caption.
/// Roll-up and paint-on captions are written straight to the screen.
/// Each time the screen changes at a command, the previous screen's text becomes a <see cref="CaptionEvent"/>.
///
/// Text output matches MakeMKV's "CC→Text": every non-empty row padded to 32 columns and followed by "\n", italics as &lt;i&gt;…&lt;/i&gt;.
/// </summary>
public sealed class Eia608Decoder {
	private const int Rows = 15, Columns = 32;

	private struct Cell {
		public char Char;
		public bool Italic;
	}

	private enum Mode { PopOn, RollUp, PaintOn }

	private Cell[,] _displayed = new Cell[Rows, Columns];
	private Cell[,] _hidden = new Cell[Rows, Columns];
	private Mode _mode = Mode.PopOn;
	private int _rollUpRows = 2;
	private int _row = Rows - 1, _col;
	private bool _italic;
	private bool _channel1 = true;
	private (byte, byte) _lastControl;

	private string _shownText = "";
	private long _shownSince;
	private bool _dirty;
	private readonly List<CaptionEvent> _events = [];

	/// <summary>Decode one byte pair (with parity bits) received at <paramref name="timeNs"/>.</summary>
	public void Feed(long timeNs, byte b1, byte b2) {
		b1 &= 0x7F;
		b2 &= 0x7F;
		if (b1 == 0 && b2 == 0) return;   // padding

		if (b1 >= 0x10 && b1 <= 0x1F) {
			// Control codes are sent twice in a row; act on the first only.
			if (_lastControl == (b1, b2)) {
				_lastControl = default;
				return;
			}
			_lastControl = (b1, b2);
			// Roll-up and paint-on text goes straight to the screen; show it as of the next command.
			ScreenChanged(timeNs);
			_channel1 = (b1 & 0x08) == 0;
			if (_channel1) Control(timeNs, (byte)(b1 & 0x17), b2);
			return;
		}
		_lastControl = default;

		if (!_channel1 || b1 < 0x20) return;
		Put(CharFor(b1));
		if (b2 >= 0x20) Put(CharFor(b2));
		if (_mode != Mode.PopOn) _dirty = true;
	}

	/// <summary>End of stream: close the caption still on screen.</summary>
	public void Finish(long timeNs) => ScreenChanged(timeNs, force: true, clearedText: "");

	public List<CaptionEvent> TakeEvents() {
		var r = new List<CaptionEvent>(_events);
		_events.Clear();
		return r;
	}

	private void Control(long t, byte b1, byte b2) {
		if (b1 == 0x14 && b2 >= 0x20 && b2 <= 0x2F) {
			switch (b2) {
				case 0x20: _mode = Mode.PopOn; break;                                         // RCL resume caption loading
				case 0x21: if (_col > 0) { _col--; Target[_row, _col] = default; } break;      // BS backspace
				case 0x24: for (int c = _col; c < Columns; c++) Target[_row, c] = default; break;   // DER delete to end of row
				case 0x25 or 0x26 or 0x27:                                                   // RU2/RU3/RU4 roll-up
					if (_mode != Mode.RollUp) { Clear(_displayed); Clear(_hidden); _row = Rows - 1; }
					_mode = Mode.RollUp;
					_rollUpRows = b2 - 0x23;
					_col = 0;
					break;
				case 0x29: _mode = Mode.PaintOn; break;                                       // RDC resume direct captioning
				case 0x2C: Clear(_displayed); ScreenChanged(t, force: true); break;          // EDM erase displayed memory
				case 0x2D:                                                                   // CR carriage return (roll-up)
					if (_mode == Mode.RollUp) {
						ScreenChanged(t);
						RollUp();
					}
					break;
				case 0x2E: Clear(_hidden); break;                                             // ENM erase non-displayed memory
				case 0x2F:                                                                   // EOC end of caption: swap
					(_displayed, _hidden) = (_hidden, _displayed);
					_mode = Mode.PopOn;
					// A new pop-on caption starts a new event even if its text is the same (MakeMKV does the same, e.g. repeated "[RING]").
					ScreenChanged(t, force: true, restart: true);
					break;
			}
			return;
		}
		if (b1 == 0x17 && b2 >= 0x21 && b2 <= 0x23) {   // tab offset 1-3
			_col = Math.Min(Columns - 1, _col + (b2 - 0x20));
			return;
		}
		if (b1 == 0x11 && b2 >= 0x20 && b2 <= 0x2F) {   // mid-row style code: shown as a space
			_italic = ((b2 >> 1) & 7) == 7;
			Put(' ');
			return;
		}
		if (b1 == 0x11 && b2 >= 0x30 && b2 <= 0x3F) {   // special characters
			Put("®°½¿™¢£♪à èâêîôû"[b2 - 0x30]);
			return;
		}
		if ((b1 == 0x12 || b1 == 0x13) && b2 >= 0x20 && b2 <= 0x3F) {   // extended characters replace the previous one
			if (_col > 0) _col--;
			// Curly quotes are output as ASCII ' and " like MakeMKV (seen for ’ on the test disc; the others assumed).
			Put(b1 == 0x12 ? "ÁÉÓÚÜü'¡*'—©℠•\"\"ÀÂÇÈÊËëÎÏïÔÙùÛ«»"[b2 - 0x20] : "ÃãÍÌìÒòÕõ{}\\^_|~ÄäÖöß¥¤│ÅåØø┌┐└┘"[b2 - 0x20]);
			return;
		}
		if (b2 >= 0x40 && b2 <= 0x7F) {   // preamble address code: row, indent, style
			int row = PacRow(b1, b2);
			if (row < 0) return;
			if (_mode == Mode.RollUp) {
				// Roll-up keeps its window; a PAC moves the base row.
				if (row != _row) MoveRollWindow(row);
			} else {
				_row = row;
			}
			if ((b2 & 0x10) != 0) {
				_col = ((b2 >> 1) & 7) * 4;
				_italic = false;
			} else {
				_col = 0;
				_italic = ((b2 >> 1) & 7) == 7;
			}
		}
	}

	private Cell[,] Target => _mode == Mode.PopOn ? _hidden : _displayed;

	private void Put(char c) {
		if (_col >= Columns) _col = Columns - 1;
		Target[_row, _col] = new Cell { Char = c, Italic = _italic };
		_col++;
	}

	private void RollUp() {
		int top = Math.Max(0, _row - _rollUpRows + 1);
		for (int r = 0; r < Rows; r++) {
			for (int c = 0; c < Columns; c++) {
				if (r < top || r > _row) _displayed[r, c] = default;
				else if (r < _row) _displayed[r, c] = _displayed[r + 1, c];
				else _displayed[r, c] = default;
			}
		}
		_col = 0;
	}

	private void MoveRollWindow(int newBase) {
		var moved = new Cell[Rows, Columns];
		for (int r = 0; r < _rollUpRows; r++) {
			int from = _row - r, to = newBase - r;
			if (from < 0 || to < 0) break;
			for (int c = 0; c < Columns; c++) moved[to, c] = _displayed[from, c];
		}
		_displayed = moved;
		_row = newBase;
	}

	private static void Clear(Cell[,] screen) => Array.Clear(screen);

	/// <summary>
	/// The screen may have changed: if its text differs from what is being shown, close the current event and start a new one.
	/// With <paramref name="restart"/>, a new event starts even when the text is unchanged.
	/// </summary>
	private void ScreenChanged(long t, bool force = false, string? clearedText = null, bool restart = false) {
		if (!force && !_dirty) return;
		_dirty = false;
		string text = clearedText ?? Render(_displayed);
		if (text == _shownText && !(restart && text.Length > 0)) return;
		if (_shownText.Length > 0 && t > _shownSince) _events.Add(new CaptionEvent(_shownSince, t, _shownText));
		_shownText = text;
		_shownSince = t;
	}

	/// <summary>Every non-empty row, padded to 32 columns, with &lt;i&gt; tags around italic runs, each followed by "\n".</summary>
	private static string Render(Cell[,] screen) {
		var sb = new StringBuilder();
		for (int r = 0; r < Rows; r++) {
			int last = -1;
			for (int c = 0; c < Columns; c++)
				if (screen[r, c].Char is not '\0' and not ' ') last = c;
			if (last < 0) continue;

			bool italic = false;
			for (int c = 0; c < Columns; c++) {
				var cell = screen[r, c];
				bool wantItalic = cell.Char != '\0' && cell.Italic && c <= last;
				if (wantItalic && !italic) { sb.Append("<i>"); italic = true; }
				if (!wantItalic && italic) { sb.Append("</i>"); italic = false; }
				sb.Append(cell.Char == '\0' ? ' ' : cell.Char);
				if (c == last && italic) { sb.Append("</i>"); italic = false; }
			}
			sb.Append('\n');
		}
		return sb.ToString();
	}

	/// <summary>Row (0-based) for a preamble address code, or -1. Rows come in pairs selected by bit 0x20 of the second byte.</summary>
	private static int PacRow(byte b1, byte b2) {
		bool second = (b2 & 0x20) != 0;
		int row = b1 switch {
			0x11 => second ? 2 : 1,
			0x12 => second ? 4 : 3,
			0x15 => second ? 6 : 5,
			0x16 => second ? 8 : 7,
			0x17 => second ? 10 : 9,
			0x10 => second ? -1 : 11,
			0x13 => second ? 13 : 12,
			0x14 => second ? 15 : 14,
			_ => -1,
		};
		return row < 0 ? -1 : row - 1;
	}

	/// <summary>The EIA-608 basic character set is ASCII except for a few positions.</summary>
	private static char CharFor(byte b) => b switch {
		0x2A => 'á',
		0x5C => 'é',
		0x5E => 'í',
		0x5F => 'ó',
		0x60 => 'ú',
		0x7B => 'ç',
		0x7C => '÷',
		0x7D => 'Ñ',
		0x7E => 'ñ',
		0x7F => '█',
		_ => (char)b,
	};
}
