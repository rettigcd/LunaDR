using System.Globalization;
using System.Windows.Data;

namespace RipDvd.Gui;

/// <summary>Turns true into false and back, for bindings like "enabled when not ripping".</summary>
public sealed class NotConverter : IValueConverter {
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
