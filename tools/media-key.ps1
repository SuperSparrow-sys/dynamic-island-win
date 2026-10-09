# Sends a media key (next / prev / playpause) like a keyboard would - used to test track-change handling.
# Usage: powershell -File tools\media-key.ps1 -Key next
param([ValidateSet("next", "prev", "playpause")] [string]$Key = "next")
Add-Type 'using System; using System.Runtime.InteropServices; public static class MK { [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra); }'
$vk = @{ next = 0xB0; prev = 0xB1; playpause = 0xB3 }[$Key]
[MK]::keybd_event([byte]$vk, 0, 0, [IntPtr]::Zero)
[MK]::keybd_event([byte]$vk, 0, 2, [IntPtr]::Zero)
"sent $Key"
