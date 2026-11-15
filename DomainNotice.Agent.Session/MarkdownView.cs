using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using WpfInline = System.Windows.Documents.Inline;
using WpfList = System.Windows.Documents.List;

namespace DomainNotice.Agent.Session;

/// <summary>
/// Converts announcement Markdown into a WPF FlowDocument (ported from OpenNotifier). Raw HTML is disabled, images
/// are not loaded (their alt text is shown), and only http/https links are clickable; they open in the browser.
/// </summary>
internal static class MarkdownView
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();
    private static readonly System.Windows.Media.FontFamily BodyFont = new("Segoe UI");
    private static readonly System.Windows.Media.FontFamily CodeFont = new("Consolas");

    public static FlowDocument ToDocument(string? markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = BodyFont,
            FontSize = 14,
        };

        foreach (var block in Markdown.Parse(markdown ?? "", Pipeline))
            AddBlock(document.Blocks, block, 0);

        if (document.Blocks.Count == 0)
            document.Blocks.Add(new Paragraph());

        return document;
    }

    private static void AddBlock(BlockCollection blocks, MdBlock block, int quoteDepth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                blocks.Add(new Paragraph(InlineRuns(heading.Inline))
                {
                    FontSize = heading.Level <= 1 ? 22 : heading.Level == 2 ? 18 : 16,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(quoteDepth * 16, 8, 0, 4),
                });
                break;
            case ParagraphBlock paragraph:
                blocks.Add(new Paragraph(InlineRuns(paragraph.Inline)) { Margin = new Thickness(quoteDepth * 16, 0, 0, 8) });
                break;
            case ListBlock list:
                var element = BuildList(list);
                element.Margin = new Thickness(quoteDepth * 16, 0, 0, 8);
                blocks.Add(element);
                break;
            case QuoteBlock quote:
                foreach (var child in quote)
                    AddBlock(blocks, child, quoteDepth + 1);
                break;
            case FencedCodeBlock or CodeBlock:
                var code = (LeafBlock)block;
                blocks.Add(new Paragraph(new Run(code.Lines.ToString()))
                {
                    FontFamily = CodeFont,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 245)),
                    Padding = new Thickness(6),
                    Margin = new Thickness(quoteDepth * 16, 0, 0, 8),
                });
                break;
            case ThematicBreakBlock:
                blocks.Add(new Paragraph(new Run("—")) { TextAlignment = TextAlignment.Center });
                break;
        }
    }

    private static WpfList BuildList(ListBlock list)
    {
        var element = new WpfList
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 8),
        };

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var listItem = new ListItem();
            foreach (var child in item)
            {
                if (child is ParagraphBlock paragraph)
                    listItem.Blocks.Add(new Paragraph(InlineRuns(paragraph.Inline)));
                else if (child is ListBlock nested)
                    listItem.Blocks.Add(BuildList(nested));
            }

            if (listItem.Blocks.Count == 0)
                listItem.Blocks.Add(new Paragraph());
            element.ListItems.Add(listItem);
        }

        return element;
    }

    private static WpfInline InlineRuns(ContainerInline? container)
    {
        var span = new Span();
        if (container is null)
            return span;

        foreach (var inline in container)
            span.Inlines.Add(CreateInline(inline));
        return span;
    }

    private static WpfInline CreateInline(MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return new Run(literal.Content.ToString());
            case EmphasisInline emphasis:
                var formatted = new Span(InlineRuns(emphasis));
                if (emphasis.DelimiterChar == '~')
                    formatted.TextDecorations = TextDecorations.Strikethrough;
                else if (emphasis.DelimiterCount >= 2)
                    formatted.FontWeight = FontWeights.Bold;
                else
                    formatted.FontStyle = FontStyles.Italic;
                return formatted;
            case LinkInline { IsImage: true } image:
                // Remote images are never loaded; show the alt text.
                return InlineRuns(image);
            case LinkInline link:
                var text = InlineRuns(link);
                if (IsSafeLink(link.Url, out var uri))
                {
                    var hyperlink = new Hyperlink(text) { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
                    hyperlink.RequestNavigate += OpenLink;
                    return hyperlink;
                }

                return text;
            case AutolinkInline autolink when IsSafeLink(autolink.Url, out var autoUri):
                var auto = new Hyperlink(new Run(autolink.Url)) { NavigateUri = autoUri, ToolTip = autoUri.AbsoluteUri };
                auto.RequestNavigate += OpenLink;
                return auto;
            case AutolinkInline autolink:
                return new Run(autolink.Url);
            case CodeInline code:
                return new Run(code.Content) { FontFamily = CodeFont };
            case LineBreakInline lineBreak:
                return lineBreak.IsHard ? new LineBreak() : new Run(" ");
            case ContainerInline container:
                return InlineRuns(container);
            default:
                return new Run("");
        }
    }

    /// <summary>Only absolute http and https links are clickable.</summary>
    internal static bool IsSafeLink(string? url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (!IsSafeLink(e.Uri?.AbsoluteUri, out var uri)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SessionLog.Write($"Opening link failed: {ex.Message}");
        }
    }
}
