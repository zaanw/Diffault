using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ErrorHunt;

internal sealed record WorkspaceCommand(string Title, string Shortcut, Action Execute);

public partial class CommandPalette : Window
{
    private readonly WorkspaceCommand[] commands;

    internal CommandPalette(WorkspaceCommand[] commands)
    {
        this.commands = commands;
        InitializeComponent();
        Refresh();
        Loaded += (_, _) => Query.Focus();
    }

    internal WorkspaceCommand? ChosenCommand { get; private set; }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (Actions is not null) Refresh();
    }

    private void Refresh()
    {
        var query = Query.Text.Trim();
        var matches = commands.Where(command => query.Length == 0 ||
            command.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        Actions.ItemsSource = matches;
        Actions.SelectedIndex = matches.Length > 0 ? 0 : -1;
        QueryHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HandleKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
            case Key.Down:
                if (Actions.Items.Count > 0)
                {
                    var change = e.Key == Key.Down ? 1 : -1;
                    Actions.SelectedIndex = Math.Clamp(Actions.SelectedIndex + change, 0, Actions.Items.Count - 1);
                    Actions.ScrollIntoView(Actions.SelectedItem);
                }
                break;
            case Key.Enter:
                Accept();
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void InvokeSelection(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Actions, source) is ListBoxItem)
            Accept();
    }

    private void Accept()
    {
        if (Actions.SelectedItem is WorkspaceCommand command)
        {
            ChosenCommand = command;
            DialogResult = true;
        }
    }
}
