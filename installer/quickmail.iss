; QuickMail InnoSetup Installer Script
; Copyright (c) 2026 Kelly Ford.
;
; QuickMail ships as a self-contained, single-file win-x64 executable: the .NET 8
; runtime is bundled inside QuickMail.exe, so no .NET runtime needs to be installed.
; The only external prerequisite is the Microsoft Edge WebView2 Runtime, which the
; installer detects and installs on demand (see [Code] below).

#define MyAppName "Eudora QuickMail"
#define MyAppNameLower "eudora-quickmail"
#define MyAppPublisher "Kelly Ford"
#define MyAppURL "https://github.com/DinamicaTech/QuickMail"
#define MyAppSupportURL MyAppURL + "/issues"
#define MyAppExeName "QuickMail.exe"
#define MyAppDescription "Keyboard-first, accessible desktop email client for Windows"
#define MailtoProgId "EudoraQuickMail.Url.Mailto"

; Source path (relative to this script). Matches the output of `build.bat publish`
; and the GitHub Actions release step (`dotnet publish ... -o publish/`).
#define SourcePath "..\publish"

; Read the version straight from the compiled executable's FileVersion.
#define MyAppVersion GetVersionNumbersString(SourcePath + "\" + MyAppExeName)

[Setup]
; Application information
AppId={{D3B8E95A-6354-48E4-A91F-615BCFF20C9F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppSupportURL}
AppUpdatesURL={#MyAppURL}/releases
AppCopyright=Copyright (c) 2026 {#MyAppPublisher}.

; Installation directory
DefaultDirName={autopf}\Eudora QuickMail
DefaultGroupName={#MyAppName}
AllowNoIcons=yes

; Output configuration
OutputDir=Output
OutputBaseFilename={#MyAppNameLower}-v{#MyAppVersion}-setup
SetupIconFile=..\QuickMail\Assets\App\QuickMail.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

; Uninstall configuration
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}

; System requirements (Windows 10 1809+, 64-bit)
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Program files are installed machine-wide by default. The data directory is
; selected separately and remains writable by the current user.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

; If QuickMail is running during an upgrade, use the Restart Manager to detect the
; locked executable and prompt the user to close it (the app defines no mutex).
CloseApplications=yes
RestartApplications=no

DisableProgramGroupPage=yes

; License shown during installation
LicenseFile=..\LICENSE

; Language options
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl,Languages\Custom.en.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Include the self-contained executables plus the offline HTML editor, calendar and
; dictionaries. Debug symbols and NuGet IntelliSense XML are not runtime assets.
Source: "{#SourcePath}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--profileDir ""{code:GetDataDir}"""; Comment: "{cm:AppDescription}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--profileDir ""{code:GetDataDir}"""; Comment: "{cm:AppDescription}"; Tasks: desktopicon

[Registry]
; Register as a per-user candidate for the mailto protocol. Windows intentionally does not let
; installers force a default app: the user makes the final association in Default Apps.
Root: HKCU; Subkey: "Software\Classes\{#MailtoProgId}"; ValueType: string; ValueName: ""; ValueData: "URL:Eudora QuickMail MailTo Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#MailtoProgId}"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\{#MailtoProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\{#MailtoProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" --profileDir ""{code:GetDataDir}"" --mailto ""%1"""
Root: HKCU; Subkey: "Software\EudoraQuickMail\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\EudoraQuickMail\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "{#MyAppDescription}"
Root: HKCU; Subkey: "Software\EudoraQuickMail\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\EudoraQuickMail\Capabilities\UrlAssociations"; ValueType: string; ValueName: "mailto"; ValueData: "{#MailtoProgId}"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: "Software\EudoraQuickMail\Capabilities"; Flags: uninsdeletevalue

[Run]
Filename: "ms-settings:defaultapps?registeredAppUser=Eudora%20QuickMail"; Description: "Open Windows Default Apps to make Eudora QuickMail my default email app"; Flags: shellexec postinstall skipifsilent unchecked
Filename: "{app}\{#MyAppExeName}"; Parameters: "--profileDir ""{code:GetDataDir}"""; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

#include "CodeDependencies.iss"

[Code]
var
  DataDirPage: TInputDirWizardPage;

procedure InitializeWizard;
begin
  DataDirPage := CreateInputDirPage(wpSelectDir,
    'Eudora QuickMail data folder',
    'Where should Eudora QuickMail store your data?',
    'Choose a folder for email, indexes, settings, and copied or moved attachments, including embedded images. This folder is kept when Eudora QuickMail is upgraded.',
    False, '');
  DataDirPage.Add('');
  DataDirPage.Values[0] := GetPreviousData('DataDir', ExpandConstant('{userdocs}\Eudora QuickMail'));
end;

function GetDataDir(Param: String): String;
begin
  if DataDirPage <> nil then
    Result := DataDirPage.Values[0]
  else
    Result := ExpandConstant('{userdocs}\Eudora QuickMail');
end;

function IsInsideOrEqual(PathValue: String; RootValue: String): Boolean;
var
  NormalizedPath: String;
  NormalizedRoot: String;
begin
  NormalizedPath := Lowercase(AddBackslash(ExpandFileName(PathValue)));
  NormalizedRoot := Lowercase(AddBackslash(ExpandFileName(RootValue)));
  Result := Pos(NormalizedRoot, NormalizedPath) = 1;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  DataPath: String;
begin
  Result := True;
  if CurPageID <> DataDirPage.ID then Exit;
  DataPath := GetDataDir('');
  if IsInsideOrEqual(DataPath, ExpandConstant('{commonpf}')) or
     IsInsideOrEqual(DataPath, ExpandConstant('{commonpf32}')) then
  begin
    MsgBox('The data folder cannot be stored inside Program Files because Eudora QuickMail needs normal write access. Choose Documents\Eudora QuickMail or another folder outside Program Files.',
      mbError, MB_OK);
    Result := False;
  end;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'DataDir', GetDataDir(''));
end;

function InitializeSetup(): Boolean;
begin
  // App is x64 only; keep dependency installers 64-bit too.
  Dependency_ForceX86 := False;

  // QuickMail renders HTML mail through WebView2. The runtime is preinstalled on
  // Windows 11 and recent Windows 10, but install it on demand when missing.
  Dependency_AddWebView2;

  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    ForceDirectories(GetDataDir(''));
    SaveStringToFile(ExpandConstant('{app}\QuickMailDataPath.txt'), GetDataDir(''), False);
  end;
end;

// Offer to remove the production profile. Credentials remain in Windows Credential Manager.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserDataPath: String;
  StoredDataPath: AnsiString;
  ProgramPath: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if LoadStringFromFile(ExpandConstant('{app}\QuickMailDataPath.txt'), StoredDataPath) then
    begin
      UserDataPath := Trim(String(StoredDataPath));
      ProgramPath := AddBackslash(ExpandConstant('{app}'));
      if CompareText(AddBackslash(ExpandFileName(UserDataPath)), ProgramPath) = 0 then
      begin
        // When program and data share the exact directory, Inno Setup removes only
        // its registered application files and leaves all other profile files intact.
        // Never recursively delete the shared directory as user data.
        Exit;
      end;
      if (UserDataPath <> '') and DirExists(UserDataPath) and
         (MsgBox(CustomMessage('RemoveUserData'),
                 mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      begin
        DelTree(UserDataPath, True, True, True);
      end;
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpReady then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall)
  else if CurPageID = wpFinished then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonFinish)
  else
    WizardForm.NextButton.Caption := SetupMessage(msgButtonNext);
end;
