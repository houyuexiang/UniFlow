object DM: TDM
  OldCreateOrder = False
  OnCreate = DataModuleCreate
  Left = 743
  Top = 240
  Height = 172
  Width = 342
  object ZConnMysql: TZConnection
    ControlsCodePage = cGET_ACP
    AutoEncodeStrings = False
    Properties.Strings = (
      ''
      'controls_cp=GET_ACP')
    Port = 0
    Left = 32
    Top = 24
  end
  object ZQuery: TZQuery
    Connection = ZConnMysql
    Params = <>
    Left = 96
    Top = 24
  end
  object dspQry: TDataSetProvider
    DataSet = ZQuery
    Options = [poAllowCommandText]
    Left = 160
    Top = 24
  end
  object cdsQry: TClientDataSet
    Aggregates = <>
    Params = <>
    ProviderName = 'dspQry'
    Left = 224
    Top = 24
  end
end
