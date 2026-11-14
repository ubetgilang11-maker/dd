using System;
using System.Windows;
using ResourceGuardian.ViewModels;

namespace ResourceGuardian
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainViewModel();
            DataContext = _vm;
            Closing += (_, _) => _vm.Shutdown();
        }

        /// <summary>
        /// Parses the hex affinity mask textbox and pushes it onto the selected
        /// row's AffinityMask field (kept out of the binding chain since it needs
        /// hex parsing rather than a straight numeric convert).
        /// </summary>
        private void AffinityBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_vm.SelectedRow == null) return;
            var text = AffinityBox.Text.Trim().TrimStart('0', 'x', 'X');
            if (string.IsNullOrEmpty(text)) return;

            if (ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out ulong mask))
            {
                _vm.SelectedRow.AffinityMask = mask;
            }
        }
    }
}
