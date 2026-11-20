using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RipDvd.Core.Ripping;

namespace RipDvd.Gui.Controls;

/// <summary>
/// Compact graphical titles-vs-cells map (the GUI version of the probe's "cellmap" table).
/// Rows are titles, columns are cells in disc order, grouped by title set; every cell is drawn the same width (owner's choice).
///   * blue block: the title plays this cell, and no other title does;
///   * orange block: the title plays this cell, and so does at least one other title (shared video: duplicates, compilations);
///   * dark notch at a block's left edge: a chapter starts there;
///   * the selected title's row is highlighted, and so are the labels of the titles that share cells with it;
///   * selected cells (to rip on their own) are marked by a band down their column.
/// Clicking a row selects that title. Clicking a cell column also selects that cell; Ctrl+click adds or removes a cell (the order of clicks is the
/// playback order); Shift+click selects the range from the last clicked cell. Hovering a block reports the cell in <see cref="HoverText"/>.
/// </summary>
public sealed class CellMapView : FrameworkElement {
	private const double CellWidth = 7, CellGap = 1, SetGap = 6, RowHeight = 16, LabelWidth = 34, HeaderHeight = 22;
	private const double StripY = 15, StripHeight = 5;   // rip progress strip, between the title-set numbers and the first row

	/// <summary>
	/// G6: while ripping, the playlist being ripped (cell numbers, 1-based, in order: a title's cells or the chosen cells),
	/// the position being read in it, and how far into that cell. Finished cells and the current one are shown in a strip above the map,
	/// and on the row of <see cref="RipTitle"/> (when a title, not chosen cells, is being ripped).
	/// </summary>
	public static readonly DependencyProperty RipCellsProperty = DependencyProperty.Register(
		nameof(RipCells), typeof(IReadOnlyList<int>), typeof(CellMapView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
	public static readonly DependencyProperty RipPositionProperty = DependencyProperty.Register(
		nameof(RipPosition), typeof(int), typeof(CellMapView), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));
	public static readonly DependencyProperty RipCellFractionProperty = DependencyProperty.Register(
		nameof(RipCellFraction), typeof(double), typeof(CellMapView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
	public static readonly DependencyProperty RipTitleProperty = DependencyProperty.Register(
		nameof(RipTitle), typeof(int?), typeof(CellMapView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

	public IReadOnlyList<int>? RipCells { get => (IReadOnlyList<int>?)GetValue(RipCellsProperty); set => SetValue(RipCellsProperty, value); }
	public int RipPosition { get => (int)GetValue(RipPositionProperty); set => SetValue(RipPositionProperty, value); }
	public double RipCellFraction { get => (double)GetValue(RipCellFractionProperty); set => SetValue(RipCellFractionProperty, value); }
	public int? RipTitle { get => (int?)GetValue(RipTitleProperty); set => SetValue(RipTitleProperty, value); }

	private static readonly Brush DoneBrush = Frozen(Color.FromRgb(0x10, 0xB9, 0x81));
	private static readonly Brush StripBackground = Frozen(Color.FromArgb(0x30, 0x80, 0x80, 0x80));

	public static readonly DependencyProperty MapProperty = DependencyProperty.Register(
		nameof(Map), typeof(CellMap), typeof(CellMapView),
		new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((CellMapView)d).Rebuild()));

	public static readonly DependencyProperty SelectedTitleProperty = DependencyProperty.Register(
		nameof(SelectedTitle), typeof(int?), typeof(CellMapView),
		new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

	/// <summary>Selected cells as cell numbers (1 = first cell of the map, as in <see cref="CellSelection"/>), in the order they were clicked.</summary>
	public static readonly DependencyProperty SelectedCellsProperty = DependencyProperty.Register(
		nameof(SelectedCells), typeof(IReadOnlyList<int>), typeof(CellMapView),
		new FrameworkPropertyMetadata(Array.Empty<int>(), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

	public static readonly DependencyProperty HoverTextProperty = DependencyProperty.Register(
		nameof(HoverText), typeof(string), typeof(CellMapView),
		new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

	/// <summary>Text color, inherited from the window (so it follows the light/dark theme).</summary>
	public static readonly DependencyProperty ForegroundProperty = System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(typeof(CellMapView),
		new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

	public CellMap? Map { get => (CellMap?)GetValue(MapProperty); set => SetValue(MapProperty, value); }
	public int? SelectedTitle { get => (int?)GetValue(SelectedTitleProperty); set => SetValue(SelectedTitleProperty, value); }
	public IReadOnlyList<int>? SelectedCells { get => (IReadOnlyList<int>?)GetValue(SelectedCellsProperty); set => SetValue(SelectedCellsProperty, value); }
	public string HoverText { get => (string)GetValue(HoverTextProperty); set => SetValue(HoverTextProperty, value); }
	public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

	private static readonly Brush OwnBrush = Frozen(Color.FromRgb(0x3B, 0x82, 0xF6));
	private static readonly Brush SharedBrush = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
	private static readonly Brush ChapterBrush = Frozen(Color.FromArgb(0xC0, 0x10, 0x10, 0x10));
	private static readonly Brush SelectedRowBrush = Frozen(Color.FromArgb(0x40, 0x3B, 0x82, 0xF6));
	private static readonly Brush RelatedRowBrush = Frozen(Color.FromArgb(0x30, 0xF5, 0x9E, 0x0B));
	private static readonly Brush HoverBrush = Frozen(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
	private static readonly Brush SelectedCellBrush = Frozen(Color.FromArgb(0x50, 0x10, 0xB9, 0x81));
	private static readonly Pen SelectedCellPen = FrozenPen(Color.FromRgb(0x05, 0x96, 0x69));
	private int? _anchorCell;   // last plainly clicked or Ctrl+clicked cell, start of a Shift+click range

	private static Pen FrozenPen(Color c) {
		var p = new Pen(Frozen(c), 1);
		p.Freeze();
		return p;
	}

	// Layout computed from the map: x of each cell column, users per cell, title-set groups.
	private double[] _cellX = [];
	private int[] _usersPerCell = [];
	private List<(string Group, double X)> _setStarts = [];
	private double _width;
	private int _hoverRow = -1, _hoverCell = -1;

	private static Brush Frozen(Color c) {
		var b = new SolidColorBrush(c);
		b.Freeze();
		return b;
	}

	private void Rebuild() {
		var map = Map;
		_setStarts = [];
		if (map is null) {
			_cellX = [];
			_usersPerCell = [];
			_width = 0;
			return;
		}
		_cellX = new double[map.Cells.Count];
		double x = LabelWidth;
		string? lastSet = null;
		for (int i = 0; i < map.Cells.Count; i++) {
			if (map.Cells[i].Group != lastSet) {
				if (lastSet is not null) x += SetGap;
				lastSet = map.Cells[i].Group;
				_setStarts.Add((lastSet, x));
			}
			_cellX[i] = x;
			x += CellWidth + CellGap;
		}
		_width = x + 4;
		_usersPerCell = new int[map.Cells.Count];
		foreach (var t in map.Titles)
			foreach (int c in t.Cells.Distinct()) _usersPerCell[c]++;
	}

	protected override Size MeasureOverride(Size availableSize) =>
		Map is null ? new Size(0, 0) : new Size(_width, HeaderHeight + Map.Titles.Count * RowHeight + 2);

	protected override void OnRender(DrawingContext dc) {
		var map = Map;
		if (map is null) return;
		double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
		dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));   // so the whole area takes mouse input

		// Title-set numbers (DVD) or clip names (Blu-ray) above their groups, where there is room (a one-segment clip is too narrow for its name).
		for (int s = 0; s < _setStarts.Count; s++) {
			var (group, x) = _setStarts[s];
			double room = (s + 1 < _setStarts.Count ? _setStarts[s + 1].X : _width) - x;
			if (TextWidth(group, 9, dpi) <= room - 2) DrawText(dc, group, x, 1, 9, dpi, 0.7);
		}

		var selected = map.Titles.FirstOrDefault(t => t.TitleNumber == SelectedTitle);
		var selectedCells = selected?.Cells.ToHashSet() ?? [];

		for (int row = 0; row < map.Titles.Count; row++) {
			var title = map.Titles[row];
			double y = HeaderHeight + row * RowHeight;
			bool isSelected = selected == title;
			bool isRelated = !isSelected && selectedCells.Count > 0 && title.Cells.Any(selectedCells.Contains);
			if (isSelected) dc.DrawRectangle(SelectedRowBrush, null, new Rect(0, y, _width, RowHeight));
			else if (isRelated) dc.DrawRectangle(RelatedRowBrush, null, new Rect(0, y, _width, RowHeight));
			if (row == _hoverRow) dc.DrawRectangle(HoverBrush, null, new Rect(0, y, _width, RowHeight));

			DrawText(dc, $"t{title.TitleNumber}", 2, y + 1, 10, dpi, isSelected || isRelated ? 1.0 : 0.85, bold: isSelected);

			for (int pos = 0; pos < title.Cells.Count; pos++) {
				int c = title.Cells[pos];
				var block = new Rect(_cellX[c], y + 3, CellWidth, RowHeight - 6);
				dc.DrawRectangle(_usersPerCell[c] > 1 ? SharedBrush : OwnBrush, null, block);
				if (title.ChapterStarts.Contains(pos)) dc.DrawRectangle(ChapterBrush, null, new Rect(block.X, block.Y, 2, block.Height));
			}
		}

		DrawRipProgress(dc, map);

		// Selected cells: a band down each selected column.
		double height = map.Titles.Count * RowHeight;
		foreach (int number in SelectedCells ?? []) {
			if (number < 1 || number > _cellX.Length) continue;
			dc.DrawRectangle(SelectedCellBrush, SelectedCellPen, new Rect(_cellX[number - 1] - 0.5, HeaderHeight, CellWidth + 1, height));
		}
	}

	/// <summary>
	/// G6: rip progress. In the strip above the map, each cell of the playlist being ripped gets a grey slot, filled green when finished
	/// (the current cell partly, by how much of it has been read). On the ripped title's row, finished blocks turn green the same way.
	/// </summary>
	private void DrawRipProgress(DrawingContext dc, CellMap map) {
		if (RipCells is not { Count: > 0 } cells) return;
		int position = RipPosition;
		int row = RipTitle is { } t ? map.Titles.ToList().FindIndex(x => x.TitleNumber == t) : -1;
		for (int pos = 0; pos < cells.Count; pos++) {
			int index = cells[pos] - 1;
			if (index < 0 || index >= _cellX.Length) continue;
			double x = _cellX[index];
			double fill = pos < position ? 1 : pos == position ? Math.Clamp(RipCellFraction, 0, 1) : 0;
			dc.DrawRectangle(StripBackground, null, new Rect(x, StripY, CellWidth, StripHeight));
			if (fill <= 0) continue;
			dc.DrawRectangle(DoneBrush, null, new Rect(x, StripY, CellWidth * fill, StripHeight));
			if (row >= 0) dc.DrawRectangle(DoneBrush, null, new Rect(x, HeaderHeight + row * RowHeight + 3, CellWidth * fill, RowHeight - 6));
		}
	}

	private FormattedText Text(string text, double size, double dpi, bool bold = false) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
		new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
		size, Foreground, dpi);

	private double TextWidth(string text, double size, double dpi) => Text(text, size, dpi).Width;

	private void DrawText(DrawingContext dc, string text, double x, double y, double size, double dpi, double opacity, bool bold = false) {
		var ft = Text(text, size, dpi, bold);
		dc.PushOpacity(opacity);
		dc.DrawText(ft, new Point(x, y));
		dc.Pop();
	}

	/// <summary>The title row and cell under a point, or -1.</summary>
	private (int Row, int Cell) HitTest(Point p) {
		var map = Map;
		if (map is null || p.Y < HeaderHeight) return (-1, -1);
		int row = (int)((p.Y - HeaderHeight) / RowHeight);
		if (row < 0 || row >= map.Titles.Count) return (-1, -1);
		int cell = -1;
		for (int i = 0; i < _cellX.Length; i++)
			if (p.X >= _cellX[i] && p.X < _cellX[i] + CellWidth + CellGap) { cell = i; break; }
		return (row, cell);
	}

	protected override void OnMouseMove(MouseEventArgs e) {
		base.OnMouseMove(e);
		var map = Map;
		var (row, cell) = HitTest(e.GetPosition(this));
		if (row == _hoverRow && cell == _hoverCell) return;
		_hoverRow = row;
		_hoverCell = cell;
		InvalidateVisual();
		if (map is null || row < 0) {
			HoverText = "";
			return;
		}
		var title = map.Titles[row];
		if (map.Bluray) {
			HoverText = BlurayHoverText(map, title, cell);
			return;
		}
		if (cell < 0) {
			HoverText = $"Title {title.TitleNumber}: {title.Cells.Count} cells, {title.ChapterStarts.Count} chapters";
			return;
		}
		var info = map.Cells[cell];
		var positions = title.Cells.Select((c, i) => (c, i)).Where(x => x.c == cell).Select(x => x.i).ToList();
		string inTitle = positions.Count == 0
			? $"not played by title {title.TitleNumber}"
			: $"title {title.TitleNumber} plays it as cell {string.Join(", ", positions.Select(i => i + 1))}" +
			  (positions.Any(title.ChapterStarts.Contains) ? " (a chapter starts here)" : "");
		var users = map.TitlesUsing(cell).ToList();
		string length = TimeSpan.FromTicks(info.DurationNs / 100).ToString(@"m\:ss\.f");
		HoverText = $"Cell {cell + 1} (title set {info.TitleSet}, VOB {info.VobId}, cell {info.CellId}): sectors {info.FirstSector:N0}–{info.LastSector:N0}, " +
					$"length {length}; used by title{(users.Count == 1 ? "" : "s")} {string.Join(", ", users)}; {inTitle}.";
	}

	/// <summary>Hover text on a Blu-ray map: rows are playlists, cells are clip segments.</summary>
	private static string BlurayHoverText(CellMap map, TitlePlaylist playlist, int cell) {
		static string Clock(long ns) => TimeSpan.FromTicks(ns / 100).ToString(ns >= 3_600_000_000_000 ? @"h\:mm\:ss" : @"m\:ss");
		if (cell < 0) {
			int clips = playlist.Cells.Select(c => map.Cells[c].Clip).Distinct().Count();
			return $"Playlist {playlist.TitleNumber:00000}: {playlist.Cells.Count} segments of {clips} clip{(clips == 1 ? "" : "s")}, {playlist.ChapterStarts.Count} chapters";
		}
		var info = map.Cells[cell];
		var positions = playlist.Cells.Select((c, i) => (c, i)).Where(x => x.c == cell).Select(x => x.i).ToList();
		string inPlaylist = positions.Count == 0
			? $"not played by playlist {playlist.TitleNumber:00000}"
			: $"playlist {playlist.TitleNumber:00000} plays it as segment {string.Join(", ", positions.Select(i => i + 1))}" +
			  (positions.Any(playlist.ChapterStarts.Contains) ? " (a chapter starts here)" : "");
		var users = map.TitlesUsing(cell).Select(n => $"{n:00000}").ToList();
		long start = info.ClipIn * 1_000_000_000L / 45_000, end = info.ClipOut * 1_000_000_000L / 45_000;
		return $"Segment {cell + 1} (clip {info.Clip}, part {info.CellId}): {Clock(start)}–{Clock(end)} on the clip's timeline, length {Clock(info.DurationNs)}; " +
			   $"used by playlist{(users.Count == 1 ? "" : "s")} {string.Join(", ", users)}; {inPlaylist}.";
	}

	protected override void OnMouseLeave(MouseEventArgs e) {
		base.OnMouseLeave(e);
		_hoverRow = _hoverCell = -1;
		HoverText = "";
		InvalidateVisual();
	}

	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
		base.OnMouseLeftButtonDown(e);
		var (row, cell) = HitTest(e.GetPosition(this));
		if (row < 0 || Map is not { } map) return;
		SelectedTitle = map.Titles[row].TitleNumber;
		if (cell < 0) return;

		int number = cell + 1;
		var current = SelectedCells ?? [];
		var modifiers = Keyboard.Modifiers;
		if (modifiers.HasFlag(ModifierKeys.Shift) && _anchorCell is int anchor) {
			// The range from the anchor to this cell, in the direction clicked.
			int step = number >= anchor ? 1 : -1;
			var range = new List<int>();
			for (int n = anchor; n != number + step; n += step) range.Add(n);
			SelectedCells = range;
			return;
		}
		SelectedCells = modifiers.HasFlag(ModifierKeys.Control)
			? (current.Contains(number) ? current.Where(n => n != number).ToList() : [.. current, number])
			: [number];
		_anchorCell = number;
	}
}
