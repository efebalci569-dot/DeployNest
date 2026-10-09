using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DeployNest.App.Services;

public enum DialogResult
{
    None,
    Primary,
    Secondary,
}

public sealed class DialogRequest
{
    public required string Title { get; init; }
    public required string Message { get; init; }
    public required string PrimaryText { get; init; }
    public string? SecondaryText { get; init; }
    public string? CloseText { get; init; }
    public bool PrimaryIsDanger { get; init; }
}

/// <summary>In-window modal dialog (rendered by MainWindow) that matches the app theme.</summary>
public sealed partial class DialogService : ObservableObject
{
    private TaskCompletionSource<DialogResult>? _pending;

    [ObservableProperty]
    private DialogRequest? _current;

    public Task<DialogResult> ShowAsync(DialogRequest request)
    {
        _pending?.TrySetResult(DialogResult.None);
        _pending = new TaskCompletionSource<DialogResult>();
        Current = request;
        return _pending.Task;
    }

    [RelayCommand]
    private void Primary() => Complete(DialogResult.Primary);

    [RelayCommand]
    private void Secondary() => Complete(DialogResult.Secondary);

    [RelayCommand]
    private void Close() => Complete(DialogResult.None);

    private void Complete(DialogResult result)
    {
        Current = null;
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(result);
    }
}

/// <summary>Short, self-dismissing confirmation messages.</summary>
public sealed partial class ToastService : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3.5) };

    [ObservableProperty]
    private string? _message;

    public ToastService()
    {
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Message = null;
        };
    }

    public void Show(string message)
    {
        Message = message;
        _timer.Stop();
        _timer.Start();
    }
}

/// <summary>Opens folders and web pages with the user's default handlers.</summary>
public sealed class ShellService(ILogger<ShellService> logger)
{
    public bool OpenUrl(Uri? url)
    {
        if (url is null || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp && url.Scheme != "ms-windows-store"))
            return false;
        return Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }

    public bool OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(ex, "Klasör oluşturulamadı");
            return false;
        }

        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        info.ArgumentList.Add(path);
        return Start(info);
    }

    public bool OpenFile(string path) =>
        File.Exists(path) && Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private bool Start(ProcessStartInfo info)
    {
        try
        {
            using var _ = Process.Start(info);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Kabuk işlemi başlatılamadı");
            return false;
        }
    }
}

public sealed class FileDialogService
{
    public string? SaveTextFile(string defaultFileName, string title)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = defaultFileName,
            DefaultExt = ".txt",
            Filter = "Metin dosyası (*.txt)|*.txt",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string? initialDirectory, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
