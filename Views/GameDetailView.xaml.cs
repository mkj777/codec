using Codec.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.IO;
using System.Runtime.InteropServices;

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
            public string? lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public string? lpstrFileTitle;
            public int nMaxFileTitle;
            public string? lpstrInitialDir;
            public string? lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string? lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string? lpTemplateName;
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

        private void HeroActionButton_PointerEntered(object sender, PointerRoutedEventArgs e)
            => UpdateHeroActionVisualState(sender as Button, isHovered: true, isPressed: false);

        private void HeroActionButton_PointerExited(object sender, PointerRoutedEventArgs e)
            => UpdateHeroActionVisualState(sender as Button, isHovered: false, isPressed: false);

        private void HeroActionButton_PointerPressed(object sender, PointerRoutedEventArgs e)
            => UpdateHeroActionVisualState(sender as Button, isHovered: true, isPressed: true);

        private void HeroActionButton_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                bool isPointerOver = button.IsPointerOver;
                UpdateHeroActionVisualState(button, isPointerOver, isPressed: false);

                if (isPointerOver)
                {
                    AnimateHeroActionClick(button);
                }
            }
        }

        private void HeroActionButton_PointerCanceled(object sender, PointerRoutedEventArgs e)
            => UpdateHeroActionVisualState(sender as Button, isHovered: false, isPressed: false);

        private void HeroActionButton_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && !button.IsPointerOver)
            {
                UpdateHeroActionVisualState(button, isHovered: false, isPressed: false);
            }
        }

        private void UpdateHeroActionVisualState(Button? button, bool isHovered, bool isPressed)
        {
            if (button == null)
                return;

            HeroActionVisualTarget? target = GetHeroActionVisualTarget(button);
            if (target == null)
                return;

            double targetScale = isPressed
                ? target.PressedScale
                : isHovered
                    ? target.HoverScale
                    : 1.0;
            double highlightOpacity = isPressed
                ? target.PressedHighlightOpacity
                : isHovered
                    ? target.HoverHighlightOpacity
                    : 0.0;
            double translateY = isPressed
                ? target.PressedTranslateY
                : 0.0;

            AnimateHeroAction(target.Transform, target.Highlight, targetScale, highlightOpacity, translateY);
        }

        private void AnimateHeroActionClick(Button button)
        {
            HeroActionVisualTarget? target = GetHeroActionVisualTarget(button);
            if (target == null)
                return;

            var storyboard = new Storyboard();

            storyboard.Children.Add(CreateKeyFrameAnimation(
                target.Transform,
                "(CompositeTransform.ScaleX)",
                (TimeSpan.FromMilliseconds(85), target.HoverScale + target.ClickPulseScaleDelta),
                (TimeSpan.FromMilliseconds(190), target.HoverScale)));
            storyboard.Children.Add(CreateKeyFrameAnimation(
                target.Transform,
                "(CompositeTransform.ScaleY)",
                (TimeSpan.FromMilliseconds(85), target.HoverScale + target.ClickPulseScaleDelta),
                (TimeSpan.FromMilliseconds(190), target.HoverScale)));
            storyboard.Children.Add(CreateKeyFrameAnimation(
                target.Transform,
                "(CompositeTransform.TranslateY)",
                (TimeSpan.FromMilliseconds(85), target.ClickPulseTranslateY),
                (TimeSpan.FromMilliseconds(190), 0d)));
            storyboard.Children.Add(CreateKeyFrameAnimation(
                target.Highlight,
                "Opacity",
                (TimeSpan.FromMilliseconds(85), target.HoverHighlightOpacity + target.ClickPulseHighlightDelta),
                (TimeSpan.FromMilliseconds(190), target.HoverHighlightOpacity)));

            storyboard.Begin();
        }

        private static void AnimateHeroAction(CompositeTransform transform, UIElement highlight, double targetScale, double targetHighlightOpacity, double targetTranslateY)
        {
            var storyboard = new Storyboard();

            storyboard.Children.Add(CreateDoubleAnimation(transform, "(CompositeTransform.ScaleX)", targetScale));
            storyboard.Children.Add(CreateDoubleAnimation(transform, "(CompositeTransform.ScaleY)", targetScale));
            storyboard.Children.Add(CreateDoubleAnimation(transform, "(CompositeTransform.TranslateY)", targetTranslateY));
            storyboard.Children.Add(CreateDoubleAnimation(highlight, "Opacity", targetHighlightOpacity));

            storyboard.Begin();
        }

        private static DoubleAnimation CreateDoubleAnimation(DependencyObject target, string propertyPath, double to)
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(150),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, propertyPath);
            return animation;
        }

        private static DoubleAnimationUsingKeyFrames CreateKeyFrameAnimation(
            DependencyObject target,
            string propertyPath,
            params (TimeSpan time, double value)[] keyFrames)
        {
            var animation = new DoubleAnimationUsingKeyFrames
            {
                EnableDependentAnimation = true
            };

            foreach ((TimeSpan time, double value) in keyFrames)
            {
                animation.KeyFrames.Add(new EasingDoubleKeyFrame
                {
                    KeyTime = KeyTime.FromTimeSpan(time),
                    Value = value,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }

            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, propertyPath);
            return animation;
        }

        private HeroActionVisualTarget? GetHeroActionVisualTarget(Button button)
        {
            if (ReferenceEquals(button, DetailsPlayButton))
            {
                return new HeroActionVisualTarget(
                    DetailsPlayTransform,
                    DetailsPlayHighlight,
                    HoverScale: 1.025,
                    PressedScale: 0.97,
                    HoverHighlightOpacity: 0.1,
                    PressedHighlightOpacity: 0.18,
                    PressedTranslateY: 1.5,
                    ClickPulseScaleDelta: 0.018,
                    ClickPulseHighlightDelta: 0.08,
                    ClickPulseTranslateY: -0.8);
            }

            if (ReferenceEquals(button, DetailsSettingsButton))
            {
                return CreateCompactHeroActionTarget(DetailsSettingsTransform, DetailsSettingsHighlight);
            }

            if (ReferenceEquals(button, BackButton))
            {
                return CreateCompactHeroActionTarget(BackButtonTransform, BackButtonHighlight);
            }

            return null;
        }

        private static HeroActionVisualTarget CreateCompactHeroActionTarget(CompositeTransform transform, UIElement highlight)
            => new(
                Transform: transform,
                Highlight: highlight,
                HoverScale: 1.018,
                PressedScale: 0.974,
                HoverHighlightOpacity: 0.08,
                PressedHighlightOpacity: 0.14,
                PressedTranslateY: 1.1,
                ClickPulseScaleDelta: 0.013,
                ClickPulseHighlightDelta: 0.055,
                ClickPulseTranslateY: -0.55);

        private sealed record HeroActionVisualTarget(
            CompositeTransform Transform,
            UIElement Highlight,
            double HoverScale,
            double PressedScale,
            double HoverHighlightOpacity,
            double PressedHighlightOpacity,
            double PressedTranslateY,
            double ClickPulseScaleDelta,
            double ClickPulseHighlightDelta,
            double ClickPulseTranslateY);
    }
}
