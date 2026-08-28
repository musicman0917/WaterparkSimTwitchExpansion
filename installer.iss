; WaterparkSimTwitchExpansion end-user installer.
;
; This is source for the Inno Setup Compiler (free, https://jrsoftware.org/isinfo.php) - it is
; NOT itself an installer. To produce the actual distributable Setup.exe:
;   1. Build the mod for Release first (see package.ps1's own build step - this script does NOT
;      build the mod itself, it just packages whatever's already sitting in
;      WaterparkSimTwitchExpansion\bin\Release\net6.0\).
;   2. Bump MyAppVersion below to match Plugin.cs's PluginVersion constant (kept as a separate,
;      manually-synced constant here rather than parsed out of Plugin.cs at compile time, to
;      avoid fragile text-parsing Pascal preprocessor code for a value that only changes once per
;      release anyway).
;   3. Open this file in Inno Setup and click Build > Compile (or run ISCC.exe installer.iss from
;      a command line). The compiled Setup.exe lands in release\ alongside package.ps1's zip.
;
; What this handles that package.ps1's plain zip doesn't:
;   - Tries to auto-detect the Waterpark Simulator Steam install folder (registry + parsing
;     libraryfolders.vdf for non-default library drives), pre-filling the destination page
;     instead of leaving it as a "paste your own path" text field - still fully editable/
;     browsable if the guess is wrong or nothing was found.
;   - Warns (without hard-blocking) if BepInEx's IL2CPP pack doesn't look installed yet in the
;     chosen folder, since this mod is useless without it and that's an easy thing to forget -
;     same one-time external prerequisite install.ps1/SETUP.md already describe, not bundled here
;     (matches every other Waterpark Simulator IL2CPP mod's expectations - see README's
;     "Distributing a release" section for why bundling it here would be the wrong call).
;   - Asks for Twitch channel/bot/OAuth token on one wizard page and writes them straight into the
;     BepInEx config's [Twitch] section (auto-creating the file if it doesn't exist yet - it
;     normally only appears after the game's first launch) - so most people never have to hand-
;     edit a text file at all. Left blank, nothing is written and SETUP.md's manual-edit
;     instructions still apply exactly as before.
;   - Offers to launch the game when done.

#define MyAppName "WaterparkSimTwitchExpansion"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "musicman0917"
#define MyAppURL "https://github.com/musicman0917/WaterparkSimTwitchExpansion"
#define MyAppExeName "WaterparkSimulator.exe"
#define PluginConfigFileName "com.musicman0917.waterparksimtwitchexpansion.cfg"

[Setup]
; A fixed, stable GUID identifies this installer's app entry across versions (Add/Remove
; Programs, upgrade detection) - do not change this once published, generate it once and keep it.
AppId={{6C9B7B7B-6D0B-4C7B-9B5C-6E6E7C7D8A11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={code:GetGameDir}
DisableDirPage=no
; No Start Menu group - there's nothing to shortcut to (this installs a plugin DLL, not its own
; program), so the group-selection page would just be a pointless extra click.
DisableProgramGroupPage=yes
; Steam library folders are normally writable by the current user even under Program Files (x86)
; - matches install.ps1, which never requests elevation either. If this ever turns out wrong for
; some setups, the fix is to drop this line (Inno then asks per-user vs. all-users at startup).
PrivilegesRequired=lowest
OutputDir=release
OutputBaseFilename=WaterparkSimTwitchExpansion-Setup-v{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
; No special "don't touch {app} itself" directive needed - this installer's [Files] entries only
; ever target the BepInEx\plugins\WaterparkSimTwitchExpansion subfolder they create, never the
; game's own files, and Inno's default uninstall only removes what it installed.

[Files]
Source: "WaterparkSimTwitchExpansion\bin\Release\net6.0\*"; DestDir: "{app}\BepInEx\plugins\WaterparkSimTwitchExpansion"; Flags: recursesubdirs ignoreversion; Excludes: "*.pdb"
Source: "SETUP.md"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Waterpark Simulator now"; Flags: postinstall skipifsilent nowait

[Code]
var
  TwitchPage: TInputQueryWizardPage;

{ ---------------------------------------------------------------------------
  Steam install auto-detection - best-effort only. Falls back to a plausible
  default guess if nothing is found; the destination page is never locked, so
  a wrong/missing guess just means the player browses to the right folder
  themselves, same as typing a -GameDir into install.ps1 by hand. }

function TryGameDirUnder(LibraryPath: String; var Found: String): Boolean;
var
  Candidate: String;
begin
  Candidate := LibraryPath + '\steamapps\common\WaterPark Simulator';
  Result := FileExists(Candidate + '\WaterparkSimulator.exe');
  if Result then
    Found := Candidate;
end;

{ Pascal Script has no PosEx (that's a Delphi StrUtils function, not part of Inno Setup's
  scripting engine) - this finds the first occurrence of SubStr at or after StartPos using only
  the built-in Pos/Copy, the same way PosEx would. }
function PosFrom(SubStr, S: String; StartPos: Integer): Integer;
var
  FoundInTail: Integer;
begin
  FoundInTail := Pos(SubStr, Copy(S, StartPos, MaxInt));
  if FoundInTail = 0 then
    Result := 0
  else
    Result := FoundInTail + StartPos - 1;
end;

{ Extracts every quoted "path"  "X:\some\library" pair out of libraryfolders.vdf - a minimal,
  good-enough parser for Valve's simple quoted-key-value format, not a full VDF implementation. }
function FindGameDirViaLibraryFolders(SteamPath: String; var Found: String): Boolean;
var
  VdfPath: String;
  Lines: TArrayOfString;
  I, FirstQuote, SecondQuote, ThirdQuote: Integer;
  Line, LibPath: String;
begin
  Result := False;
  VdfPath := SteamPath + '\steamapps\libraryfolders.vdf';
  if not FileExists(VdfPath) then
    Exit;
  if not LoadStringsFromFile(VdfPath, Lines) then
    Exit;

  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    { Looking for lines shaped like:   "path"		"D:\\SteamLibrary" }
    if Pos('"path"', Line) = 0 then
      Continue;

    FirstQuote := Pos('"path"', Line) + Length('"path"');
    SecondQuote := PosFrom('"', Line, FirstQuote);
    if SecondQuote = 0 then
      Continue;
    ThirdQuote := PosFrom('"', Line, SecondQuote + 1);
    if ThirdQuote = 0 then
      Continue;

    LibPath := Copy(Line, SecondQuote + 1, ThirdQuote - SecondQuote - 1);
    { VDF escapes backslashes as \\ - undo that. }
    StringChangeEx(LibPath, '\\', '\', True);

    if TryGameDirUnder(LibPath, Found) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function GetGameDir(Param: String): String;
var
  SteamPath, Found: String;
begin
  { HKCU\Software\Valve\Steam\SteamPath is the most consistently documented/reliable location
    (Steam always writes it for the current user, regardless of install elevation history) - try
    it first. Steam being a 32-bit app also writes an HKLM InstallPath value, physically under
    WOW6432Node on 64-bit Windows, which the plain (non-"64"/"32"-suffixed) path already resolves
    correctly via normal registry redirection - HKLM64's un-redirected native view is checked too
    only as a last resort, in case of some future 64-bit Steam registration. }
  if not RegQueryStringValue(HKCU, 'Software\Valve\Steam', 'SteamPath', SteamPath) then
    if not RegQueryStringValue(HKLM, 'SOFTWARE\Valve\Steam', 'InstallPath', SteamPath) then
      RegQueryStringValue(HKLM64, 'SOFTWARE\Valve\Steam', 'InstallPath', SteamPath);

  if SteamPath <> '' then
  begin
    StringChangeEx(SteamPath, '/', '\', True);

    if TryGameDirUnder(SteamPath, Found) then
    begin
      Result := Found;
      Exit;
    end;

    if FindGameDirViaLibraryFolders(SteamPath, Found) then
    begin
      Result := Found;
      Exit;
    end;
  end;

  { Nothing found - a plausible guess so the destination page isn't blank, still fully editable. }
  Result := 'C:\Program Files (x86)\Steam\steamapps\common\WaterPark Simulator';
end;

{ ---------------------------------------------------------------------------
  BepInEx prerequisite check - warns, doesn't block, since this installer
  deliberately never bundles/downloads BepInEx itself (see the header
  comment above for why). }

function BepInExLooksInstalled(GameDir: String): Boolean;
begin
  Result := FileExists(GameDir + '\BepInEx\core\BepInEx.Unity.IL2CPP.dll')
    and FileExists(GameDir + '\doorstop_config.ini');
end;

{ ---------------------------------------------------------------------------
  Twitch credentials page - optional, left blank means "configure later by
  hand", exactly like every path through SETUP.md already describes. }

procedure InitializeWizard;
begin
  TwitchPage := CreateInputQueryPage(wpSelectDir,
    'Connect your Twitch bot (optional)',
    'You can fill this in now, or skip it and edit the config file later.',
    'Leave any of these blank to configure them by hand afterward - see SETUP.md, bundled ' +
    'alongside the mod, for how to get an OAuth token. Nothing here is required to finish ' +
    'installing.');
  TwitchPage.Add('Twitch channel name (no leading #):', False);
  TwitchPage.Add('Bot account username:', False);
  TwitchPage.Add('Bot OAuth token (starts with oauth:):', True);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Response: Integer;
begin
  Result := True;

  if CurPageID = wpSelectDir then
  begin
    if not BepInExLooksInstalled(WizardDirValue) then
    begin
      Response := MsgBox(
        'BepInEx (the IL2CPP mod loader) doesn''t look installed in this folder yet - this mod ' +
        'won''t do anything without it.' + #13#10 + #13#10 +
        'Get the "BepInEx IL2CPP for Waterpark Simulator" pack first, from:' + #13#10 +
        'https://www.nexusmods.com/waterparksimulator/mods/62' + #13#10 + #13#10 +
        'Continue installing the mod files anyway? (Fine to do if you''re about to install ' +
        'BepInEx right after this, or already know it''s there under a different check.)',
        mbConfirmation, MB_YESNO);
      Result := (Response = IDYES);
    end;
  end;
end;

{ ---------------------------------------------------------------------------
  Write whatever Twitch fields were filled in straight into the plugin's
  config, once installed - SetIniString creates the file/section if it
  doesn't exist yet (it normally only appears after the game's first
  launch), same net effect as install.ps1's own config-seeding step. }

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigPath, Channel, BotUsername, OAuthToken: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  Channel := TwitchPage.Values[0];
  BotUsername := TwitchPage.Values[1];
  OAuthToken := TwitchPage.Values[2];

  if (Channel = '') and (BotUsername = '') and (OAuthToken = '') then
    Exit;

  ConfigPath := ExpandConstant('{app}') + '\BepInEx\config\{#PluginConfigFileName}';
  ForceDirectories(ExpandConstant('{app}') + '\BepInEx\config');

  if Channel <> '' then
    SetIniString('Twitch', 'ChannelName', Channel, ConfigPath);
  if BotUsername <> '' then
    SetIniString('Twitch', 'BotUsername', BotUsername, ConfigPath);
  if OAuthToken <> '' then
    SetIniString('Twitch', 'OAuthToken', OAuthToken, ConfigPath);
end;
