program DisposeSamples;

uses
  Forms,
  Main in 'Main.pas' {Mainform},
  DMUnt in 'DMUnt.pas' {DM: TDataModule},
  PubUnt in 'PubUnt.pas';

{$R *.res}

begin
  Application.Initialize;
  Application.CreateForm(TDM, DM);
  Application.CreateForm(TMainform, Mainform);
  Application.Run;
end.
