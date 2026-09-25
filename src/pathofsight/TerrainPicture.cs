using System.Windows.Media;
using System.Windows.Media.Imaging;
using POE2Radar.Core.Game;

namespace pathofsight;

public sealed record TerrainPicture(BitmapSource Bitmap, byte[] Png)
{
    public static TerrainPicture Create(Poe2Live.TerrainData t)
    {
        var pixels = new byte[checked(t.Width * t.Height * 4)];
        for (var y = 0; y < t.Height; y++)
        for (var x = 0; x < t.Width; x++)
        {
            var i = y * t.Width + x;
            if (t.Walkable[i] == 0) continue;
            var edge = x == 0 || y == 0 || x == t.Width-1 || y == t.Height-1
                || t.Walkable[i-1] == 0 || t.Walkable[i+1] == 0 || t.Walkable[i-t.Width] == 0 || t.Walkable[i+t.Width] == 0;
            pixels[i*4] = 180; pixels[i*4+1] = 173; pixels[i*4+2] = 153; pixels[i*4+3] = edge ? (byte)220 : (byte)38;
        }
        var image = BitmapSource.Create(t.Width, t.Height, 96, 96, PixelFormats.Bgra32, null, pixels, t.Width*4);
        image.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var bytes = new MemoryStream(); encoder.Save(bytes);
        return new(image, bytes.ToArray());
    }
}
