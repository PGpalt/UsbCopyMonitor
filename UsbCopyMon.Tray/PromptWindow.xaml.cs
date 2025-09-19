using System.Linq;
using System.Windows;
using UsbCopyMon.Shared;

namespace UsbCopyMon.Tray;

public partial class PromptWindow : Window
{
    public string? Answer { get; private set; }

    public PromptWindow(CopyLog log)
    {
        InitializeComponent();

        var filesPreview = string.Join(", ", log.FileNames.Take(3));
        if (log.FileNames.Count > 3) filesPreview += $" (+{log.FileNames.Count - 3} more)";

        Summary.Text =
            $"Device: {log.DeviceName}\n" +
            $"From: {log.SourcePath}\n" +
            $"To:   {log.DestPath}\n" +
            $"Files: {filesPreview}\n" +
            $"Time:  {log.Timestamp.LocalDateTime}\n" +
            $"User: {log.User}";

        // Pre-fill with the process user who wrote files, as a hint
        NameBox.Text = log.User;
        NameBox.SelectAll();
        NameBox.Focus();
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        Answer = NameBox.Text?.Trim();
        DialogResult = true;
        Close();
    }
}
