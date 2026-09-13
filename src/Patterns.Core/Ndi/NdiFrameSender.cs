using System.Runtime.InteropServices;
using Patterns.Core.Services;

namespace Patterns.Core.Ndi;

/// <summary>
/// An NDI sender fed frames by its caller — a node's picture, drawn by the node's own loop — where
/// <see cref="NdiSender"/> renders the show from the snapshot bus on a thread of its own. Opened on
/// the first frame (the runtime probed then), closed on dispose; a missing runtime is a status,
/// never a fault.
/// </summary>
public sealed class NdiFrameSender : IDisposable
{
    private IntPtr _sender;
    private IntPtr _namePtr;
    private long _frames;

    /// <param name="clockVideo">
    /// True (the default) lets the NDI runtime pace the caller — a send waits until the frame's
    /// time has come, so a loop with nothing else to time itself by runs at the picture's rate.
    /// False sends at once: for a lane that is paced by the frames it is handed, so a send never
    /// holds the thread that draws.
    /// </param>
    public NdiFrameSender(string name, bool clockVideo = true)
    {
        Name = name;
        ClockVideo = clockVideo;
    }

    public string Name { get; }

    /// <summary>Whether the runtime paces the sends (see the constructor).</summary>
    public bool ClockVideo { get; }
    public string Status { get; private set; } = "Off";
    public int Connections { get; private set; }
    public bool IsOpen => _sender != IntPtr.Zero;
    public long Frames => _frames;

    /// <summary>The sender on the network under its name; false with the reason in <see cref="Status"/>.</summary>
    public bool Open()
    {
        if (IsOpen) return true;
        NdiInterop.ReprobeIfUnavailable();
        if (!NdiInterop.Available)
        {
            Status = NdiSender.RuntimeHelp;
            return false;
        }
        try
        {
            _namePtr = NdiInterop.Utf8(Name);
            var create = new NdiInterop.SendCreate { NdiName = _namePtr, Groups = IntPtr.Zero, ClockVideo = ClockVideo, ClockAudio = false };
            _sender = NdiInterop.NDIlib_send_create(ref create);
            if (_sender == IntPtr.Zero)
            {
                Status = $"Could not create NDI sender '{Name}' (name in use?).";
                Marshal.FreeHGlobal(_namePtr);
                _namePtr = IntPtr.Zero;
                return false;
            }
            Status = $"'{Name}' · no receivers yet";
            Log.Info($"NDI sender '{Name}' created.");
            return true;
        }
        catch (Exception ex)
        {
            Status = $"NDI error: {ex.Message}";
            Log.Warn($"NDI sender '{Name}' could not open.", ex);
            return false;
        }
    }

    /// <summary>One BGRA frame — the pixels stay the caller's until this returns (the runtime copies them before it does).</summary>
    public bool Send(IntPtr bgra, int width, int height, int strideBytes, int rateN, int rateD)
    {
        if (!IsOpen && !Open()) return false;
        try
        {
            var frame = new NdiInterop.VideoFrameV2
            {
                Xres = width,
                Yres = height,
                FourCc = NdiInterop.FourCcBgrx,
                FrameRateN = rateN,
                FrameRateD = rateD,
                PictureAspectRatio = 0,
                FrameFormatType = NdiInterop.FrameFormatProgressive,
                Timecode = NdiInterop.SendTimecodeSynthesize,
                Data = bgra,
                LineStrideInBytes = strideBytes,
                Metadata = IntPtr.Zero,
                Timestamp = 0,
            };
            NdiInterop.NDIlib_send_send_video_v2(_sender, ref frame);
            _frames++;
            if ((_frames & 0x1F) == 1)
            {
                Connections = NdiInterop.NDIlib_send_get_no_connections(_sender, 0);
                Status = $"'{Name}' · {width}×{height} @ {(double)rateN / rateD:0.##} · {Connections} receiver{(Connections == 1 ? "" : "s")}";
            }
            return true;
        }
        catch (Exception ex)
        {
            Status = $"NDI error: {ex.Message}";
            Close();
            return false;
        }
    }

    public void Close()
    {
        if (_sender != IntPtr.Zero)
        {
            try { NdiInterop.NDIlib_send_destroy(_sender); }
            catch (Exception ex) { Log.Warn($"NDI sender '{Name}' destroy failed.", ex); }
            _sender = IntPtr.Zero;
        }
        if (_namePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_namePtr);
            _namePtr = IntPtr.Zero;
        }
        Connections = 0;
        Status = "Off";
    }

    public void Dispose() => Close();
}
