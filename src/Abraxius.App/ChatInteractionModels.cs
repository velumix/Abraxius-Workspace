namespace Abraxius.App;

public enum ChatMode
{
    Chat,
    Mission
}

public sealed record ChatContextChip(string Id, string Label, string Kind, bool IsExplicit = true);

public sealed record ChatSuggestion(string Prefix, string Value, string Label, string Detail);
