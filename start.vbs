' TakeTop DSH Web - silent launcher (no console window)
' Double-click this to start TakeTop DSH Web without any black cmd window.
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

' Run start.bat hidden (window style 0 = hidden), don't wait.
shell.Run """" & fso.GetParentFolderName(WScript.ScriptFullName) & "\start.bat" & """", 0, False
