using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Dictate;

/// <summary>
/// Desktop Acrylic that stays translucent while its window is inactive. The built-in backdrop
/// falls back to a solid color when the window doesn't have focus, and the recording pill never does.
/// </summary>
internal sealed class AlwaysActiveAcrylic : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _config;
    private FrameworkElement? _root;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        _config = new SystemBackdropConfiguration { IsInputActive = true };
        _root = xamlRoot.Content as FrameworkElement;
        if (_root is not null)
        {
            _root.ActualThemeChanged += OnThemeChanged;
            OnThemeChanged(_root, null);
        }

        _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        _controller.AddSystemBackdropTarget(target);
        _controller.SetSystemBackdropConfiguration(_config);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        if (_root is not null) _root.ActualThemeChanged -= OnThemeChanged;
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
        base.OnTargetDisconnected(target);
    }

    private void OnThemeChanged(FrameworkElement sender, object? args)
    {
        if (_config is null) return;
        _config.Theme = sender.ActualTheme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }
}
