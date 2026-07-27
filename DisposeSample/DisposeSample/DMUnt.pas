unit DMUnt;

interface

uses
  SysUtils, Classes, DBClient, Provider, DB, ZAbstractRODataset,Dialogs,IniFiles,
  ZAbstractDataset, ZDataset, ZAbstractConnection, ZConnection,MidasLib,DateUtils,
  ZStoredProcedure;

type
  TDM = class(TDataModule)
    cdsRunDay: TClientDataSet;
    cdsDisTimeRange: TClientDataSet;
    cdsDisTimeRangestartTime: TStringField;
    cdsDisTimeRangeendTime: TStringField;
    cdsDisTimeRangethreshold: TStringField;
    ZQryTem: TZQuery;
    cdsRunDayday: TStringField;
    cdsSample: TClientDataSet;
    StringField7: TStringField;
    StringField8: TStringField;
    StringField9: TStringField;
    StringField10: TStringField;
    StringField11: TStringField;
    StringField12: TStringField;
    ZQrySql: TZQuery;
    ZConnMysql: TZConnection;
    ZQuery: TZQuery;
    cdsSamplestype: TStringField;
    cdsErrorCode: TClientDataSet;
    cdsErrorCodeErrorcode: TStringField;
    procedure DataModuleCreate(Sender: TObject);
  private
    { Private declarations }

    procedure Connect_To_Mysql;
  public
    { Public declarations }
    procedure MySql_Ping;
    procedure IniRunDay;
    procedure IniDisposeTimeRange;
    function  Decode_SRM_Status(var aStatus:string; aMsg:string):Boolean;
    procedure IniSrmErrorcode;
    function  Check_Work_Time(var athreshold:Integer):Boolean;
    Function  Check_SRM_SampleCount:Integer;

    function  Get_Dispose_sample(icmdtype:Integer):Integer;
    procedure Get_Dispose_Data_Set;
    function  Insert_Dispose_Record(iDispcount:Integer):Integer;

    function  check_t_Sample_Dispose_Status(aSam:string):Boolean;
    procedure Update_Dispose_Sample_Status(aSam,aField:string);
    function  Get_Sample_checkcount(aSam:string):Integer;
    function  select_one_send_record(var abarcode,alocation,stype,res_1:string):Integer;
    procedure Delete_UnSend_Record;
    procedure Delete_History_dispose_Record;
  end;

var
  DM: TDM;
  MutHandle:THandle;
implementation

uses
  PubUnt,Main;

{$R *.dfm}

procedure TDM.DataModuleCreate(Sender: TObject);
begin
  ReadConfig;
  Connect_To_Mysql;
  cdsSample.CreateDataSet;
  cdsRunDay.CreateDataSet;
  cdsDisTimeRange.CreateDataSet;
  cdsErrorCode.CreateDataSet;
  IniRunDay;
  IniDisposeTimeRange;
  IniSRMErrorcode;
end;

function TDM.Check_SRM_SampleCount: Integer;
var
  SqlTxt,t_location:string;
begin
  Result:=0;
  //MySql_Ping;//20210206
  t_location:='&1-'+FConfig.SRM_NodeID+'-%';
  SqlTxt:=' Select count(*) as Count from t_sample where t_status=''C'''+' and t_location like '+''''+t_location+'''';
  {try
    cdsQry.Close;
    cdsQry.CommandText:=SqlTxt;
    cdsQry.Open;
    Result:=cdsQry.FieldByName('count').Value;
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-Check_SRM_SampleCount:'+E.Message);
    end;
  end;}
  try
    ZQuery.Close;
    ZQuery.SQL.Text:=SqlTxt;
    ZQuery.Open;
    Result:=ZQuery.FieldByName('count').Value;
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-Check_SRM_SampleCount:'+E.Message);
    end;
  end;
end;

function TDM.Get_Dispose_sample(icmdtype:Integer): Integer;
var
  iCount:Integer;
  SqlTxt:string;
begin
  if icmdtype=0 then
    SqlTxt:='Select * from '+FConfig.command_name+' limit '+IntToStr(FConfig.max_onetime_select_discard_count)
  else
    SqlTxt:='Call '+FConfig.command_name;
  try
    ZQrySql.Close;
    ZQrySql.SQL.Text:=SqlTxt;
    ZQrySql.Open;
    iCount:=ZQrySql.RecordCount;
    if iCount>0 then
      Get_Dispose_Data_Set;
    if icmdtype=1 then
      ZQrySql.Close;
    Result:=iCount;
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-Get_Dispose_sample:'+E.Message);
    end;
  end;
end;

procedure TDM.Get_Dispose_Data_Set;
begin
  try
    cdsSample.EmptyDataSet;
    ZQrySql.First;
    while not ZQrySql.Eof do
    begin
      cdsSample.Edit;
      cdsSample.Append;
      cdsSample.FieldByName('barcode').Value:=ZQrySql.FieldByName('barcode').AsString;
      cdsSample.FieldByName('patient').Value:=ZQrySql.FieldByName('patient').AsString;
      cdsSample.FieldByName('stype').Value:=ZQrySql.FieldByName('stype').AsString;
      cdsSample.FieldByName('location').Value:=ZQrySql.FieldByName('location').Value;
      cdsSample.FieldByName('update_time').Value:=ZQrySql.FieldByName('update_time').AsString;
      cdsSample.FieldByName('res_1').Value:=ZQrySql.FieldByName('res_1').AsString;
      cdsSample.FieldByName('res_2').Value:=ZQrySql.FieldByName('res_2').AsString;
      cdsSample.Post;
      ZQrySql.Next;
    end;
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-Storedproc_Get_DataSet:'+E.Message);
    end;
  end;
end;

function TDM.Insert_Dispose_Record(iDispcount: Integer): Integer;
var
  iExistsCount,iInsertCount:Integer;
  barcode,location,rack,SqlTxt:string;
begin
  try
    iExistsCount:=0;
    iInsertCount:=0;
    cdsSample.First;
    while Not cdsSample.Eof do
    begin
      barcode:=cdsSample.FieldByName('barcode').AsString;
      try //check dispose sample exists or not
        ZQrySql.Close;
        ZQrySql.SQL.Text:='select barcode from sam_dispose_status where barcode='+''''+barcode+'''';
        ZQrySql.Open;
      except
        on E:Exception do          
        begin
          Mainform.ShowMsgInfo('[>>Error]-Check_Sample:'+E.Message);
        end;
      end;
      if ZQrySql.RecordCount>0 then Inc(iExistsCount)
      else begin
        if iInsertCount< iDispcount then
        begin
          Inc(iInsertCount);
          location:=cdsSample.FieldByName('location').AsString;
          rack:=Get_Location_RackNo(location);
          SqlTxt:='Insert into sam_dispose_status(barcode,patient,stype,location,update_time,res_1,res_2,rack,send,disposed,checkcount) '
                          +'values('
                          +''''+barcode+''''+','
                          +''''+cdsSample.FieldByName('patient').AsString+''''+','
                          +''''+cdsSample.FieldByName('stype').AsString+''''+','
                          +''''+location+''''+','
                          +''''+cdsSample.FieldByName('update_time').AsString+''''+','
                          +''''+cdsSample.FieldByName('res_1').AsString+''''+','
                          +''''+cdsSample.FieldByName('res_2').AsString+''''+','
                          +''''+rack+''''+','
                          +IntToStr(0)+','+IntToStr(0)+','+IntToStr(0)
                          +')';
          try
            ZQrySql.Close;
            ZQrySql.SQL.Text:=SqlTxt;
            ZQrySql.ExecSQL;
          except
            on E:Exception do
            begin
              Mainform.ShowMsgInfo('[>>Error]-Insert_Dispose:'+E.Message);
            end;
          end;
        end;
      end;
      cdsSample.Next;
    end;
    Result:=iInsertCount;
    Mainform.ShowMsgInfo('Insert dispose table counts:'+IntToStr(iInsertCount)+' Exists counts:'+IntToStr(iExistsCount));
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-View_Insert_Dispose_Record:'+E.Message);
    end;
  end;
end;

function TDM.select_one_send_record(var abarcode, alocation, stype, res_1: string): Integer;
var
  iCount:Integer;
begin
  try
    ZQryTem.Close;
    ZQryTem.SQL.Text:='select barcode,location,stype,res_1 from sam_dispose_status where send=0 order by ID limit 1';
    ZQryTem.Open;
    abarcode:=ZQryTem.FieldByName('barcode').AsString;
    alocation:=ZQryTem.FieldByName('location').AsString;
    stype:=ZQryTem.FieldByName('stype').AsString;
    res_1:=ZQryTem.FieldByName('res_1').AsString;
    iCount:=ZQryTem.RecordCount;
    if iCount>0 then
      Mainform.ShowMsgInfo('Ready dispose sample:Barcde='+abarcode);
    Result:=iCount;
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-select_one_send_record:'+E.Message);
    end;
  end;
end;

function TDM.check_t_Sample_Dispose_Status(aSam: string): Boolean;
var
  location:string;
begin
  Result:=False;
  location:='&3-'+FConfig.SRM_NodeID+'-%';
  try
    ZQryTem.Close;
    ZQryTem.SQL.Text:='Select sample_id collate latin1_swedish_ci as barcode'
                    +' from t_sample'
                    +' where sample_id='+''''+aSam+''''
                    +' and t_location like'+''''+location+'''';
    ZQryTem.Open;
    if ZQryTem.RecordCount>0 then
      Result:=True;
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-check_t_Sample_Dispose_Status:'+E.Message);
    end;
  end;
end;

function TDM.Check_Work_Time(var athreshold: Integer): Boolean;
var
  aCount:Integer;
  NowDay,starTime,endTime:string;
begin
  Result:=False;
  athreshold:=0;
  try
    NowDay:= IntToStr(DayOfTheWeek(Now));//1:Monday;7:Sunday
    if cdsRunDay.Locate('day',NowDay,[]) then
    begin
      cdsDisTimeRange.First;
      while not cdsDisTimeRange.Eof do
      begin
        starTime:=cdsDisTimeRange.fieldByName('startTime').AsString;
        endTime :=cdsDisTimeRange.fieldByName('endTime').AsString;
        aCount:=cdsDisTimeRange.fieldByName('threshold').AsInteger;
        if Check_Dispose_Time_Range(starTime,endTime) then
        begin
          athreshold:=aCount;
          Result:=True;
          Mainform.ShowMsgInfo('Work time range:'+starTime+'-'+endTime+' '+IntToStr(athreshold));
          Break;
        end;
        cdsDisTimeRange.Next;
      end;
    end;
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-Check_Work_Time:'+E.Message);
    end;
  end;
end;

function TDM.Get_Sample_checkcount(aSam: string): Integer;
begin
  try
    ZQryTem.Close;
    ZQryTem.SQL.Text:='select checkcount from sam_dispose_status where barcode='+''''+aSam+'''';
    ZQryTem.Open;
    Result:=ZQryTem.FieldByName('checkcount').AsInteger;
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-Get_Sample_checkcount:'+E.Message);
    end;
  end;
end;

procedure TDM.Update_Dispose_Sample_Status(aSam, aField: string);
var
  SqlTxt:string;
  NwTime:string;
begin
  try
    NwTime:=FormatDateTime('YYYYMMDDHHMMSS',NOW);
    SqlTxt:='Update sam_dispose_status ';
    if aField='send' then SqlTxt:=SqlTxt+'set send=1,send_time='+''''+NwTime+''''
    else if aField='disposed' then SqlTxt:=SqlTxt+'set disposed=1, disposed_time='+''''+NwTime+''''
    else if aField='checkcount' then SqlTxt:=SqlTxt+'set checkcount=checkcount+1, check_time='+''''+NwTime+'''';
    SqlTxt:=SqlTxt+'where barcode='+''''+aSam+'''';
    ZQryTem.Close;
    ZQryTem.SQL.Text:=SqlTxt;
    ZQryTem.ExecSQL;
    Mainform.ShowMsgInfo('Update dispose table flag:Barcode='+aSam+' Field='+aField);
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-Update_Dispose_Sample_Status:'+E.Message);
    end;
  end;
end;

procedure TDM.IniRunDay;
var
  i:Integer;
  aDate:string;
  RunDateList:TStrList;
begin
  try
    cdsRunDay.EmptyDataSet;
    RunDateList:=GetArrayFromStr(FConfig.Discard_Run_Date,',');
    for i:=0 to High(RunDateList) do
    begin
      aDate:=Trim(RunDateList[i]);
      if aDate<>'' then
      begin
        cdsRunDay.Edit;
        cdsRunDay.FieldByName('day').Value:=aDate;
        cdsRunDay.Append;
        cdsRunDay.Post;
      end;
    end;
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-IniRunDay:'+E.Message);
    end;
  end;
end;

procedure TDM.IniSrmErrorcode;
var
  i:Integer;
  Errcode:string;
  ErrList:TStrList;
begin
  try
    cdsErrorCode.EmptyDataSet;
    ErrList:=GetArrayFromStr(FConfig.allow_srm_error_code,',');
    for i:=0 to High(ErrList) do
    begin
      Errcode:=ErrList[i];
      if Errcode<>'' then
      begin
        cdsErrorCode.Edit;
        cdsErrorCode.Append;
        cdsErrorCode.FieldByName('Errorcode').Value:=Errcode;
        cdsErrorCode.Post;
      end;
    end;
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-IniSRMErrorcode:'+E.Message);
    end;
  end;
end;

function TDM.Decode_SRM_Status(var aStatus: string; aMsg: string): Boolean;
var
  i:Integer;
  ReList,NodeList:TStrList;
  Tem,SMode,SType,Error,ErrMsg:string;
begin
  Result:=False;
  //\18^SRM^1^PA^^^^0^0^0^^\
  ReList:=GetArrayFromStr(aMsg,'\');
  for i:=0 to High(ReList) do
  begin
    Tem:=ReList[i];
    if Tem<>'' then
    begin
      if Pos('SRM',Tem)>0 then
      begin
        NodeList:= GetArrayFromStr(Tem,'^');
        SMode:=NodeList[3];
        Error:=NodeList[4];
        SType:=NodeList[5];
        aStatus:='Node-Mode:'+SMode+' Status:'+SType+' Error-Code:'+Error;
        if cdsErrorCode.Locate('Errorcode',Error,[]) then
          ErrMsg:='0000'
        else Errmsg:=Error;
        if (SMode='ON') and (((SType='G') or (SType='Y')) and (ErrMsg='0000')) then
            Result:=True;
        Break;
      end;
    end;
  end;
end;

procedure TDM.IniDisposeTimeRange;
var
  i:Integer;
  aRange,athreshold,starTime,endTime:string;
  RangeList,aList,TimeList:TStrList;
begin
  try
    cdsDisTimeRange.EmptyDataSet;
    RangeList:=GetArrayFromStr(FConfig.Discard_Time_Range,';');
    for i:=0 to High(RangeList) do
    begin
      if Trim(RangeList[i])<>'' then
      begin
        aRange:=Trim(RangeList[i]);
        aList:=GetArrayFromStr(aRange,',');
        TimeList:=GetArrayFromStr(aList[0],'-');
        starTime:=Trim(TimeList[0]);
        endTime:=Trim(TimeList[1]);
        athreshold:=Trim(aList[1]);
        cdsDisTimeRange.Edit;
        cdsDisTimeRange.Append;
        cdsDisTimeRange.FieldByName('startTime').Value:=starTime;
        cdsDisTimeRange.FieldByName('endTime').Value:=endTime;
        cdsDisTimeRange.FieldByName('threshold').Value:=athreshold;
        cdsDisTimeRange.Post;
      end;
    end;
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-IniDisposeTimeRange:'+E.Message);
    end;
  end;
end;

procedure TDM.Connect_To_Mysql;
begin
  try
    ZConnMysql.Protocol:='mysql';
    ZConnMysql.HostName:=FConfig.Aptio_IP;
    ZConnMysql.Port:=3306;
    ZConnMysql.User:='root';
    ZConnMysql.Password:='root';
    ZConnMysql.Database:='flexlab';
    ZConnMysql.Connect;
  except
    on e:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-Connect_To_DB:'+E.Message);
    end;
  end;
end;

procedure TDM.MySql_Ping;
begin
  try
    if Not ZConnMysql.Ping then
    begin
      ZConnMysql.Disconnect;
      ZConnMysql.Connect;
    end;
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-Ping_Mysql:'+E.Message);
    end;
  end;
end;

procedure TDM.Delete_UnSend_Record;
begin
  try
    ZQrySql.Close;
    ZQrySql.SQL.Text:='Delete from sam_dispose_status where send=0';
    ZQrySql.ExecSQL;
    Mainform.ShowMsgInfo('Delete Unsend record before close program');
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-Delete_UnSend_Record:'+E.Message);
    end;
  end;
end;

procedure TDM.Delete_History_dispose_Record;
begin
  try
    ZQrySql.Close;
    ZQrySql.SQL.Text:='delete from sam_dispose_status where send=1 and TIMESTAMPDIFF(DAY,send_time,Now())>10';
    ZQrySql.ExecSQL;
  except
    on E:Exception do
    begin
      Mainform.ShowMsgInfo('[>>Error]-Delete_History_dispose_Record:'+E.Message);
    end;
  end;
end;

end.
