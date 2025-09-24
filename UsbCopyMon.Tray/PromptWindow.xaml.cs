// PromptWindow.xaml.cs
using System;
using System.Windows;

namespace UsbCopyMon.Tray
{
    public partial class PromptWindow : Window
    {
        public string? Answer { get; private set; }

        public PromptWindow(string? suggestedName)
        {
            InitializeComponent();
            NameBox.Text = string.IsNullOrWhiteSpace(suggestedName)
                ? Environment.UserName
                : suggestedName;
            NameBox.SelectAll();
            NameBox.Focus();
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            Answer = NameBox.Text?.Trim();
            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
