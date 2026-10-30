; ============================================================================================
;  tbc-revit-mcp Setup (Inno Setup 6)
;  Build with installer\build.ps1, which extracts the setup ZIP into installer\payload and
;  passes the version. Do not compile this file directly.
; ============================================================================================
;
;  What it does: copies the setup package to %LOCALAPPDATA%\NourRouas\tbc-revit-mcp\setup and
;  runs its install.ps1, which installs the Revit add-ins and the MCP server, migrates data from
;  rvt-mcp and wires the chosen AI clients. install.ps1 already checks, rolls back and reports
;  every step, so this wizard only adds the pre-checks, the client choice and an uninstaller.
;
;  Per-user install: add-ins go to %APPDATA%\Autodesk\Revit\Addins and the server to
;  %LOCALAPPDATA%, so no administrator rights are needed.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "tbc-revit-mcp"
#define AppPublisher "Nour Rouas - TeamBIMCivil"
#define AppUrl "https://desarrolloteambimcivil.github.io/tbc_mcp_revit/"
#define RepoUrl "https://github.com/desarrolloteambimcivil/tbc_mcp_revit"

[Setup]
AppId={{9F3C2B71-5E84-4D0A-B6E2-7A41C9D85F13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases/latest
VersionInfoVersion={#AppVersion}
VersionInfoCompany=TeamBIMCivil
VersionInfoDescription=tbc-revit-mcp Setup
VersionInfoCopyright=Copyright 2026 Nour Rouas. Based on rvt-mcp by Khoa Le (Apache-2.0)

; install.ps1 decides where the add-ins and the server go; this folder only keeps the package
; for uninstall and repair, so the user is not asked for a directory.
DefaultDirName={localappdata}\NourRouas\tbc-revit-mcp\setup
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

OutputDir=output
OutputBaseFilename=TbcRevitMcp_Setup_{#AppVersion}
SetupIconFile=assets\teambimcivil.ico
UninstallDisplayIcon={app}\teambimcivil.ico
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
WizardImageFile=assets\wizard-large.bmp
WizardSmallImageFile=assets\wizard-small.bmp
LicenseFile=..\LICENSE
ShowLanguageDialog=auto

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; The extracted setup ZIP: install.ps1, uninstall.ps1, manifest.json, plugins\, server\.
Source: "payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "assets\teambimcivil.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE"; DestDir: "{app}"; Flags: ignoreversion

[UninstallRun]
; First the client entries and add-ins, then the server copies; personal data is kept.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install.ps1"" -Uninstall -Client auto"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveClientsAndAddins"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall.ps1"" -Yes"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveServer"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Messages]
spanish.WelcomeLabel2=Se va a instalar [name/ver] en este equipo.%n%ntbc-revit-mcp conecta Claude y otros asistentes de IA con Autodesk Revit 2022–2027: consultan, revisan y editan el modelo en local, y cada cambio se puede deshacer.%n%nCierra Revit y tus asistentes de IA (Claude Code, Claude Desktop…) antes de continuar.
english.WelcomeLabel2=This will install [name/ver] on your computer.%n%ntbc-revit-mcp connects Claude and other AI assistants to Autodesk Revit 2022–2027: they read, check and edit the model locally, and every change can be undone.%n%nClose Revit and your AI assistants (Claude Code, Claude Desktop…) before continuing.
spanish.FinishedLabel=[name] se ha instalado correctamente.%n%nAbre Revit y, si no arranca solo, pulsa Complementos → RvtMcp. Después reinicia tu asistente de IA y pídele, por ejemplo: «¿En qué vista estoy?».
english.FinishedLabel=[name] has been installed.%n%nOpen Revit and, if it does not start by itself, click Add-Ins → RvtMcp. Then restart your AI assistant and ask, for example: "Which view am I in?".

[CustomMessages]
spanish.ClientsCaption=Asistentes de IA
spanish.ClientsHeading=¿Con qué asistentes de IA quieres usar tbc-revit-mcp?
spanish.ClientsHint=Se añade la entrada «tbc-revit-mcp» a su configuración (con copia de seguridad). Los detectados en este equipo ya están marcados.
spanish.ClientClaudeCode=Claude Code
spanish.ClientClaudeDesktop=Claude Desktop
spanish.ClientCursor=Cursor
spanish.ClientCodex=Codex
spanish.NoRevit=No se ha encontrado Revit 2022–2027 en este equipo. tbc-revit-mcp necesita Revit instalado.
spanish.CloseFirst=Cierra estos programas y pulsa Siguiente para volver a comprobarlo:%n%n%1%nLos asistentes de IA (Claude Code, Claude Desktop, Cursor…) mantienen el servidor en uso mientras están abiertos.
spanish.CloseFirstUninstall=Cierra estos programas y vuelve a ejecutar la desinstalación:%n%n%1
spanish.Running=Instalando los complementos de Revit y configurando los asistentes…
spanish.Failed=La instalación no se ha completado y se ha dejado todo como estaba.%n%nDetalles en:%n%1
spanish.FailedFinished=La instalación no se ha completado. No se ha cambiado nada en Revit ni en tus asistentes.%n%nRevisa el registro en %1 y vuelve a ejecutar el instalador.
english.ClientsCaption=AI assistants
english.ClientsHeading=Which AI assistants do you want to use tbc-revit-mcp with?
english.ClientsHint=A "tbc-revit-mcp" entry is added to their configuration (a backup is made first). Assistants found on this computer are already ticked.
english.ClientClaudeCode=Claude Code
english.ClientClaudeDesktop=Claude Desktop
english.ClientCursor=Cursor
english.ClientCodex=Codex
english.NoRevit=Revit 2022–2027 was not found on this computer. tbc-revit-mcp needs Revit installed.
english.CloseFirst=Close these programs, then click Next to check again:%n%n%1%nAI assistants (Claude Code, Claude Desktop, Cursor…) keep the server in use while they are open.
english.CloseFirstUninstall=Close these programs, then run the uninstaller again:%n%n%1
english.Running=Installing the Revit add-ins and wiring your AI assistants…
english.Failed=Installation did not complete and everything was left as it was.%n%nDetails:%n%1
english.FailedFinished=Installation did not complete. Nothing was changed in Revit or in your AI assistants.%n%nCheck the log at %1 and run the installer again.

[Code]
var
  ClientsPage: TInputOptionWizardPage;
  InstallFailed: Boolean;

{ Running processes whose name matches one of the given names, one per line. A WMI failure
  returns an empty list: the check is a convenience and install.ps1 checks Revit again. }
function RunningProcesses(const Names: array of String): String;
var
  Locator, Service, Items, Item: Variant;
  Query, Name: String;
  I: Integer;
begin
  Result := '';
  Query := '';
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    if Query <> '' then Query := Query + ' OR ';
    Query := Query + 'Name = ''' + Names[I] + '''';
  end;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Items := Service.ExecQuery('SELECT Name FROM Win32_Process WHERE ' + Query);
    for I := 0 to Items.Count - 1 do
    begin
      Item := Items.ItemIndex(I);
      Name := Item.Name;
      if Pos(Name, Result) = 0 then
        Result := Result + '  • ' + Name + #13#10;
    end;
  except
    Result := '';
  end;
end;

function RevitInstalled(): Boolean;
var
  Year: Integer;
begin
  Result := False;
  for Year := 2022 to 2027 do
    if FileExists(ExpandConstant('{commonpf64}\Autodesk\Revit ') + IntToStr(Year) + '\Revit.exe') then
      Result := True;
end;

function ClaudeDesktopPresent(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := DirExists(ExpandConstant('{userappdata}\Claude')) or DirExists(ExpandConstant('{localappdata}\AnthropicClaude'));
  if not Result and FindFirst(ExpandConstant('{localappdata}\Packages\Claude_*'), FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := RevitInstalled();
  if not Result then
    MsgBox(CustomMessage('NoRevit'), mbError, MB_OK);
end;

procedure InitializeWizard();
begin
  ClientsPage := CreateInputOptionPage(wpLicense,
    CustomMessage('ClientsCaption'), CustomMessage('ClientsHeading'), CustomMessage('ClientsHint'),
    False, False);
  ClientsPage.Add(CustomMessage('ClientClaudeCode'));
  ClientsPage.Add(CustomMessage('ClientClaudeDesktop'));
  ClientsPage.Add(CustomMessage('ClientCursor'));
  ClientsPage.Add(CustomMessage('ClientCodex'));
  ClientsPage.Values[0] := FileExists(ExpandConstant('{%USERPROFILE}\.claude.json'));
  ClientsPage.Values[1] := ClaudeDesktopPresent();
  ClientsPage.Values[2] := DirExists(ExpandConstant('{%USERPROFILE}\.cursor'));
  ClientsPage.Values[3] := DirExists(ExpandConstant('{%USERPROFILE}\.codex'));
end;

{ install.ps1 client names for the ticked boxes; none keeps every client config untouched. }
function SelectedClients(): String;
begin
  Result := '';
  if ClientsPage.Values[0] then Result := Result + ',claude';
  if ClientsPage.Values[1] then Result := Result + ',claude-desktop';
  if ClientsPage.Values[2] then Result := Result + ',cursor';
  if ClientsPage.Values[3] then Result := Result + ',codex';
  if Result = '' then Result := 'none' else Delete(Result, 1, 1);
end;

{ Runs before any file is copied. Revit locks the add-in DLLs and an open AI assistant keeps
  the installed server running, so replacing them would fail and install.ps1 would roll back. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Running: String;
begin
  Running := RunningProcesses(['Revit.exe', 'tbc-revit-mcp.exe', 'RvtMcp.Server.exe', 'rvt-mcp.exe']);
  if Running <> '' then
    Result := FmtMessage(CustomMessage('CloseFirst'), [Running])
  else
    Result := '';
end;

procedure RunInstallScript();
var
  Log, Command: String;
  ResultCode: Integer;
begin
  Log := ExpandConstant('{app}\install.log');
  WizardForm.StatusLabel.Caption := CustomMessage('Running');
  Command := '-NoProfile -ExecutionPolicy Bypass -Command "& { try { & ''' + ExpandConstant('{app}\install.ps1') +
    ''' -Client ' + SelectedClients() + ' *>&1 | Out-File -FilePath ''' + Log +
    ''' -Encoding utf8; exit 0 } catch { $_ | Out-File -FilePath ''' + Log + ''' -Append -Encoding utf8; exit 1 } }"';
  if not Exec('powershell.exe', Command, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    InstallFailed := True;
    MsgBox(FmtMessage(CustomMessage('Failed'), [Log]), mbError, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RunInstallScript();
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and InstallFailed then
    WizardForm.FinishedLabel.Caption := FmtMessage(CustomMessage('FailedFinished'), [ExpandConstant('{app}\install.log')]);
end;

function InitializeUninstall(): Boolean;
var
  Running: String;
begin
  Running := RunningProcesses(['Revit.exe', 'tbc-revit-mcp.exe', 'RvtMcp.Server.exe']);
  Result := Running = '';
  if not Result then
    MsgBox(FmtMessage(CustomMessage('CloseFirstUninstall'), [Running]), mbError, MB_OK);
end;
