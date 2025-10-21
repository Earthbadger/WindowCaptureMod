@echo off
setlocal

echo =================================================
echo      BUILDING 64-BIT (x64) COMPONENTS
echo =================================================
if not exist build mkdir build
cd build
cmake -A x64 ..
if %errorlevel% neq 0 (
    echo CMake configuration for x64 failed.
    exit /b %errorlevel%
)
cmake --build . --config Release
if %errorlevel% neq 0 (
    echo Build for x64 failed.
    exit /b %errorlevel%
)
cd ..
echo.
echo =================================================
echo      COLLECTING FILES INTO 'dist' FOLDER
echo =================================================
if exist dist rmdir /s /q dist
mkdir dist
 
xcopy "build\Release" "dist\" /E /I /Y

echo.
echo Build complete! All necessary files are in the 'dist' folder.
echo You can now copy the contents of 'dist' to your EmuVR\UserData\WindowCapture folder.

endlocal
