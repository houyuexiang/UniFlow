program SRM_ExportDisposeSample;

uses
  Forms,
  Unt_Main in 'Unt_Main.pas' {MainFrm},
  Unt_DM in 'Unt_DM.pas' {DM: TDataModule},
  Unt_PubFun in 'Unt_PubFun.pas';

{$R *.res}

begin
  Application.Initialize;
  Application.CreateForm(TDM, DM);
  Application.CreateForm(TMainFrm, MainFrm);
  Application.Run;
end.
