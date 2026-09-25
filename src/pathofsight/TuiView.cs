using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace pathofsight;

internal sealed class TuiView : IDisposable
{
    private const string Primary = "210;80;80", Muted = "112;119;130", White = "255;255;255", Status = "220;220;220";
    private const string CommandGray = "180;180;180", Red = "180;65;65", Green = "78;186;101";
    private static readonly string[] Logo = ReadLogo();
    private readonly bool ansi;
    private readonly nint handle;
    private readonly uint originalMode;
    private int previousWidth;
    private int previousHeight;

    public TuiView()
    {
        if (Console.IsOutputRedirected) return;
        handle = GetStdHandle(-11);
        ansi = GetConsoleMode(handle, out originalMode) && SetConsoleMode(handle, originalMode | 4);
        if (ansi) Console.Write("\x1b[?1049h\x1b[?25l\x1b[?7l\x1b[2J");
    }

    public void Draw(MapSnapshot snapshot, string notice, bool connected, int width, int height)
    {
        var lines = Layout(snapshot, notice, connected, width, height);
        if (ansi)
        {
            Console.Write(RenderFrame(lines, width, height));
        }
        else
        {
            if (!Console.IsOutputRedirected) Console.Clear();
            foreach (var line in lines.Take(Math.Max(1, height - 1))) Console.WriteLine(line.Text);
        }
    }

    internal string RenderFrame(List<(string Text, string Color, bool Bold)> lines, int width, int height)
    {
        var output = new StringBuilder();
        if (previousWidth != width || previousHeight != height)
            output.Append("\x1b[2J");
        // Terminal reflow can invalidate any row, including after a resize round-trip.
        // Repaint the full frame; disabled autowrap prevents scrolling during resize races.
        for (var row = 0; row < height; row++)
        {
            output.Append($"\x1b[{row + 1};1H\x1b[0m\x1b[2K");
            if (row >= lines.Count) continue;
            var line = lines[row];
            output.Append($"\x1b[38;2;{line.Color}m");
            if (line.Bold) output.Append("\x1b[1m");
            var currentColor = line.Color;
            for (var column = 0; column < line.Text.Length; column++)
            {
                var c = line.Text[column];
                var color = ColorAt(line, column);
                if (color != currentColor) output.Append($"\x1b[38;2;{color}m");
                currentColor = color;
                output.Append(c);
            }
        }
        output.Append("\x1b[0m");
        previousWidth = width;
        previousHeight = height;
        return output.ToString();
    }

    internal static string ColorAt((string Text, string Color, bool Bold) line, int column)
    {
        var c = line.Text[column];
        if ("│─╭╮╰╯┬┴├┤┼".Contains(c)) return Muted;
        for (var y = 0; y < Logo.Length; y++)
        {
            var start = line.Text.IndexOf(Logo[y], StringComparison.Ordinal);
            var x = column - start;
            if (start < 0 || x < 0 || x >= Logo[y].Length) continue;
            var pupil = y is 3 or 4 ? x is >= 10 and <= 17 : y is 2 or 5 && x is >= 11 and <= 16;
            return pupil ? Red : White;
        }
        var commandStart = line.Text.IndexOf("1  Connect to game", StringComparison.Ordinal);
        if (commandStart < 0) commandStart = line.Text.IndexOf("2  Open web map", StringComparison.Ordinal);
        if (commandStart >= 0 && column >= commandStart) return c == '✓' ? Green : CommandGray;
        return line.Color;
    }

    internal static List<(string Text, string Color, bool Bold)> Layout(
        MapSnapshot snapshot, string notice, bool connected, int width, int height)
    {
        var content = new List<(string Text, string Color, bool Bold)>();
        var limit = Math.Max(0, width - 1);
        string Clean(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        string Fit(string text, int size) => text.Length > size ? text[..Math.Max(0, size - 1)] + "…" : text.PadRight(size);
        void Add(string text = "", string color = White, bool bold = false)
        {
            text = Clean(text);
            content.Add((text.Length > limit ? text[..limit] : text, color, bold));
        }
        var ready = snapshot.Status == "ready" && snapshot.Fresh;
        var state = ready ? "✓" : snapshot.Status == "demo" ? "DEMO" : connected ? "WAIT" : "OFF";
        if (limit < 64 || height < 18)
        {
            if (height >= 5) Add("Path Of Sight  v0.1.0", Primary, true);
            if (height >= 4) Add(snapshot.Message, Status);
            if (height >= 3)
            {
                Add("1  Connect to game  " + state);
                Add("2  Open web map");
                Add("Q  Quit", Muted);
            }
            else if (height == 2)
            {
                Add("1 Connect  ·  2 Web map");
                Add("Q Quit", Muted);
            }
            else if (height == 1) Add("1 Connect · 2 Web map · Q Quit");
        }
        else
        {
            var boxWidth = Math.Min(limit, 100);
            var leftWidth = Math.Clamp(boxWidth * 2 / 5, 28, 36);
            var rightWidth = boxWidth - leftWidth - 3;
            var bodyHeight = Math.Min(height - 5, 19);
            string Center(string text) => text.PadLeft((leftWidth + text.Length) / 2);
            void Row(string left = "", string right = "", string color = White, bool bold = false) =>
                Add("│" + Fit(Clean(left), leftWidth) + "│" + Fit(Clean(right), rightWidth) + "│", color, bold);
            const string title = " Path Of Sight  v0.1.0 ";
            Add("╭─" + title + new string('─', leftWidth - title.Length - 1) + "┬" + new string('─', rightWidth) + "╮", Muted);
            var artTop = Math.Max(1, (bodyHeight - Logo.Length - 5) / 2);
            for (var y = 0; y < bodyHeight; y++)
            {
                var left = "";
                var right = "";
                var color = White;
                var bold = false;
                if (y >= artTop && y < artTop + Logo.Length)
                {
                    left = Center(Logo[y - artTop]);
                    color = White;
                }
                if (y == artTop + Logo.Length + 1) { left = Center("Path Of Sight"); bold = true; }
                if (y == artTop + Logo.Length + 3)
                {
                    left = Center("● " + (ready ? "Game connected" : snapshot.Status switch
                    {
                        "demo" => "Demo mode",
                        "loading" => "Game detected",
                        "access" => "Access denied",
                        "error" => "Connection error",
                        "incompatible" => "Update required",
                        _ => connected ? "Waiting for game" : "Not connected"
                    }));
                    color = ready ? "78;186;101" : Muted;
                }
                if (y == 1) { right = "  Functions"; color = Primary; bold = true; }
                if (y == 3) right = "  " + "1  Connect to game".PadRight(rightWidth - 10) + state;
                if (y == 4) right = "  2  Open web map";
                Row(left, right, color, bold);
            }
            Add("├" + new string('─', leftWidth) + "┼" + new string('─', rightWidth) + "┤", Muted);
            Row("", "  [1-2] Select".PadRight(rightWidth - 10) + "[Q] Quit", Primary);
            Add("╰" + new string('─', leftWidth) + "┴" + new string('─', rightWidth) + "╯", Muted);
            var detail = Clean(snapshot.Area.Length > 0 ? snapshot.Message + " · " + snapshot.Area : snapshot.Message);
            var message = Clean(notice);
            if (message == "Connecting to game" && snapshot.Status != "waiting") message = "";
            if (message.Length > 0) detail += " · " + message;
            while (detail.Length > 0 && content.Count < height)
            {
                var take = Math.Min(detail.Length, Math.Max(1, limit - 2));
                Add("  " + detail[..take], Status);
                detail = detail[take..];
            }
        }
        while (content.Count < height) Add();
        return content;
    }
    private static string[] ReadLogo()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("pathofsight.Art.eye.txt")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n').Select(x => x.TrimEnd('\r'))
            .SkipWhile(string.IsNullOrWhiteSpace).Reverse().SkipWhile(string.IsNullOrWhiteSpace)
            .Reverse().ToArray();
    }

    public void Dispose()
    {
        if (!ansi) return;
        Console.Write("\x1b[0m\x1b[?7h\x1b[?25h\x1b[?1049l");
        SetConsoleMode(handle, originalMode);
    }

    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int number);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(nint handle, uint mode);
}

