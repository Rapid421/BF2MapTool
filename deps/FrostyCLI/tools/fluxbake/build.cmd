@echo off
rem Build fluxbake.exe against DICE's Flux (Frostbite tree). FLUX_ROOT defaults to the local engine checkout.
setlocal
if "%FLUX_ROOT%"=="" set FLUX_ROOT=E:\frostbite\Code\External\EA\Flux
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
if not exist bin mkdir bin
cl /nologo /std:c++17 /O2 /EHsc /MD /W3 /I"%FLUX_ROOT%\Include" fluxbake.cpp /Fo:bin\ /Fe:bin\fluxbake.exe /link "%FLUX_ROOT%\Lib\win64\Flux.lib" || exit /b 1
copy /y "%FLUX_ROOT%\Lib\win64\Flux.dll" bin\ >nul
echo built bin\fluxbake.exe
