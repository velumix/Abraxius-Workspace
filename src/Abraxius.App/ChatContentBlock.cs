namespace Abraxius.App;

/// <summary>
/// Safe, renderer-owned content produced from Markdown. No HTML or executable markup
/// crosses this boundary into the Avalonia view.
/// </summary>
public abstract record ChatContentBlock
{
}

public sealed record ChatParagraphBlock(string Text) : ChatContentBlock;
public sealed record ChatHeadingBlock(string Text, int Level) : ChatContentBlock;
public sealed record ChatListBlock(IReadOnlyList<string> Items, bool Ordered) : ChatContentBlock;
public sealed record ChatQuoteBlock(string Text) : ChatContentBlock;
public sealed record ChatCodeFenceBlock(string Code, string Language) : ChatContentBlock;
public sealed record ChatLinkBlock(string Text, string Url) : ChatContentBlock;
public sealed record ChatSeparatorBlock : ChatContentBlock;
