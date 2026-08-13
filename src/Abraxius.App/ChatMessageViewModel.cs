using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Abraxius.App;

public sealed class ChatMessageViewModel : INotifyPropertyChanged
{
    private string _rawModelText;
    private string _text;
    private bool _isStreaming;
    private bool _isError;
    private bool _isCancelled;

    public ChatMessageViewModel(Guid id, string speaker, string text, DateTimeOffset timestamp, bool isUser, bool isStreaming = false, bool isError = false)
    {
        Id = id;
        Speaker = speaker;
        _text = text;
        _rawModelText = text;
        Timestamp = timestamp;
        IsUser = isUser;
        _isStreaming = isStreaming;
        _isError = isError;
        RebuildContent();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; }
    public string Speaker { get; }
    public DateTimeOffset Timestamp { get; }
    public bool IsUser { get; }
    public bool IsAssistant => !IsUser;
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    public string StateText => IsStreaming ? "Thinking…" : IsCancelled ? "Response cancelled" : IsError ? "Unable to answer" : string.Empty;
    public string RawModelText => _rawModelText;
    public string Text { get => _text; private set => SetProperty(ref _text, value); }
    public bool IsStreaming { get => _isStreaming; private set => SetProperty(ref _isStreaming, value); }
    public bool IsComplete => !IsStreaming;
    public bool IsError { get => _isError; private set => SetProperty(ref _isError, value); }
    public bool IsCancelled { get => _isCancelled; private set => SetProperty(ref _isCancelled, value); }
    public ObservableCollection<ChatContentBlock> ContentBlocks { get; } = [];

    public void UpdateStreamingText(string text)
    {
        _rawModelText = text;
        Text = text;
        OnPropertyChanged(nameof(RawModelText));
        OnPropertyChanged(nameof(StateText));
    }

    public void Complete(string text, bool error = false, bool cancelled = false)
    {
        _rawModelText = text;
        Text = text;
        IsError = error;
        IsCancelled = cancelled;
        IsStreaming = false;
        OnPropertyChanged(nameof(IsComplete));
        RebuildContent();
        OnPropertyChanged(nameof(RawModelText));
        OnPropertyChanged(nameof(StateText));
    }

    private void RebuildContent()
    {
        ContentBlocks.Clear();
        foreach (var block in ChatMarkdownParser.Parse(Text))
        {
            ContentBlocks.Add(block);
        }
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
