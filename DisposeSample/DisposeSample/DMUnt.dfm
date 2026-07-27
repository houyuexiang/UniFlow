object DM: TDM
  OldCreateOrder = False
  OnCreate = DataModuleCreate
  Left = 716
  Top = 136
  Height = 281
  Width = 601
  object cdsRunDay: TClientDataSet
    Aggregates = <>
    Params = <>
    Left = 360
    Top = 24
    object cdsRunDayday: TStringField
      FieldName = 'day'
      Size = 2
    end
  end
  object cdsDisTimeRange: TClientDataSet
    Aggregates = <>
    Params = <>
    Left = 368
    Top = 88
    object cdsDisTimeRangestartTime: TStringField
      FieldName = 'startTime'
      Size = 6
    end
    object cdsDisTimeRangeendTime: TStringField
      FieldName = 'endTime'
      Size = 6
    end
    object cdsDisTimeRangethreshold: TStringField
      FieldName = 'threshold'
      Size = 6
    end
  end
  object ZQryTem: TZQuery
    Connection = ZConnMysql
    Params = <>
    Left = 88
    Top = 88
  end
  object cdsSample: TClientDataSet
    Aggregates = <>
    Params = <>
    ProviderName = 'dspQry'
    Left = 224
    Top = 96
    object StringField7: TStringField
      FieldName = 'barcode'
      Size = 25
    end
    object StringField8: TStringField
      FieldName = 'patient'
      Size = 61
    end
    object cdsSamplestype: TStringField
      FieldName = 'stype'
      Size = 2
    end
    object StringField9: TStringField
      FieldName = 'location'
      Size = 24
    end
    object StringField10: TStringField
      FieldName = 'update_time'
      Size = 15
    end
    object StringField11: TStringField
      FieldName = 'res_1'
      Size = 12
    end
    object StringField12: TStringField
      FieldName = 'res_2'
      Size = 12
    end
  end
  object ZQrySql: TZQuery
    Connection = ZConnMysql
    Params = <>
    Left = 160
    Top = 88
  end
  object ZConnMysql: TZConnection
    ControlsCodePage = cGET_ACP
    AutoEncodeStrings = False
    Properties.Strings = (
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
  object cdsErrorCode: TClientDataSet
    Aggregates = <>
    Params = <>
    Left = 224
    Top = 152
    object cdsErrorCodeErrorcode: TStringField
      FieldName = 'Errorcode'
      Size = 6
    end
  end
end
