; NSIS installer for RetroLabel LP2443 (Windows 10/11 x64)
; Build:  makensis /DVERSION=2.0.0 /DSRCDIR=<publish dir> installer\RetroLabel.nsi
Unicode true
!ifndef VERSION
  !define VERSION "2.0.0"
!endif
!ifndef SRCDIR
  !error "Pass /DSRCDIR=<self-contained publish folder>"
!endif
!define APP "RetroLabel LP2443"
!define EXE "RetroLabel.exe"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\RetroLabel"
!define OLDAPP "Zebra Label Printer"

Name "${APP}"
OutFile "RetroLabel-Setup-${VERSION}-x64.exe"
InstallDir "$PROGRAMFILES64\${APP}"
InstallDirRegKey HKLM "Software\RetroLabel" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APP} ${VERSION}"

!include "MUI2.nsh"
!include "x64.nsh"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start ${APP} now"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "This program needs 64-bit Windows."
    Abort
  ${EndIf}
  SetRegView 64
FunctionEnd

Section "${APP} (required)" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"
  ; stop a running copy so files can be replaced
  ExecWait '"$SYSDIR\taskkill.exe" /F /IM ${EXE}'
  ; ---- remove v1 ("Zebra Label Printer"). Its data folder is left alone; the app copies it on first start.
  ExecWait '"$SYSDIR\taskkill.exe" /F /IM ZebraLabelPrinter.exe'
  Sleep 800
  RMDir /r "$PROGRAMFILES64\${OLDAPP}"
  RMDir /r "$SMPROGRAMS\${OLDAPP}"
  Delete "$DESKTOP\${OLDAPP}.lnk"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\ZebraLabelPrinter"
  DeleteRegKey HKLM "Software\ZebraLabelPrinter"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "ZebraLabelPrinter"
  ExecWait '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="${OLDAPP} (phone printing)"'
  File /r "${SRCDIR}\*.*"
  File "..\README.md"
  File "..\LICENSE"
  File "..\THIRD-PARTY-NOTICES.md"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\${APP}"
  CreateShortcut "$SMPROGRAMS\${APP}\${APP}.lnk" "$INSTDIR\${EXE}"
  CreateShortcut "$SMPROGRAMS\${APP}\Diagnostics report.lnk" "$INSTDIR\${EXE}" "--diagnose"
  CreateShortcut "$SMPROGRAMS\${APP}\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\RetroLabel" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${APP}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "RetroLabel (open source, MIT)"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
SectionEnd

Section "Desktop shortcut" SecDesktop
  CreateShortcut "$DESKTOP\${APP}.lnk" "$INSTDIR\${EXE}"
SectionEnd

Section "Allow phones on my home network (Windows Firewall, Private networks only)" SecFirewall
  ExecWait '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="${APP} (phone printing)"'
  ExecWait '"$SYSDIR\netsh.exe" advfirewall firewall add rule name="${APP} (phone printing)" dir=in action=allow program="$INSTDIR\${EXE}" profile=private enable=yes'
SectionEnd

LangString D_Main ${LANG_ENGLISH} "The application, PDF engine and .NET runtime (nothing else to install). Replaces Zebra Label Printer v1 if present."
LangString D_Desk ${LANG_ENGLISH} "Add a shortcut on the desktop."
LangString D_FW ${LANG_ENGLISH} "Adds an inbound rule for this program on Private networks so phones on your Wi-Fi can reach it. Skip if you will not use phone printing."
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} $(D_Main)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} $(D_Desk)
  !insertmacro MUI_DESCRIPTION_TEXT ${SecFirewall} $(D_FW)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  SetRegView 64
  ExecWait '"$SYSDIR\taskkill.exe" /F /IM ${EXE}'
  Sleep 800
  ExecWait '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="${APP} (phone printing)"'
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "RetroLabel"
  Delete "$DESKTOP\${APP}.lnk"
  RMDir /r "$SMPROGRAMS\${APP}"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKLM "${UNINST_KEY}"
  DeleteRegKey HKLM "Software\RetroLabel"
  MessageBox MB_YESNO|MB_ICONQUESTION "Also delete your settings, print history and stored PDFs?$\n($LOCALAPPDATA\RetroLabel)" IDNO done
  RMDir /r "$LOCALAPPDATA\RetroLabel"
  done:
SectionEnd
