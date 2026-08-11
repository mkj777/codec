> [!IMPORTANT]
> **Codec is currently in early development.** Features may be partly or completely unfinished/missing.  
> Screenshots and Information are subject to change.  

<img src="https://github.com/user-attachments/assets/b935f0ff-bf2b-4c9e-8a09-de25201c729d"  align="left" width="130" alt="stand" style="margin-right: 15px;"/>


### Codec
Codec is a clean Windows game library for everything you own.  
It brings installed games, third-party downloads, and launcher libraries into one shelf.  
   
---
### Features
Scans your PC and enriches games with detailed metadata and artwork.  
Works with Steam, Epic Games, Riot Games, and manually added executables.  
Steam integration can add owned games, sync achievements, and launch through the existing ecosystem.  
Games can be launched directly or through custom launch scripts.

### Installation
Download it from the Website: [Codec](https://codeclibrary.vercel.app/)  
or the installer "Codec-win-Setup.exe" from the [Releases](https://github.com/mkj777/codec-releases/releases) section  

### Requirements
Windows 10 or later  

### Development
Codec is a Windows x64 C# desktop application using .NET 9 and Avalonia UI.  
`Codec.Avalonia/Codec.Avalonia.csproj` is the executable project; `Codec.Core` contains models and services.  
Velopack remains responsible for installation and updates.

Create a Windows x64 release with the existing Codec package identity:

```powershell
.\scripts\package.ps1 -Version 0.8.0
```

Publish output is written to `obj\publish-avalonia`; Velopack artifacts are written to `obj\velopack-releases`.

thanks to [Leon Terme](https://github.com/LeMIT2427) for the idea and help with the concept  
thanks to [SteamKit2](https://github.com/SteamRE/SteamKit) for the steam integration to reliably fetch steam assets  
thanks to [Stefan Gabriel Craescu](https://github.com/Deadlock-too) for [howlongtobeat-ts](https://github.com/Deadlock-too/howlongtobeat-ts) to integrate HowLongToBeat  

### Screenshots
<img width="1903" height="1047" alt="image" src="https://github.com/user-attachments/assets/4f19a25d-71aa-401a-801e-21d0fb7a71eb" />

<img width="1751" height="1060" alt="image" src="https://github.com/user-attachments/assets/c3dc8ba1-c1fa-48ef-96d4-732291b982b8" />

<img width="1751" height="1060" alt="image" src="https://github.com/user-attachments/assets/d7fbf338-87bc-46a4-8f94-1fd7ce7bbff4" />
