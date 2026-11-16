Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"

!define APP_NAME "Thumbnail Explorer"
!define APP_VERSION "1.0.0"
!define APP_PUBLISHER "PGShutaTakegami"
!define APP_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\PGShutaTakegami.ThumbnailExplorer"

Name "${APP_NAME}"
OutFile "${SetupOutput}"
InstallDir "$LOCALAPPDATA\Programs\Thumbnail Explorer"
InstallDirRegKey HKCU "${APP_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor zlib
VIProductVersion "1.0.0.0"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "LegalCopyright" "Copyright PGShutaTakegami"
VIAddVersionKey "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey "FileDescription" "Thumbnail Explorer Setup"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"

!define MUI_ABORTWARNING
!define MUI_ICON "..\App\Assets\ThumbnailExplorer.ico"
!define MUI_UNICON "..\App\Assets\ThumbnailExplorer.ico"
!define MUI_LICENSEPAGE_CHECKBOX
!define MUI_LICENSEPAGE_BUTTON "$(ContinueButton)"
!insertmacro MUI_PAGE_LICENSE "$(TermsFile)"
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE ValidateDirectory
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "$(FinishText)"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Japanese"

LicenseLangString TermsFile ${LANG_ENGLISH} "Terms.en.txt"
LicenseLangString TermsFile ${LANG_JAPANESE} "Terms.ja.txt"
LangString ContinueButton ${LANG_ENGLISH} "&Next >"
LangString ContinueButton ${LANG_JAPANESE} "次へ(&N) >"
LangString NeedX64 ${LANG_ENGLISH} "This installer requires 64-bit Windows."
LangString NeedX64 ${LANG_JAPANESE} "このインストーラーには 64 ビット版 Windows が必要です。"
LangString EmptyFolder ${LANG_ENGLISH} "Choose a new or empty folder for Thumbnail Explorer."
LangString EmptyFolder ${LANG_JAPANESE} "Thumbnail Explorer のインストール先には、新規または空のフォルダーを指定してください。"
LangString ExistingInstall ${LANG_ENGLISH} "Choose the existing installation folder, or uninstall it before choosing a different one."
LangString ExistingInstall ${LANG_JAPANESE} "既存のインストール先を指定してください。変更する場合は先にアンインストールしてください。"
LangString InstallFailed ${LANG_ENGLISH} "Installation could not be completed."
LangString InstallFailed ${LANG_JAPANESE} "インストールを完了できませんでした。"
LangString UninstallFailed ${LANG_ENGLISH} "Some files could not be removed. Close Thumbnail Explorer and run Uninstall again."
LangString UninstallFailed ${LANG_JAPANESE} "一部のファイルを削除できませんでした。Thumbnail Explorer を閉じてからアンインストールをやり直してください。"
LangString RuntimeNote ${LANG_ENGLISH} "Thumbnail Explorer requires the .NET 10 Desktop Runtime (x64)."
LangString RuntimeNote ${LANG_JAPANESE} "アプリの起動には .NET 10 Desktop Runtime（x64）が必要です。"
LangString FinishText ${LANG_ENGLISH} "Thumbnail Explorer has been installed.$\r$\n$\r$\n$(RuntimeNote)$\r$\n$\r$\nClick Finish to close Setup."
LangString FinishText ${LANG_JAPANESE} "Thumbnail Explorer をインストールしました。$\r$\n$\r$\n$(RuntimeNote)$\r$\n$\r$\n完了を押してセットアップを閉じてください。"

Function .onInit
    IfSilent 0 +3
        SetErrorLevel 2
        Abort
    ${IfNot} ${RunningX64}
        MessageBox MB_OK|MB_ICONSTOP "$(NeedX64)"
        Abort
    ${EndIf}
    SetShellVarContext current
FunctionEnd

Function ValidateDirectory
    ; Normalize using Win32 so a folder can be chosen before it exists.
    System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r0, p 0) i.r1'
    IntCmp $1 0 invalid invalid
    IntCmp $1 ${NSIS_MAX_STRLEN} invalid normalized invalid
normalized:
    StrCpy $INSTDIR $0
    ${GetRoot} "$INSTDIR" $0
    StrCmp "$INSTDIR" "$0" invalid
    StrCmp "$INSTDIR" "$0\" invalid
    StrCpy $1 "$INSTDIR" 1 -1
    StrCmp $1 "\" 0 +2
    StrCpy $INSTDIR "$INSTDIR" -1

    ReadRegStr $0 HKCU "${APP_KEY}" "InstallLocation"
    StrCmp $0 "" checkEmpty
    StrCmp $0 $INSTDIR 0 different
    IfFileExists "$INSTDIR\Uninstall.exe" done

checkEmpty:
    IfFileExists "$INSTDIR\*.*" 0 done
    ClearErrors
    FindFirst $0 $1 "$INSTDIR\*.*"
    IfErrors invalid
scan:
    StrCmp $1 "" closeDone
    StrCmp $1 "." advance
    StrCmp $1 ".." advance
    FindClose $0
    Goto invalid
advance:
    FindNext $0 $1
    Goto scan
closeDone:
    FindClose $0
done:
    Return
different:
    MessageBox MB_OK|MB_ICONEXCLAMATION "$(ExistingInstall)"
    Abort
invalid:
    MessageBox MB_OK|MB_ICONEXCLAMATION "$(EmptyFolder)"
    Abort
FunctionEnd

Section "Application"
    Call ValidateDirectory
    SetOutPath "$INSTDIR"
    File "${PayloadDir}\ThumbnailExplorer.exe"
    File "${PayloadDir}\ThumbnailExplorer.dll"
    File "${PayloadDir}\ThumbnailExplorer.deps.json"
    File "${PayloadDir}\ThumbnailExplorer.runtimeconfig.json"
    File "Terms.en.txt"
    File "Terms.ja.txt"
    File "LICENSE-dotnet-apphost.txt"
    File /oname=LICENSE-NSIS.txt "${NSISDIR}\COPYING"
    SetOutPath "$INSTDIR\ja"
    File "${PayloadDir}\ja\ThumbnailExplorer.resources.dll"
    SetOutPath "$INSTDIR"

    ClearErrors
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    IfErrors failed
    CreateShortcut "$SMPROGRAMS\Thumbnail Explorer.lnk" "$INSTDIR\ThumbnailExplorer.exe"
    IfErrors failed
    WriteRegStr HKCU "${APP_KEY}" "DisplayName" "${APP_NAME}"
    WriteRegStr HKCU "${APP_KEY}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKCU "${APP_KEY}" "Publisher" "${APP_PUBLISHER}"
    WriteRegStr HKCU "${APP_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${APP_KEY}" "DisplayIcon" "$INSTDIR\ThumbnailExplorer.exe,0"
    WriteRegStr HKCU "${APP_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
    WriteRegDWORD HKCU "${APP_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${APP_KEY}" "NoRepair" 1
    IfErrors failed
    Goto complete
failed:
    Abort "$(InstallFailed)"
complete:
SectionEnd

Section "Uninstall"
    SetShellVarContext current
    ClearErrors
    Delete "$INSTDIR\ThumbnailExplorer.exe"
    IfErrors inUse
    Delete "$INSTDIR\ThumbnailExplorer.dll"
    Delete "$INSTDIR\ThumbnailExplorer.deps.json"
    Delete "$INSTDIR\ThumbnailExplorer.runtimeconfig.json"
    Delete "$INSTDIR\Terms.en.txt"
    Delete "$INSTDIR\Terms.ja.txt"
    Delete "$INSTDIR\LICENSE-dotnet-apphost.txt"
    Delete "$INSTDIR\LICENSE-NSIS.txt"
    Delete "$INSTDIR\ja\ThumbnailExplorer.resources.dll"
    IfErrors inUse
    RMDir "$INSTDIR\ja"

    Delete "$SMPROGRAMS\Thumbnail Explorer.lnk"
    DeleteRegKey HKCU "${APP_KEY}"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
    Goto complete
inUse:
    Abort "$(UninstallFailed)"
complete:
SectionEnd
