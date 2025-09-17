using System.Windows;
using UsbCopyMon.Shared;

namespace UsbCopyMon.Tray;

public partial class PromptWindow : Window
{
    private readonly PromptRequest _req;

    // Expose the result so MainWindow can read it after ShowDialog()
    public PromptResponse? Result { get; private set; }

    public PromptWindow(PromptRequest req)
    {
        InitializeComponent();
        _req = req;
        Title = "External Transfer Detected";
        TitleText.Text = $"{(_req.Direction == Direction.ToRemovable ? "To" : "From")} {_req.Device.Label} ({_req.Device.DriveLetter})";
        SummaryText.Text =
            $"Process: {_req.ProcessName} (PID {_req.Pid})\n" +
            $"Files: {_req.FileCount}, Size: {_req.TotalBytes} bytes\n" +
            $"Examples: {string.Join(", ", _req.SampleFiles)}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = new PromptResponse(_req.SessionId, WhoBox.Text, ReasonBox.Text, System.DateTimeOffset.Now);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = new PromptResponse(_req.SessionId, null, null, System.DateTimeOffset.Now);
        DialogResult = false;
        Close();
    }
}
