using System.Globalization;

namespace pathofsight;

internal sealed class TuiMenu(SettingsStore store)
{
    public string Page { get; private set; } = "Home";
    public int Selected { get; private set; }
    public string Notice { get; private set; } = "";
    private readonly Stack<(string Page, int Selected)> _back = new();
    private sealed record Item(string Label, Action<int> Run);

    private void Open(string page)
    {
        _back.Push((Page, Selected)); Page = page; Selected = 0; Notice = "";
    }
    private List<Item> Items()
    {
        var view = store.Read();
        var s = view.ProfileSettings;
        var items = new List<Item>();
        void Link(string label) => items.Add(new(label, _ => Open(label)));
        void Toggle(string label, bool value, Func<DisplaySettings, DisplaySettings> change) =>
            items.Add(new($"[{(value ? '✓' : ' ')}] {label}", _ => store.Update(change)));
        void Number(string label, double value, double step, double min, double max, Func<DisplaySettings, double, DisplaySettings> change) =>
            items.Add(new($"{label}: {value.ToString("0.##", CultureInfo.InvariantCulture)}", direction =>
                store.Update(current => change(current, Math.Round(Math.Clamp(value + step * direction, min, max), 2)))));
        switch (Page)
        {
            case "Home":
                Link("Connect to game"); Link("Open web map"); Link("Settings"); Link("Profiles"); break;
            case "Profiles":
                foreach (var name in ProfileSettings.Names)
                    items.Add(new($"{Array.IndexOf(ProfileSettings.Names, name) + 1}  {name}" + (view.Profile == name ? "  [active]" : ""),
                        _ => store.SelectProfile(name)));
                items.Add(new("Reset current", _ => store.SelectProfile(store.Read().Profile, reset: true)));
                break;
            case "Settings":
                Link("Active POI"); Link("Routing"); Link("Display"); Link("Alignment"); break;
            case "Active POI":
                foreach (var section in PoiCatalog.Sections) Link(section);
                break;
            case "Routing":
                foreach (var (id, label) in new[] { ("off", "Off"), ("manual", "Manual"), ("atlas", "Atlas bosses") })
                    items.Add(new($"[{(s.Routing == id ? '✓' : ' ')}] {label}", _ => store.Update(current => current with { Routing = id })));
                break;
            case "Display":
                Toggle("Overlay", s.Overlay, current => current with { Overlay = !current.Overlay });
                Toggle("Labels", s.Labels, current => current with { Labels = !current.Labels });
                Toggle("Terrain", s.Terrain, current => current with { Terrain = !current.Terrain });
                Number("Opacity", s.Opacity, .05, .1, 1, (current, value) => current with { Opacity = value });
                Number("Icon size", s.IconSize, 1, 3, 16, (current, value) => current with { IconSize = value });
                break;
            case "Alignment":
                Number("Scale", s.Scale, .01, .25, 4, (current, value) => current with { Scale = value });
                Number("Offset X", s.OffsetX, 1, -2000, 2000, (current, value) => current with { OffsetX = value });
                Number("Offset Y", s.OffsetY, 1, -2000, 2000, (current, value) => current with { OffsetY = value });
                items.Add(new("Reset alignment", _ => store.Update(current => current with { Scale = 1, OffsetX = 0, OffsetY = 0 })));
                break;
            default:
                foreach (var filter in PoiCatalog.All.Where(x => x.Section == Page))
                    items.Add(new($"[{(!filter.Available ? '-' : s.Categories.Contains(filter.Id) ? '✓' : ' ')}] {filter.Name}"
                        + (filter.Note.Length > 0 ? " · " + filter.Note : ""), _ =>
                    {
                        if (!filter.Available) { Notice = filter.Note; return; }
                        store.Update(current => current with { Categories = current.Categories.Contains(filter.Id)
                            ? current.Categories.Where(x => x != filter.Id).ToArray() : current.Categories.Append(filter.Id).ToArray() }, filter.Id);
                    }));
                break;
        }
        return items;
    }

    // Return only commands owned by the app (connect, browser, quit).
    public char Handle(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Q || char.ToLowerInvariant(key.KeyChar) == 'q') return 'q';
        if (key.Key == ConsoleKey.Escape || key.KeyChar == '\u001b')
        {
            if (_back.TryPop(out var previous)) { Page = previous.Page; Selected = previous.Selected; }
            Notice = ""; return '\0';
        }
        var items = Items();
        if (key.Key == ConsoleKey.UpArrow) Selected = Math.Max(0, Selected - 1);
        else if (key.Key == ConsoleKey.DownArrow) Selected = Math.Min(items.Count - 1, Selected + 1);
        else
        {
            var digit = key.KeyChar - '1';
            var numbered = Page == "Home" && digit is >= 0 and < 4 || Page == "Profiles" && digit is >= 0 and < 6;
            if (numbered) Selected = digit;
            if (!numbered && key.Key is not (ConsoleKey.Enter or ConsoleKey.Spacebar or ConsoleKey.LeftArrow or ConsoleKey.RightArrow)
                && key.KeyChar is not ('\r' or '\n' or ' ')) return '\0';
            if (Page == "Home" && Selected < 2) return (char)('1' + Selected);
            try { Notice = ""; items[Selected].Run(key.Key == ConsoleKey.LeftArrow ? -1 : 1); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            { Notice = "Could not save settings: " + e.Message; }
        }
        return '\0';
    }

    public List<(string Text, string Color, bool Bold)> Layout(MapSnapshot snapshot, string notice, bool connected, int width, int height)
    {
        var view = store.Read();
        if (Page == "Home")
        {
            var lines = TuiView.Layout(snapshot, $"Profile: {view.Profile}" + (notice.Length > 0 ? " · " + notice : ""), connected, width, height);
            for (var i = 0; i < lines.Count; i++)
                if (lines[i].Text.Contains($"{Selected + 1}  ", StringComparison.Ordinal))
                    lines[i] = (lines[i].Text, "255;255;255", true);
            return lines;
        }
        var items = Items();
        Selected = Math.Clamp(Selected, 0, items.Count - 1);
        if (width >= 65 && height >= 18)
            return TuiView.Layout(snapshot, Notice.Length > 0 ? Notice : items[Selected].Label,
                connected, width, height, new(Page, view.Profile, items.Select(x => x.Label).ToArray(), Selected));
        var result = new List<(string Text, string Color, bool Bold)>();
        var limit = Math.Max(0, width - 1);
        void Add(string text, bool active = false, bool accent = false)
        {
            var clean = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            result.Add((clean[..Math.Min(clean.Length, limit)], accent ? TuiView.Primary : active ? "255;255;255" : "180;180;180", active));
        }
        if (height >= 3) Add($"{Page} · Profile: {view.Profile}", accent: true);
        var rows = Math.Max(1, height - (height >= 3 ? 2 : 0));
        if (height >= 5 && Notice.Length > 0) { Add(Notice); rows--; }
        var start = Math.Max(0, Selected - rows + 1);
        for (var i = start; i < Math.Min(items.Count, start + rows); i++) Add((i == Selected ? "> " : "  ") + items[i].Label, i == Selected);
        if (height >= 3)
        {
            while (result.Count < height - 1) Add("");
            Add($"↑↓ Select · Enter/Space · ←→ Adjust · Esc Back · {Selected + 1}/{items.Count}", accent: true);
        }
        while (result.Count < height) Add("");
        return result.Take(height).ToList();
    }
}
