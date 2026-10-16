using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Assistant.Core.Characters;
using Assistant.Core.Diagnostics;

namespace Assistant.App;

/// <summary>Decoded frames and how long each one shows. Frozen, so it can be built on a background thread.</summary>
public sealed record FrameAnimation(IReadOnlyList<BitmapSource> Frames, IReadOnlyList<TimeSpan> Delays);

/// <summary>
/// A character the user made: plays the GIF or PNG frames from its folder for each mood. A mood without files
/// shows the Idle animation with a glow in the mood's color (the window still shows the mood's label).
/// </summary>
public sealed class CustomLook : CharacterLook
{
    public const double Box = 120; // the figure's size before the Size setting; pictures are fitted inside it

    readonly CharacterPacks packs;
    readonly Image image = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Bottom };
    readonly Border missing;
    readonly Dictionary<string, (string Stamp, FrameAnimation? Animation)> cache = [];
    string showing = "";
    int request; // a newer mood wins over a slow load

    public string Name { get; }
    public override FrameworkElement Figure { get; }
    public override bool UsesMotion => false;

    public CustomLook(CharacterPacks packs, string name)
    {
        this.packs = packs;
        Name = name;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        // Shown until the Idle mood has an animation, so an empty character isn't invisible.
        missing = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(14),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0x20, 0x20, 0x20)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock
            {
                Text = $"{name}\n\nAdd an Idle animation in Settings > Character",
                Foreground = Brushes.White,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6),
            },
            Visibility = Visibility.Collapsed,
        };

        var figure = new Grid { Width = Box, Height = Box };
        figure.Children.Add(image);
        figure.Children.Add(missing);
        Figure = figure;
    }

    public override void Apply(Mood mood, MoodLook look, bool calm, bool overridden)
    {
        int mine = ++request;
        string slot = mood.ToString();
        bool own = packs.Slot(Name, slot).Kind != SlotKind.Empty;
        string used = own ? slot : nameof(Mood.Idle);

        // The glow stands in for moods without their own animation, and shows the screensaver's colors.
        bool glow = overridden || (!own && mood != Mood.Idle);
        image.Effect = glow ? new DropShadowEffect { Color = look.Color, ShadowDepth = 0, BlurRadius = 22, Opacity = 0.95 } : null;

        _ = ShowAsync(used, mine);
    }

    async Task ShowAsync(string slot, int mine)
    {
        FrameAnimation? animation;
        string stamp = packs.Stamp(Name, slot);
        if (cache.TryGetValue(slot, out var cached) && cached.Stamp == stamp)
        {
            animation = cached.Animation;
        }
        else
        {
            SlotContent content = packs.Slot(Name, slot);
            int fps = packs.FramesPerSecond(Name);
            try
            {
                animation = await Task.Run(() => AnimationFiles.Load(content, fps));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DiagnosticLog.Error($"Could not load the {slot} animation", ex);
                animation = null;
            }
            cache[slot] = (stamp, animation);
        }
        if (mine != request) return; // the mood changed again while loading

        if (animation is null)
        {
            Stop();
            image.Source = null;
            showing = "";
            missing.Visibility = Visibility.Visible;
            return;
        }
        missing.Visibility = Visibility.Collapsed;

        // The same animation keeps playing (a color change shouldn't restart it).
        string key = slot + "|" + stamp;
        if (key == showing) return;
        showing = key;
        Play(animation);
    }

    void Play(FrameAnimation animation)
    {
        image.BeginAnimation(Image.SourceProperty, null);
        image.Source = animation.Frames[0];
        if (animation.Frames.Count == 1) return;

        var frames = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        TimeSpan at = TimeSpan.Zero;
        for (int i = 0; i < animation.Frames.Count; i++)
        {
            frames.KeyFrames.Add(new DiscreteObjectKeyFrame(animation.Frames[i], KeyTime.FromTimeSpan(at)));
            at += animation.Delays[i];
        }
        frames.Duration = new Duration(at);
        Timeline.SetDesiredFrameRate(frames, Motion.FramesPerSecond);
        image.BeginAnimation(Image.SourceProperty, frames);
    }

    public override void Stop()
    {
        image.BeginAnimation(Image.SourceProperty, null);
        showing = ""; // play again from the start when shown
    }
}

/// <summary>
/// Reads a slot's files into frames: an animated GIF (put together frame by frame, the way browsers do),
/// or PNG frames. Big pictures are shrunk to 256 pixels, which is plenty for the character's size.
/// </summary>
public static class AnimationFiles
{
    const int MaxSide = 256;
    static readonly TimeSpan MinGifDelay = TimeSpan.FromMilliseconds(20);
    static readonly TimeSpan SlowGifDelay = TimeSpan.FromMilliseconds(100); // what browsers use for "0" delays

    public static FrameAnimation? Load(SlotContent content, int framesPerSecond) => content.Kind switch
    {
        SlotKind.Gif => Gif(content.Files[0]),
        SlotKind.Frames => Pngs(content.Files, framesPerSecond),
        _ => null,
    };

    /// <summary>The first frame, small, for the previews in Settings.</summary>
    public static BitmapSource? Thumbnail(SlotContent content, int height)
    {
        if (content.Kind == SlotKind.Empty) return null;
        try
        {
            using var stream = File.OpenRead(content.Files[0]);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // reads it all now, so the file isn't kept open
            bitmap.DecodePixelHeight = height;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    static FrameAnimation? Pngs(IReadOnlyList<string> files, int framesPerSecond)
    {
        var frames = new List<BitmapSource>();
        foreach (string file in files.Take(CharacterPacks.MaxFrames))
        {
            using var stream = File.OpenRead(file);
            BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frames.Add(Shrink(frame));
        }
        if (frames.Count == 0) return null;
        var delay = TimeSpan.FromSeconds(1.0 / framesPerSecond);
        return new FrameAnimation(frames, frames.Select(_ => delay).ToList());
    }

    // GIF frames are often only the part that changed, drawn over what was there: build each full picture.
    static FrameAnimation? Gif(string file)
    {
        using var stream = File.OpenRead(file);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return null;

        BitmapMetadata? screen = MetadataOf(() => decoder.Metadata);
        int width = Query(screen, "/logscrdesc/Width") is int w and > 0 ? w : decoder.Frames[0].PixelWidth;
        int height = Query(screen, "/logscrdesc/Height") is int h and > 0 ? h : decoder.Frames[0].PixelHeight;
        int stride = width * 4;
        byte[] canvas = new byte[stride * height];

        var frames = new List<BitmapSource>();
        var delays = new List<TimeSpan>();
        foreach (BitmapFrame frame in decoder.Frames.Take(CharacterPacks.MaxFrames))
        {
            BitmapMetadata? metadata = MetadataOf(() => frame.Metadata);
            int left = Query(metadata, "/imgdesc/Left") ?? 0;
            int top = Query(metadata, "/imgdesc/Top") ?? 0;
            int disposal = Query(metadata, "/grctlext/Disposal") ?? 0;
            int hundredths = Query(metadata, "/grctlext/Delay") ?? 10;

            var pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int frameWidth = pixels.PixelWidth, frameHeight = pixels.PixelHeight;
            byte[] source = new byte[frameWidth * frameHeight * 4];
            pixels.CopyPixels(source, frameWidth * 4, 0);

            byte[]? before = disposal == 3 ? (byte[])canvas.Clone() : null;

            // GIF transparency is on or off, so a pixel is either drawn or left as it was.
            for (int y = 0; y < frameHeight; y++)
            {
                int canvasY = top + y;
                if (canvasY < 0 || canvasY >= height) continue;
                for (int x = 0; x < frameWidth; x++)
                {
                    int canvasX = left + x;
                    if (canvasX < 0 || canvasX >= width) continue;
                    int from = (y * frameWidth + x) * 4;
                    if (source[from + 3] == 0) continue;
                    Buffer.BlockCopy(source, from, canvas, canvasY * stride + canvasX * 4, 4);
                }
            }

            var full = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, stride);
            frames.Add(Shrink(full));
            var delay = TimeSpan.FromMilliseconds(hundredths * 10);
            delays.Add(delay < MinGifDelay ? SlowGifDelay : delay);

            // What the next frame is drawn over.
            if (disposal == 2) Clear(canvas, stride, width, height, left, top, frameWidth, frameHeight);
            else if (before is not null) canvas = before;
        }
        return new FrameAnimation(frames, delays);
    }

    static void Clear(byte[] canvas, int stride, int width, int height, int left, int top, int frameWidth, int frameHeight)
    {
        int x0 = Math.Max(0, left), x1 = Math.Min(width, left + frameWidth);
        if (x1 <= x0) return;
        for (int y = Math.Max(0, top); y < Math.Min(height, top + frameHeight); y++)
            Array.Clear(canvas, y * stride + x0 * 4, (x1 - x0) * 4);
    }

    // Some files have no readable metadata; then the defaults are used.
    static BitmapMetadata? MetadataOf(Func<ImageMetadata> read)
    {
        try
        {
            return read() as BitmapMetadata;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    static int? Query(BitmapMetadata? metadata, string query)
    {
        try
        {
            return metadata?.GetQuery(query) switch
            {
                ushort value => value,
                byte value => value,
                int value => value,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    // Shrinks to at most 256 pixels on the longer side and makes the picture usable on any thread.
    static BitmapSource Shrink(BitmapSource bitmap)
    {
        double scale = Math.Min(1.0, (double)MaxSide / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        BitmapSource result = scale < 1.0 ? new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)) : bitmap;

        // Copy the pixels out, so nothing lazy (or a 72-dpi size) is left for the UI thread.
        var pixels = new FormatConvertedBitmap(result, PixelFormats.Pbgra32, null, 0);
        int stride = pixels.PixelWidth * 4;
        byte[] buffer = new byte[stride * pixels.PixelHeight];
        pixels.CopyPixels(buffer, stride, 0);
        var copy = BitmapSource.Create(pixels.PixelWidth, pixels.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, buffer, stride);
        copy.Freeze();
        return copy;
    }
}
