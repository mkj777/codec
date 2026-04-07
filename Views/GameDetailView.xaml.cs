using Codec.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Codec.Views
{
    public sealed partial class GameDetailView : UserControl
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetOpenFileName(ref OPENFILENAME lpofn);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAME
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_NOCHANGEDIR = 0x00000008;

        private MainViewModel? ViewModel => DataContext as MainViewModel;

        public GameDetailView()
        {
            InitializeComponent();
            Loaded += GameDetailView_Loaded;
            SizeChanged += GameDetailView_SizeChanged;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
            => ViewModel?.BackCommand.Execute(null);

        private void Scrim_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (ViewModel?.CloseGameSettingsCommand.CanExecute(null) == true)
                ViewModel.CloseGameSettingsCommand.Execute(null);
        }

        private async void SelectScriptButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.SelectedGame == null)
                return;

            string? gameFolderPath = ViewModel.SelectedGame.FolderLocation;
            string? selectedFilePath = OpenFileDialog(gameFolderPath);

            if (!string.IsNullOrWhiteSpace(selectedFilePath))
            {
                if (ViewModel.SetLaunchScriptCommand.CanExecute(selectedFilePath))
                    await ViewModel.SetLaunchScriptCommand.ExecuteAsync(selectedFilePath);
            }
        }

        private string? OpenFileDialog(string? initialDirectory)
        {
            var fileBuffer = new char[260];
            var fileBufferPtr = Marshal.AllocHGlobal(260 * 2);
            Marshal.Copy(fileBuffer, 0, fileBufferPtr, fileBuffer.Length);

            try
            {
                var ofn = new OPENFILENAME
                {
                    lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                    hwndOwner = GetActiveWindow(),
                    lpstrFilter = "Batch Files (*.bat)\0*.bat\0All Files (*.*)\0*.*\0\0",
                    nFilterIndex = 1,
                    lpstrFile = fileBufferPtr,
                    nMaxFile = 260,
                    lpstrInitialDir = !string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)
                        ? initialDirectory
                        : null,
                    lpstrTitle = "Select Launch Script",
                    Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
                };

                if (GetOpenFileName(ref ofn))
                {
                    return Marshal.PtrToStringUni(fileBufferPtr);
                }

                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(fileBufferPtr);
            }
        }

        private async void DeleteSelectedGameButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.SelectedGame == null)
                return;

            var dialog = new ContentDialog
            {
                Title = "Delete Game",
                Content = $"Remove '{ViewModel.SelectedGame.Name}' from your library? This won't delete any files from disk.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            if (ViewModel.DeleteSelectedGameCommand.CanExecute(null))
                await ViewModel.DeleteSelectedGameCommand.ExecuteAsync(null);
        }

        private void GameDetailView_Loaded(object sender, RoutedEventArgs e)
            => UpdateSettingsLayoutState();

        private void GameDetailView_SizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateSettingsLayoutState();

        private void UpdateSettingsLayoutState()
        {
            if (RootLayout == null)
                return;

            double width = ActualWidth;
            double height = ActualHeight;

            string stateName = "SettingsComfortableState";

            if (width <= 1100 || height <= 690)
            {
                stateName = "SettingsTightState";
            }
            else if (width <= 1320 || height <= 820)
            {
                stateName = "SettingsCompactState";
            }

            _ = VisualStateManager.GoToState(this, stateName, useTransitions: true);
        }

        private void HeroOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double padding = Math.Clamp(e.NewSize.Width * 0.03, 20, 64);
            if (HeroLogo != null)
            {
                HeroLogo.Margin = new Thickness(padding, padding, 0, 0);
                double targetHeight = Math.Clamp(e.NewSize.Width * 0.18, 170, 320);
                HeroLogo.Height = targetHeight;
                HeroLogo.MaxWidth = Math.Clamp(targetHeight * 2.2, 360, 640);
            }
        }
    }
}
