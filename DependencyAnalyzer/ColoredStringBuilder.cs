using System.Text;

class ColoredSegment
{
    public ConsoleColor Color { get; }
    public string Text { get; }

    public ColoredSegment(ConsoleColor color, string text)
    {
        Color = color;
        Text = text;
    }
}

public class ColoredStringBuilder
{
    private readonly List<ColoredSegment> _segments = new();

    public ColoredStringBuilder Append(string text, ConsoleColor color)
    {
        _segments.Add(new ColoredSegment(color, text));
        return this;
    }

    public ColoredStringBuilder AppendLine(string text, ConsoleColor color)
    {
        _segments.Add(new ColoredSegment(color, text + '\n'));
        return this;
    }

    public void Write()
    {
        foreach (var segment in _segments)
        {
            Console.ForegroundColor = segment.Color;
            Console.Write(segment.Text);
        }
        Console.ResetColor();
        Console.WriteLine(); // Optional: move to new line
    }

    public string ToString()
    {
        var sb = new StringBuilder();
        foreach (var segment in _segments)
        {
            sb.Append(segment.Text);
        }

        return sb.ToString();
    }

    public string ToHTMLString()
    {
        var sb = new StringBuilder();
        foreach (var segment in _segments)
        {
            sb.Append(
                $"<span style=\"color: {ConsoleColorToHexString(segment.Color)};\">" +
                    $"{System.Net.WebUtility.HtmlEncode(segment.Text)}" +
                $"</span>");
        }
        return sb.ToString();
    }

    private string ConsoleColorToHexString(ConsoleColor color)
    {
        return color switch
        {
            ConsoleColor.Black => "#000000",
            ConsoleColor.DarkBlue => "#00008B",
            ConsoleColor.DarkGreen => "#006400",
            ConsoleColor.DarkCyan => "#008B8B",
            ConsoleColor.DarkRed => "#8B0000",
            ConsoleColor.DarkMagenta => "#8B008B",
            ConsoleColor.DarkYellow => "#DAA520",
            ConsoleColor.Gray => "#C0C0C0",
            ConsoleColor.DarkGray => "#A9A9A9",
            ConsoleColor.Blue => "#4169E1",
            ConsoleColor.Green => "#00E600",
            ConsoleColor.Cyan => "#00FFFF",
            ConsoleColor.Red => "#FF0000",
            ConsoleColor.Magenta => "#FF69B4",
            ConsoleColor.Yellow => "#FFFF00",
            ConsoleColor.White => "#FFFFFF",
            _ => "#000000" // Fallback
        };
    }
}
