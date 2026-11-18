using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace RipDvd.Gui.ViewModels;

/// <summary>Base for view models: raises PropertyChanged so WPF bindings update.</summary>
public abstract class ViewModelBase : INotifyPropertyChanged {
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Sets a backing field and notifies bindings if the value changed. Returns true when it changed.</summary>
	protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) {
		if (EqualityComparer<T>.Default.Equals(field, value)) return false;
		field = value;
		Raise(name!);
		return true;
	}

	protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A button command that runs an action; WPF re-checks <c>canExecute</c> after user input (CommandManager).</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand {
	public event EventHandler? CanExecuteChanged {
		add => CommandManager.RequerySuggested += value;
		remove => CommandManager.RequerySuggested -= value;
	}

	public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
	public void Execute(object? parameter) => execute();
}
