@echo off
REM Install the cs4ai SKILL.md into the user-global Claude Code skills location.
cs4ai --create-skill "%USERPROFILE%\.claude\skills"
pause
