using System.Text;

namespace PiSharp.Tui;

public static class AnsiString
{
    public static string Strip(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('\u001b'))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);

        for (var index = 0; index < value.Length;)
        {
            if (value[index] != '\u001b')
            {
                builder.Append(value[index]);
                index++;
                continue;
            }

            index = ReadEscapeSequenceEnd(value, index);
        }

        return builder.ToString();
    }

    public static int VisibleLength(string value) => Strip(value).Length;

    public static string Fit(string value, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(value))
        {
            return new string(' ', width);
        }

        if (!value.Contains('\u001b'))
        {
            return value.Length >= width
                ? value[..width]
                : value.PadRight(width);
        }

        var builder = new StringBuilder(value.Length);
        var visible = 0;

        for (var index = 0; index < value.Length && visible < width;)
        {
            if (value[index] == '\u001b')
            {
                var end = ReadEscapeSequenceEnd(value, index);
                builder.Append(value.AsSpan(index, end - index));
                index = end;
                continue;
            }

            builder.Append(value[index]);
            visible++;
            index++;
        }

        if (visible < width)
        {
            builder.Append(' ', width - visible);
        }
        else
        {
            builder.Append(Ansi.Reset);
        }

        return builder.ToString();
    }

    private static int ReadEscapeSequenceEnd(string value, int index)
    {
        if (index + 1 >= value.Length)
        {
            return index + 1;
        }

        return value[index + 1] switch
        {
            '[' => ReadUntilFinalByte(value, index + 2),
            ']' => ReadOscSequence(value, index + 2),
            _ => Math.Min(value.Length, index + 2),
        };
    }

    private static int ReadUntilFinalByte(string value, int index)
    {
        while (index < value.Length)
        {
            var candidate = value[index];
            index++;
            if (candidate is >= '@' and <= '~')
            {
                break;
            }
        }

        return index;
    }

    private static int ReadOscSequence(string value, int index)
    {
        while (index < value.Length)
        {
            if (value[index] == '\u0007')
            {
                return index + 1;
            }

            if (value[index] == '\u001b' && index + 1 < value.Length && value[index + 1] == '\\')
            {
                return index + 2;
            }

            index++;
        }

        return index;
    }
}
