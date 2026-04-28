using System.Text;

namespace DiscordScraper.Contracts.IR;

/// <summary>
/// Produces flat plain text from a MessageIR for semantic use — embedding model input and
/// read-side plain_text column. Goal is semantic fidelity, not visual fidelity: formatting
/// markers are dropped, structural hints (code fences, quotes) become whitespace breaks.
/// MessageRenderer handles full display rendering with name resolution.
/// </summary>
public static class IrTextFlattener
{
    public static string Flatten(MessageIR ir)
    {
        var sb = new StringBuilder();
        AppendNodes(ir.Body, sb);
        return sb.ToString().Trim();
    }

    private static void AppendNodes(IReadOnlyList<MessageNode> nodes, StringBuilder sb)
    {
        foreach (var node in nodes)
            AppendNode(node, sb);
    }

    private static void AppendNode(MessageNode node, StringBuilder sb)
    {
        switch (node)
        {
            case TextNode t:
                sb.Append(t.Text);
                break;

            case MentionNode m:
                // Use captured fallback when available; fall back to ID string so the
                // embedding model sees a token rather than nothing.
                sb.Append('@');
                sb.Append(m.Fallback ?? m.Id?.ToString() ?? "unknown");
                break;

            case ChannelRefNode c:
                sb.Append('#');
                sb.Append(c.Fallback ?? c.ChannelId.ToString());
                break;

            case EmojiNode e when e.Kind == EmojiKind.Unicode:
                // Unicode glyphs are meaningful semantic tokens for the embedding model.
                sb.Append(e.NameOrGlyph);
                break;

            case EmojiNode e:
                // Custom emoji: use colon-delimited name as a semantic token.
                sb.Append(':');
                sb.Append(e.NameOrGlyph);
                sb.Append(':');
                break;

            case TimestampNode ts:
                // ISO-8601 is the most widely understood format for LLM input.
                sb.Append(ts.Value.ToString("o"));
                break;

            case FormattingNode f:
                // Drop formatting markers — bold/italic/spoiler carry no semantic weight.
                AppendNodes(f.Children, sb);
                break;

            case CodeBlockNode cb:
                // Preserve content with newline breaks; language is metadata, not text.
                sb.Append('\n');
                sb.Append(cb.Content);
                sb.Append('\n');
                break;

            case InlineCodeNode ic:
                sb.Append(ic.Content);
                break;

            case QuoteNode q:
                // "> " prefix signals quoted context to the embedding model.
                sb.Append("> ");
                AppendNodes(q.Children, sb);
                break;

            case LinkNode l:
                // Display text carries semantic meaning; URL is secondary context.
                // Emit display text first; append URL only when display is empty so the
                // embedding model always has something to embed.
                var displayStart = sb.Length;
                AppendNodes(l.DisplayChildren, sb);
                var hasDisplay = sb.Length > displayStart;
                if (hasDisplay)
                {
                    sb.Append(" (");
                    sb.Append(l.Url);
                    sb.Append(')');
                }
                else
                {
                    sb.Append(l.Url);
                }
                break;
        }
    }
}
