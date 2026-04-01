using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FatimaTTS.Services;

/// <summary>
/// Sends Windows 10/11 native toast notifications using WinRT XML API
/// via shell COM interop — no UWP package required.
/// Falls back silently on older Windows.
/// </summary>
public class ToastService
{
    private const string AppId = "FatimaTTS";

    public void ShowJobCompleted(string title, string detail)
        => Show("✦ Generation Complete", $"{title}\n{detail}", ToastType.Success);

    public void ShowJobFailed(string title, string error)
        => Show("Generation Failed", $"{title}\n{error}", ToastType.Error);

    public void ShowJobResumed(string title)
        => Show("Job Resumed", title, ToastType.Info);

    public enum ToastType { Success, Error, Info }

    public void Show(string heading, string body, ToastType type = ToastType.Info)
    {
        try
        {
            // Use PowerShell to fire a Windows toast — works on Win10/11
            // without requiring WinRT projection or MSIX packaging
            var icon = type switch
            {
                ToastType.Success => "✦",
                ToastType.Error   => "✕",
                _                 => "ℹ"
            };

            var escapedHeading = heading.Replace("'", "`'").Replace("\"", "`\"");
            var escapedBody    = body.Replace("'", "`'").Replace("\"", "`\"")
                                     .Replace("\n", " · ");

            var script = $"""
                $ErrorActionPreference = 'SilentlyContinue'
                [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
                [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null
                $xml = [Windows.Data.Xml.Dom.XmlDocument]::new()
                $xml.LoadXml('<toast><visual><binding template="ToastGeneric"><text>{escapedHeading}</text><text>{escapedBody}</text></binding></visual></toast>')
                $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
                [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Fatima TTS').Show($toast)
                """;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"",
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = false,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Toast is non-critical — never crash the app
        }
    }
}
