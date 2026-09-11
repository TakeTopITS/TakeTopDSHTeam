' TakeTop DSH Team - silent launcher (no console window from this script).
' Double-click THIS file for the cleanest start: it elevates to Administrator
' (one UAC prompt) and runs start.bat in a single visible window, so there is
' no black window that flashes open and closes.
'
' Tip: you can right-click this file -> Send to -> Desktop (create shortcut)
'      and rename the shortcut, e.g. "TakeTopDSH".
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh  = CreateObject("Shell.Application")
base = fso.GetParentFolderName(WScript.ScriptFullName)
' ShellExecute(file, args, workingdir, verb, show)   show: 0=hidden, 1=normal
sh.ShellExecute "cmd.exe", "/c """ & base & "\start.bat""", base, "runas", 1
