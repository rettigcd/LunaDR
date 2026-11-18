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
///   * the selected title's row is highlighted, and so are the labels of the titles that share cells with it.
/// Clicking a row selects that title; hovering a block reports the cell in <see cref="HoverText"/>.
/// </summary>
public sealed class CellMapView : FrameworkElement {
	private const double CellWidth = 7, CellGap = 1, SetGap = 6, RowHeight = 16, LabelWidth = 34, HeaderHeight = 16;

	public static readonly DependencyProperty MapProperty = DependencyProperty.Register(
		nameof(Map), typeof(CellMap), typeof(CellMapView),
		new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((CellMapView)d).Rebuild()));

	public static readonly DependencyProperty SelectedTitleProperty = DependencyProperty.Register(
		nameof(SelectedTitle), typeof(int?), typeof(CellMapView),
		new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

	public static readonly DependencyProperty HoverTextProperty = DependencyProperty.Register(
		nameof(HoverText), typeof(string), typeof(CellMapView),
		new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

	/// <summary>Text color, inherited from the window (so it follows the light/dark theme).</summary>
	public static readonly DependencyProperty ForegroundProperty = System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(typeof(CellMapView),
		new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

	public CellMap? Map { get => (CellMap?)GetValue(MapProperty); set => SetValue(MapProperty, value); }
	public int? SelectedTitle { get => (int?)GetValue(SelectedTitleProperty); set => SetValue(SelectedTitleProperty, value); }
	public string HoverText { get => (string)GetValue(HoverTextProperty); set => SetValue(HoverTextProperty, value); }
	public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

	private static readonly Brush OwnBrush = Frozen(Color.FromRgb(0x3B, 0x82, 0xF6));
	private static readonly Brush SharedBrush = Frozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
	private static readonly Brush ChapterBrush = Frozen(Color.FromArgb(0xC0, 0x10, 0x10, 0x10));
	private static readonly Brush SelectedRowBrush = Frozen(Color.FromArgb(0x40, 0x3B, 0x82, 0xF6));
	private static readonly Brush RelatedRowBrush = Frozen(Color.FromArgb(0x30, 0xF5, 0x9E, 0x0B));
	private static readonly Brush HoverBrush = Frozen(Color.FromArgb(0x30, 0x80, 0x80, 0x80));

	// Layout computed from the map: x of each cell column, users per cell, title-set groups.
	private double[] _cellX = [];
	private int[] _usersPerCell = [];
	private List<(int TitleSet, double X)> _setStarts = [];
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
		int lastSet = -1;
		for (int i = 0; i < map.Cells.Count; i++) {
			if (map.Cells[i].TitleSet != lastSet) {
				if (lastSet != -1) x += SetGap;
				lastSet = map.Cells[i].TitleSet;
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

		// Title-set numbers above their groups.
		foreach (var (set, x) in _setStarts) DrawText(dc, $"{set}", x, 1, 10, dpi, 0.7);

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

			DrawText(dc, $"t{title.TitleNumber}", 2, y + 1, 11, dpi, isSelected || isRelated ? 1.0 : 0.85, bold: isSelected);

			for (int pos = 0; pos < title.Cells.Count; pos++) {
				int c = title.Cells[pos];
				var block = new Rect(_cellX[c], y + 3, CellWidth, RowHeight - 6);
				dc.DrawRectangle(_usersPerCell[c] > 1 ? SharedBrush : OwnBrush, null, block);
				if (title.ChapterStarts.Contains(pos)) dc.DrawRectangle(ChapterBrush, null, new Rect(block.X, block.Y, 2, block.Height));
			}
		}
	}

	private void DrawText(DrawingContext dc, string text, double x, double y, double size, double dpi, double opacity, bool bold = false) {
		var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
			new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
			size, Foreground, dpi);
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
		HoverText = $"Cell {info.Label} (title set {info.TitleSet}, VOB {info.VobId}, cell {info.CellId}): sectors {info.FirstSector:N0}–{info.LastSector:N0}, " +
					$"length {length}; used by title{(users.Count == 1 ? "" : "s")} {string.Join(", ", users)}; {inTitle}.";
	}

	protected override void OnMouseLeave(MouseEventArgs e) {
		base.OnMouseLeave(e);
		_hoverRow = _hoverCell = -1;
		HoverText = "";
		InvalidateVisual();
	}

	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
		base.OnMouseLeftButtonDown(e);
		var (row, _) = HitTest(e.GetPosition(this));
		if (row >= 0 && Map is { } map) SelectedTitle = map.Titles[row].TitleNumber;
	}
}
