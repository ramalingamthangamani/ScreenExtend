@echo off
setlocal
cd /d "%~dp0"

rem Builds the host exe, copies it into the Python package and builds the wheel + sdist into python\dist\.

where dotnet >nul 2>&1 || (echo ERROR: dotnet not found. Install the .NET 10 SDK. & goto :fail)
where python >nul 2>&1 || (echo ERROR: python not found. & goto :fail)

echo [1/3] Publishing ScreenExtend.exe ...
if exist dist rmdir /s /q dist
dotnet publish src\ScreenExtend -c Release -p:SelfContained=false -p:PublishSingleFile=true -o dist -v q || goto :fail
if not exist dist\ScreenExtend.exe (echo ERROR: dist\ScreenExtend.exe missing & goto :fail)

echo [2/3] Copying into python\screenextend\bin ...
if not exist python\screenextend\bin mkdir python\screenextend\bin
copy /y dist\ScreenExtend.exe python\screenextend\bin\ScreenExtend.exe >nul || goto :fail
> python\screenextend\bin\BUILD.txt echo Built %DATE% %TIME% from %CD%

echo [3/3] Building wheel and sdist ...
python -m pip install --disable-pip-version-check -q build || goto :fail
cd python
if exist dist rmdir /s /q dist
if exist build rmdir /s /q build
python -m build || goto :fail
cd ..

echo.
echo Done. Packages:
dir /b python\dist
echo.
echo Upload with:  python -m pip install twine ^&^& python -m twine upload python\dist\*
endlocal
exit /b 0

:fail
endlocal
if /i not "%~1"=="--no-pause" pause
exit /b 1
