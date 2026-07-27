unit Main;

interface

uses
  Windows, Messages, SysUtils, Variants, Classes, Graphics, Controls, Forms,MidasLib,
  Dialogs, StdCtrls, Menus, ExtCtrls, ScktComp, RzTray, Grids, DBGrids, DateUtils,
  DB;

type
  TMainform = class(TForm)
    pm: TPopupMenu;
    MemLog: TMemo;
    ClientSocket: TClientSocket;
    CtrlTimer: TTimer;
    RzTrayIcon: TRzTrayIcon;
    NShow: TMenuItem;
    N1: TMenuItem;
    NHide: TMenuItem;
    N2: TMenuItem;
    NClose: TMenuItem;
    procedure NShowClick(Sender: TObject);
    procedure NHideClick(Sender: TObject);
    procedure NCloseClick(Sender: TObject);
    procedure FormCloseQuery(Sender: TObject; var CanClose: Boolean);
    procedure FormCreate(Sender: TObject);
    procedure ClientSocketDisconnect(Sender: TObject;
      Socket: TCustomWinSocket);
    procedure ClientSocketConnect(Sender: TObject;
      Socket: TCustomWinSocket);
    procedure ClientSocketError(Sender: TObject; Socket: TCustomWinSocket;
      ErrorEvent: TErrorEvent; var ErrorCode: Integer);
    procedure ClientSocketRead(Sender: TObject; Socket: TCustomWinSocket);
    procedure CtrlTimerTimer(Sender: TObject);
    procedure Button1Click(Sender: TObject);
    procedure FormDestroy(Sender: TObject);
  private
    { Private declarations }
    function  Connect_To_Aptio_Server:Boolean;
  public
    { Public declarations }
    procedure ShowMsgInfo(aMsg:string);
    function  SendOrderFrame(OrderStr:String):integer;
    function  Send_SRM_Status_Check:Integer;
    function  Ready_Dispose_Sample_Record(SRMThreshod,icmdtype:integer):Integer;
  end;

var
  Mainform: TMainform;

implementation

uses
  PubUnt,DMUnt;

{$R *.dfm}

procedure TMainform.FormCreate(Sender: TObject);
begin
  Connect_To_Aptio_Server;
  //Application.ShowMainForm:=False;
  CtrlTimer.Interval:=FConfig.Loop_Interval_Time*1000;
  CtrlTimer.Enabled:=True;
end;

procedure TMainform.ClientSocketRead(Sender: TObject;
  Socket: TCustomWinSocket);
var
  SrmFlag:Boolean;
  ReStr,SrmMsg:string;
  barcode,location,stype,res_1:string;
begin
  try
    CtrlTimer.Enabled:=False;
    try
      ReStr:=Socket.ReceiveText;
      //SRM status return msg
      if Pos('STATUS',ReStr)=1 then
      begin
        SrmFlag:=DM.Decode_SRM_Status(SrmMsg,ReStr);
        ShowMsgInfo('[SRM Check] '+SrmMsg);
        if SrmFlag=True then
        begin
          if DM.select_one_send_record(barcode,location,stype,res_1)>0 then
            SendOrderFrame(barcode+'|'+location+'|'+stype+'|'+res_1)
          else
            SysStatus:=None;
        end;
      end
      //Dispose return msg
      else if Pos('ACK',ReStr)=1 then
      begin
        SysStatus:=ACK;
        ShowMsgInfo('<R>:'+MsgChange(ReStr));
        DM.Update_Dispose_Sample_Status(FPreBarcode,'send');
      end;
    except
      on e:Exception do
      begin
        ShowMsgInfo('[>>Error]-SOCKET:'+E.Message);
      end;
    end;
  finally
    CtrlTimer.Enabled:=True;
  end;
end;

procedure TMainform.CtrlTimerTimer(Sender: TObject);
var
  SRMThreshod:Integer;
  barcode,location,res_1:string;
begin
  try
    CtrlTimer.Enabled:=False;
    try
      if Not ClientSocket.Active then Connect_To_Aptio_Server
      else begin
        DM.MySql_Ping;//20210206
        if DM.Check_Work_Time(SRMThreshod) then
        begin

          if MemLog.Lines.Count>500 then MemLog.Clear;
          if SysStatus=None then
          begin
            DM.Delete_History_dispose_Record;
            if Ready_Dispose_Sample_Record(SRMThreshod,FConfig.Command_type)>0 then SysStatus:=Ready;
          end;
          if SysStatus=Ready then
            Send_SRM_Status_Check;
          if SysStatus=ACK then
          begin
            if DM.check_t_Sample_Dispose_Status(FPreBarcode) then
            begin
              DM.Update_Dispose_Sample_Status(FPreBarcode,'disposed');
              SysStatus:=DISPOSE;
            end else begin
              if FConfig.max_wait_discard_count>0 then
              begin
                if DM.Get_Sample_checkcount(FPreBarcode)<FConfig.max_wait_discard_count then
                  DM.Update_Dispose_Sample_Status(FPreBarcode,'checkcount')
                else SysStatus:=DISPOSE;
              end else SysStatus:=DISPOSE;
            end;
          end;
          if SysStatus=DISPOSE then
            Send_SRM_Status_Check;
        end else
          ShowMsgInfo('Not in work time............');
      end;
      DeleLogFile(GetCurrentDir() + '\log\','log',30);
    except
      on e:Exception do
      begin
        ShowMsgInfo('[>>Error]-CtrlTimerTimer:'+E.Message);
      end;
    end;
  finally
    CtrlTimer.Enabled:=True;
  end;
end;

function TMainform.Ready_Dispose_Sample_Record(SRMThreshod, icmdtype: integer): Integer;
var
  iInsrtCount,iNeedDisp:Integer;
  iSRMSamCount:Integer;
  iGetCount:Integer;
begin
  iNeedDisp:=0;
  iInsrtCount:=0;
  ShowMsgInfo('- - - - - Ready dispose Records - - - - - -');
  //SRM sample count
  iSRMSamCount:=DM.Check_SRM_SampleCount;
  ShowMsgInfo('SRM->Sample Counts:'+IntToStr(iSRMSamCount));
  //threshold count
  ShowMsgInfo('SRM->Sample Threshold Counts:'+IntToStr(SRMThreshod));
  if iSRMSamCount>SRMThreshod then
  begin
    iNeedDisp:=iSRMSamCount-SRMThreshod;
    ShowMsgInfo('SRM->Need Dispose Sample Counts:'+IntToStr(iNeedDisp));
    iGetCount:=DM.Get_Dispose_sample(icmdtype);
    ShowMsgInfo('[SQL] return record counts:'+IntToStr(iGetCount));
    if iGetCount>0 then
      iInsrtCount:=DM.Insert_Dispose_Record(iNeedDisp);
    Result:=iInsrtCount;
  end else
    ShowMsgInfo('SRM->Need Dispose Sample Counts:0');
end;

function TMainform.SendOrderFrame(OrderStr: String): integer;
var
  TemList:TStrList;
  location,barcode,stype,res_1:string;
  SendStr,SamInfo:string;
begin
  Try
    if Not ClientSocket.Active then Connect_To_Aptio_Server;
    TemList:=GetArrayFromStr(OrderStr,'|');
    barcode:=TemList[0];
    FPreBarcode:=barcode;
    location:=TemList[1];
    stype:=TemList[2];
    res_1:=TemList[3];
    Sendstr:='COMMENT S002^'+barcode+'\TRASH^S'+#13#10;
    Result:=ClientSocket.Socket.SendText(SendStr);
    SamInfo:='['+stype+' '+res_1+' '+location+'] '+Sendstr;
    ShowMsgInfo('<S>:'+MsgChange(SamInfo));
  except
    On E:Exception do
    begin
      ShowMsgInfo('[>>Error]-SendOrderFrame:'+E.Message);
    end;
  end;
end;

function TMainform.Send_SRM_Status_Check: Integer;
var
  SendStr,SamInfo:string;
begin
  try
    if Not ClientSocket.Active then Connect_To_Aptio_Server;
    SendStr:='STATUS-REQUEST 1'+#13#10;
    Result:=ClientSocket.Socket.SendText(SendStr);
    SamInfo:='[SRM Check] '+SendStr;
    ShowMsgInfo(MsgChange(SamInfo));
  except
    On E:Exception do
    begin
      ShowMsgInfo('[>>Error]-SendOrderFrame:'+E.Message);
    end;
  end;
end;

function TMainform.Connect_To_Aptio_Server: Boolean;
begin
  try
    ClientSocket.Close;
    ClientSocket.Host:=FConfig.Aptio_IP;
    ClientSocket.Port:= 2055;
    ClientSocket.Active:=True;
  except
    on E:Exception do
    begin
      ShowMsgInfo('[>>Error]-Connect_To_Aptio_Server:'+E.Message);
    end;
  end;
end;

procedure TMainform.ClientSocketDisconnect(Sender: TObject;
  Socket: TCustomWinSocket);
begin
  ShowMsgInfo('DisConnect to Aptio Server !');
end;

procedure TMainform.ClientSocketConnect(Sender: TObject;
  Socket: TCustomWinSocket);
begin
  ShowMsgInfo('Connect to Aptio Server success !');
end;

procedure TMainform.ClientSocketError(Sender: TObject;
  Socket: TCustomWinSocket; ErrorEvent: TErrorEvent;
  var ErrorCode: Integer);
begin
  ShowMsgInfo('Connect to Aptio Server Error '+IntToStr(ErrorCode));
  try //if ErrorCode=10053 then
    ClientSocket.Close;
  except
    on E:Exception do
    begin
      ShowMsgInfo('[>>Error]-ClientSocketError:'+E.Message);
    end;
  end;
  ErrorCode:=0;
end;

procedure TMainform.NShowClick(Sender: TObject);
begin
  Mainform.Show;
  WindowState := TWindowState(tag);
  SetForegroundWindow(Handle);
end;

procedure TMainform.NHideClick(Sender: TObject);
begin
  Mainform.Hide;
end;

procedure TMainform.NCloseClick(Sender: TObject);
begin
  if MessageBox(Self.Handle,'Do you want to Close the Dispose Sample Application ?','Question',MB_ICONQUESTION+MB_OkCancel)= idOk then
    Application.Terminate;
end;

procedure TMainform.FormCloseQuery(Sender: TObject; var CanClose: Boolean);
begin
  Tag := Ord(WindowState);
  WindowState := wsMinimized;
  Mainform.Hide;
  CanClose := False;
end;

procedure TMainform.Button1Click(Sender: TObject);
var
  aa,bb,ss,CC:string;
  scount,i:Integer;
begin
  //Send_SRM_Status_Check;
  //DM.ZQuery.Close;
  {//所有住院标本
  DM.ZQuery.SQL.Text:='select sample_id collate latin1_swedish_ci as barcode,patient,s_type,t_location collate latin1_swedish_ci as location,t_status,update_time,res_2,res_1 as InPatient,'
                     +' DATE_FORMAT(NOW(),''%Y%m%d%H%i%s'') as NowTime '
                     +' from t_sample '
                     +' where substring(patient,1,3)=''005'''
                     +' and t_location like ''&1-09-%'''
                     +' and t_status='+'''C'''
                     +' and res_1>='+''''+'202010221308'+''''
                     +' order by res_1 desc';}
  {DM.ZQuery.Open;
  scount:=DM.ZQuery.RecordCount;
  ss:=DM.ZQuery.Fields[3].Value;
   ss:=DM.ZQuery.FieldByName('location').AsString;}

  {try
   //CLIENT_MULTI_STATEMENTS=TRUE可调用存储过程
   //DM.ZQuery.Params.CreateParam(ftInteger,'@SS',ptOutput);
   DM.ZQuery.SQL.Text:='CALL mytest(@a)';//out param

   DM.ZQuery.Open;

   i:=DM.ZQuery.RecordCount;
   i:=DM.ZQuery.Fields.Count;
   aa:=DM.ZQuery.Fields[0].Value;
   bb:=DM.ZQuery.Fields[0].FieldName;
   ss:=DM.ZQuery.FieldByName('srmcount').AsString;

   MemLog.Lines.Add(aa+'='+bb+'='+ss);
    //DM.Srm_Auto_dispose_Count(72.1);
  except
    on e:Exception do
    begin
      MemLog.Lines.Add(e.Message);
    end;
  end;}
end;

procedure TMainform.ShowMsgInfo(aMsg: string);
begin
  WriteALog(aMsg);
  MemLog.Lines.Add(FormatDateTime('HH:MM:SS ',Now)+aMsg);
end;

procedure TMainform.FormDestroy(Sender: TObject);
begin
  DM.Delete_UnSend_Record;
end;

initialization
  MutHandle:=OpenMutex(MUTEX_ALL_ACCESS,False,Pchar(ExtractFileName(application.ExeName)));
  if MutHandle<>0 then
  begin
     messagebox(0,Pchar('Have a program '+ExtractFileName(application.ExeName)+' is Running'),'Run',0);{提示程序已运行}
     halt; {退出程序}
  end
  else
    MutHandle:=CreateMutex(nil,false,PChar(ExtractFileName(application.ExeName)));
finalization
  if MutHandle<>0 then CloseHandle(MutHandle);

end.
