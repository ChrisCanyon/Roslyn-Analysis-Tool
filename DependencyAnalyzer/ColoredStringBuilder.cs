using System;
using System.Collections.Generic;
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
}
