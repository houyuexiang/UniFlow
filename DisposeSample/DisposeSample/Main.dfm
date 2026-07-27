object Mainform: TMainform
  Left = 407
  Top = 180
  BorderStyle = bsDialog
  Caption = 'Automatic Dispose Samples'
  ClientHeight = 411
  ClientWidth = 787
  Color = clBtnFace
  Font.Charset = DEFAULT_CHARSET
  Font.Color = clWindowText
  Font.Height = -11
  Font.Name = 'MS Sans Serif'
  Font.Style = []
  OldCreateOrder = False
  OnCloseQuery = FormCloseQuery
  OnCreate = FormCreate
  OnDestroy = FormDestroy
  PixelsPerInch = 96
  TextHeight = 13
  object MemLog: TMemo
    Left = 0
    Top = 0
    Width = 787
    Height = 411
    Align = alClient
    Color = clNone
    Ctl3D = False
    Font.Charset = DEFAULT_CHARSET
    Font.Color = clWhite
    Font.Height = -13
    Font.Name = 'MS Sans Serif'
    Font.Style = [fsBold]
    ImeName = #20013#25991'('#31616#20307') - '#25628#29399#25340#38899#36755#20837#27861
    ParentCtl3D = False
    ParentFont = False
    ScrollBars = ssBoth
    TabOrder = 0
  end
  object pm: TPopupMenu
    Left = 88
    Top = 312
    object NShow: TMenuItem
      Caption = 'Show'
      OnClick = NShowClick
    end
    object N1: TMenuItem
      Caption = '-'
    end
    object NHide: TMenuItem
      Caption = 'Hide'
      OnClick = NHideClick
    end
    object N2: TMenuItem
      Caption = '-'
    end
    object NClose: TMenuItem
      Caption = 'Close'
      OnClick = NCloseClick
    end
  end
  object ClientSocket: TClientSocket
    Active = False
    ClientType = ctNonBlocking
    Port = 0
    OnConnect = ClientSocketConnect
    OnDisconnect = ClientSocketDisconnect
    OnRead = ClientSocketRead
    OnError = ClientSocketError
    Left = 136
    Top = 312
  end
  object CtrlTimer: TTimer
    Enabled = False
    Interval = 5000
    OnTimer = CtrlTimerTimer
    Left = 32
    Top = 312
  end
  object RzTrayIcon: TRzTrayIcon
    Hint = 'Automatic Dispose Samples'
    PopupMenu = pm
    Left = 192
    Top = 313
  end
end
