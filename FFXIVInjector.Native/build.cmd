@echo off
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
cd /d "%~dp0"
if not exist out mkdir out
cl /nologo /O2 /MT /c /I vendor\minhook-1.3.4\include /Foout\ vendor\minhook-1.3.4\src\buffer.c vendor\minhook-1.3.4\src\hook.c vendor\minhook-1.3.4\src\trampoline.c vendor\minhook-1.3.4\src\hde\hde64.c
if errorlevel 1 exit /b 1
cl /nologo /O2 /MT /EHsc /std:c++20 /DUNICODE /D_UNICODE /LD loader.cpp out\buffer.obj out\hook.obj out\trampoline.obj out\hde64.obj /Foout\loader.obj /Feout\dinput8.dll /link /DEF:loader.def bcrypt.lib user32.lib
if errorlevel 1 exit /b 1
cl /nologo /O2 /MT /EHsc /std:c++20 /DUNICODE /D_UNICODE tests.cpp out\buffer.obj out\hook.obj out\trampoline.obj out\hde64.obj /Foout\tests.obj /Feout\tests.exe /link bcrypt.lib user32.lib
if errorlevel 1 exit /b 1
out\tests.exe
if errorlevel 1 exit /b 1

copy /Y out\dinput8.dll ..\FFXIVInjector.Core\Native\dinput8.dll >nul
if errorlevel 1 exit /b 1
