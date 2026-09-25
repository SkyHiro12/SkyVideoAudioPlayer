using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;

namespace FluentPlayer
{
    public partial class App : Application
    {
        private Window? _window;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        public App()
        {
            this.InitializeComponent();

            this.UnhandledException += (sender, e) =>
            {
                e.Handled = true;
                string error = $"Unhandled Exception:\n{e.Exception?.ToString() ?? e.Message}";
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), error);
                MessageBox(IntPtr.Zero, error, "FluentPlayer Error", 0x10);
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                string error = $"Fatal AppDomain Exception:\n{e.ExceptionObject?.ToString()}";
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), error);
                MessageBox(IntPtr.Zero, error, "FluentPlayer Fatal", 0x10);
            };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                _window = new MainWindow();
                _window.Activate();
            }
            catch (Exception ex)
            {
                string error = $"OnLaunched Exception:\n{ex}";
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), error);
                MessageBox(IntPtr.Zero, error, "FluentPlayer Launch Error", 0x10);
            }
        }
    }
}