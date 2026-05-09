#ifndef AppVersion
  #define AppVersion "0.0.1"
#endif

[Setup]
AppId={{8F2E4A1B-3C5D-4E6F-A7B8-9C0D1E2F3A4B}
AppName=Hunt Monitor
AppVersion={#AppVersion}
AppVerName=Hunt Monitor {#AppVersion}
AppPublisher=Hunt Monitor
AppPublisherURL=https://tibiasquare.com
DefaultDirName={localappdata}\Programs\Hunt Monitor
DefaultGroupName=Hunt Monitor
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
OutputBaseFilename=TibiaSquareHuntMonitor-Setup
SetupIconFile=..\src\TibiaSquare.HuntMonitor\Assets\tray-icon.ico
Compression=lzma2/fast
SolidCompression=no
WizardStyle=modern
DisableWelcomePage=no
InfoBeforeFile=info-obs.rtf
UninstallDisplayIcon={app}\TibiaSquare.HuntMonitor.exe
VersionInfoVersion={#AppVersion}.0
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Please read this page
WelcomeLabel2=
ClickNext=
InfoBeforeLabel=Third-party software
InfoBeforeClickLabel=
SelectDirLabel3=
FinishedHeadingLabel=Setup Complete
FinishedLabel=Hunt Monitor has been installed.%n%nKeep your Hunt Analyser panel open and visible in Tibia for best results.

[Files]
Source: "..\..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "flag-en.bmp"; Flags: dontcopy
Source: "flag-pl.bmp"; Flags: dontcopy
Source: "flag-br.bmp"; Flags: dontcopy

[Icons]
Name: "{userprograms}\Hunt Monitor"; Filename: "{app}\TibiaSquare.HuntMonitor.exe"; Check: StartMenuSelected
Name: "{userdesktop}\Hunt Monitor"; Filename: "{app}\TibiaSquare.HuntMonitor.exe"; Check: DesktopSelected

[Run]
Filename: "{app}\TibiaSquare.HuntMonitor.exe"; Description: "Launch Hunt Monitor"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TibiaSquareHuntMonitor"; ValueData: """{app}\TibiaSquare.HuntMonitor.exe"""; Flags: uninsdeletevalue; Check: StartupSelected

[UninstallDelete]
Type: filesandordirs; Name: "{app}\obs-portable"
Type: filesandordirs; Name: "{app}"

[Code]
const
  DarkBg     = $1A1A1A;
  DarkPanel  = $262626;
  DarkInput  = $333333;
  GoldColor  = $24BFFB;
  WhiteText  = $F0F0F0;
  MutedText  = $999999;

  LANG_EN = 0;
  LANG_PL = 1;
  LANG_PT = 2;

var
  ShortcutList: TNewCheckListBox;
  CurrentLang: Integer;
  // Welcome page labels
  LblHeader1, LblBody1: TNewStaticText;
  LblHeader2, LblBody2, LblBold: TNewStaticText;
  LblHeader3, LblBody3: TNewStaticText;
  // Flag buttons
  FlagEn, FlagPl, FlagBr: TBitmapImage;

function GetUserDefaultUILanguage: Integer;
  external 'GetUserDefaultUILanguage@kernel32.dll stdcall';

function InitializeSetup: Boolean;
var
  ResultCode: Integer;
begin
  // Kill running instances before install/upgrade
  Exec('taskkill', '/F /IM TibiaSquare.HuntMonitor.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill', '/F /IM obs64.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

function StartMenuSelected: Boolean;
begin
  Result := ShortcutList.Checked[0];
end;

function DesktopSelected: Boolean;
begin
  Result := ShortcutList.Checked[1];
end;

function StartupSelected: Boolean;
begin
  Result := ShortcutList.Checked[2];
end;

// --- Translation strings ---

function T_Title(L: Integer): String;
begin
  case L of
    LANG_PL: Result := 'Przeczytaj t'#281' stron'#281;
    LANG_PT: Result := 'Por favor, leia esta p'#225'gina';
  else Result := 'Please read this page';
  end;
end;

function T_H1(L: Integer): String;
begin
  case L of
    LANG_PL: Result := 'Zostaw w'#322#261'czon'#261;
    LANG_PT: Result := 'Mantenha ligado';
  else Result := 'Keep it on';
  end;
end;

function T_B1(L: Integer): String;
begin
  case L of
    LANG_PL: Result := 'Ta aplikacja dzia'#322'a w tle ca'#322'y czas. Jest bardzo lekka '#8212' w trybie u'#347'pienia korzysta z trybu energooszcz'#281'dno'#347'ci Windows i jedynie sprawdza co kilka sekund, czy Tibia jest uruchomiona.';
    LANG_PT: Result := 'Este app foi feito para ficar sempre ligado. '#201' muito leve em repouso '#8212' usa o modo de efici'#234'ncia energ'#233'tica do Windows e s'#243' verifica a cada poucos segundos se o Tibia est'#225' aberto.';
  else Result := 'This app is designed to be always on. It''s very light when idle '#8212' Hunt Monitor uses Windows energy efficiency mode for idle state and all it does then is check whether Tibia is opened every few seconds.';
  end;
end;

function T_H2(L: Integer): String;
begin
  case L of
    LANG_PL: Result := 'Jak to dzia'#322'a';
    LANG_PT: Result := 'Como funciona';
  else Result := 'How it works';
  end;
end;

function T_B2(L: Integer): String;
begin
  case L of
    LANG_PL: Result := 'Gdy Tibia zostanie wykryta, Hunt Monitor si'#281' uruchamia, szuka Hunt Analysera i odczytuje jego warto'#347'ci. Rozpoznaje, '#380'e polujesz, rejestruje sesje i automatycznie przesy'#322'a je po zamkni'#281'ciu Tibii.';
    LANG_PT: Result := 'Quando o Tibia '#233' detectado, o Hunt Monitor inicia, procura o Hunt Analyser e l'#234' seus valores. Ele determina que voc'#234' est'#225' ca'#231'ando, registra suas sess'#245'es e as envia automaticamente ap'#243's fechar o Tibia.';
  else Result := 'Once Tibia is detected, Hunt Monitor actually starts, looks for your Hunt Analyser and once found reads its values. It then determines you went hunting, records your hunting sessions and automatically uploads them after you close Tibia.';
  end;
end;

function T_Bold(L: Integer): String;
begin
  case L of
    LANG_PL: Result := 'Upewnij si'#281', '#380'e Hunt Analyser jest otwarty ze wszystkimi warto'#347'ciami do Loot.'#13#10'Nie przechwytujemy loot.';
    LANG_PT: Result := 'Certifique-se de que o Hunt Analyser esteja aberto com todos os valores at'#233' Loot vis'#237'veis.'#13#10'N'#227'o capturamos loot.';
  else Result := 'Make sure to have the Hunt Analyser open with all values until Loot visible.'#13#10'We don''t capture loot.';
  end;
end;

function T_H3(L: Integer): String;
begin
  case L of
    LANG_PL: Result := 'Co jest w zestawie';
    LANG_PT: Result := 'O que est'#225' incluso';
  else Result := 'What''s included';
  end;
end;

function T_B3(L: Integer): String;
begin
  case L of
    LANG_PL: Result := 'Aplikacja zawiera OBS Studio do przechwytywania okna gry. Nie musisz nic instalowa'#263' ani konfigurowa'#263' '#8212' wszystko dzia'#322'a automatycznie. Zobaczysz ikon'#281' OBS w zasobniku systemowym gdy Tibia jest uruchomiona. Tak ma by'#263'.';
    LANG_PT: Result := 'Este app inclui o OBS Studio para capturar a janela do jogo. Voc'#234' n'#227'o precisa instalar ou configurar nada '#8212' tudo '#233' feito automaticamente. Voc'#234' ver'#225' um '#237'cone do OBS na bandeja do sistema quando o Tibia estiver rodando. Isso '#233' esperado.';
  else Result := 'This app bundles OBS Studio to capture your game window. You don''t need to install or configure anything '#8212' it''s all handled automatically. You''ll see an OBS icon in your system tray when Tibia is running. This is expected.';
  end;
end;

// --- Apply translations ---

procedure SetLanguage(Lang: Integer);
begin
  CurrentLang := Lang;
  WizardForm.WelcomeLabel1.Caption := T_Title(Lang);
  LblHeader1.Caption := T_H1(Lang);
  LblBody1.Caption := T_B1(Lang);
  LblHeader2.Caption := T_H2(Lang);
  LblBody2.Caption := T_B2(Lang);
  LblBold.Caption := T_Bold(Lang);
  LblHeader3.Caption := T_H3(Lang);
  LblBody3.Caption := T_B3(Lang);
end;

procedure OnFlagEnClick(Sender: TObject); begin SetLanguage(LANG_EN); end;
procedure OnFlagPlClick(Sender: TObject); begin SetLanguage(LANG_PL); end;
procedure OnFlagBrClick(Sender: TObject); begin SetLanguage(LANG_PT); end;

// --- Dark theme ---

procedure ApplyDarkTheme;
begin
  WizardForm.Color := DarkBg;

  WizardForm.MainPanel.Color := DarkPanel;
  WizardForm.PageNameLabel.Font.Color := GoldColor;
  WizardForm.PageNameLabel.Font.Size := 11;
  WizardForm.PageDescriptionLabel.Font.Color := MutedText;

  WizardForm.WelcomePage.Color := DarkBg;
  WizardForm.FinishedPage.Color := DarkBg;
  WizardForm.InnerPage.Color := DarkBg;

  WizardForm.WelcomeLabel1.Font.Color := GoldColor;
  WizardForm.WelcomeLabel1.Font.Size := 18;
  WizardForm.WelcomeLabel1.Font.Name := 'Segoe UI';
  WizardForm.WelcomeLabel2.Font.Color := WhiteText;

  WizardForm.FinishedHeadingLabel.Font.Color := GoldColor;
  WizardForm.FinishedHeadingLabel.Font.Size := 18;
  WizardForm.FinishedHeadingLabel.Font.Name := 'Segoe UI';
  WizardForm.FinishedLabel.Font.Color := WhiteText;
  WizardForm.FinishedLabel.Font.Size := 10;
  WizardForm.FinishedLabel.Font.Name := 'Segoe UI';

  WizardForm.InfoBeforeMemo.Color := DarkInput;
  WizardForm.InfoBeforeMemo.Font.Color := WhiteText;
  WizardForm.InfoBeforeMemo.Font.Size := 10;
  WizardForm.InfoBeforeMemo.Font.Name := 'Segoe UI';
  WizardForm.InfoBeforeClickLabel.Font.Color := MutedText;

  WizardForm.SelectDirLabel.Font.Color := WhiteText;
  WizardForm.SelectDirBrowseLabel.Font.Color := WhiteText;
  WizardForm.DirEdit.Color := DarkInput;
  WizardForm.DirEdit.Font.Color := WhiteText;
  WizardForm.DiskSpaceLabel.Font.Color := WhiteText;

  WizardForm.SelectTasksLabel.Font.Color := WhiteText;
  WizardForm.TasksList.Color := DarkBg;
  WizardForm.TasksList.Font.Color := WhiteText;

  WizardForm.ReadyLabel.Font.Color := WhiteText;
  WizardForm.ReadyMemo.Color := DarkInput;
  WizardForm.ReadyMemo.Font.Color := WhiteText;

  WizardForm.StatusLabel.Font.Color := WhiteText;
  WizardForm.FilenameLabel.Font.Color := MutedText;

  WizardForm.Bevel.Visible := False;
  WizardForm.Bevel1.Visible := False;

  WizardForm.RunList.Color := DarkBg;
  WizardForm.RunList.Font.Color := WhiteText;
end;

// --- Build welcome page ---

procedure BuildWelcomePage;
var
  Y, FlagTop, FlagRight: Integer;
begin
  WizardForm.WelcomeLabel1.Left := ScaleX(20);
  WizardForm.WelcomeLabel1.Width := WizardForm.InnerPage.Width - ScaleX(120);
  WizardForm.WelcomeLabel2.Visible := False;

  // Flag buttons in top-right corner
  FlagTop := WizardForm.WelcomeLabel1.Top + ScaleY(6);
  FlagRight := WizardForm.WelcomeLabel1.Left + WizardForm.WelcomeLabel1.Width + ScaleX(80);

  ExtractTemporaryFile('flag-br.bmp');
  FlagBr := TBitmapImage.Create(WizardForm);
  FlagBr.Parent := WizardForm.WelcomePage;
  FlagBr.Bitmap.LoadFromFile(ExpandConstant('{tmp}\flag-br.bmp'));
  FlagBr.Stretch := True;
  FlagBr.Width := ScaleX(20);
  FlagBr.Height := ScaleY(15);
  FlagBr.Left := FlagRight - FlagBr.Width;
  FlagBr.Top := FlagTop;
  FlagBr.Cursor := crHand;
  FlagBr.OnClick := @OnFlagBrClick;

  ExtractTemporaryFile('flag-pl.bmp');
  FlagPl := TBitmapImage.Create(WizardForm);
  FlagPl.Parent := WizardForm.WelcomePage;
  FlagPl.Bitmap.LoadFromFile(ExpandConstant('{tmp}\flag-pl.bmp'));
  FlagPl.Stretch := True;
  FlagPl.Width := ScaleX(20);
  FlagPl.Height := ScaleY(15);
  FlagPl.Left := FlagBr.Left - FlagPl.Width - ScaleX(6);
  FlagPl.Top := FlagTop;
  FlagPl.Cursor := crHand;
  FlagPl.OnClick := @OnFlagPlClick;

  ExtractTemporaryFile('flag-en.bmp');
  FlagEn := TBitmapImage.Create(WizardForm);
  FlagEn.Parent := WizardForm.WelcomePage;
  FlagEn.Bitmap.LoadFromFile(ExpandConstant('{tmp}\flag-en.bmp'));
  FlagEn.Stretch := True;
  FlagEn.Width := ScaleX(20);
  FlagEn.Height := ScaleY(15);
  FlagEn.Left := FlagPl.Left - FlagEn.Width - ScaleX(6);
  FlagEn.Top := FlagTop;
  FlagEn.Cursor := crHand;
  FlagEn.OnClick := @OnFlagEnClick;

  Y := WizardForm.WelcomeLabel1.Top + WizardForm.WelcomeLabel1.Height + ScaleY(4);

  // Section 1
  LblHeader1 := TNewStaticText.Create(WizardForm);
  LblHeader1.Parent := WizardForm.WelcomePage;
  LblHeader1.Left := ScaleX(20);
  LblHeader1.Top := Y;
  LblHeader1.Width := WizardForm.InnerPage.Width - ScaleX(40);
  LblHeader1.AutoSize := True;
  LblHeader1.Font.Color := WhiteText;
  LblHeader1.Font.Size := 11;
  LblHeader1.Font.Name := 'Segoe UI';
  LblHeader1.Font.Style := [fsBold];
  Y := Y + ScaleY(20);

  LblBody1 := TNewStaticText.Create(WizardForm);
  LblBody1.Parent := WizardForm.WelcomePage;
  LblBody1.Left := ScaleX(20);
  LblBody1.Top := Y;
  LblBody1.Width := WizardForm.InnerPage.Width - ScaleX(40);
  LblBody1.WordWrap := True;
  LblBody1.AutoSize := False;
  LblBody1.Height := ScaleY(52);
  LblBody1.Font.Color := WhiteText;
  LblBody1.Font.Size := 10;
  LblBody1.Font.Name := 'Segoe UI';
  Y := Y + ScaleY(62);

  // Section 2
  LblHeader2 := TNewStaticText.Create(WizardForm);
  LblHeader2.Parent := WizardForm.WelcomePage;
  LblHeader2.Left := ScaleX(20);
  LblHeader2.Top := Y;
  LblHeader2.Width := WizardForm.InnerPage.Width - ScaleX(40);
  LblHeader2.AutoSize := True;
  LblHeader2.Font.Color := WhiteText;
  LblHeader2.Font.Size := 11;
  LblHeader2.Font.Name := 'Segoe UI';
  LblHeader2.Font.Style := [fsBold];
  Y := Y + ScaleY(20);

  LblBody2 := TNewStaticText.Create(WizardForm);
  LblBody2.Parent := WizardForm.WelcomePage;
  LblBody2.Left := ScaleX(20);
  LblBody2.Top := Y;
  LblBody2.Width := WizardForm.InnerPage.Width - ScaleX(40);
  LblBody2.WordWrap := True;
  LblBody2.AutoSize := False;
  LblBody2.Height := ScaleY(50);
  LblBody2.Font.Color := WhiteText;
  LblBody2.Font.Size := 10;
  LblBody2.Font.Name := 'Segoe UI';
  Y := Y + ScaleY(52);

  LblBold := TNewStaticText.Create(WizardForm);
  LblBold.Parent := WizardForm.WelcomePage;
  LblBold.Left := ScaleX(20);
  LblBold.Top := Y;
  LblBold.Width := WizardForm.InnerPage.Width - ScaleX(40);
  LblBold.WordWrap := True;
  LblBold.AutoSize := False;
  LblBold.Height := ScaleY(36);
  LblBold.Font.Color := WhiteText;
  LblBold.Font.Size := 10;
  LblBold.Font.Name := 'Segoe UI';
  LblBold.Font.Style := [fsBold];
  Y := Y + ScaleY(46);

  // Section 3
  LblHeader3 := TNewStaticText.Create(WizardForm);
  LblHeader3.Parent := WizardForm.WelcomePage;
  LblHeader3.Left := ScaleX(20);
  LblHeader3.Top := Y;
  LblHeader3.Width := WizardForm.InnerPage.Width - ScaleX(40);
  LblHeader3.AutoSize := True;
  LblHeader3.Font.Color := WhiteText;
  LblHeader3.Font.Size := 11;
  LblHeader3.Font.Name := 'Segoe UI';
  LblHeader3.Font.Style := [fsBold];
  Y := Y + ScaleY(20);

  LblBody3 := TNewStaticText.Create(WizardForm);
  LblBody3.Parent := WizardForm.WelcomePage;
  LblBody3.Left := ScaleX(20);
  LblBody3.Top := Y;
  LblBody3.Width := WizardForm.InnerPage.Width - ScaleX(40);
  LblBody3.WordWrap := True;
  LblBody3.AutoSize := False;
  LblBody3.Height := ScaleY(50);
  LblBody3.Font.Color := WhiteText;
  LblBody3.Font.Size := 10;
  LblBody3.Font.Name := 'Segoe UI';
end;

// --- Detect system language ---

function DetectLanguage: Integer;
var
  UILang: Integer;
begin
  UILang := GetUserDefaultUILanguage;
  // Polish: $0415
  if UILang = $0415 then
    Result := LANG_PL
  // Portuguese (Brazil): $0416, Portuguese (Portugal): $0816
  else if (UILang = $0416) or (UILang = $0816) then
    Result := LANG_PT
  else
    Result := LANG_EN;
end;

// --- Main init ---

procedure InitializeWizard;
begin
  ApplyDarkTheme;

  WizardForm.WizardBitmapImage.Visible := False;
  WizardForm.WizardBitmapImage2.Visible := False;
  WizardForm.WizardSmallBitmapImage.Visible := False;

  BuildWelcomePage;

  // Detect system language and apply
  SetLanguage(DetectLanguage);

  // Finished page
  WizardForm.FinishedHeadingLabel.Left := ScaleX(20);
  WizardForm.FinishedHeadingLabel.Width := WizardForm.InnerPage.Width - ScaleX(40);
  WizardForm.FinishedLabel.Left := ScaleX(20);
  WizardForm.FinishedLabel.Width := WizardForm.InnerPage.Width - ScaleX(40);

  // White border line above footer
  with TPanel.Create(WizardForm) do
  begin
    Parent := WizardForm;
    Left := 0;
    Top := WizardForm.ClientHeight - ScaleY(48);
    Width := WizardForm.ClientWidth;
    Height := ScaleY(1);
    BevelOuter := bvNone;
    Color := WhiteText;
  end;

  // Shortcut checkboxes on dir page
  ShortcutList := TNewCheckListBox.Create(WizardForm);
  ShortcutList.Parent := WizardForm.SelectDirPage;
  ShortcutList.Left := WizardForm.DirEdit.Left;
  ShortcutList.Top := WizardForm.DirEdit.Top + WizardForm.DirEdit.Height + ScaleY(16);
  ShortcutList.Width := WizardForm.DirEdit.Width;
  ShortcutList.Height := ScaleY(68);
  ShortcutList.Flat := True;
  ShortcutList.BorderStyle := bsNone;
  ShortcutList.Color := DarkBg;
  ShortcutList.Font.Color := WhiteText;
  ShortcutList.Font.Size := 9;
  ShortcutList.Font.Name := 'Segoe UI';
  ShortcutList.AddCheckBox('Create a Start Menu shortcut', '', 0, True, True, False, False, nil);
  ShortcutList.AddCheckBox('Create a Desktop shortcut', '', 0, False, True, False, False, nil);
  ShortcutList.AddCheckBox('Start with Windows', '', 0, True, True, False, False, nil);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpInfoBefore then
  begin
    WizardForm.InfoBeforeMemo.RtfText :=
      '{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}{\colortbl;\red240\green240\blue240;}' +
      '\cf1\f0\fs20' +
      ' This software bundles OBS Studio, which is licensed under the GNU General Public License v2.0 (GPL-2.0).\par' +
      '\par' +
      'OBS Studio source code: https://github.com/obsproject/obs-studio\par' +
      '\par' +
      'By continuing the installation, you acknowledge the inclusion of OBS Studio and its license terms.\par' +
      '}';
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = wpSelectTasks) or (PageID = wpReady);
end;

function InitializeUninstall: Boolean;
var
  ResultCode: Integer;
begin
  // Kill the app and OBS before uninstalling
  Exec('taskkill', '/F /IM TibiaSquare.HuntMonitor.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill', '/F /IM obs64.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;
