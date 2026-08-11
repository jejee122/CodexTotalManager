Option Explicit

Dim shell, fso, launcher, quote
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
quote = Chr(34)

launcher = fso.BuildPath(fso.GetParentFolderName(WScript.ScriptFullName), "Open-New-Manager-ControlPanel.ps1")
If Not fso.FileExists(launcher) Then
    shell.Popup "Launcher not found: " & launcher, 5, "Codex Total Manager", 16
    WScript.Quit 2
End If

shell.Run "powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -WindowStyle Hidden -File " & quote & launcher & quote, 0, False
WScript.Quit 0
