@echo off
:: Check for Python and capture the version
for /f "delims=" %%i in ('python --version 2^>^&1') do set "pyver=%%i"

:: Check if the "pyver" variable starts with "Python" indicating Python is installed
echo Checking for Python...
if "%pyver:~0,6%"=="Python" (
    echo Found: %pyver%
    echo.
    echo Running the script...
    :: Add the command to run your Python script below this line. For example:
    python devtool_script.py
    pause
) else (
    echo WARNING: Python is not installed or not found in the system PATH.
    echo Please install Python from https://www.python.org/ and ensure it's added to the system PATH.
    pause
)
