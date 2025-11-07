using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ssp_1;

public static class ImageHelper
{
    public static Stream AddTextToImage(Stream imageStream, params (string text, (float x, float y) position, int fontSize, string colorHex)[] texts)
    {
        MemoryStream memoryStream = new MemoryStream();

        using Image image = Image.Load(imageStream);

        image.Mutate(img =>
        {
            TextGraphicsOptions textGraphicsOptions = new TextGraphicsOptions
            {
                TextOptions = { WrapTextWidth = image.Width - 10 }
            };

            foreach (var (text, (x, y), fontSize, colorHex) in texts)
            {
                Font font = SystemFonts.CreateFont("Verdana", fontSize);
                Rgba32 color = Rgba32.ParseHex(colorHex);

                img.DrawText(textGraphicsOptions, text, font, color, new PointF(x, y));
            }
        });

        image.SaveAsPng(memoryStream);
        memoryStream.Position = 0;

        return memoryStream;
    }
}

