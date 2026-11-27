![](https://gitlab.com/iotlink/iotlink/raw/develop/Assets/images/logos/logo_full.png)

IOT Link is a full featured service for connecting devices with IOT enabled services using MQTT. 


### Features

- Open source software.
- Download the Windows installer from [GitHub Releases](https://github.com/DOCaCola/IOTLink/releases/latest).
- Minimal configuration setup.
- Changes to engine configuration will be reloaded **automatically**.
- Widely customizable using [Addons](https://gitlab.com/iotlink/iotlink/wikis/Addons/Home).
- Fully working as a **Windows Service** to provide reliable data as soon as the windows boot up, without having to wait for a windows user logon.

### Building the .NET 11 migration branch

The installer on this branch targets x64 Windows 10 build 19041 or later and includes the .NET runtime. Installing the Windows service requires administrator rights. The GitHub Releases link above provides the stable release.

To build and test on Windows, install the .NET 11 SDK and run these commands from the repository root.

```powershell
dotnet build IOTLink.sln -c Release
dotnet test IOTLink.sln -c Release --no-build
```

To package the service, agent and bundled add-ons, install Inno Setup 6 and run `./Build-Installer.ps1`. The installer is written to `Setup/`. The script accepts `-CompilerPath` for a custom Inno Setup installation.

### More Information.

- More information available at our [Wiki Pages](https://gitlab.com/iotlink/iotlink/wikis/).
