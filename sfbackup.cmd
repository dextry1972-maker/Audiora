@echo off
rem Sonos Flow - copias de seguridad del proyecto. Ejemplos:
rem   sfbackup create "Antes de cambiar el streaming"
rem   sfbackup list
rem   sfbackup verify SonosFlow_2026-10-26_03-31-13
rem   sfbackup restore SonosFlow_2026-10-26_03-31-13
dotnet run --project "%~dp0src\SonosFlow.BackupCli\SonosFlow.BackupCli.csproj" -c Release -- %* --project "%~dp0."
