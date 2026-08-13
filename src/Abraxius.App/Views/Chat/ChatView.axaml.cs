using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using System.Collections.Specialized;

namespace Abraxius.App.Views.Chat;

public partial class ChatView : UserControl
{
    private bool _userIsNearBottom = true;
    private bool _ignoreScrollChange;
    private MainViewModel? _subscribedViewModel;

    public ChatView()
    {
        InitializeComponent();
        ChatInput.AddHandler(InputElement.KeyDownEvent, OnChatKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
    }

    private MainViewModel ViewModel => DataContext as MainViewModel
        ?? throw new InvalidOperationException("ChatView requires a MainViewModel data context.");

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.Chat.Messages.CollectionChanged -= OnMessagesChanged;
            foreach (var message in _subscribedViewModel.Chat.Messages)
            {
                message.PropertyChanged -= OnMessagePropertyChanged;
            }
        }

        _subscribedViewModel = DataContext as MainViewModel;
        if (_subscribedViewModel is null) return;

        _subscribedViewModel.Chat.Messages.CollectionChanged += OnMessagesChanged;
        foreach (var message in _subscribedViewModel.Chat.Messages)
        {
            message.PropertyChanged += OnMessagePropertyChanged;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var message in e.OldItems.OfType<ChatMessageViewModel>())
            {
                message.PropertyChanged -= OnMessagePropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var message in e.NewItems.OfType<ChatMessageViewModel>())
            {
                message.PropertyChanged += OnMessagePropertyChanged;
            }
        }

        FollowLatestIfNeeded();
    }

    private void OnMessagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatMessageViewModel.Text) or nameof(ChatMessageViewModel.ContentBlocks) or nameof(ChatMessageViewModel.IsStreaming))
        {
            FollowLatestIfNeeded();
        }
    }

    private void OnChatKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var commandModifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (shift)
        {
            // AcceptsReturn handles Shift+Enter as a normal line break.
            return;
        }

        e.Handled = true;
        if (commandModifier)
        {
            ViewModel.RunChatAsMissionCommand.Execute(null);
        }
        else if (ViewModel.Chat.SendCommand.CanExecute(null))
        {
            ViewModel.Chat.SendCommand.Execute(null);
        }
    }

    private void OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_ignoreScrollChange || sender is not ScrollViewer viewer) return;
        _userIsNearBottom = viewer.Offset.Y >= Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height - 48);
        LatestButton.IsVisible = !_userIsNearBottom;
    }

    private void OnLatestClicked(object? sender, RoutedEventArgs e)
    {
        if (TranscriptScroller is null) return;
        _ignoreScrollChange = true;
        TranscriptScroller.ScrollToEnd();
        _ignoreScrollChange = false;
        _userIsNearBottom = true;
        LatestButton.IsVisible = false;
    }

    private async void OnCopyMessage(object? sender, RoutedEventArgs e)
    {
        await CopyTextAsync(sender);
    }

    private async void OnCopyBlock(object? sender, RoutedEventArgs e)
    {
        await CopyTextAsync(sender);
    }

    private async Task CopyTextAsync(object? sender)
    {
        if (sender is not Button { Tag: string text } || TopLevel.GetTopLevel(this) is not TopLevel topLevel || topLevel.Clipboard is null) return;
        await topLevel.Clipboard.SetTextAsync(text);
    }

    public void FollowLatestIfNeeded()
    {
        if (!_userIsNearBottom) return;
        Dispatcher.UIThread.Post(() => TranscriptScroller?.ScrollToEnd(), DispatcherPriority.Background);
    }
}
