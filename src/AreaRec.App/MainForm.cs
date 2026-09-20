using AreaRec.Capture;
using AreaRec.Core.Recording;
using AreaRec.Graphics;
using AreaRec.Media;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace AreaRec.App;

internal sealed class MainForm : Form
{
    private readonly Label _status = new();
    private readonly Label _region = new();
    private readonly Button _record = new();
    private readonly Button _stop = new();
    private readonly ComboBox _fps = new();
    private readonly ComboBox _quality = new();
    private readonly CheckBox _cursor = new();
    private readonly System.Windows.Forms.Timer _recordingTimer = new() { Interval = 250 };
    private readonly Stopwatch _recordingClock = new();
    private AppSettings _settings;
    private PhysicalRegion? _selectedRegion;
    private RecordingSession? _session;
    private readonly NotifyIcon _tray;
    private bool _closing;

    private const int HotkeyId = 0x4152;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VirtualKeyR = 0x52;

    public MainForm()
    {
        Text = "AreaRec";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(420, 380);
        _settings = AppSettingsStore.Load();

        var title = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            Location = new Point(24, 22),
            Text = "AreaRec",
        };

        var description = new Label
        {
            AutoSize = true,
            Location = new Point(26, 64),
            Text = "Select a region. Record it. Get an MP4.",
        };

        _region.AutoSize = true;
        _region.Location = new Point(26, 88);
        _region.Text = "No region selected";

        var fpsLabel = new Label { AutoSize = true, Location = new Point(26, 215), Text = "FPS" };
        _fps.DropDownStyle = ComboBoxStyle.DropDownList;
        _fps.Items.AddRange(["30", "60"]);
        _fps.SelectedItem = _settings.FramesPerSecond == 60 ? "60" : "30";
        _fps.Location = new Point(64, 211);
        _fps.Size = new Size(72, 28);

        var qualityLabel = new Label { AutoSize = true, Location = new Point(154, 215), Text = "Quality" };
        _quality.DropDownStyle = ComboBoxStyle.DropDownList;
        _quality.Items.AddRange(Enum.GetNames<VideoQuality>());
        _quality.SelectedItem = Enum.IsDefined(_settings.Quality) ? _settings.Quality.ToString() : VideoQuality.High.ToString();
        _quality.Location = new Point(210, 211);
        _quality.Size = new Size(100, 28);

        _cursor.AutoSize = true;
        _cursor.Checked = _settings.IncludeCursor;
        _cursor.Location = new Point(320, 214);
        _cursor.Text = "Cursor";

        var select = new Button
        {
            Location = new Point(24, 116),
            Size = new Size(372, 38),
            Text = "Select region",
        };
        select.Click += (_, _) => SelectRegion();

        _record.Location = new Point(24, 166);
        _record.Size = new Size(180, 38);
        _record.Text = "Record";
        _record.Enabled = false;
        _record.Click += (_, _) => StartRecording();

        _stop.Location = new Point(216, 166);
        _stop.Size = new Size(180, 38);
        _stop.Text = "Stop";
        _stop.Enabled = false;
        _stop.Click += async (_, _) => await StopRecordingAsync();

        _status.AutoSize = true;
        _status.Location = new Point(26, 335);
        _status.Text = "Native selector · capture ready · recording ready";
        _recordingTimer.Tick += (_, _) =>
        {
            _status.Text = $"Recording · {_recordingClock.Elapsed:mm\\:ss} · native capture + D3D11 + Media Foundation";
        };

        Controls.AddRange([title, description, _region, select, _record, _stop, fpsLabel, _fps, qualityLabel, _quality, _cursor, _status]);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Select region", null, (_, _) => SelectRegion());
        trayMenu.Items.Add("Exit", null, (_, _) => Close());
        _tray = new NotifyIcon
        {
            ContextMenuStrip = trayMenu,
            Icon = SystemIcons.Application,
            Text = "AreaRec",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) =>
        {
            Show();
            Activate();
        };

        Load += (_, _) => RegisterGlobalHotkey();
        FormClosed += (_, _) =>
        {
            UnregisterHotKey(Handle, HotkeyId);
            _tray.Dispose();
            _recordingTimer.Dispose();
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_session is not null && !_closing)
        {
            e.Cancel = true;
            _closing = true;
            _ = CloseAfterRecordingAsync();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey && message.WParam.ToInt32() == HotkeyId)
        {
            if (_session is null)
            {
                SelectRegion();
            }
            else
            {
                _ = StopRecordingAsync();
            }

            return;
        }

        base.WndProc(ref message);
    }

    private void SelectRegion()
    {
        Hide();
        try
        {
            using var selector = new RegionSelectorForm(
                region =>
                {
                    _selectedRegion = region;
                    _region.Text = $"Region: {region}";
                    _status.Text = "Region selected · ready to record";
                    _record.Enabled = true;
                },
                () => _status.Text = "Selection cancelled");
            selector.ShowDialog(this);
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private async void StartRecording()
    {
        if (_selectedRegion is not { } region || _session is not null)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "mp4",
            Filter = "MP4 video (*.mp4)|*.mp4",
            FileName = $"AreaRec-{DateTime.Now:yyyyMMdd-HHmmss}.mp4",
            InitialDirectory = GetInitialSaveFolder(),
            OverwritePrompt = true,
            Title = "Save recording",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var selectedFps = (string)_fps.SelectedItem! == "60" ? 60 : 30;
        var selectedQuality = Enum.Parse<VideoQuality>((string)_quality.SelectedItem!);
        var settings = new CaptureSettings(region, selectedFps, selectedQuality, _cursor.Checked);
        var capture = CaptureSourceFactory.Create(region);
        var sink = new MediaFoundationMp4Sink(dialog.FileName);
        var session = new RecordingSession(
            capture,
            sink,
            processorFactory: source =>
            {
                if (source is not INativeGraphicsContext context || context.NativeDevice == 0 || context.NativeContext == 0)
                {
                    throw new InvalidOperationException("The selected capture backend has no D3D11 context.");
                }

                return new D3D11FrameProcessor(context.NativeDevice, context.NativeContext);
            });
        _session = session;
        _record.Enabled = false;
        _stop.Enabled = true;
        _status.Text = "Starting native recording…";

        try
        {
            _settings = _settings with
            {
                FramesPerSecond = selectedFps,
                Quality = selectedQuality,
                IncludeCursor = _cursor.Checked,
                SaveFolder = Path.GetDirectoryName(dialog.FileName),
            };
            AppSettingsStore.Save(_settings);
            // Media Foundation creates COM objects here; keep the sink and its
            // frame consumer on the worker/MTA side instead of the WinForms STA.
            await Task.Run(
                () => session.StartAsync(settings, CancellationToken.None).AsTask(),
                CancellationToken.None);
            _recordingClock.Restart();
            _recordingTimer.Start();
            _status.Text = "Recording · 00:00 · native capture + D3D11 + Media Foundation";
        }
        catch (Exception exception)
        {
            _status.Text = $"Recording error: {DescribeException(exception)}";
            await DisposeSessionAsync(session);
        }
    }

    private async Task StopRecordingAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        _stop.Enabled = false;
        _recordingTimer.Stop();
        _recordingClock.Stop();
        _status.Text = "Stopping and saving MP4…";
        try
        {
            // Keep finalization on the same worker/MTA boundary as startup.
            await Task.Run(
                () => session.StopAsync(CancellationToken.None).AsTask(),
                CancellationToken.None);
            var statistics = session.Statistics;
            _status.Text = $"Saved · {statistics.EncodedFrames} frames · {statistics.EffectiveFramesPerSecond:0.0} FPS";
        }
        catch (Exception exception)
        {
            _status.Text = $"Save error: {DescribeException(exception)}";
        }
        finally
        {
            await DisposeSessionAsync(session);
            _record.Enabled = _selectedRegion.HasValue;
        }
    }

    private async Task CloseAfterRecordingAsync()
    {
        await StopRecordingAsync();
        Close();
    }

    private async Task DisposeSessionAsync(RecordingSession session)
    {
        await session.DisposeAsync();
        if (ReferenceEquals(_session, session))
        {
            _session = null;
        }
    }

    private string? GetInitialSaveFolder()
    {
        return !string.IsNullOrWhiteSpace(_settings.SaveFolder) && Directory.Exists(_settings.SaveFolder)
            ? _settings.SaveFolder
            : null;
    }

    private static string DescribeException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!messages.Contains(current.Message, StringComparer.Ordinal))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" → ", messages);
    }

    private void RegisterGlobalHotkey()
    {
        if (!RegisterHotKey(Handle, HotkeyId, ModControl | ModShift, VirtualKeyR))
        {
            _status.Text = "Native selector ready · Ctrl+Shift+R unavailable";
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
