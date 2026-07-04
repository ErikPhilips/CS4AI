@echo off
setlocal
REM ── cs4ai installer ─────────────────────────────────────────────────────────
REM Installs the cs4ai dotnet tool from the .nupkg sitting NEXT TO this script,
REM then writes the Claude Code skill to your user-global skills folder.
REM Requires the .NET 10 SDK: https://dotnet.microsoft.com/download

where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERROR: dotnet not found. Install the .NET 10 SDK first:
    echo   https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo == Installing cs4ai from %~dp0
dotnet tool install --global ErikPhilips.Cs4Ai --add-source "%~dp0." >nul 2>nul
if errorlevel 1 (
    echo    already installed - updating instead
    dotnet tool update --global ErikPhilips.Cs4Ai --add-source "%~dp0."
    if errorlevel 1 (
        echo ERROR: install failed. Is the .nupkg next to this script?
        pause
        exit /b 1
    )
)

echo == Installed:
cs4ai --version
if errorlevel 1 (
    echo NOTE: open a NEW terminal if 'cs4ai' isn't found yet - the tools
    echo folder is added to PATH on first dotnet tool install.
)

echo == Writing the Claude Code skill (user-global)
cs4ai --create-skill "%USERPROFILE%\.claude\skills"

echo.
echo Done. Quick start, from any C# repo:
echo   cs4ai session MySolution.sln      ^(returns a sess_ token^)
echo   cs4ai inspect ^<sess^> SomeClass
echo   cs4ai --help                      ^(full verb list^)
echo Edits write straight to disk - undo is git's job. Run 'cs4ai verify ^<sess^>' when done.
pause
