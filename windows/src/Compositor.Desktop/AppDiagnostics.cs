using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Compositor.Core.Diagnostics;

namespace Compositor.Desktop;

/// <summary>Local, bounded diagnostics. Recording a failure must never become a second application failure.</summary>
internal static class AppDiagnostics
{
    private static DiagnosticLog? _log;
    private static string? _initializationError;
    private static string _directory = "unavailable";
    private static bool _dispatcherAttached;

    internal static string Version => typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    internal static string DirectoryPath => _log?.DirectoryPath ?? _directory;

    private static string DefaultDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrWhiteSpace(local) ? Path.GetTempPath() : local, "Compositor", "Diagnostics");
    }

    internal static string EnvironmentSummary() => string.Join(Environment.NewLine,
        $"Compositor: {Version}",
        $"OS: {RuntimeInformation.OSDescription}",
        $"OS architecture: {RuntimeInformation.OSArchitecture}",
        $"Process architecture: {RuntimeInformation.ProcessArchitecture}",
        $"Runtime: {RuntimeInformation.FrameworkDescription}",
        $"Culture: {CultureInfo.CurrentCulture.Name}",
        $"UI culture: {CultureInfo.CurrentUICulture.Name}",
        "Selected interface language is recorded in the session log.",
        "Logs remain local. Exception messages and stack traces may contain file paths.",
        "Project files, images and command-line arguments are not collected.");

    private static void Initialize()
    {
        if (_log is not null) return;
        try
        {
            _directory = DefaultDirectory();
            _log = new DiagnosticLog(_directory, EnvironmentSummary());
            _log.Write("Application session started.");
        }
        catch (Exception error)
        {
            _initializationError = error.Message;
        }
    }

    internal static void Record(string message) => _log?.Write(message);
    internal static void Report(string operation, Exception error) => _log?.Write(operation, error);

    internal static void AttachDispatcher()
    {
        if (_dispatcherAttached) return;
        Dispatcher.UIThread.UnhandledException += DispatcherFailure;
        _dispatcherAttached = true;
    }

    private static void DispatcherFailure(object sender, DispatcherUnhandledExceptionEventArgs args) =>
        Report("Unhandled UI exception", args.Exception);

    internal static bool Export(string destination, out string? error)
    {
        Initialize();
        if (_log is null)
        {
            error = _initializationError ?? "Diagnostics could not be initialized.";
            return false;
        }
        Record("Diagnostics export requested.");
        return _log.Export(destination, out error);
    }

    /// <summary>Keep the normal failure behavior; do not mark unknown UI exceptions as handled and keep editing.</summary>
    internal static int Run(Func<int> action, bool interactive, bool showFailureNotice = true)
    {
        Initialize();
        void Unhandled(object sender, UnhandledExceptionEventArgs args)
        {
            if (args.ExceptionObject is Exception error) Report("Unhandled process exception", error);
        }
        void Unobserved(object? sender, UnobservedTaskExceptionEventArgs args) =>
            Report("Unobserved task exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += Unhandled;
        TaskScheduler.UnobservedTaskException += Unobserved;
        try
        {
            var result = action();
            Record($"Application returned exit code {result}.");
            return result;
        }
        catch (Exception error)
        {
            Report("Fatal application failure", error);
            var logStatus = _log?.IsAvailable == true ? $"Logs / 日志：{DirectoryPath}"
                : $"Logs unavailable / 无法写入日志：{_log?.LastError ?? _initializationError ?? DirectoryPath}";
            var message = $"Compositor stopped unexpectedly.\nCompositor 意外停止。\n\n{error.GetType().Name}\n\n{logStatus}";
            if (showFailureNotice) Console.Error.WriteLine(message);
            if (interactive && OperatingSystem.IsWindows())
            {
                try { MessageBox(IntPtr.Zero, message, "Compositor", 0x10); }
                catch (Exception) { /* A native error dialog is optional; the local log remains available. */ }
            }
            return 1;
        }
        finally
        {
            if (_dispatcherAttached)
            {
                Dispatcher.UIThread.UnhandledException -= DispatcherFailure;
                _dispatcherAttached = false;
            }
            TaskScheduler.UnobservedTaskException -= Unobserved;
            AppDomain.CurrentDomain.UnhandledException -= Unhandled;
        }
    }

    internal static int Check()
    {
        var folder = Path.Combine(Path.GetTempPath(), "compositor-diagnostics-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var marker = "diagnostic-check-" + Guid.NewGuid().ToString("N");
            var status = Run(() => throw new InvalidOperationException(marker), interactive: false, showFailureNotice: false);
            if (status != 1) throw new InvalidOperationException("A failed application run did not return a failure status.");
            var output = Path.Combine(folder, "诊断信息.zip");
            if (!Export(output, out var error)) throw new IOException(error);
            using var archive = ZipFile.OpenRead(output);
            var environment = archive.GetEntry("environment.txt") ?? throw new InvalidOperationException("The environment summary is missing.");
            using var reader = new StreamReader(environment.Open());
            if (!reader.ReadToEnd().Contains(Version, StringComparison.Ordinal))
                throw new InvalidOperationException("The diagnostic summary has the wrong application version.");
            var found = false;
            foreach (var entry in archive.Entries.Where(entry => entry.Name.EndsWith(".log", StringComparison.Ordinal)))
            {
                using var log = new StreamReader(entry.Open());
                found |= log.ReadToEnd().Contains(marker, StringComparison.Ordinal);
            }
            if (!found) throw new InvalidOperationException("The application failure was not captured in the diagnostic export.");
            Console.WriteLine("Diagnostics checks passed: failure exit status, exception capture, environment and ZIP export.");
            return 0;
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
