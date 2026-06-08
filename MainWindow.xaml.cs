using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace FileConverter;

public partial class MainWindow : Window
{
    private string? _ffmpegPath;
    private readonly List<string> _inputFiles = new();
    private CancellationTokenSource? _cts;
    private Process? _currentProcess;
    private bool _logAutoScroll = true;

    private static string ExeDir =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? ".";

    private static string FfmpegStorageDir
    {
        get
        {
            var portable = Path.Combine(ExeDir, "ffmpeg");
            if (Directory.Exists(portable) && HasWriteAccess(portable))
                return portable;
            if (HasWriteAccess(ExeDir))
                return portable;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileConverter", "ffmpeg");
        }
    }

    private static bool HasWriteAccess(string dir)
    {
        try
        {
            var test = Path.Combine(dir, Path.GetRandomFileName());
            File.Create(test).Dispose();
            File.Delete(test);
            return true;
        }
        catch { return false; }
    }

    private static readonly Dictionary<string, string[]> FormatMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mkv"]  = new[] { ".mp4", ".mov", ".avi", ".webm", ".gif" },
        [".mp4"]  = new[] { ".mkv", ".mov", ".avi", ".webm", ".gif" },
        [".mov"]  = new[] { ".mkv", ".mp4", ".avi", ".webm", ".gif" },
        [".avi"]  = new[] { ".mkv", ".mp4", ".mov", ".webm", ".gif" },
        [".webm"] = new[] { ".mkv", ".mp4", ".mov", ".avi", ".gif" },
        [".wmv"]  = new[] { ".mkv", ".mp4", ".mov", ".avi", ".webm", ".gif" },
        [".gif"]  = new[] { ".mkv", ".mp4", ".mov", ".avi", ".webm" },
        [".png"]  = new[] { ".jpg", ".bmp", ".webp", ".tiff", ".gif" },
        [".jpg"]  = new[] { ".png", ".bmp", ".webp", ".tiff", ".gif" },
        [".jpeg"] = new[] { ".png", ".bmp", ".webp", ".tiff", ".gif" },
        [".bmp"]  = new[] { ".png", ".jpg", ".webp", ".tiff", ".gif" },
        [".tiff"] = new[] { ".png", ".jpg", ".bmp", ".webp", ".gif" },
        [".tif"]  = new[] { ".png", ".jpg", ".bmp", ".webp", ".gif" },
        [".mp3"]  = new[] { ".wav", ".flac", ".ogg", ".m4a", ".wma", ".opus" },
        [".wav"]  = new[] { ".mp3", ".flac", ".ogg", ".m4a", ".wma", ".opus" },
        [".flac"] = new[] { ".mp3", ".wav", ".ogg", ".m4a", ".wma", ".opus" },
        [".ogg"]  = new[] { ".mp3", ".wav", ".flac", ".m4a", ".wma", ".opus" },
        [".m4a"]  = new[] { ".mp3", ".wav", ".flac", ".ogg", ".wma", ".opus" },
        [".wma"]  = new[] { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".opus" },
        [".opus"] = new[] { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".wma" },
        [".aac"]  = new[] { ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".wma", ".opus" },
    };

    private static readonly HashSet<string> CopySafeContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".mov"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tiff", ".tif"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".wma", ".opus", ".aac"
    };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        DropZone.Drop += DropZone_Drop;
        DropZone.PreviewDragOver += (_, e) => e.Effects = System.Windows.DragDropEffects.Copy;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        TxtOutputFolder.Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        await DetectOrDownloadFfmpeg();
    }

    private async Task DetectOrDownloadFfmpeg()
    {
        _ffmpegPath = FindFfmpegOnPath();

        if (_ffmpegPath != null)
        {
            TxtFfmpegStatus.Text = $"FFmpeg: found in PATH";
            return;
        }

        var localPath = Directory.GetFiles(FfmpegStorageDir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (localPath != null)
        {
            _ffmpegPath = localPath;
            TxtFfmpegStatus.Text = "FFmpeg: found (local)";
            return;
        }

        var result = System.Windows.MessageBox.Show(
            "FFmpeg is required but not found. Download it now?",
            "FFmpeg Required", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await DownloadFfmpeg();
        }
        else
        {
            TxtFfmpegStatus.Text = "FFmpeg: not available";
            BtnConvert.IsEnabled = false;
        }
    }

    private static string? FindFfmpegOnPath()
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? [];
        foreach (var p in paths)
        {
            var candidate = Path.Combine(p.Trim(), "ffmpeg.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private async Task DownloadFfmpeg()
    {
        TxtFfmpegStatus.Text = "FFmpeg: downloading...";

        var ffmpegDir = FfmpegStorageDir;
        Directory.CreateDirectory(ffmpegDir);

        var zipPath = Path.Combine(ffmpegDir, "ffmpeg.zip");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FileConverter/1.0");

            var release = await GetLatestFfmpegReleaseUrl(client);
            if (release == null)
            {
                TxtFfmpegStatus.Text = "FFmpeg: download failed";
                return;
            }

            AppendLog("Downloading FFmpeg...");
            using (var stream = await client.GetStreamAsync(release))
            using (var fileStream = File.Create(zipPath))
            {
                await stream.CopyToAsync(fileStream);
            }

            AppendLog("Extracting...");

            foreach (var dir in Directory.GetDirectories(ffmpegDir))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { }
            }

            ZipFile.ExtractToDirectory(zipPath, ffmpegDir, overwriteFiles: true);

            var exe = Directory.GetFiles(ffmpegDir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe != null)
            {
                _ffmpegPath = exe;
                TxtFfmpegStatus.Text = "FFmpeg: ready";
                AppendLog("FFmpeg downloaded successfully.");
            }

            if (File.Exists(zipPath)) File.Delete(zipPath);
        }
        catch (Exception ex)
        {
            TxtFfmpegStatus.Text = "FFmpeg: download error";
            AppendLog($"Error downloading FFmpeg: {ex.Message}");
        }
    }

    private static async Task<string?> GetLatestFfmpegReleaseUrl(HttpClient client)
    {
        var json = await client.GetStringAsync("https://api.github.com/repos/yt-dlp/ffmpeg-Builds/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var assets = doc.RootElement.GetProperty("assets");
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (name != null && name.Contains("win64") && name.Contains("gpl") && name.EndsWith(".zip"))
            {
                var url = asset.GetProperty("browser_download_url").GetString();
                if (url != null)
                    return url;
            }
        }
        return null;
    }

    private void SetInputFiles(string[] files)
    {
        _inputFiles.Clear();
        _inputFiles.AddRange(files);

        if (files.Length == 1)
        {
            TxtInputFile.Text = files[0];
            TxtFileName.Text = Path.GetFileNameWithoutExtension(files[0]);
            TxtFileName.IsEnabled = true;
            FileList.Visibility = Visibility.Collapsed;
        }
        else
        {
            TxtInputFile.Text = $"{files.Length} files selected";
            TxtFileName.Text = "";
            TxtFileName.IsEnabled = false;
            FileList.ItemsSource = files.Select(f => Path.GetFileName(f)).ToList();
            FileList.Visibility = Visibility.Visible;
        }

        var exts = files.Select(f => Path.GetExtension(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (exts.Count > 1)
            AppendLog($"Files have mixed formats ({string.Join(", ", exts)}). Verify the output format is compatible with all inputs.");

        var firstKnown = exts.FirstOrDefault(e => FormatMap.ContainsKey(e));
        PopulateFormats(firstKnown ?? exts[0]);
    }

    private void PopulateFormats(string inputExtension)
    {
        CboFormat.Items.Clear();

        if (!FormatMap.TryGetValue(inputExtension, out var formats))
        {
            CboFormat.Items.Add("N/A");
            CboFormat.SelectedIndex = 0;
            CboFormat.IsEnabled = false;
            LblExtension.Text = "";
            return;
        }

        CboFormat.IsEnabled = true;
        foreach (var fmt in formats)
            CboFormat.Items.Add(fmt.TrimStart('.').ToUpper());

        CboFormat.SelectedIndex = 0;
    }

    private void CboFormat_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CboFormat.SelectedItem is string fmt && fmt != "N/A")
            LblExtension.Text = "." + fmt.ToLower();
        else
            LblExtension.Text = "";
    }

    private void DropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            if (files.Length > 0)
                SetInputFiles(files);
        }
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "All supported|*.mkv;*.mp4;*.avi;*.mov;*.webm;*.wmv;*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif;*.mp3;*.wav;*.flac;*.ogg;*.m4a;*.wma;*.opus;*.aac|Video files|*.mkv;*.mp4;*.avi;*.mov;*.webm;*.wmv|Audio files|*.mp3;*.wav;*.flac;*.ogg;*.m4a;*.wma;*.opus;*.aac|Image files|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif|All files|*.*",
            Title = "Select files to convert"
        };
        if (dialog.ShowDialog() == true)
            SetInputFiles(dialog.FileNames);
    }

    private void BtnOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        dialog.SelectedPath = TxtOutputFolder.Text;
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TxtOutputFolder.Text = dialog.SelectedPath;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        KillCurrentProcess();
        _cts?.Cancel();
    }

    private void KillCurrentProcess()
    {
        if (_currentProcess != null && !_currentProcess.HasExited)
        {
            try { _currentProcess.Kill(entireProcessTree: true); }
            catch { }
        }
    }

    private async void BtnConvert_Click(object sender, RoutedEventArgs e)
    {
        if (_ffmpegPath == null)
        {
            AppendLog("FFmpeg is not available.");
            return;
        }

        if (_inputFiles.Count == 0)
        {
            AppendLog("Please select input file(s).");
            return;
        }

        var formatItem = CboFormat.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(formatItem) || formatItem == "N/A")
        {
            AppendLog("Please select an output format.");
            return;
        }

        var outputFolder = TxtOutputFolder.Text.Trim();
        if (string.IsNullOrEmpty(outputFolder))
        {
            AppendLog("Please select an output folder.");
            return;
        }

        var outputExt = "." + formatItem.ToLower();

        SetUiEnabled(false);
        LogPanel.Children.Clear();
        _logAutoScroll = true;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.Value = 0;
        BtnCancel.Visibility = Visibility.Visible;

        _cts = new CancellationTokenSource();

        try
        {
            for (int i = 0; i < _inputFiles.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();

                var inputFile = _inputFiles[i];
                var fileName = _inputFiles.Count == 1
                    ? TxtFileName.Text.Trim()
                    : Path.GetFileNameWithoutExtension(inputFile);
                var outputFile = Path.Combine(outputFolder, fileName + outputExt);

                if (string.IsNullOrEmpty(fileName))
                {
                    AppendLog($"Skipping: {Path.GetFileName(inputFile)} has invalid name");
                    continue;
                }

                var inputExt = Path.GetExtension(inputFile);
                if (!FormatMap.TryGetValue(inputExt, out var allowed) ||
                    !allowed.Contains(outputExt, StringComparer.OrdinalIgnoreCase))
                {
                    AppendLog($"Skipping: {Path.GetFileName(inputFile)} ({inputExt}) cannot be converted to {outputExt}");
                    continue;
                }

                if (File.Exists(outputFile) && ChkOverwrite.IsChecked != true)
                {
                    AppendLog($"Skipping: {Path.GetFileName(outputFile)} exists (enable overwrite)");
                    continue;
                }

                var freeSpace = CheckDiskSpace(outputFolder, inputFile);
                if (freeSpace.HasValue)
                {
                    AppendLog($"Skipping: {Path.GetFileName(inputFile)} — only {freeSpace.Value} free on destination drive");
                    continue;
                }

                TxtStatus.Text = $"Converting ({i + 1}/{_inputFiles.Count}): {Path.GetFileName(inputFile)}";
                AppendLog($"Processing: {Path.GetFileName(inputFile)}");

                var args = BuildFfmpegArgs(inputFile, outputFile);
                var progressStart = (double)i / _inputFiles.Count * 100;
                var progressEnd = (double)(i + 1) / _inputFiles.Count * 100;
                await ConvertFile(args, outputFile, _cts.Token, progressStart, progressEnd);
            }

            AppendLog("All conversions complete!");
            TxtStatus.Text = "Done";
            ProgressBar.Value = 100;
        }
        catch (OperationCanceledException)
        {
            AppendLog("Conversion cancelled.");
            TxtStatus.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            TxtStatus.Text = "Error";
        }
        finally
        {
            _currentProcess = null;
            _cts?.Dispose();
            _cts = null;
            SetUiEnabled(true);
            BtnCancel.Visibility = Visibility.Collapsed;
        }
    }

    private long? CheckDiskSpace(string outputFolder, string inputFile)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(outputFolder)!);
            var needed = new FileInfo(inputFile).Length * 1.1;
            if (drive.AvailableFreeSpace < needed)
                return drive.AvailableFreeSpace;
        }
        catch { }
        return null;
    }

    private static string FfmpegErrorString(int exitCode)
    {
        return exitCode switch
        {
            -28 => "No space left on device",
            -2  => "No such file or directory",
            -13 => "Permission denied",
            -22 => "Invalid argument",
            -17 => "File already exists",
            -12 => "Out of memory",
            -32 => "Broken pipe",
            -54 => "Connection reset by peer",
            -61 => "Connection refused",
            -84 => "Invalid data found when processing",
            1   => "Operation not permitted / Unknown error",
            _   => "Unknown error"
        };
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            _ => $"{bytes / 1024.0:F1} KB"
        };
    }

    private async Task ConvertFile(string args, string outputFile, CancellationToken ct, double progressStart = 0, double progressEnd = 100)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        _currentProcess = process;

        process.Start();

        var totalDuration = TimeSpan.Zero;
        var durationParsed = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var line = await process.StandardError.ReadLineAsync(ct);
            if (line == null) break;

            Dispatcher.Invoke(() => AppendLog(line));

            if (!durationParsed && line.Contains("Duration:"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"Duration: (\d+):(\d+):(\d+\.\d+)");
                if (match.Success)
                {
                    totalDuration = new TimeSpan(
                        0, int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
                        (int)double.Parse(match.Groups[3].Value));
                    durationParsed = true;
                }
            }

            if (durationParsed && line.Contains("time="))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"time=(\d+):(\d+):(\d+\.\d+)");
                if (match.Success)
                {
                    var current = new TimeSpan(
                        0, int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
                        (int)double.Parse(match.Groups[3].Value));

                    if (totalDuration > TimeSpan.Zero)
                    {
                        var fileProgress = (current.TotalSeconds / totalDuration.TotalSeconds) * 100;
                        var overall = progressStart + (fileProgress / 100) * (progressEnd - progressStart);
                        Dispatcher.Invoke(() => ProgressBar.Value = Math.Min(overall, progressEnd - 1));
                    }
                }
            }
        }

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new Exception($"FFmpeg exited with code {process.ExitCode} ({FfmpegErrorString(process.ExitCode)})");
    }

    private string BuildFfmpegArgs(string input, string output)
    {
        var inputExt = Path.GetExtension(input);
        var outputExt = Path.GetExtension(output);

        if (ImageExtensions.Contains(inputExt))
        {
            var imgOut = outputExt.ToLower();
            if (imgOut == ".jpg" || imgOut == ".jpeg")
                return $"-i \"{input}\" -c:v mjpeg -q:v 2 \"{output}\"";
            if (imgOut == ".png")
                return $"-i \"{input}\" -c:v png \"{output}\"";
            if (imgOut == ".bmp")
                return $"-i \"{input}\" -c:v bmp \"{output}\"";
            if (imgOut == ".webp")
                return $"-i \"{input}\" -c:v libwebp -quality 80 \"{output}\"";
            if (imgOut == ".tiff" || imgOut == ".tif")
                return $"-i \"{input}\" -c:v tiff \"{output}\"";
            return $"-i \"{input}\" \"{output}\"";
        }

        if (AudioExtensions.Contains(inputExt) || AudioExtensions.Contains(outputExt))
        {
            var audOut = outputExt.ToLower();
            if (audOut == ".mp3")
                return $"-i \"{input}\" -c:a libmp3lame -q:a 2 \"{output}\"";
            if (audOut == ".wav")
                return $"-i \"{input}\" -c:a pcm_s16le \"{output}\"";
            if (audOut == ".flac")
                return $"-i \"{input}\" -c:a flac \"{output}\"";
            if (audOut == ".ogg")
                return $"-i \"{input}\" -c:a libvorbis -q:a 3 \"{output}\"";
            if (audOut == ".m4a")
                return $"-i \"{input}\" -c:a aac -b:a 192k \"{output}\"";
            if (audOut == ".wma")
                return $"-i \"{input}\" -c:a wmav2 \"{output}\"";
            if (audOut == ".opus")
                return $"-i \"{input}\" -c:a libopus -b:a 128k \"{output}\"";
            return $"-i \"{input}\" \"{output}\"";
        }

        if (CopySafeContainers.Contains(inputExt) && CopySafeContainers.Contains(outputExt))
            return $"-i \"{input}\" -c copy \"{output}\"";

        return outputExt.ToLower() switch
        {
            ".webm" => $"-i \"{input}\" -c:v libvpx-vp9 -c:a libopus -crf 30 -b:v 0 \"{output}\"",
            ".gif"  => $"-i \"{input}\" -vf \"fps=10,scale=480:-1:flags=lanczos\" -c:v gif \"{output}\"",
            ".avi"  => $"-i \"{input}\" -c:v mpeg4 -c:a mp3 -q:v 5 \"{output}\"",
            ".wmv"  => $"-i \"{input}\" -c:v wmv2 -c:a wmav2 \"{output}\"",
            _       => $"-i \"{input}\" -c:v libx264 -c:a aac -crf 23 \"{output}\""
        };
    }

    private async Task ConvertFile(string args, string outputFile, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        _currentProcess = process;

        process.Start();

        var totalDuration = TimeSpan.Zero;
        var durationParsed = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var line = await process.StandardError.ReadLineAsync(ct);
            if (line == null) break;

            Dispatcher.Invoke(() => AppendLog(line));

            if (!durationParsed && line.Contains("Duration:"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"Duration: (\d+):(\d+):(\d+\.\d+)");
                if (match.Success)
                {
                    totalDuration = new TimeSpan(
                        0, int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
                        (int)double.Parse(match.Groups[3].Value));
                    durationParsed = true;
                }
            }

            if (durationParsed && line.Contains("time="))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"time=(\d+):(\d+):(\d+\.\d+)");
                if (match.Success)
                {
                    var current = new TimeSpan(
                        0, int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
                        (int)double.Parse(match.Groups[3].Value));

                    if (totalDuration > TimeSpan.Zero)
                    {
                        var progress = (current.TotalSeconds / totalDuration.TotalSeconds) * 100;
                        Dispatcher.Invoke(() => ProgressBar.Value = Math.Min(progress, 99));
                    }
                }
            }
        }

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new Exception($"FFmpeg exited with code {process.ExitCode} ({FfmpegErrorString(process.ExitCode)})");
    }

    private void SetUiEnabled(bool enabled)
    {
        BtnBrowse.IsEnabled = enabled;
        BtnOutputFolder.IsEnabled = enabled;
        BtnConvert.IsEnabled = enabled;
        CboFormat.IsEnabled = enabled;
        TxtFileName.IsEnabled = enabled;
        DropZone.IsEnabled = enabled;
    }

    private void AppendLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var tb = new System.Windows.Controls.TextBlock
        {
            Text = $"[{timestamp}] {message}",
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xaa, 0xaa, 0xaa)),
            TextWrapping = System.Windows.TextWrapping.Wrap,
        };
        LogPanel.Children.Add(tb);
        if (_logAutoScroll)
            LogScroller.ScrollToEnd();
    }

    private void LogScroller_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (LogScroller.VerticalOffset >= LogScroller.ScrollableHeight - 2)
            _logAutoScroll = true;
        else if (e.VerticalChange != 0)
            _logAutoScroll = false;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_cts != null)
        {
            var result = System.Windows.MessageBox.Show(
                "A conversion is in progress. Closing will cancel it.",
                "Conversion in progress",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Warning);
            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
        }

        KillCurrentProcess();
        _cts?.Cancel();
    }
}
