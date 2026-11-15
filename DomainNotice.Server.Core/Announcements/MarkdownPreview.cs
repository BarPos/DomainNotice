using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DomainNotice.Server.Announcements;

/// <summary>
/// Safe HTML for the Console's live preview: Markdig with raw HTML disabled, and only absolute http/https links and
/// images (anything else — javascript:, data:, relative, mailto: — is reduced to its text).
/// </summary>
public static class MarkdownPreview
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var document = Markdown.Parse(markdown, Pipeline);

        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (IsAllowedUrl(link.Url)) continue;
            if (link.IsImage)
            {
                var alt = string.Concat(link.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
                link.ReplaceBy(new LiteralInline(alt));
            }
            else
            {
                Unwrap(link);
            }
        }
        foreach (var auto in document.Descendants<AutolinkInline>().ToList())
        {
            if (!auto.IsEmail && IsAllowedUrl(auto.Url)) continue;
            auto.ReplaceBy(new LiteralInline(auto.Url));
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    public static bool IsAllowedUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Replaces a link by its children (the link text stays, the href goes).</summary>
    private static void Unwrap(LinkInline link)
    {
        Inline anchor = link;
        var child = link.FirstChild;
        while (child is not null)
        {
            var next = child.NextSibling;
            child.Remove();
            anchor.InsertAfter(child);
            anchor = child;
            child = next;
        }
        link.Remove();
    }
}
