@echo off
REM Example launcher for Day0Gen.
REM The tool loads the game's assemblies at runtime, so the working directory
REM must be the TAB install dir and Day0Gen.exe must live there.
REM This regenerates the weekly Community Challenge map as a day-0 survival save.

cd /d "C:\Program Files (x86)\Steam\steamapps\common\They Are Billions"
Day0Gen.exe seed --seed 550040233 --name "CC 550040233"
pause
