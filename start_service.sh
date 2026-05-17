#!/usr/bin/env bash
cd ./MusicPlayerSyncServer
dotnet ef database update
dotnet run -c Release
