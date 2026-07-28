unit Unt_DM;

interface

uses
  SysUtils, Classes, DB, ZAbstractRODataset, ZAbstractDataset, ZDataset,
  ZAbstractConnection, ZConnection,Dialogs, MidasLib, DBClient, Provider,
  inifiles;

type
  TConfig=record
    IP:string;
    Port:Integer;
    SRM_Nodeid:string;
    Export_Time:string;
    UserName:string;
    Password:string;
    DataBaseName:string;
  end;
  TDM = class(TDataModule)
    ZConnMysql: TZConnection;
    ZQuery: TZQuery;
    dspQry: TDataSetProvider;
    cdsQry: TClientDataSet;
    procedure DataModuleCreate(Sender: TObject);
  private
    { Private declarations }

  public
    { Public declarations }
    procedure ReadConfig;
    procedure ConnectToMysql;
    procedure MySql_Ping;
    procedure Get_Dispose_Sample;
  end;

var
  DM: TDM;
  Config:TConfig;

implementation

{$R *.dfm}

procedure TDM.DataModuleCreate(Sender: TObject);
begin
  ReadConfig;
end;

procedure TDM.ReadConfig;
var
  FileName:string;
  IniFile:TIniFile;
begin
  FileName:=GetCurrentDir()+'\SRM_ExportDisposeSample.ini';
  IniFile:=TIniFile.Create(FileName);
  Config.IP:=IniFile.ReadString('system','IP','127.0.0.1');
  Config.Port:=IniFile.ReadInteger('system','Port',3306);
  Config.SRM_Nodeid:=IniFile.ReadString('system','SRM_Nodeid','');
  Config.Export_Time:=IniFile.ReadString('system','Export_Time','03:30');
  //20260624
  Config.UserName:=IniFile.ReadString('system','UserName','root');
  Config.Password:=IniFile.ReadString('system','Password','root');
  Config.DataBaseName:=IniFile.ReadString('system','DataBaseName','flexlab');
  IniFile.Free;
end;

procedure TDM.ConnectToMysql;
begin
  try
    ZConnMysql.Protocol:='mysql';
    ZConnMysql.Port:=Config.Port;
    ZConnMysql.HostName:=config.IP;
    ZConnMysql.User:=Config.UserName;
    ZConnMysql.Password:=Config.Password;
    ZConnMysql.Database:=Config.DataBaseName;
    ZConnMysql.Connect;
  except
    on e:Exception do
    begin
      ShowMessage(E.Message);
    end;
  end;
end;

procedure TDM.Get_Dispose_Sample;
var
  t_location:string;
begin
  MySql_Ping;
  cdsQry.Filtered:=False;
  cdsQry.Close;
  try
    t_location:='&3-'+Config.SRM_Nodeid+'-%'; //在线IOM 正常
    cdsQry.CommandText:=' Select sample_id collate latin1_swedish_ci as barcode, t_location collate latin1_swedish_ci as location, update_time '
                       +' from t_sample '
                       +' where update_time>= DATE_FORMAT(curdate()-1,''%Y%m%d'') and update_time<DATE_FORMAT(curdate(),''%Y%m%d'')'
                       +' and t_location like '+''''+t_location+'''';
    cdsQry.Open;
  except
  end;
end;

{Function TDM.Get_Lane_Sample(laneno: string):integer;
begin
  Result:=0;
  cdsQry.Filtered:=False;
  cdsQry.Filter:='location like'+''''+'&1-01-%-00-'+laneno+'-%'+'''';
  cdsQry.Filtered:=True;
  Result:=cdsQry.RecordCount;
  cdsQry.Filtered:=False;
end;}

procedure TDM.MySql_Ping;
begin
  if Not ZConnMysql.Ping then
  begin
    ZConnMysql.Disconnect;
    ZConnMysql.Connect;
  end;
end;

end.
