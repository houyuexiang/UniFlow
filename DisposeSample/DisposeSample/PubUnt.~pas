unit PubUnt;

interface

Uses
  Windows, Messages, SysUtils, Variants, Classes, Graphics, Controls, Forms,
  Dialogs, ExtCtrls, IniFiles;

type
  TStrList = array of String ;
  TConfig = record
    Command_type:Integer;
    command_name:string;
    aptio_ip:string;
    srm_nodeid:string;
    //srm_capacity:Integer;
    discard_run_date:string;
    discard_time_range:string;
    loop_interval_time:Integer;
    max_wait_discard_count:Integer;
    max_onetime_select_discard_count:Integer;
    allow_srm_error_code:string;
  end;
  TSysStatus=(None,Ready,ACK,DISPOSE);
  
procedure WriteALog(Msg: string);
procedure ReNameLogFile(aOldFile:string);
Function  GetArrayFromStr(Str,Split : String) : TStrList ;
procedure DeleLogFile(aFilePath,aFileExt: String; aDelCount: Integer);
function  MsgChange(const Str: String): String;
procedure ReadConfig;
Function  Check_Dispose_Time_Range(const StarTime,EndTime:string):Boolean;
Function  Get_Location_RackNo(alocation:string):string;


var
  FConfig:TConfig;
  FPreBarcode:string;//记录上次丢弃的sample
  SysStatus:TSysStatus;
implementation

procedure ReadConfig;
var
  FileName:string;
  IniFile:TIniFile;
begin
  FileName:=GetCurrentDir()+'\DisposeSamples.ini';
  IniFile:=TIniFile.Create(FileName);
  FConfig.Command_type:=IniFile.ReadInteger('system','Command_type',0);
  FConfig.command_name:=IniFile.ReadString('system','command_name','');
  FConfig.aptio_ip:=IniFile.ReadString('system','aptio_ip','127.0.0.1');
  FConfig.srm_nodeid:=Trim(IniFile.ReadString('system','srm_nodeid',''));
  FConfig.discard_run_date:=Trim(IniFile.ReadString('system','discard_run_date','1,2,3,4,5'));
  FConfig.discard_time_range:=Trim(IniFile.ReadString('system','discard_time_range','00:00-23:59'));
  if FConfig.discard_time_range[Length(FConfig.discard_time_range)]<>';' then
    FConfig.discard_time_range:=FConfig.discard_time_range+';';
  FConfig.loop_interval_time:=IniFile.ReadInteger('system','loop_interval_time',6);
  FConfig.max_wait_discard_count:=IniFile.ReadInteger('system','max_wait_discard_count',0);
  FConfig.max_onetime_select_discard_count:=IniFile.ReadInteger('system','max_onetime_select_discard_count',10);
  FConfig.allow_srm_error_code:=IniFile.ReadString('system','allow_srm_error_code','');
  if FConfig.allow_srm_error_code[Length(FConfig.allow_srm_error_code)]<>',' then
    FConfig.allow_srm_error_code:=FConfig.allow_srm_error_code+',';

  IniFile.Free;
end;

Function Check_Dispose_Time_Range(const StarTime,EndTime:string):Boolean;
var
  NowTime:string;
begin
  Result:=False;
  NowTime:=FormatDateTime('HH:MM',Now);
  if StarTime<=EndTime then
  begin
    //the same day
    if (NowTime>=StarTime) and (NowTime<=EndTime) then
      Result:=True;
  end else begin
    //different day
    if (NowTime>=StarTime) or (NowTime<=EndTime) then
      Result:=True;
  end;
end;

Function Get_Location_RackNo(alocation:string):string;
var
  TemList:TStrList;
begin
  //&3-16-000012-02-03-05
  TemList:=GetArrayFromStr(alocation,'-');
  Result:=Trim(TemList[2]);
end;

function GetArrayFromStr(Str, Split: String): TStrList;
var
  iPos,Index : Integer ;
  Data : String ;
begin
  try
    SetLength(Result,Length(Str)+1) ;
    if Length(Str) = 0 then Exit ;
    Index := 0 ;
    Str:=Str+Split;
    iPos := Pos(Split,Str) ;
    While iPos <> 0 do
    begin
      Data := Copy(Str,1,iPos -1) ;
      Result[Index] := Data ;
      Inc(Index) ;
      Delete(Str,1,iPos+Length(Split)-1) ;
      iPos := Pos(Split,Str) ;
    end ;
    SetLength(Result,Index) ;
  except
  end;
end ;

procedure WriteALog(Msg: string);
var
  logFile: TextFile;
  sFileName, sDir, sLog: string;
begin
  try
    sDir := GetCurrentDir() + '\Log\';
    if not DirectoryExists(sDir) then CreateDir(sDir);
    sFileName := sDir +'DisposeSample'+'_'+ FormatDateTime('YYYYMMDD', Now) + '.log';
    try
      AssignFile(logFile, sFileName);
      if FileExists(sFileName) then
        Append(logFile)
      else
        ReWrite(logFile);
      sLog :=FormatDateTime('HH:MM:SS',Now)+' '+Msg;
      WriteLn(logFile, sLog);
      Flush(logFile);
    finally
      CloseFile(logFile);
    end;
    ReNameLogFile(sFileName);
  except
  end;
end;

procedure ReNameLogFile(aOldFile:string);
var
  aSize:Integer;
  NwTime,filepath:string;
  searchrec:TSearchRec;
begin
  try
    filepath:=ExtractFilePath(aOldFile);
    if FindFirst(aOldFile,faAnyFile,searchrec)=0 then
    begin
      aSize:=searchrec.Size;
      if (aSize div (1024*1024)) >=3 then
      begin
        NwTime:=FormatDateTime('YYYYMMDDHHMMSS',Now);
        RenameFile(aOldFile,filepath+'DisposeSample'+'_'+NwTime+'.log');
      end;
    end;
    FindClose(searchrec);//Add 20211014 避免内存增加
  except
    on e:Exception do
    begin
      WriteALog('[>>Error]-ReNameLogFile:'+E.Message);
    end;
  end;
end;

procedure DeleLogFile(aFilePath, aFileExt: String; aDelCount: Integer);
var
  Sr:TSearchRec;
  Found:Integer;
  aFileDateStr,NowDateStr:Integer;
begin
  NowDateStr:=StrToInt(FormatDateTime('YYYYMMDD',Now));
  Try
    Found:=Findfirst(aFilePath+'*.'+aFileExt,faAnyFile,sr);
    While Found=0 do
    begin
      aFileDateStr:=StrToInt(FormatDateTime('YYYYMMDD',FileDateToDateTime(Sr.Time)));
      if NowDateStr-aFileDateStr>aDelCount then
        DeleteFile(aFilePath+Sr.Name);
      Found:=findNext(Sr);
    end;
    FindClose(sr);//Add 20211014 避免内存增加
  except
  end;
end;

function MsgChange(const Str: String): String;
var
  TemStr:String;
begin
  TemStr:=StringReplace(Str,#13,'<CR>',[rfReplaceAll]);
  TemStr:=StringReplace(TemStr,#10,'<LF>',[rfReplaceAll]);
  Result:=TemStr;
end;

end.