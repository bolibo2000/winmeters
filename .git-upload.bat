@echo off

echo "Script will upload current folder to a given git repo."
set /p REMOTE_REPOSITORY_URL="Enter your git URL: "

git remote get-url origin >nul 2>&1
if errorlevel 1 (
    git remote add origin "%REMOTE_REPOSITORY_URL%")
 else (
    git remote set-url origin "%REMOTE_REPOSITORY_URL%")
git init
git add .
git commit -m "Initial commit"
git branch -M main
git push -u origin main
