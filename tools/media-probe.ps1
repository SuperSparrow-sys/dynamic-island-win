# Lists the media sessions Windows reports (SMTC) and whether each one sends a cover. Only reads, plays nothing.
# Usage: powershell -File tools\media-probe.ps1
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Await($op, [Type]$type) { $t = $asTask.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(-1) | Out-Null; $t.Result }

[void][Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
$mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
$sessions = $mgr.GetSessions()
if ($sessions.Count -eq 0) { Write-Output "No media sessions (no player open)." }
foreach ($s in $sessions) {
    $p = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
    # Thumbnail is only a reference; DynamicBay reads it the same way (MediaService). Null = the player sends no cover.
    $cover = if ($p.Thumbnail) { "yes" } else { "none" }
    [pscustomobject]@{
        App = $s.SourceAppUserModelId
        Status = $s.GetPlaybackInfo().PlaybackStatus
        Title = $p.Title
        Artist = $p.Artist
        Album = $p.AlbumTitle
        Cover = $cover
    }
}
