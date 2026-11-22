using System.Windows;

namespace ErrorHunt;

public partial class DiagnosticLogWindow : Window
{
    internal DiagnosticLogWindow(string entries)
    {
        InitializeComponent();
        LogText.Text = entries.Length == 0 ? "There are no diagnostic entries yet." : entries;
    }
}
