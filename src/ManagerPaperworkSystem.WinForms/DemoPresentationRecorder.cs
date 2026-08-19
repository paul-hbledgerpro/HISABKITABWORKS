using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ManagerPaperworkSystem.WinForms;

internal sealed class DemoPresentationRecorder : IDisposable
{
    private const int VideoWidth = 2560;
    private const int VideoHeight = 1440;
    private readonly Form _mainForm;
    private readonly int _framesPerSecond;
    private readonly string _videoPath;
    private readonly BlockingCollection<byte[]> _frames = new(30);
    private readonly Stopwatch _clock = new();
    private readonly System.Windows.Forms.Timer _captureTimer;
    private Process? _encoder;
    private Task? _writer;
    private long _submittedFrames;

    public Point Pointer { get; private set; } = new(VideoWidth / 2, VideoHeight / 2);

    public DemoPresentationRecorder(Form mainForm, string outputDirectory, int framesPerSecond)
    {
        _mainForm = mainForm;
        _framesPerSecond = Math.Max(2, framesPerSecond);
        _videoPath = Path.Combine(outputDirectory, "detailed-demo-raw.mkv");
        _captureTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(50, 1000 / _framesPerSecond)
        };
        _captureTimer.Tick += CaptureDueFrames;
    }

    public void Start()
    {
        var ffmpeg = @"C:\ffmpeg\bin\ffmpeg.exe";
        if (!File.Exists(ffmpeg))
            throw new FileNotFoundException("FFmpeg is required for the background demo recording.", ffmpeg);

        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "rawvideo", "-pixel_format", "bgra",
                     "-video_size", $"{VideoWidth}x{VideoHeight}",
                     "-framerate", _framesPerSecond.ToString(), "-i", "pipe:0",
                     "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18",
                     "-pix_fmt", "yuv420p", _videoPath
                 })
            startInfo.ArgumentList.Add(argument);

        _encoder = Process.Start(startInfo)
                   ?? throw new InvalidOperationException("The background video encoder could not start.");
        _ = _encoder.StandardError.ReadToEndAsync();
        _writer = Task.Run(WriteFramesAsync);
        _clock.Start();
        _captureTimer.Start();
        CaptureDueFrames(this, EventArgs.Empty);
    }

    public void SetPointer(Point screenPoint) => Pointer = screenPoint;

    private void CaptureDueFrames(object? sender, EventArgs eventArgs)
    {
        if (_frames.IsAddingCompleted)
            return;

        var targetCount = Math.Max(1L, (long)Math.Ceiling(_clock.Elapsed.TotalSeconds * _framesPerSecond));
        var missing = Math.Min(targetCount - _submittedFrames, _framesPerSecond * 2L);
        if (missing <= 0)
            return;

        var bytes = CaptureFrame();
        for (var index = 0L; index < missing; index++)
        {
            if (!_frames.TryAdd(bytes))
                break;
            _submittedFrames++;
        }
    }

    private byte[] CaptureFrame()
    {
        using var canvas = new Bitmap(VideoWidth, VideoHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(WinTheme.Bg);
            using var mainBitmap = new Bitmap(
                Math.Max(1, _mainForm.ClientSize.Width),
                Math.Max(1, _mainForm.ClientSize.Height),
                PixelFormat.Format32bppArgb);
            _mainForm.DrawToBitmap(mainBitmap, new Rectangle(Point.Empty, mainBitmap.Size));
            graphics.DrawImageUnscaled(mainBitmap, 0, 0);

            foreach (Form form in Application.OpenForms)
            {
                if (ReferenceEquals(form, _mainForm) || !form.Visible || form.IsDisposed)
                    continue;
                using var child = new Bitmap(
                    Math.Max(1, form.Width),
                    Math.Max(1, form.Height),
                    PixelFormat.Format32bppArgb);
                form.DrawToBitmap(child, new Rectangle(Point.Empty, child.Size));
                var x = form.Left - _mainForm.Left;
                var y = form.Top - _mainForm.Top;
                if (x < 0 || y < 0 || x + form.Width > VideoWidth || y + form.Height > VideoHeight)
                {
                    x = (VideoWidth - form.Width) / 2;
                    y = (VideoHeight - form.Height) / 2;
                }
                using var shade = new SolidBrush(Color.FromArgb(80, Color.Black));
                graphics.FillRectangle(shade, 0, 0, VideoWidth, VideoHeight);
                graphics.DrawImageUnscaled(child, x, y);
            }

            var pointerX = Pointer.X - _mainForm.Left;
            var pointerY = Pointer.Y - _mainForm.Top;
            using var halo = new SolidBrush(Color.FromArgb(210, WinTheme.Copper));
            using var center = new SolidBrush(Color.White);
            graphics.FillEllipse(halo, pointerX - 13, pointerY - 13, 26, 26);
            graphics.FillEllipse(center, pointerX - 5, pointerY - 5, 10, 10);
        }

        var rectangle = new Rectangle(0, 0, VideoWidth, VideoHeight);
        var data = canvas.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var length = Math.Abs(data.Stride) * VideoHeight;
            var bytes = new byte[length];
            Marshal.Copy(data.Scan0, bytes, 0, length);
            return bytes;
        }
        finally
        {
            canvas.UnlockBits(data);
        }
    }

    private async Task WriteFramesAsync()
    {
        if (_encoder is null)
            return;
        var stream = _encoder.StandardInput.BaseStream;
        foreach (var frame in _frames.GetConsumingEnumerable())
            await stream.WriteAsync(frame);
        await stream.FlushAsync();
        _encoder.StandardInput.Close();
    }

    public async Task StopAsync()
    {
        _captureTimer.Stop();
        CaptureDueFrames(this, EventArgs.Empty);
        _frames.CompleteAdding();
        if (_writer is not null)
            await _writer;
        if (_encoder is not null && !_encoder.WaitForExit(30000))
            _encoder.Kill(true);
        if (_encoder is not null && _encoder.ExitCode != 0)
            throw new InvalidOperationException($"The background video encoder exited with code {_encoder.ExitCode}.");
    }

    public void Dispose()
    {
        _captureTimer.Dispose();
        _frames.Dispose();
        _encoder?.Dispose();
    }
}
