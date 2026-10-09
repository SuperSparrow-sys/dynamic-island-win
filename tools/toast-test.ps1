# Shows a test toast on behalf of an app id (default: Snipping Tool) - used to verify banner suppression.
# Usage: powershell -File tools\toast-test.ps1 [-AppId "Microsoft.ScreenSketch_8wekyb3d8bbwe!App"] [-Text "..."]
param(
    [string]$AppId = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App",
    [string]$Text = "DynamicBay Test-Benachrichtigung"
)
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>$Text</text><text>$(Get-Date -Format HH:mm:ss)</text></binding></visual></toast>")
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
$toast.Tag = "dynamicbay-test"
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($AppId).Show($toast)
"toast sent as $AppId"
