using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Abraxius.App;

public static class ChatMarkdownParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    public static IReadOnlyList<ChatContentBlock> Parse(string markdown)
    {
        var result = new List<ChatContentBlock>();
        var document = Markdown.Parse(markdown ?? string.Empty, Pipeline);

        foreach (var block in document)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    AddText(result, new ChatHeadingBlock(InlineText(heading.Inline), heading.Level));
                    break;
                case ParagraphBlock paragraph:
                    AddParagraph(result, paragraph.Inline);
                    break;
                case FencedCodeBlock code:
                    result.Add(new ChatCodeFenceBlock(CodeText(code), code.Info ?? string.Empty));
                    break;
                case QuoteBlock quote:
                    AddText(result, new ChatQuoteBlock(BlockText(quote)));
                    break;
                case ListBlock list:
                    result.Add(new ChatListBlock(
                        list.OfType<ListItemBlock>().Select(BlockText).Where(static text => text.Length > 0).ToArray(),
                        list.IsOrdered));
                    break;
                case ThematicBreakBlock:
                    result.Add(new ChatSeparatorBlock());
                    break;
                case HtmlBlock html:
                    // Raw HTML is represented as text; it is never interpreted as UI markup.
                    AddText(result, new ChatParagraphBlock(BlockText(html)));
                    break;
                default:
                    AddText(result, new ChatParagraphBlock(BlockText(block)));
                    break;
            }
        }

        if (result.Count == 0 && !string.IsNullOrWhiteSpace(markdown))
        {
            result.Add(new ChatParagraphBlock(markdown.Trim()));
        }

        return result;
    }

    private static void AddParagraph(List<ChatContentBlock> result, ContainerInline? inline)
    {
        var text = InlineText(inline);
        if (text.Length > 0)
        {
            result.Add(new ChatParagraphBlock(text));
        }
    }

    private static void AddText(List<ChatContentBlock> result, ChatContentBlock block)
    {
        var text = block switch
        {
            ChatParagraphBlock paragraph => paragraph.Text,
            ChatHeadingBlock heading => heading.Text,
            ChatQuoteBlock quote => quote.Text,
            _ => null
        };

        if (text is not null && text.Length == 0) return;

        result.Add(block);
    }

    private static string InlineText(ContainerInline? inline)
    {
        if (inline is null) return string.Empty;
        return string.Concat(inline.Descendants<LiteralInline>().Select(static literal => literal.Content.ToString())).Trim();
    }

    private static string BlockText(Block block)
    {
        // ParagraphBlock is a leaf block whose content lives in Inline rather than
        // child Block objects.  Falling through to Block.ToString() leaks Markdig's
        // runtime type name into the transcript (especially for list items).
        if (block is ParagraphBlock paragraph)
        {
            return InlineText(paragraph.Inline);
        }

        if (block is HeadingBlock heading)
        {
            return InlineText(heading.Inline);
        }

        if (block is ContainerBlock container)
        {
            return string.Join(Environment.NewLine,
                container.OfType<Block>()
                    .Select(BlockText)
                    .Where(static text => text.Length > 0))
                .Trim();
        }

        // Unknown Markdown nodes are intentionally omitted rather than rendered
        // through ToString(), which is not user-facing content.
        return string.Empty;
    }

    private static string CodeText(FencedCodeBlock code)
    {
        return string.Join(Environment.NewLine, code.Lines.Lines
            .Take(code.Lines.Count)
            .Select(static line => line.ToString()))
            .TrimEnd();
    }
}
