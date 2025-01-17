﻿using System.Text.Json;
using devicesConnector.Configs;

using devicesConnector.FiscalRegistrar.Objects;
using devicesConnector.FiscalRegistrar.Objects.CountrySpecificData.Russia;
using Enums = devicesConnector.FiscalRegistrar.Objects.Enums;

namespace devicesConnector.FiscalRegistrar.Devices.Russia;

public partial class KkmServer : IFiscalRegistrarDevice
{

    private Device _device;
    private KkmPrintCheck _kkmCheckCommand;

    public KkmServer(Device device)
    {
        _device = device;


    }



    public KkmStatus GetStatus()
    {
        var c =new KkmGetInfo();

     var r=  SendCommand(c);

     var answer =r.Rezult.Deserialize< KkmServerKktInfoAnswer> ();

        var status = new KkmStatus
        {
            SessionStatus = answer.Info.SessionState switch
            {
                1 => Enums.SessionStatuses.Close,
                2 => Enums.SessionStatuses.Open,
                3 => Enums.SessionStatuses.OpenMore24Hours,
                _ => Enums.SessionStatuses.Unknown
            },
            CheckStatus = Enums.CheckStatuses.Close,
            CheckNumber = answer.CheckNumber,
            SessionNumber = answer.SessionNumber,
            SoftwareVersion = answer.Info.Firmware_Version,
            FnDateEnd = answer.Info.FN_DateEnd,
            CashSum = answer.Info.BalanceCash,
            FactoryNumber = answer.Info.KktNumber
            
        };





        return status;
    }

    public void OpenSession(Cashier cashier)
    {
        var c = new KkmOpenSession();
        SendCommand(c);

    }

    public void GetReport(Enums.ReportTypes type, Cashier cashier)
    {
        switch (type)
        {
            case Enums.ReportTypes.ZReport:
                SendCommand(new KkmCloseShift
                {
                    CashierName = cashier.Name,
                    CashierVATIN = cashier.TaxId
                });
                break;
            case Enums.ReportTypes.XReport:
                SendCommand(new KkmGetXReport
                {

                });
                break;
            case Enums.ReportTypes.XReportWithGoods:
                throw new NotSupportedException();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }

      
    }

    public void OpenReceipt(ReceiptData? receipt)
    {
        if (receipt == null)
        {
            throw new NullReferenceException();
        }

        //регистрация кассира
        _kkmCheckCommand = new KkmPrintCheck()
        {
            CashierName = receipt.Cashier.Name,
            CashierVATIN = receipt.Cashier.TaxId

        };

        //регистрация покупателя
        if (receipt.Contractor != null)
        {
            _kkmCheckCommand.ClientInfo = receipt.Contractor.Name;
            _kkmCheckCommand.ClientINN = receipt.Contractor.TaxId;
        }

     

        //установка типа чека
        _kkmCheckCommand.TypeCheck = receipt.OperationType switch
        {
            Enums.ReceiptOperationTypes.Sale => 0,
            Enums.ReceiptOperationTypes.ReturnSale => 1,
            Enums.ReceiptOperationTypes.Buy => 10,
            Enums.ReceiptOperationTypes.ReturnBuy => 11,
            _ => throw new ArgumentOutOfRangeException()
        };

        //признак фискальности
        _kkmCheckCommand.IsFiscalCheck = receipt.FiscalType == Enums.ReceiptFiscalTypes.Fiscal;

    
        //специфичные для РФ данные
        #region Специфичные для РФ данные

        Objects.CountrySpecificData.Russia.ReceiptData? ruData = receipt.CountrySpecificData.Deserialize<Objects.CountrySpecificData.Russia.ReceiptData>();

     

        if (ruData != null)
        {
            if (ruData.DigitalReceiptAddress != null)
            {
                _kkmCheckCommand.ClientAddress = ruData.DigitalReceiptAddress;
            }

            _kkmCheckCommand.TaxVariant = ruData.TaxVariantIndex;

            _kkmCheckCommand.NotPrint = receipt.IsPrintReceipt == false;
        }

        #endregion



      
        

       


    }

    public void CloseReceipt()
    {
        SendCommand(_kkmCheckCommand);
        
    }

    public void CancelReceipt()
    {
        throw new NotImplementedException();
    }

    public void RegisterItem(ReceiptItem item)
    {

        //значение налоговой ставки (НДС)
       var taxValue = item.TaxRateIndex switch
        {
            1 => -1,
            2 => 0,
            3 => 10,
            4 => 20,
            5 => 110,
            6 => 120,
            _ => -1
        };

 
        //позиция в чеке
       var cs = new KkmPrintCheck.CheckString
       {
           //позиция для регистрации товара
           Register = new KkmPrintCheck.Register
           {
               Name = item.Name,
               Price = item.Price,
               Quantity = item.Quantity,
               Amount = Math.Round(item.Price * item.Quantity, 2, MidpointRounding.AwayFromZero), 
               Department = item.DepartmentIndex ,
               Tax = taxValue
           }
       };

       //специфичные для РФ данные
       var ruData = item.CountrySpecificData.Deserialize<ReceiptItemData>();


       if (ruData != null)
       {
           //ФФД 
           cs.Register.SignCalculationObject = (int) ruData.FfdData.Subject;
           cs.Register.SignMethodCalculation = (int) ruData.FfdData.Method;

           //Маркировка

           if (ruData.MarkingInfo != null)
           {
                cs.Register.GoodCodeData = new KkmPrintCheck.Register.GoodCode()
               {
                   BarCode = ruData.MarkingInfo.RawCode,
                   AcceptOnBad = true,
                   ContainsSerialNumber = false
               };
           }
        
        }

       //добавляем в чек
       _kkmCheckCommand.CheckStrings.Add(cs);


    }

    public void RegisterPayment(ReceiptPayment payment)
    {
        switch (payment.MethodIndex)
        {
            case 1:
                _kkmCheckCommand.Cash = payment.Sum;
                break;
            case 2:
                _kkmCheckCommand.ElectronicPayment = payment.Sum;
                break;
            case 103:
                _kkmCheckCommand.AdvancePayment  = payment.Sum;
                break;
            case 104:
                _kkmCheckCommand.Credit = payment.Sum;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(payment.MethodIndex));
        }
    }

    public void PrintText(string text)
    {
    }



    public void Connect()
    {
        //можно проверять доступность ККМ-сервера
    }

    public void Disconnect()
    {
        //ничего не делаем
    }


    public void CashIn(decimal sum, Cashier cashier)
    {
        var c = new KkmCashIn
        {
            CashierName = cashier.Name,
            CashierVATIN = cashier.TaxId,
            Amount = sum
        };

        SendCommand(c);

    }

    public void CashOut(decimal sum, Cashier cashier)
    {
        var c = new KkmCashOut
        {
            CashierName = cashier.Name,
            CashierVATIN = cashier.TaxId,
            Amount = sum
        };

        SendCommand(c);
    }

    public void CutPaper()
    {
        throw new NotImplementedException();
    }

    public void OpenCashBox()
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        //
    }
}