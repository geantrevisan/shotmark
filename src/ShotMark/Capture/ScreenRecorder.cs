using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShotMark.Services;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Security.Cryptography;
using Windows.Storage;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace ShotMark.Capture
{
    /// <summary>
    /// Records a screen area to MP4 (H.264 through Windows Media Foundation, no extra components)
    /// or to an animated GIF. Frames are grabbed with GDI on a background thread.
    /// </summary>
    public sealed class ScreenRecorder
    {
        private readonly Drawing.Rectangle _area;
        private readonly int _fps;
        private readonly bool _cursor;
        private readonly bool _gif;
        private readonly string _path;
        private readonly BlockingCollection<(byte[] Data, TimeSpan Time)> _queue;
        private readonly Stopwatch _clock = new Stopwatch();
        private Thread? _captureThread;
        private volatile bool _stop;
        private volatile bool _cancelled;
        private Task _encodeTask = Task.CompletedTask;

        private ScreenRecorder(Drawing.Rectangle area, int fps, bool cursor, bool gif, string path)
        {
            _area = area;
            _fps = Math.Max(1, Math.Min(60, fps));
            _cursor = cursor;
            _gif = gif;
            _path = path;
            _queue = new BlockingCollection<(byte[], TimeSpan)>(_fps * 3);
        }

        public string OutputPath => _path;
        public bool IsGif => _gif;
        public bool IsPaused { get; private set; }
        public TimeSpan Elapsed => _clock.Elapsed;
        public int DroppedFrames { get; private set; }

        /// <summary>Starts recording. MP4 areas are rounded down to even sizes (an H.264 requirement).</summary>
        public static ScreenRecorder Start(Drawing.Rectangle area, int fps, bool cursor, bool gif, string path)
        {
            if (!gif)
            {
                area.Width &= ~1;
                area.Height &= ~1;
            }
            if (area.Width < 16 || area.Height < 16) throw new ArgumentException("A área de gravação é pequena demais.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var rec = new ScreenRecorder(area, gif ? Math.Min(fps, 12) : fps, cursor, gif, path);
            rec._encodeTask = gif ? rec.RunGifEncoderAsync() : Task.Run(rec.RunMp4EncoderAsync);
            rec._clock.Start();
            rec._captureThread = new Thread(rec.CaptureLoop) { IsBackground = true, Name = "ShotMark capture", Priority = ThreadPriority.AboveNormal };
            rec._captureThread.Start();
            return rec;
        }

        public void TogglePause()
        {
            IsPaused = !IsPaused;
            if (IsPaused) _clock.Stop(); else _clock.Start();
        }

        /// <summary>Stops and finishes the file.</summary>
        public async Task StopAsync()
        {
            _stop = true;
            if (IsPaused) TogglePause();
            await Task.Run(() => _captureThread?.Join());
            await _encodeTask;
        }

        /// <summary>Stops and deletes the partial file.</summary>
        public async Task CancelAsync()
        {
            _cancelled = true;
            try { await StopAsync(); } catch (Exception) { }
            try { if (File.Exists(_path)) File.Delete(_path); } catch (Exception) { }
        }

        // ---- capture --------------------------------------------------------------------

        private void CaptureLoop()
        {
            try
            {
                var interval = TimeSpan.FromSeconds(1.0 / _fps);
                using var bmp = new Drawing.Bitmap(_area.Width, _area.Height, DrawingImaging.PixelFormat.Format32bppRgb);
                using var g = Drawing.Graphics.FromImage(bmp);
                var next = TimeSpan.Zero;
                int stride = _area.Width * 4;

                while (!_stop)
                {
                    if (IsPaused)
                    {
                        Thread.Sleep(25);
                        continue;
                    }

                    var t = _clock.Elapsed;
                    g.CopyFromScreen(_area.Left, _area.Top, 0, 0, _area.Size, Drawing.CopyPixelOperation.SourceCopy);
                    if (_cursor) ScreenCapturer.DrawCursor(g, _area);

                    var data = bmp.LockBits(new Drawing.Rectangle(0, 0, _area.Width, _area.Height), DrawingImaging.ImageLockMode.ReadOnly, DrawingImaging.PixelFormat.Format32bppRgb);
                    var bytes = new byte[stride * _area.Height];
                    try
                    {
                        for (int y = 0; y < _area.Height; y++)
                        {
                            System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * stride, stride);
                        }
                    }
                    finally
                    {
                        bmp.UnlockBits(data);
                    }
                    // Opaque alpha (GDI leaves it undefined).
                    for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;

                    if (!_queue.TryAdd((bytes, t))) DroppedFrames++;

                    next += interval;
                    var wait = next - _clock.Elapsed;
                    if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                    else next = _clock.Elapsed; // running late: don't try to catch up
                }
            }
            catch (Exception ex)
            {
                Log.Error("Gravação: captura de quadros", ex);
            }
            finally
            {
                _queue.CompleteAdding();
            }
        }

        // ---- MP4 ----------------------------------------------------------------------

        private async Task RunMp4EncoderAsync()
        {
            int w = _area.Width, h = _area.Height;
            var props = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)w, (uint)h);
            var source = new MediaStreamSource(new VideoStreamDescriptor(props)) { BufferTime = TimeSpan.Zero };
            source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
            source.SampleRequested += OnSampleRequested;

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Audio = null;
            profile.Video.Width = (uint)w;
            profile.Video.Height = (uint)h;
            profile.Video.FrameRate.Numerator = (uint)_fps;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.Bitrate = (uint)Math.Max(1_500_000, Math.Min(16_000_000, (long)w * h * _fps / 6));

            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(_path)!);
            var file = await folder.CreateFileAsync(Path.GetFileName(_path), CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);

            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile);
            if (!prepared.CanTranscode)
                throw new InvalidOperationException("O Windows não conseguiu iniciar o codificador de vídeo: " + prepared.FailureReason);
            await prepared.TranscodeAsync();
        }

        private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            var request = args.Request;
            var deferral = request.GetDeferral();
            Task.Run(() =>
            {
                try
                {
                    while (true)
                    {
                        if (_cancelled)
                        {
                            request.Sample = null;
                            return;
                        }
                        if (_queue.TryTake(out var frame, 100))
                        {
                            // Media Foundation expects bottom-up rows for uncompressed RGB.
                            var flipped = FlipRows(frame.Data, _area.Width * 4, _area.Height);
                            var sample = MediaStreamSample.CreateFromBuffer(CryptographicBuffer.CreateFromByteArray(flipped), frame.Time);
                            sample.Duration = TimeSpan.FromSeconds(1.0 / _fps);
                            request.Sample = sample;
                            return;
                        }
                        if (_queue.IsCompleted)
                        {
                            request.Sample = null; // end of stream
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Gravação: entrega de quadro", ex);
                    request.Sample = null;
                }
                finally
                {
                    deferral.Complete();
                }
            });
        }

        /// <summary>Set to false if recordings ever come out upside down.</summary>
        internal static bool FlipForEncoder = true;

        private static byte[] FlipRows(byte[] src, int stride, int height)
        {
            if (!FlipForEncoder) return src;
            var dst = new byte[src.Length];
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(src, y * stride, dst, (height - 1 - y) * stride, stride);
            }
            return dst;
        }

        // ---- GIF ----------------------------------------------------------------------

        private Task RunGifEncoderAsync()
        {
            var done = new TaskCompletionSource<bool>();
            var thread = new Thread(() =>
            {
                try
                {
                    var gif = new GifWriter();
                    double scale = Math.Min(1.0, 960.0 / _area.Width);
                    TimeSpan? last = null;
                    foreach (var frame in _queue.GetConsumingEnumerable())
                    {
                        if (_cancelled) break;
                        var src = BitmapSource.Create(_area.Width, _area.Height, 96, 96, PixelFormats.Bgr32, null, frame.Data, _area.Width * 4);
                        BitmapSource img = src;
                        if (scale < 0.999)
                        {
                            img = new TransformedBitmap(src, new ScaleTransform(scale, scale));
                        }
                        img.Freeze();
                        if (last.HasValue) gif.SetLastDelay((int)(frame.Time - last.Value).TotalMilliseconds);
                        gif.AddFrame(img, 1000 / _fps);
                        last = frame.Time;
                    }
                    if (!_cancelled && gif.FrameCount > 0) gif.Save(_path);
                    done.SetResult(true);
                }
                catch (Exception ex)
                {
                    Log.Error("Gravação: GIF", ex);
                    done.SetException(ex);
                }
            })
            { IsBackground = true, Name = "ShotMark GIF" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return done.Task;
        }
    }
}
