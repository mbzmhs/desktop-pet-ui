using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Border = System.Windows.Controls.Border;
using Orientation = System.Windows.Controls.Orientation;

namespace DesktopPetUi.Core;

/// <summary>
/// 轻量 Markdown → WPF 元素渲染（聊天窗暗色主题）。支持：#/##/### 标题、``` 代码块、
/// -/*/数字 列表、--- 分隔线、**粗体**、*斜体*、`行内代码`、~~删除线~~、[文字](链接)。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly Brush TextBrush = Frozen("#E8E8E8");
    private static readonly Brush CodeBg = Frozen("#26282C");
    private static readonly Brush InlineCodeBg = Frozen("#4A4D55");
    private static readonly Brush LinkBrush = Frozen("#7EB8F5");
    private static readonly FontFamily Mono = new("Consolas");

    public static UIElement Render(string text)
    {
        var root = new StackPanel { Orientation = Orientation.Vertical };
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        for (var idx = 0; idx < lines.Length; idx++)
        {
            var line = lines[idx];
            if (line.TrimStart().StartsWith("```"))
            {
                var sb = new StringBuilder();
                idx++;
                while (idx < lines.Length && !lines[idx].TrimStart().StartsWith("```"))
                {
                    sb.Append(lines[idx]).Append('\n');
                    idx++;
                }
                AddCodeBlock(root, sb.ToString().TrimEnd('\n'));
            }
            else
            {
                AppendLine(root, line);
            }
        }
        if (root.Children.Count == 0)
            root.Children.Add(new TextBlock { Foreground = TextBrush, FontSize = 13.5 });
        return root;
    }

    private static void AddCodeBlock(StackPanel root, string code)
    {
        root.Children.Add(new Border
        {
            Background = CodeBg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 4, 0, 4),
            Child = new TextBlock
            {
                Text = code.Length > 0 ? code : " ",
                FontFamily = Mono,
                FontSize = 12.5,
                Foreground = Frozen("#D8DEE4"),
                LineHeight = 17,
            },
        });
    }

    private static void AppendLine(StackPanel root, string line)
    {
        var trimmed = line.TrimStart();

        if (trimmed.StartsWith("### "))
        {
            root.Children.Add(Para(trimmed[4..], bold: true, size: 14.5));
            return;
        }
        if (trimmed.StartsWith("## "))
        {
            root.Children.Add(Para(trimmed[3..], bold: true, size: 15.5));
            return;
        }
        if (trimmed.StartsWith("# "))
        {
            root.Children.Add(Para(trimmed[2..], bold: true, size: 16.5));
            return;
        }

        if (trimmed.Length >= 3 && trimmed.All(ch => ch == '-') || trimmed.Length >= 3 && trimmed.All(ch => ch == '*'))
        {
            root.Children.Add(new Border
            {
                Height = 1,
                Background = Frozen("#444"),
                Margin = new Thickness(0, 6, 0, 6),
            });
            return;
        }

        if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
        {
            root.Children.Add(ListItem("•  ", trimmed[2..]));
            return;
        }
        var i = 0;
        while (i < line.Length && char.IsDigit(line[i])) i++;
        if (i > 0 && i < line.Length && (line[i] == '.' || line[i] == ')') && i + 1 < line.Length && line[i + 1] == ' ')
        {
            root.Children.Add(ListItem(line[..(i + 1)] + " ", line[(i + 2)..]));
            return;
        }

        if (trimmed.Length == 0) return; // 空行 = 段落间距（由 Margin 承担）
        root.Children.Add(Para(trimmed, bold: false, size: 13.5));
    }

    private static TextBlock Para(string text, bool bold, double size)
    {
        var tb = new TextBlock
        {
            FontSize = size,
            Foreground = TextBrush,
            LineHeight = size + 5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
        };
        if (bold) tb.FontWeight = FontWeights.Bold;
        AppendInlines(tb.Inlines, text);
        return tb;
    }

    private static TextBlock ListItem(string prefix, string rest)
    {
        var tb = new TextBlock
        {
            FontSize = 13.5,
            Foreground = TextBrush,
            LineHeight = 18.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 2, 0, 2),
        };
        tb.Inlines.Add(new Run(prefix) { Foreground = Frozen("#9AA0A6") });
        AppendInlines(tb.Inlines, rest);
        return tb;
    }

    /// <summary>行内格式：`code` &gt; **bold** &gt; ~~strike~~ &gt; *italic* &gt; [text](url)。未配对的格式字符按字面输出。</summary>
    private static void AppendInlines(InlineCollection target, string s)
    {
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];

            if (c == '`')
            {
                var end = s.IndexOf('`', i + 1);
                if (end > i)
                {
                    target.Add(new Run(s[(i + 1)..end])
                    {
                        FontFamily = Mono,
                        FontSize = 12.5,
                        Background = InlineCodeBg,
                    });
                    i = end + 1; // 跳过收尾反引号
                    continue;
                }
                target.Add(new Run("`"));
                i++;
                continue;
            }

            if (c == '*' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 1)
                {
                    var span = new Span();
                    AppendInlines(span.Inlines, s[(i + 2)..end]);
                    foreach (Inline inl in span.Inlines)
                        if (inl is Run r) r.FontWeight = FontWeights.Bold;
                    target.Add(span);
                    i = end + 2; // 跳过收尾 **
                    continue;
                }
            }

            if (c == '~' && i + 1 < s.Length && s[i + 1] == '~')
            {
                var end = s.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 1)
                {
                    target.Add(new Run(s[(i + 2)..end]) { TextDecorations = TextDecorations.Strikethrough });
                    i = end + 2; // 跳过收尾 ~~
                    continue;
                }
            }

            if (c == '*')
            {
                var end = s.IndexOf('*', i + 1);
                if (end > i + 1)
                {
                    target.Add(new Run(s[(i + 1)..end]) { FontStyle = FontStyles.Italic });
                    i = end + 1; // 跳过收尾 *
                    continue;
                }
            }

            if (c == '[')
            {
                var close = s.IndexOf(']', i + 1);
                if (close > i && close + 1 < s.Length && s[close + 1] == '(')
                {
                    var paren = s.IndexOf(')', close + 2);
                    if (paren > close)
                    {
                        target.Add(new Run(s[(i + 1)..close]) { Foreground = LinkBrush });
                        i = paren + 1; // 跳过收尾 )
                        continue;
                    }
                }
                // 不是合法链接：'[' 按字面输出
                target.Add(new Run("["));
                i++;
                continue;
            }

            // 普通段直到下一个格式字符；若当前位置本身就是未配对的格式字符（如孤立 * ~），按字面输出保证前进
            var start = i;
            while (i < s.Length && !(s[i] is '`' or '*' or '~' or '[')) i++;
            if (i > start) target.Add(new Run(s[start..i]));
            else { target.Add(new Run(c.ToString())); i++; }
        }
    }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
