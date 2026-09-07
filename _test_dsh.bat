@echo off
cd /d "E:\WorkBuddy\UploadGitVersion\TakeTopDshTeam"
echo === Starting DSH ===
"E:\WorkBuddy\UploadGitVersion\TakeTopDshTeam\node\node.exe" --expose-internals "E:\WorkBuddy\UploadGitVersion\TakeTopDshTeam\node\node_modules\@deepseek-ai\dsh\lib\bin.js" --profile web --port 46000 --no-open 2>&1
echo === DSH exited with code %ERRORLEVEL% ===
pause
