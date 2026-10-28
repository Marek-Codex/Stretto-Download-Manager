Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"
!ifndef STRETTO_PAYLOAD
  !error "Use build-packages.ps1"
!endif
!define ARP_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Stretto"
Name "Stretto ${STRETTO_VERSION}"
OutFile "${STRETTO_OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\Stretto"
RequestExecutionLevel user
BrandingText "Stretto Download Manager"
VIProductVersion "${STRETTO_VERSION}"
VIAddVersionKey "ProductName" "Stretto"
VIAddVersionKey "FileDescription" "Stretto installer"
VIAddVersionKey "FileVersion" "${STRETTO_VERSION}"
VIAddVersionKey "LegalCopyright" "${STRETTO_PUBLISHER}"
!define MUI_ICON "..\Stretto.App\Assets\stretto.ico"
!define MUI_UNICON "..\Stretto.App\Assets\stretto.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "Install Stretto for your Windows account. The .NET and Windows App SDK runtimes are included.$\r$\n$\r$\nClose Stretto from its notification-area menu before upgrading. Startup remains an opt-in setting inside the app."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Stretto requires 64-bit Windows."
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "Stretto requires Windows 10 version 1809 or later."
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastBuild} 17763
    MessageBox MB_ICONSTOP "Stretto requires Windows 10 version 1809 or later."
    Abort
  ${EndIf}
  SetShellVarContext current
  SetRegView 64
FunctionEnd

!macro CheckNotRunning
  ${If} ${FileExists} "$INSTDIR\Stretto.App.exe"
    System::Call 'kernel32::CreateFileW(w "$INSTDIR\Stretto.App.exe", i 0x40000000, i 1, p 0, i 3, i 0x80, p 0) p.r0'
    ${If} $0 == -1
      MessageBox MB_ICONSTOP "Exit Stretto from its notification-area menu before continuing."
      Abort
    ${EndIf}
    System::Call 'kernel32::CloseHandle(p r0)'
  ${EndIf}
!macroend

Section "Install"
  !insertmacro CheckNotRunning
  SetOutPath "$INSTDIR"
  ClearErrors
  File /r "${STRETTO_PAYLOAD}\*"
  ${If} ${Errors}
    MessageBox MB_ICONSTOP "Stretto could not be installed."
    Abort
  ${EndIf}
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\Stretto.lnk" "$INSTDIR\Stretto.App.exe" "" "$INSTDIR\Assets\stretto.ico"
  WriteRegStr HKCU "Software\Classes\stretto" "" "URL:Stretto Download Manager"
  WriteRegStr HKCU "Software\Classes\stretto" "URL Protocol" ""
  WriteRegStr HKCU "Software\Classes\stretto\shell\open\command" "" '$\"$INSTDIR\Stretto.App.exe$\" $\"%1$\"'
  WriteRegStr HKCU "${ARP_KEY}" "DisplayName" "Stretto"
  WriteRegStr HKCU "${ARP_KEY}" "DisplayVersion" "${STRETTO_VERSION}"
  WriteRegStr HKCU "${ARP_KEY}" "Publisher" "${STRETTO_PUBLISHER}"
  WriteRegStr HKCU "${ARP_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${ARP_KEY}" "DisplayIcon" "$INSTDIR\Assets\stretto.ico"
  WriteRegStr HKCU "${ARP_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${ARP_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${ARP_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${ARP_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "${ARP_KEY}" "EstimatedSize" $0
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  ; Only this fixed, installer-owned directory can be removed recursively.
  ${If} $INSTDIR != "$LOCALAPPDATA\Programs\Stretto"
    MessageBox MB_ICONSTOP "Unexpected install directory. Uninstall stopped."
    Abort
  ${EndIf}
  !insertmacro CheckNotRunning
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Stretto"
  ${If} $0 == '$\"$INSTDIR\Stretto.App.exe$\" --tray'
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Stretto"
  ${EndIf}
  ReadRegStr $0 HKCU "Software\Classes\stretto\shell\open\command" ""
  ${If} $0 == '$\"$INSTDIR\Stretto.App.exe$\" $\"%1$\"'
    DeleteRegKey HKCU "Software\Classes\stretto"
  ${EndIf}
  Delete "$SMPROGRAMS\Stretto.lnk"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${ARP_KEY}"
  ; Downloads and %LOCALAPPDATA%\Stretto settings/tools are intentionally retained.
SectionEnd
