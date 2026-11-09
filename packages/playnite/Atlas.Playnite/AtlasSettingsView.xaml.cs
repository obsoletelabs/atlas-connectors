using System.Windows;
using System.Windows.Controls;

namespace Atlas.Playnite
{
    public partial class AtlasSettingsView : UserControl
    {
        public AtlasSettingsView()
        {
            InitializeComponent();
        }

        // PasswordBox cannot be data-bound; hand the pasted token to the view model, which keeps it in memory until OK.
        private void OnTokenChanged(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as AtlasSettingsViewModel;
            if (vm != null) vm.SetPastedToken(TokenBox.Password);
        }
    }
}
