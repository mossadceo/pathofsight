using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace pathofsight;

internal sealed record TuiPanel(string Title, string Profile, string[] Items, int Selected);

internal sealed class TuiView : IDisposable
{
    internal const string Primary = "210;80;80";
    private const string Muted = "112;119;130", White = "255;255;255", Status = "180;180;180";
    private const string CommandGray = "180;180;180", Red = "180;65;65", Green = "78;186;101";
    private static readonly string[] Logo = ReadLogo();
    private readonly bool ansi;
    private readonly nint handle;
    private readonly uint originalMode;
    private int previousWidth;
    private int previousHeight;
    private string[] _previousRows = [];
    private long _lastFullPaint;

    public TuiView()
    {
        if (Console.IsOutputRedirected) return;
        handle = GetStdHandle(-11);
        ansi = GetConsoleMode(handle, out originalMode) && SetConsoleMode(handle, originalMode | 4);
        if (ansi) Console.Write("\x1b[?1049h\x1b[?25l\x1b[?7l\x1b[2J");
    }

    public void Draw(MapSnapshot snapshot, string notice, bool connected, int width, int height, TuiMenu? menu = null)
    {
        var lines = menu?.Layout(snapshot, notice, connected, width, height) ?? Layout(snapshot, notice, connected, width, height);
        if (ansi)
        {
            Console.Write(RenderFrame(lines, width, height));
        }
        else
        {
            if (Console.IsOutputRedirected)
                foreach (var line in lines.Take(height)) Console.WriteLine(line.Text);
            else
                for (var row = 0; row < Math.Min(lines.Count, height); row++)
                {
                    Console.SetCursorPosition(0, row);
                    Console.Write(lines[row].Text.PadRight(Math.Max(0, width - 1)));
                }
        }
    }

    internal string RenderFrame(List<(string Text, string Color, bool Bold)> lines, int width, int height, bool refresh = false)
    {
        var output = new StringBuilder();
        var now = Environment.TickCount64;
        var fullPaint = refresh || previousWidth != width || previousHeight != height || now - _lastFullPaint >= 2000;
        if (fullPaint) _lastFullPaint = now;
        var rows = new string[height];
        // Overwrite, never erase first. Periodic overwrites repair unobserved resize round-trips.
        for (var row = 0; row < height; row++)
        {
            var line = row < lines.Count ? lines[row] : (Text: "", Color: CommandGray, Bold: false);
            var rendered = new StringBuilder($"\x1b[0m\x1b[38;2;{line.Color}m");
            var currentColor = line.Color;
            var currentBold = false;
            for (var column = 0; column < width; column++)
            {
                var c = column < line.Text.Length ? line.Text[column] : ' ';
                var color = column < line.Text.Length ? ColorAt(line, column) : CommandGray;
                var bold = column < line.Text.Length && BoldAt(line, column);
                if (color != currentColor) rendered.Append($"\x1b[38;2;{color}m");
                if (bold != currentBold) rendered.Append(bold ? "\x1b[1m" : "\x1b[22m");
                currentColor = color;
                currentBold = bold;
                rendered.Append(c);
            }
            rows[row] = rendered.ToString();
            if (fullPaint || row >= _previousRows.Length || rows[row] != _previousRows[row])
                output.Append($"\x1b[{row + 1};1H").Append(rows[row]);
        }
        if (output.Length > 0) output.Append("\x1b[0m");
        _previousRows = rows;
        previousWidth = width;
        previousHeight = height;
        return output.ToString();
    }

    internal static bool BoldAt((string Text, string Color, bool Bold) line, int column)
    {
        if ("│─╭╮╰╯┬┴├┤┼".Contains(line.Text[column])) return false;
        if (line.Text.StartsWith('│') && column < line.Text.IndexOf('│', 1))
            return line.Text.AsSpan(1, line.Text.IndexOf('│', 1) - 1).Trim().SequenceEqual("Path Of Sight");
        return line.Bold;
    }

    internal static string ColorAt((string Text, string Color, bool Bold) line, int column)
    {
        var c = line.Text[column];
        if ("│─╭╮╰╯┬┴├┤┼".Contains(c)) return Muted;
        if (line.Text.StartsWith('│') && column < line.Text.IndexOf('│', 1) && line.Text.Contains("● "))
            return line.Text.Contains("● Game connected", StringComparison.Ordinal) ? Green : Muted;
        for (var y = 0; y < Logo.Length; y++)
        {
            var start = line.Text.IndexOf(Logo[y], StringComparison.Ordinal);
            var x = column - start;
            if (start < 0 || x < 0 || x >= Logo[y].Length) continue;
            var pupil = y is 3 or 4 ? x is >= 11 and <= 18 : y is 2 or 5 && x is >= 12 and <= 17;
            return pupil ? Red : White;
        }
        if (line.Text.StartsWith('│') && column < line.Text.IndexOf('│', 1))
            return line.Text.Contains("Esc Back", StringComparison.Ordinal) ? Primary : CommandGray;
        if (line.Text.StartsWith('╭')) return Primary;
        var commandStart = line.Text.IndexOf("1  Connect to game", StringComparison.Ordinal);
        if (commandStart < 0) commandStart = line.Text.IndexOf("2  Open web map", StringComparison.Ordinal);
        if (commandStart >= 0 && column >= commandStart && c == '✓') return Green;
        return line.Color;
    }

    internal static List<(string Text, string Color, bool Bold)> Layout(
        MapSnapshot snapshot, string notice, bool connected, int width, int height, TuiPanel? panel = null)
    {
        var content = new List<(string Text, string Color, bool Bold)>();
        var limit = Math.Max(0, width - 1);
        string Clean(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        string Fit(string text, int size) => text.Length > size ? text[..Math.Max(0, size - 1)] + "…" : text.PadRight(size);
        void Add(string text = "", string color = CommandGray, bool bold = false)
        {
            text = Clean(text);
            content.Add((text.Length > limit ? text[..limit] : text, color, bold));
        }
        var ready = snapshot.Status == "ready" && snapshot.Fresh;
        var state = ready ? "✓" : snapshot.Status == "demo" ? "DEMO" : connected ? "WAIT" : "OFF";
        if (limit < 64 || height < 18)
        {
            if (height >= 7) Add("Path Of Sight  v0.2.1", Primary, true);
            if (height >= 6) Add(snapshot.Message, Status);
            if (height >= 3)
            {
                Add("1  Connect to game  " + state);
                Add("2  Open web map");
                if (height >= 5) { Add("3  Settings"); Add("4  Profiles"); Add("Q  Quit", Muted); }
                else { Add("3 Settings · 4 Profiles · Q Quit", Muted); }
                if (height >= 8) Add(notice, Status);
            }
            else if (height == 2)
            {
                Add("1 Connect · 2 Web map");
                Add("3 Settings · 4 Profiles · Q Quit", Muted);
            }
            else if (height == 1) Add("1 Game · 2 Web · 3 Settings · 4 Profiles · Q Quit");
        }
        else
        {
            var boxWidth = Math.Min(limit, 100);
            var leftWidth = Math.Clamp(boxWidth * 2 / 5, 30, 36);
            var rightWidth = boxWidth - leftWidth - 3;
            var bodyHeight = Math.Min(height - 5, 19);
            var visibleItems = bodyHeight - 4;
            var firstItem = panel == null ? 0 : Math.Max(0, panel.Selected - visibleItems + 1);
            string Center(string text) => text.PadLeft((leftWidth + text.Length) / 2);
            void Row(string left = "", string right = "", string color = CommandGray, bool bold = false) =>
                Add("│" + Fit(Clean(left), leftWidth) + "│" + Fit(Clean(right), rightWidth) + "│", color, bold);
            const string title = " Path Of Sight  v0.2.1 ";
            Add("╭─" + title + new string('─', leftWidth - title.Length - 1) + "┬" + new string('─', rightWidth) + "╮", Muted);
            var artTop = Math.Max(1, (bodyHeight - Logo.Length - 5) / 2);
            for (var y = 0; y < bodyHeight; y++)
            {
                var left = "";
                var right = "";
                var color = CommandGray;
                var bold = false;
                if (y >= artTop && y < artTop + Logo.Length)
                {
                    left = Center(Logo[y - artTop]);
                    color = CommandGray;
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
                if (panel == null)
                {
                    if (y == 1) { right = "  Functions"; color = Primary; bold = true; }
                    if (y == 3) right = "  " + "1  Connect to game".PadRight(rightWidth - 10) + state;
                    if (y == 4) right = "  2  Open web map";
                    if (y == 5) right = "  3  Settings";
                    if (y == 6) right = "  4  Profiles";
                }
                else
                {
                    if (y == 0) { right = "  " + panel.Title; color = Primary; bold = true; }
                    if (y == 1) { right = "  Profile: " + panel.Profile; color = Primary; }
                    var index = firstItem + y - 3;
                    if (y >= 3 && y < 3 + visibleItems && index < panel.Items.Length)
                    {
                        var selected = index == panel.Selected;
                        right = (selected ? "> " : "  ") + panel.Items[index];
                        color = selected ? White : CommandGray;
                        bold = selected;
                    }
                    if (y == bodyHeight - 1) { right = $"  {panel.Selected + 1}/{panel.Items.Length}"; color = Primary; }
                }
                Row(left, right, color, bold);
            }
            Add("├" + new string('─', leftWidth) + "┼" + new string('─', rightWidth) + "┤", Muted);
            if (panel == null) Row("", "  [1-4] Select".PadRight(rightWidth - 10) + "[Q] Quit", Primary);
            else Row("  Esc Back · Q Quit", "  ↑↓ Select · Enter · ←→ Adjust", Primary);
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

