using System;
using System.Windows;

namespace HDMICapture.App
{
    public partial class App : Application
    {
        public App()
        {
            InitializeNDIPath();
            DispatcherUnhandledException += App_DispatcherUnhandledException;
        }

        // ダイアログ等の未処理例外でアプリ全体が落ちるのを防ぐ。
        // キャプチャ・NDI送出中にダイアログ側の不具合で落ちると実害が大きいため、
        // メッセージ表示のうえで継続できるようにする。
        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                $"予期しないエラーが発生しました:\n{e.Exception}",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private static void InitializeNDIPath()
        {
            try
            {
                // Try to get NDI runtime directory from environment variable
                string? ndiRuntimeDir = Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6")
                    ?? Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V5")
                    ?? Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V4")
                    ?? Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V3")
                    ?? Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V2");

                if (!string.IsNullOrEmpty(ndiRuntimeDir))
                {
                    // Add NDI runtime directory to PATH
                    string? currentPath = Environment.GetEnvironmentVariable("PATH");
                    if (!currentPath?.Contains(ndiRuntimeDir, StringComparison.OrdinalIgnoreCase) ?? true)
                    {
                        string newPath = $"{ndiRuntimeDir};{currentPath}";
                        Environment.SetEnvironmentVariable("PATH", newPath);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Failed to initialize NDI path: {ex.Message}");
            }
        }
    }
}
