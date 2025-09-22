using System.Windows;

namespace UsbCopyMon.Tray
{
    public partial class PromptWindow : Window
    {
        public string? Answer { get; private set; }

        // Minimal prompt: only ask for a name
        public PromptWindow(string? suggestedName)
        {
            InitializeComponent();

            // Pre-fill with suggested name (e.g., process user)
            NameBox.Text = suggestedName ?? string.Empty;
            NameBox.SelectAll();

            Loaded += (_, __) => NameBox.Focus();
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            Answer = NameBox.Text?.Trim();
            DialogResult = true;
            Close();
        }
    }
}
